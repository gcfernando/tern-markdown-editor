namespace Tern.App.Shell;

public enum ActivityState { Active, Completed, Canceled, Failed }
public enum ActivitySeverity { Neutral, Success, Warning, Error }

/// <summary>Local session feedback. Contains short operational details, never document bodies or provider payloads.</summary>
public sealed record ActivityItem(Guid Id, string Kind, string? Identity, string Phase,
    ActivityState State, ActivitySeverity Severity, DateTimeOffset Started,
    long? Completed = null, long? Total = null, string? Details = null, Action? Cancel = null)
{
    public double? Fraction => Completed is >= 0 && Total is > 0 && Completed <= Total ? (double)Completed.Value / Total.Value : null;
}

public sealed class ActivityFeed
{
    private readonly Lock _gate = new();
    private readonly List<ActivityItem> _items = [];
    public event EventHandler? Changed;
    public IReadOnlyList<ActivityItem> Items { get { lock (_gate) return _items.ToArray(); } }

    public ActivityScope Begin(string kind, string? identity = null, Action? cancel = null)
    {
        var item = new ActivityItem(Guid.NewGuid(), kind, identity, kind, ActivityState.Active,
            ActivitySeverity.Neutral, DateTimeOffset.UtcNow, Cancel: cancel);
        lock (_gate) _items.Add(item);
        Changed?.Invoke(this, EventArgs.Empty);
        return new ActivityScope(this, item.Id);
    }

    internal void Update(Guid id, Func<ActivityItem, ActivityItem> update)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(i => i.Id == id);
            if (index < 0 || _items[index].State != ActivityState.Active) return;
            _items[index] = update(_items[index]);
            // Successful routine history and failures have separate bounds; routine work cannot evict failures.
            while (_items.Count(i => i.State is ActivityState.Completed or ActivityState.Canceled) > 50)
                _items.RemoveAt(_items.FindIndex(i => i.State is ActivityState.Completed or ActivityState.Canceled));
            // Bound session failure history separately. Document save/recovery failures also
            // remain on their owning tabs until resolved, independent of notification eviction.
            while (_items.Count(i => i.State == ActivityState.Failed) > 100)
                _items.RemoveAt(_items.FindIndex(i => i.State == ActivityState.Failed));
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dismiss(Guid id)
    {
        lock (_gate) _items.RemoveAll(i => i.Id == id && i.State != ActivityState.Active);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class ActivityScope(ActivityFeed feed, Guid id) : IDisposable
{
    public Guid Id { get; } = id;
    public void Report(string phase, long? completed = null, long? total = null) =>
        feed.Update(Id, i => i with { Phase = phase, Completed = completed, Total = total });
    public void Fail(string details) => feed.Update(Id, i => i with
        { State = ActivityState.Failed, Severity = ActivitySeverity.Error, Phase = i.Kind + " failed", Details = details, Cancel = null });
    public void Cancel() => feed.Update(Id, i => i with
        { State = ActivityState.Canceled, Phase = i.Kind + " canceled", Cancel = null });
    public void Complete(string? details = null, bool warning = false) => feed.Update(Id, i => i with
        { State = ActivityState.Completed, Phase = i.Kind + (warning ? " completed with notes" : " completed"),
          Severity = warning ? ActivitySeverity.Warning : ActivitySeverity.Success, Details = details, Cancel = null });
    public void Dispose() => Complete();
}
