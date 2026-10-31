using Tern.Workspaces;

namespace Tern.Workspaces.Tests;

public sealed class WorkspaceDebounceTests
{
    private static TimeSpan Ms(int value) => TimeSpan.FromMilliseconds(value);

    [Fact]
    public void A_queued_callback_from_an_older_deadline_waits_for_the_latest_event()
    {
        var debounce = new WorkspaceDebounce(Ms(400), Ms(2000));
        debounce.Record(Ms(0));
        debounce.Record(Ms(300));
        Assert.Equal(Ms(300), debounce.Remaining(Ms(400)));
        Assert.Equal(Ms(1), debounce.Remaining(Ms(699)));
        Assert.Equal(TimeSpan.Zero, debounce.Remaining(Ms(700)));
    }

    [Fact]
    public void Continuous_events_still_flush_at_the_maximum_latency()
    {
        var debounce = new WorkspaceDebounce(Ms(400), Ms(2000));
        for (var time = 0; time <= 1800; time += 300) debounce.Record(Ms(time));
        Assert.Equal(Ms(100), debounce.Remaining(Ms(1900)));
        Assert.Equal(TimeSpan.Zero, debounce.Remaining(Ms(2000)));
    }

    [Fact]
    public void A_callback_from_the_previous_batch_cannot_flush_a_new_batch_early()
    {
        var debounce = new WorkspaceDebounce(Ms(400), Ms(2000));
        debounce.Record(Ms(0));
        Assert.Equal(TimeSpan.Zero, debounce.Remaining(Ms(400)));
        debounce.Reset();
        Assert.Equal(Timeout.InfiniteTimeSpan, debounce.Remaining(Ms(500)));
        debounce.Record(Ms(600));
        Assert.Equal(Ms(350), debounce.Remaining(Ms(650)));
        Assert.Equal(TimeSpan.Zero, debounce.Remaining(Ms(1000)));
    }
}
