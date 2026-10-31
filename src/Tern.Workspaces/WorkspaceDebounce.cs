namespace Tern.Workspaces;

/// <summary>Tracks the quiet deadline and maximum batch latency using elapsed, monotonic time.</summary>
internal sealed class WorkspaceDebounce(TimeSpan quietPeriod, TimeSpan maxLatency)
{
    private TimeSpan? _firstEvent;
    private TimeSpan _lastEvent;

    public void Record(TimeSpan now)
    {
        _firstEvent ??= now;
        _lastEvent = now;
    }

    public TimeSpan Remaining(TimeSpan now)
    {
        if (_firstEvent is not { } first) return Timeout.InfiniteTimeSpan;
        var quietRemaining = _lastEvent + quietPeriod - now;
        var maximumRemaining = first + maxLatency - now;
        var remaining = quietRemaining < maximumRemaining ? quietRemaining : maximumRemaining;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    public void Reset() => _firstEvent = null;
}
