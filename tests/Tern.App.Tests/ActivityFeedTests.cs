using Tern.App.Shell;

namespace Tern.App.Tests;

public sealed class ActivityFeedTests
{
    [Fact]
    public void Failure_history_is_bounded_without_evicting_active_work_or_clearing_recent_failures()
    {
        var feed = new ActivityFeed(); using var active = feed.Begin("Long scan");
        for (var i = 0; i < 150; i++) feed.Begin("Import", i.ToString()).Fail("Known failure " + i);
        Assert.Equal(100, feed.Items.Count(i => i.State == ActivityState.Failed));
        Assert.Contains(feed.Items, i => i.Id == active.Id && i.State == ActivityState.Active);
        for (var i = 0; i < 100; i++) feed.Begin("Save").Dispose();
        Assert.Equal(100, feed.Items.Count(i => i.State == ActivityState.Failed));
        Assert.Contains(feed.Items, i => i.Details == "Known failure 149");
        Assert.Equal(151, feed.Items.Count);
    }

    [Fact]
    public void Completing_one_operation_cannot_clear_another_failure_or_accept_late_reports()
    {
        var feed = new ActivityFeed();
        using var first = feed.Begin("Save", "document-a");
        using var second = feed.Begin("Index", "workspace-b");
        first.Fail("Permission denied");
        second.Complete();
        first.Report("Late completion", 10, 10);
        Assert.Equal(ActivityState.Failed, feed.Items.Single(i => i.Id == first.Id).State);
        Assert.Equal(ActivityState.Completed, feed.Items.Single(i => i.Id == second.Id).State);
        feed.Dismiss(first.Id);
        Assert.DoesNotContain(feed.Items, i => i.Id == first.Id);
    }

    [Fact]
    public void Only_measured_valid_totals_produce_progress_and_routine_history_is_bounded()
    {
        var feed = new ActivityFeed();
        using var operation = feed.Begin("Scan");
        operation.Report("Scanning", 4, null);
        Assert.Null(feed.Items.Single().Fraction);
        operation.Report("Scanning", 4, 3);
        Assert.Null(feed.Items.Single().Fraction);
        operation.Report("Scanning", 2, 4);
        Assert.Equal(.5, feed.Items.Single().Fraction);
        for (var i = 0; i < 100; i++) feed.Begin("Instant operation").Dispose();
        Assert.Equal(50, feed.Items.Count(i => i.State == ActivityState.Completed));
        Assert.Contains(feed.Items, i => i.Id == operation.Id && i.Cancel is null);
    }
}
