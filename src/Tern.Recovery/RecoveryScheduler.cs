using System.Diagnostics;
using Tern.Domain.Documents;

namespace Tern.Recovery;

public sealed record RecoveryCheckpointFailure(Guid SessionId, long Revision, Exception Exception);

/// <summary>
/// Writes a recovery checkpoint shortly after the user stops editing a dirty document, and retires
/// the checkpoint only when the document is clean again. Never calls Save.
/// Create it on the UI thread: documents are read on the captured synchronization context.
/// </summary>
public sealed class RecoveryScheduler : IDisposable
{
    private readonly RecoveryStore _store;
    private readonly TimeSpan _idleDelay;
    private readonly TimeSpan _maximumWait;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly Dictionary<Guid, Tracked> _tracked = [];
    private readonly Lock _gate = new();

    public RecoveryScheduler(RecoveryStore store, TimeSpan? idleDelay = null, TimeSpan? maximumWait = null)
    {
        _store = store;
        _idleDelay = idleDelay ?? TimeSpan.FromMilliseconds(1200);
        _maximumWait = maximumWait ?? TimeSpan.FromSeconds(10);
        if (_idleDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleDelay));
        if (_maximumWait <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumWait));
    }

    /// <summary>Raised when a checkpoint could not be written, so the UI can warn the user.</summary>
    public event EventHandler<Exception>? CheckpointFailed;

    /// <summary>Raised after durable persistence, on the scheduler's creating context.</summary>
    public event EventHandler<RecoveryRecord>? CheckpointWritten;

    public event EventHandler<RecoveryCheckpointFailure>? CheckpointWriteFailed;

    /// <summary>Raised when stale checkpoints could not be removed after a save (for example the recovery folder is unreadable).</summary>
    public event EventHandler? RetireFailed;

    public void Track(DocumentSession session, Func<bool>? isExternalDraft = null, Func<string?>? sourceUrl = null, Func<string?>? recoveredFromPath = null,
        Func<bool>? allowCheckpoint = null)
    {
        lock (_gate)
        {
            if (_tracked.ContainsKey(session.Id))
            {
                return;
            }

            var timer = new Timer(_ => _ = FlushAsync(session, automatic: true), null, Timeout.Infinite, Timeout.Infinite);
            _tracked[session.Id] = new Tracked(session, timer, isExternalDraft, sourceUrl, recoveredFromPath, allowCheckpoint);
            session.StateChanged += OnStateChanged;
        }
    }

    public void Untrack(DocumentSession session, bool retire)
    {
        lock (_gate)
        {
            if (_tracked.Remove(session.Id, out var tracked))
            {
                tracked.Active = false;
                session.StateChanged -= OnStateChanged;
                tracked.Timer.Dispose();
            }
        }

        if (retire)
        {
            _store.Retire(session.Id);
        }
    }

    /// <summary>Writes a checkpoint for every dirty document immediately (used on shutdown).</summary>
    public async Task<bool> FlushAllAsync()
    {
        DocumentSession[] sessions;
        lock (_gate)
        {
            sessions = [.. _tracked.Values.Select(t => t.Session)];
        }

        var succeeded = true;
        foreach (var session in sessions)
        {
            succeeded &= await FlushAsync(session).ConfigureAwait(false);
        }

        return succeeded;
    }

    /// <summary>Explicitly retries protection for the intended tracked session, using its latest snapshot.</summary>
    public Task<bool> RetryAsync(DocumentSession session) => FlushAsync(session);

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var tracked in _tracked.Values)
            {
                tracked.Active = false;
                tracked.Session.StateChanged -= OnStateChanged;
                tracked.Timer.Dispose();
            }

            _tracked.Clear();
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (sender is not DocumentSession session)
        {
            return;
        }

        var retire = false;
        lock (_gate)
        {
            if (!_tracked.TryGetValue(session.Id, out var tracked) || !tracked.Active) return;
            if (session.IsDirty)
            {
                tracked.PendingSince ??= Stopwatch.GetTimestamp();
                Schedule(tracked);
            }
            else
            {
                tracked.PendingSince = null;
                tracked.Timer.Change(Timeout.Infinite, Timeout.Infinite);
                retire = true;
            }
        }
        if (retire) RetireOrWarn(session.Id);
    }

    internal TimeSpan IdleDelayFor(int characterCount) =>
        TimeSpan.FromMilliseconds(Math.Max(_idleDelay.TotalMilliseconds,
            Math.Min(10_000, _idleDelay.TotalMilliseconds * Math.Max(1, characterCount / (4.0 * 1024 * 1024)))));

    private void Schedule(Tracked tracked)
    {
        if (tracked.PendingSince is not { } since) return;
        var remaining = _maximumWait - Stopwatch.GetElapsedTime(since);
        var idle = IdleDelayFor(tracked.Session.Buffer.Length);
        var delay = remaining < idle ? remaining : idle;
        tracked.Timer.Change(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    // Cleanup after a successful save must never throw into the save call stack; report it as a warning instead.
    private void RetireOrWarn(Guid sessionId)
    {
        if (!_store.Retire(sessionId)) RetireFailed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<bool> FlushAsync(DocumentSession session, bool automatic = false)
    {
        Tracked? tracked;
        lock (_gate)
        {
            if (!_tracked.TryGetValue(session.Id, out tracked) || !tracked.Active) return true;
            if (automatic && tracked.AutomaticFlushActive) return true;
            if (automatic) tracked.AutomaticFlushActive = true;
        }
        await tracked.WriteGate.WaitAsync().ConfigureAwait(false);
        RecoveryRecord? record = null;
        try
        {
            record = await OnContextAsync(() =>
            {
                lock (_gate)
                {
                    if (!tracked.Active || automatic && tracked.PendingSince is null) return null;
                    tracked.PendingSince = null;
                    tracked.Timer.Change(Timeout.Infinite, Timeout.Infinite);
                }
                if (tracked.AllowCheckpoint?.Invoke() == false) { RetireOrWarn(session.Id); return null; }
                return CaptureRecord(session, tracked.IsExternalDraft?.Invoke() == true && session.FilePath is null,
                    tracked.SourceUrl?.Invoke(), tracked.RecoveredFromPath?.Invoke());
            }).ConfigureAwait(false);
            if (record is null)
            {
                return true;
            }

            await _store.WriteCheckpointAsync(record).ConfigureAwait(false);

            // A save may have completed while the checkpoint was being written.
            await OnContextAsync(() =>
            {
                lock (_gate)
                {
                    if (tracked.Active && session.IsDirty && session.Revision != record.Revision)
                        tracked.PendingSince ??= Stopwatch.GetTimestamp();
                }
                if (!tracked.Active || !session.IsDirty || tracked.AllowCheckpoint?.Invoke() == false) RetireOrWarn(session.Id);
                else CheckpointWritten?.Invoke(this, record);
                return true;
            }).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await OnContextAsync(() =>
            {
                lock (_gate)
                {
                    if (!tracked.Active) return true;
                    if (session.IsDirty) tracked.PendingSince ??= Stopwatch.GetTimestamp();
                }
                CheckpointFailed?.Invoke(this, ex);
                CheckpointWriteFailed?.Invoke(this, new RecoveryCheckpointFailure(session.Id, record?.Revision ?? session.Revision, ex));
                return true;
            }).ConfigureAwait(false);
            return false;
        }
        finally
        {
            tracked.WriteGate.Release();
            await OnContextAsync(() =>
            {
                lock (_gate)
                {
                    if (automatic) tracked.AutomaticFlushActive = false;
                    if (tracked.Active && !tracked.AutomaticFlushActive) Schedule(tracked);
                }
                return true;
            }).ConfigureAwait(false);
        }
    }

    private static RecoveryRecord? CaptureRecord(DocumentSession session, bool isExternalDraft, string? sourceUrl, string? recoveredFromPath)
    {
        if (!session.IsDirty)
        {
            return null;
        }

        var snapshot = session.GetSnapshot();
        return new RecoveryRecord(
            session.Id,
            session.FilePath ?? recoveredFromPath,
            session.DisplayName,
            snapshot.Revision,
            DateTime.UtcNow,
            session.Baseline?.Sha256,
            session.Format,
            snapshot.Text,
            isExternalDraft,
            sourceUrl);
    }

    private Task<T> OnContextAsync<T>(Func<T> work)
    {
        if (_context is null)
        {
            return Task.FromResult(work());
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _context.Post(_ =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }, null);
        return completion.Task;
    }

    private sealed record Tracked(DocumentSession Session, Timer Timer, Func<bool>? IsExternalDraft, Func<string?>? SourceUrl, Func<string?>? RecoveredFromPath,
        Func<bool>? AllowCheckpoint)
    {
        public SemaphoreSlim WriteGate { get; } = new(1, 1);
        public volatile bool Active = true;
        public long? PendingSince { get; set; }
        public bool AutomaticFlushActive { get; set; }
    }
}
