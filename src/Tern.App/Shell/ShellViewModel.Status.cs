namespace Tern.App.Shell;

public enum StatusSeverity { Info, Success, Warning, Error }

public interface IUiDispatcher
{
    bool CheckAccess();
    void Post(Action action);
    Task YieldAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => completion.SetResult());
        return completion.Task;
    }
}

public sealed partial class ShellViewModel
{
    public ActivityFeed Activities { get; } = new();
    /// <summary>
    /// UI-thread dispatcher supplied by the host. When set, <see cref="SetStatus"/> called from any other thread is
    /// marshalled through it. Without a host dispatcher (tests, headless use) the message is applied on the calling thread.
    /// </summary>
    public IUiDispatcher? UiDispatcher { get; set; }
    private StatusSeverity? _explicitStatusSeverity;
    private int _statusGeneration;
    public StatusSeverity StatusSeverity { get; private set; }
    /// <summary>Set by the view while the pointer or keyboard focus is on the status area so the message is not cleared.</summary>
    public bool IsStatusHeld { get; set; }
    private int _activeStatusOperations;
    public bool HasActiveStatusOperations => Volatile.Read(ref _activeStatusOperations) > 0;

    /// <summary>Keeps the status active across asynchronous work, including nested remote operations.</summary>
    public StatusOperation BeginStatusOperation(string kind = "Working", string? identity = null, Action? cancel = null)
    {
        if (Interlocked.Increment(ref _activeStatusOperations) == 1)
            OnPropertyChanged(nameof(HasActiveStatusOperations));
        return new StatusOperation(this, Activities.Begin(kind, identity, cancel));
    }

    public sealed class StatusOperation(ShellViewModel owner, ActivityScope activity) : IDisposable
    {
        public Guid Id => activity.Id;
        public void Report(string phase, long? completed = null, long? total = null, string? unitLabel = null) => activity.Report(phase, completed, total, unitLabel);
        public void WaitForUser(string phase) => activity.WaitForUser(phase);
        public void Fail(string details) => activity.Fail(details);
        public void Cancel() => activity.Cancel();
        public void Complete(string? details = null, bool warning = false, string? outcome = null) => activity.Complete(details, warning, outcome);
        private ShellViewModel? _owner = owner;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is { } shell)
            {
                activity.Dispose();
                if (Interlocked.Decrement(ref shell._activeStatusOperations) == 0)
                    shell.OnPropertyChanged(nameof(HasActiveStatusOperations));
            }
        }
    }

    private async Task<bool> TrackActivityAsync(string kind, string? identity, Func<ActivityScope, Task<bool>> work)
    {
        using var activity = Activities.Begin(kind, identity);
        try
        {
            var result = await work(activity).ConfigureAwait(true);
            if (result) activity.Complete();
            else activity.Cancel();
            return result;
        }
        catch (OperationCanceledException) { activity.Cancel("Operation stopped; review any completed changes before retrying."); throw; }
        catch (Exception ex)
        {
            var phase = Activities.Items.FirstOrDefault(item => item.Id == activity.Id)?.Phase;
            activity.Fail((phase is null ? "" : phase + ". ") + ex.Message);
            throw;
        }
    }

    /// <summary>A plain reason for the visible message. The system's own wording is kept in Activity for troubleshooting.</summary>
    public string PlainProblem(Exception exception)
    {
        var plain = UserProblem.Plain(exception);
        var raw = (exception is AggregateException aggregate ? aggregate.GetBaseException() : exception).Message.Trim();
        if (raw.Length > 0 && !plain.Contains(raw.TrimEnd('.'), StringComparison.Ordinal))
            Activities.Post("Technical details for the last problem", ActivitySeverity.Neutral, details: raw);
        return plain;
    }

    partial void OnStatusMessageChanged(string value)
    {
        var severity = _explicitStatusSeverity ?? ClassifyLegacyStatus(value);
        PublishStatus(value, severity);
    }

    /// <summary>
    /// Shows a status message with an explicit severity (SB-01). Info and Success messages stay for at least
    /// 6 s plus 60 ms per character; Warning and Error stay until replaced. Every message is mirrored into Activity.
    /// Safe to call from any thread.
    /// </summary>
    public void SetStatus(string text, StatusSeverity severity)
    {
        if (UiDispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.Post(() => { if (!_disposed) ApplyStatus(text, severity); });
            return;
        }
        ApplyStatus(text, severity);
    }

    private void ApplyStatus(string text, StatusSeverity severity)
    {
        _explicitStatusSeverity = severity;
        try
        {
            if (StatusMessage == text) PublishStatus(text, severity);
            else StatusMessage = text;
        }
        finally { _explicitStatusSeverity = null; }
    }

    private void PublishStatus(string value, StatusSeverity severity)
    {
        var generation = ++_statusGeneration;
        if (StatusSeverity != severity)
        {
            StatusSeverity = severity;
            OnPropertyChanged(nameof(StatusSeverity));
        }
        if (string.IsNullOrWhiteSpace(value) || value == "Ready") return;
        WriteDiagnostic(null, severity);
        Activities.Post(value, severity switch
        {
            StatusSeverity.Error => ActivitySeverity.Error,
            StatusSeverity.Warning => ActivitySeverity.Warning,
            StatusSeverity.Success => ActivitySeverity.Success,
            _ => ActivitySeverity.Neutral,
        });
        if (severity is StatusSeverity.Warning or StatusSeverity.Error) return;
        _ = ReturnToReadyAsync(generation, MinimumDisplayTime(value));
    }

    internal static TimeSpan MinimumDisplayTime(string text) => TimeSpan.FromMilliseconds(6000 + 60 * (text?.Length ?? 0));

    // Compatibility path for code that still assigns StatusMessage directly: classify once, centrally.
    // New code must call SetStatus with an explicit severity.
    private static StatusSeverity ClassifyLegacyStatus(string value)
    {
        if (!IsActionableStatus(value)) return StatusSeverity.Info;
        return new[] { "could not", "cannot", "failed", "error", "denied", "unavailable" }
            .Any(word => value.Contains(word, StringComparison.OrdinalIgnoreCase)) ? StatusSeverity.Error : StatusSeverity.Warning;
    }

    private static bool IsActionableStatus(string value) => new[]
    {
        "could not", "cannot", "failed", "unavailable", "error", "denied", "not found",
        "changed while", "changed during", "try again", "review", "missing", "exceeds",
    }.Any(word => value.Contains(word, StringComparison.OrdinalIgnoreCase));

    private async Task ReturnToReadyAsync(int generation, TimeSpan minimum)
    {
        try { await Task.Delay(minimum).ConfigureAwait(true); }
        catch (OperationCanceledException) { return; }
        while (!_disposed && generation == _statusGeneration)
        {
            if (IsStatusHeld || IsBusy || IsIndexing || Volatile.Read(ref _activeStatusOperations) > 0 || StatusMessage.EndsWith('…'))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(true);
                continue;
            }
            StatusMessage = "Ready";
            return;
        }
    }
}
