namespace Tern.App.Shell;

public enum ActivityState { Active, Completed, Canceled, Failed, Queued, WaitingForUser }
public enum ActivitySeverity { Neutral, Success, Warning, Error }

/// <summary>Local session feedback. Contains short operational details, never document bodies or provider payloads.</summary>
public sealed record ActivityItem(Guid Id, string Kind, string? Identity, string Phase,
    ActivityState State, ActivitySeverity Severity, DateTimeOffset Started,
    long? Completed = null, long? Total = null, string? Details = null, Action? Cancel = null)
{
    public IReadOnlyList<BannerAction> Actions { get; init; } = [];
    public string? UnitLabel { get; init; }
    public DateTimeOffset? Finished { get; init; }
    public DateTimeOffset? RunningSince { get; init; }
    public bool IsActive => State is ActivityState.Active or ActivityState.Queued or ActivityState.WaitingForUser;
    public bool IsIndeterminate => State == ActivityState.Active && Fraction is null;
    public bool CanCancel => State is ActivityState.Active or ActivityState.Queued && Cancel is not null;
    public double? Fraction => Completed is >= 0 && Total is > 0 && Completed <= Total ? (double)Completed.Value / Total.Value : null;
}

public sealed class ActivityFeed
{
    private readonly Lock _gate = new();
    private readonly List<ActivityItem> _items = [];
    public event EventHandler? Changed;
    public IReadOnlyList<ActivityItem> Items { get { lock (_gate) return _items.ToArray(); } }

    public ActivityScope Begin(string kind, string? identity = null, Action? cancel = null, bool queued = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        kind = DisplayText(kind, 160);
        var item = new ActivityItem(Guid.NewGuid(), kind, identity is null ? null : DisplayText(identity, 512),
            queued ? "Queued: " + kind : kind, queued ? ActivityState.Queued : ActivityState.Active,
            ActivitySeverity.Neutral, DateTimeOffset.UtcNow, Cancel: cancel);
        lock (_gate) _items.Add(item);
        Changed?.Invoke(this, EventArgs.Empty);
        return new ActivityScope(this, item.Id);
    }

    /// <summary>Records a status-bar message so every user-visible outcome stays recoverable from Activity.</summary>
    public void Post(string text, ActivitySeverity severity, IReadOnlyList<BannerAction>? actions = null, string? details = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_gate)
        {
            // Coalesce an immediate repeat so a repeating background message does not flood the log.
            if (_items.Count > 0 && _items[^1] is { Kind: "Status" } last && last.Phase == text && last.Severity == severity)
            {
                var nextActions = actions?.ToArray() ?? [];
                if (last.Actions.SequenceEqual(nextActions) && last.Details == details) return;
                _items[^1] = last with { Actions = nextActions, Details = details };
            }
            else _items.Add(new ActivityItem(Guid.NewGuid(), "Status", null, text,
                severity == ActivitySeverity.Error ? ActivityState.Failed : ActivityState.Completed,
                severity, DateTimeOffset.UtcNow, Details: details)
                { Actions = actions?.ToArray() ?? [], Finished = DateTimeOffset.UtcNow });
            Trim();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void Update(Guid id, Func<ActivityItem, ActivityItem> update)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(i => i.Id == id);
            if (index < 0 || !_items[index].IsActive) return;
            var next = update(_items[index]);
            if (next == _items[index]) return;
            _items[index] = next;
            Trim();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Trim()
    {
        // Successful routine history and failures have separate bounds; routine work cannot evict failures.
        while (_items.Count(i => i.State is ActivityState.Completed or ActivityState.Canceled) > 50)
            _items.RemoveAt(_items.FindIndex(i => i.State is ActivityState.Completed or ActivityState.Canceled));
        // Bound session failure history separately. Document save/recovery failures also
        // remain on their owning tabs until resolved, independent of notification eviction.
        while (_items.Count(i => i.State == ActivityState.Failed) > 100)
            _items.RemoveAt(_items.FindIndex(i => i.State == ActivityState.Failed));
    }

    public void Dismiss(Guid id)
    {
        lock (_gate) _items.RemoveAll(i => i.Id == id && !i.IsActive);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal static string DisplayText(string text, int limit) =>
        string.Concat(text.Take(limit).Select(character => char.IsControl(character) ? ' ' : character));
}

public sealed class ActivityScope(ActivityFeed feed, Guid id) : IDisposable
{
    public Guid Id { get; } = id;
    public void Report(string phase, long? completed = null, long? total = null, string? unitLabel = null) =>
        feed.Update(Id, i => i with { State = ActivityState.Active, Phase = ActivityFeed.DisplayText(phase, 512),
            Completed = completed, Total = total, UnitLabel = unitLabel is null ? null : ActivityFeed.DisplayText(unitLabel, 80),
            RunningSince = i.State == ActivityState.Active ? i.RunningSince : DateTimeOffset.UtcNow });
    public void WaitForUser(string phase) => feed.Update(Id, i => i with
        { State = ActivityState.WaitingForUser, Phase = ActivityFeed.DisplayText(phase, 512), Completed = null, Total = null });
    public void Queue(string phase) => feed.Update(Id, i => i with
        { State = ActivityState.Queued, Phase = ActivityFeed.DisplayText(phase, 512), Completed = null, Total = null });
    public void Fail(string details) => feed.Update(Id, i => i with
        { State = ActivityState.Failed, Severity = ActivitySeverity.Error, Phase = i.Kind + " failed",
            Details = ActivityFeed.DisplayText(details, 4000), Cancel = null, Finished = DateTimeOffset.UtcNow });
    public void Cancel(string? details = null) => feed.Update(Id, i => i with
        { State = ActivityState.Canceled, Phase = i.Kind + " canceled", Details = details is null ? null : ActivityFeed.DisplayText(details, 4000),
            Cancel = null, Finished = DateTimeOffset.UtcNow });
    public void Complete(string? details = null, bool warning = false, string? outcome = null) => feed.Update(Id, i => i with
        { State = ActivityState.Completed, Phase = outcome is null ? i.Kind + (warning ? " completed with notes" : " completed") : ActivityFeed.DisplayText(outcome, 512),
          Severity = warning ? ActivitySeverity.Warning : ActivitySeverity.Success, Details = details is null ? null : ActivityFeed.DisplayText(details, 4000),
          Cancel = null, Finished = DateTimeOffset.UtcNow });
    public void Dispose() => Complete();
}
