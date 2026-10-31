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
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly Dictionary<Guid, Tracked> _tracked = [];
    private readonly Lock _gate = new();

    public RecoveryScheduler(RecoveryStore store, TimeSpan? idleDelay = null)
    {
        _store = store;
        _idleDelay = idleDelay ?? TimeSpan.FromMilliseconds(1200);
    }

    /// <summary>Raised when a checkpoint could not be written, so the UI can warn the user.</summary>
    public event EventHandler<Exception>? CheckpointFailed;

    /// <summary>Raised after durable persistence, on the scheduler's creating context.</summary>
    public event EventHandler<RecoveryRecord>? CheckpointWritten;

    public event EventHandler<RecoveryCheckpointFailure>? CheckpointWriteFailed;

    public void Track(DocumentSession session, Func<bool>? isExternalDraft = null, Func<string?>? sourceUrl = null, Func<string?>? recoveredFromPath = null)
    {
        lock (_gate)
        {
            if (_tracked.ContainsKey(session.Id))
            {
                return;
            }

            var timer = new Timer(_ => _ = FlushAsync(session), null, Timeout.Infinite, Timeout.Infinite);
            _tracked[session.Id] = new Tracked(session, timer, isExternalDraft, sourceUrl, recoveredFromPath);
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

        Tracked? tracked;
        lock (_gate)
        {
            _tracked.TryGetValue(session.Id, out tracked);
        }

        if (tracked is null)
        {
            return;
        }

        if (session.IsDirty)
        {
            tracked.Timer.Change(_idleDelay, Timeout.InfiniteTimeSpan);
        }
        else
        {
            tracked.Timer.Change(Timeout.Infinite, Timeout.Infinite);
            _store.Retire(session.Id);
        }
    }

    private async Task<bool> FlushAsync(DocumentSession session)
    {
        Tracked? tracked;
        lock (_gate) _tracked.TryGetValue(session.Id, out tracked);
        if (tracked is null) return true;
        await tracked.WriteGate.WaitAsync().ConfigureAwait(false);
        RecoveryRecord? record = null;
        try
        {
            record = await OnContextAsync(() => tracked.Active ? CaptureRecord(session, tracked.IsExternalDraft?.Invoke() == true && session.FilePath is null,
                tracked.SourceUrl?.Invoke(), tracked.RecoveredFromPath?.Invoke()) : null).ConfigureAwait(false);
            if (record is null)
            {
                return true;
            }

            await _store.WriteCheckpointAsync(record).ConfigureAwait(false);

            // A save may have completed while the checkpoint was being written.
            await OnContextAsync(() =>
            {
                if (!tracked.Active || !session.IsDirty) _store.Retire(session.Id);
                else CheckpointWritten?.Invoke(this, record);
                return true;
            }).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await OnContextAsync(() =>
            {
                CheckpointFailed?.Invoke(this, ex);
                CheckpointWriteFailed?.Invoke(this, new RecoveryCheckpointFailure(session.Id, record?.Revision ?? session.Revision, ex));
                return true;
            }).ConfigureAwait(false);
            return false;
        }
        finally
        {
            tracked.WriteGate.Release();
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

    private sealed record Tracked(DocumentSession Session, Timer Timer, Func<bool>? IsExternalDraft, Func<string?>? SourceUrl, Func<string?>? RecoveredFromPath)
    {
        public SemaphoreSlim WriteGate { get; } = new(1, 1);
        public volatile bool Active = true;
    }
}
