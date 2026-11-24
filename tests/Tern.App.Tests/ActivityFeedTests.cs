using Tern.App.Shell;
using Tern.Domain.Text;

namespace Tern.App.Tests;

public sealed class ActivityFeedTests
{
    [Fact]
    public async Task Save_progress_finishes_only_after_actual_post_write_index_work_and_never_offers_fake_cancel()
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("progress.md", "before\n");
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(fixture.Docs));
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "after ")]);
        var enteredIndex = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseIndex = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Shell.BeforeWorkspaceSavedNotification = async () => { enteredIndex.TrySetResult(); await releaseIndex.Task; };
        var saving = fixture.Shell.SaveTabAsync(tab);
        try
        {
            await enteredIndex.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("after before\n", await File.ReadAllTextAsync(path));
            Assert.False(saving.IsCompleted);
            var item = Assert.Single(fixture.Shell.Activities.Items, item => item.Kind == "Saving document");
            Assert.Equal(ActivityState.Active, item.State);
            Assert.Equal("Updating the workspace search index", item.Phase);
            Assert.True(item.IsIndeterminate);
            Assert.Null(item.Fraction);
            Assert.Null(item.Cancel);
            Assert.Null(item.Finished);
        }
        finally { releaseIndex.TrySetResult(); await saving; }
        Assert.True(await saving);
        var completed = Assert.Single(fixture.Shell.Activities.Items, item => item.Kind == "Saving document");
        Assert.Equal("Saved", completed.Phase);
        Assert.Equal(ActivityState.Completed, completed.State);
        Assert.NotNull(completed.Finished);
    }

    [Fact]
    public async Task Save_As_waiting_for_real_overwrite_confirmation_has_no_spinner_and_cancel_preserves_both_versions()
    {
        using var fixture = new ShellFixture();
        var destination = await fixture.WriteAsync("destination.md", "original bytes\n");
        var tab = fixture.Shell.NewDocument("new unsaved text");
        fixture.Dialogs.SavePath = destination;
        var prompted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Dialogs.OverwriteResponse = () => { prompted.TrySetResult(); return answer.Task; };
        var saving = fixture.Shell.SaveTabAsAsync(tab);
        try
        {
            await prompted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var item = Assert.Single(fixture.Shell.Activities.Items, item => item.Kind == "Save As");
            Assert.Equal(ActivityState.WaitingForUser, item.State);
            Assert.False(item.IsIndeterminate);
            Assert.False(item.CanCancel);
            Assert.Null(item.Cancel);
        }
        finally { answer.TrySetResult(false); await saving; }
        Assert.False(await saving);
        Assert.Equal("original bytes\n", await File.ReadAllTextAsync(destination));
        Assert.Equal("new unsaved text", tab.CurrentText);
        Assert.True(tab.IsUntitled);
        Assert.Equal(ActivityState.Canceled, Assert.Single(fixture.Shell.Activities.Items, item => item.Kind == "Save As").State);
    }

    [Fact]
    public async Task Real_read_only_destination_failure_ends_progress_without_claiming_saved()
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("locked.md", "original\n");
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "unsaved ")]);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        try
        {
            Assert.False(await fixture.Shell.SaveTabAsync(tab));
            var item = Assert.Single(fixture.Shell.Activities.Items, item => item.Kind == "Saving document");
            Assert.Equal(ActivityState.Failed, item.State);
            Assert.False(item.IsActive);
            Assert.NotNull(item.Finished);
            Assert.Equal("original\n", await File.ReadAllTextAsync(path));
            Assert.True(tab.IsDirty);
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    [Fact]
    public void Queued_and_decision_states_do_not_invent_progress_and_terminal_states_reject_late_updates()
    {
        var feed = new ActivityFeed();
        using var operation = feed.Begin("Import", cancel: () => { }, queued: true);
        Assert.Equal(ActivityState.Queued, Assert.Single(feed.Items).State);
        Assert.True(feed.Items[0].CanCancel);
        Assert.False(feed.Items[0].IsIndeterminate);
        operation.Report("Reading", 2, 4, "files");
        Assert.Equal(.5, feed.Items[0].Fraction);
        Assert.Equal("files", feed.Items[0].UnitLabel);
        operation.WaitForUser("Review the proposed changes");
        Assert.True(feed.Items[0].IsActive);
        Assert.False(feed.Items[0].CanCancel);
        Assert.False(feed.Items[0].IsIndeterminate);
        feed.Dismiss(operation.Id);
        Assert.Single(feed.Items);
        operation.Report("Applying approved changes");
        Assert.True(feed.Items[0].IsIndeterminate);
        operation.Cancel("Canceled; no changes were applied.");
        var canceled = feed.Items[0];
        Assert.NotNull(canceled.Finished);
        Assert.False(canceled.IsActive);
        operation.Complete();
        operation.Report("Stale callback", 4, 4);
        Assert.Equal(canceled, feed.Items[0]);
    }

    [Fact]
    public void Identical_reports_are_coalesced_but_distinct_outcomes_and_warnings_are_retained()
    {
        var feed = new ActivityFeed();
        var changes = 0;
        feed.Changed += (_, _) => changes++;
        using var operation = feed.Begin("Save");
        operation.Report("Writing", 1, 2);
        for (var index = 0; index < 1000; index++) operation.Report("Writing", 1, 2);
        Assert.Equal(2, changes);
        operation.Complete("File saved; index update needs retry.", warning: true, outcome: "Saved with warning");
        var saved = Assert.Single(feed.Items);
        Assert.Equal(ActivityState.Completed, saved.State);
        Assert.Equal(ActivitySeverity.Warning, saved.Severity);
        Assert.Equal("Saved with warning", saved.Phase);
        Assert.NotNull(saved.Finished);
    }

    [Fact]
    public void Repeated_errors_keep_only_the_latest_recovery_action_and_banner_actions_reach_activity()
    {
        using var f = new ShellFixture();
        var old = 0;
        var current = 0;
        f.Shell.ShowBanner("Failed", StatusSeverity.Error, new BannerAction("Retry", () => old++));
        f.Shell.ShowBanner("Failed", StatusSeverity.Error, new BannerAction("Retry", () => current++));
        var item = Assert.Single(f.Shell.Activities.Items);
        Assert.Equal(ActivityState.Failed, item.State);
        Assert.Single(item.Actions).Invoke();
        Assert.Equal(0, old);
        Assert.Equal(1, current);
        f.Shell.DismissBanner();
        Assert.Single(f.Shell.Activities.Items).Actions[0].Invoke();
        Assert.Equal(2, current);
    }

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
