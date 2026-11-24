using Tern.App.Shell;
using Tern.Search;

namespace Tern.App.Tests;

public sealed class WorkspacePreparationTests
{
    [Fact]
    public async Task Shell_publishes_the_tree_before_content_loading_and_cancellation_keeps_it()
    {
        using var f = new ShellFixture();
        await f.WriteAsync("seed.md", "# Seed zebra");
        WorkspaceSession? published = null;
        f.Shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ShellViewModel.Workspace) || f.Shell.Workspace is not { IsPreparingContent: true } workspace) return;
            published = workspace;
            Assert.Single(workspace.Snapshot.Files);
            Assert.Empty(workspace.Search("zebra").Hits);
            workspace.CancelContentPreparation();
        };

        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));

        Assert.Same(published, f.Shell.Workspace);
        Assert.False(published!.IsPreparingContent);
        Assert.False(published.IsContentReady);
        Assert.Single(published.QuickOpen("seed").Files);
        Assert.Contains("incomplete", published.ContentStatus);
        Assert.Contains(f.Shell.Banner!.Actions, action => action.Label == "Rescan");
        Assert.DoesNotContain(f.Shell.Activities.Items, item => item.State == ActivityState.Active);
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        published.FilesChanged += (_, _) => refreshed.TrySetResult();
        published.RequestRescan();
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(published.IsContentReady);
        Assert.Single(published.Search("zebra").Hits);
    }

    [Fact]
    public async Task Real_indexing_reports_actual_units_and_can_stop_between_batches_without_losing_tree()
    {
        using var f = new ShellFixture();
        for (var i = 0; i < 420; i++) await f.WriteAsync($"note-{i:D3}.md", $"# Note {i}\nzebra");
        WorkspaceSession? published = null;
        var reports = new List<IndexProgress>();
        var progress = new CallbackProgress<IndexProgress>(p =>
        {
            reports.Add(p);
            if (p.Processed >= 200) published!.CancelContentPreparation();
        });
        using var session = await WorkspaceSession.OpenAsync(f.Docs, f.Root.File("index"), progress: progress,
            treeReady: ready =>
            {
                published = ready;
                Assert.Equal(420, ready.Snapshot.Files.Count);
                Assert.Empty(ready.Search("zebra").Hits);
            });

        Assert.False(session.IsPreparingContent);
        Assert.False(session.IsContentReady);
        Assert.Contains(reports, p => p.Processed == 200 && p.Total == 420);
        Assert.All(reports, p => Assert.InRange(p.Processed, 0, p.Total));
        Assert.Equal(420, session.Snapshot.Files.Count);
        Assert.NotEmpty(session.Search("zebra").Hits);
        Assert.Contains("incomplete", session.ContentStatus);
    }

    [Fact]
    public async Task Successful_content_loading_finishes_ready_and_a_failed_load_keeps_tree_with_problem()
    {
        using var f = new ShellFixture();
        await f.WriteAsync("seed.md", "# Seed zebra");
        using (var ready = await WorkspaceSession.OpenAsync(f.Docs, f.Root.File("index-ok"), treeReady: _ => { }))
        {
            Assert.True(ready.IsContentReady);
            Assert.False(ready.IsPreparingContent);
            Assert.Equal("Search ready", ready.ContentStatus);
            Assert.Single(ready.Search("zebra").Hits);
        }

        var brokenProgress = new CallbackProgress<IndexProgress>(_ => throw new IOException("synthetic progress failure"));
        using var failed = await WorkspaceSession.OpenAsync(f.Docs, f.Root.File("index-fail"), progress: brokenProgress, treeReady: _ => { });
        Assert.Single(failed.Snapshot.Files);
        Assert.False(failed.IsContentReady);
        Assert.False(failed.IsPreparingContent);
        Assert.Contains("synthetic progress failure", failed.UpdatesPausedReason);
        Assert.Contains("incomplete", failed.ContentStatus);
    }

    [Fact]
    public async Task Cancelling_before_scan_without_a_published_tree_preserves_the_original_cancellation_contract()
    {
        using var f = new ShellFixture();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WorkspaceSession.OpenAsync(f.Docs, f.Root.File("index"), cancellationToken: cancelled.Token));
    }

    private sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
