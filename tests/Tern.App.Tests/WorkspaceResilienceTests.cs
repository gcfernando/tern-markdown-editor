using Microsoft.Data.Sqlite;
using Tern.App.Shell;

namespace Tern.App.Tests;

public sealed class WorkspaceResilienceTests
{
    [Fact]
    public async Task Watcher_removes_an_oversized_document_and_restores_it_after_it_shrinks()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("size.md", "# Initial searchablezebra");
        using var workspace = await WorkspaceSession.OpenAsync(f.Docs, f.Root.File("index"));
        Assert.Single(workspace.Search("searchablezebra").Hits);
        var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        workspace.FilesChanged += (_, _) =>
        {
            if (!workspace.Snapshot.Files.Any(file => file.FullPath == path)) gone.TrySetResult();
            else if (workspace.Search("returnedzebra").Hits.Count > 0) returned.TrySetResult();
        };
        using (var stream = File.OpenWrite(path)) stream.SetLength(Tern.Filesystem.DocumentFileService.MaximumEagerLoadBytes + 1L);
        await gone.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Empty(workspace.Search("searchablezebra").Hits);
        Assert.Empty(workspace.Graph.GetOutgoing(path));

        await File.WriteAllTextAsync(path, "# Returned returnedzebra");
        await returned.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Contains(workspace.Snapshot.Files, file => file.FullPath == path);
        Assert.Single(workspace.Search("returnedzebra").Hits);
        Assert.Empty(workspace.Search("searchablezebra").Hits);
    }

    [Theory]
    [InlineData(13)]
    [InlineData(5)]
    public async Task Full_or_busy_background_index_is_reported_then_rescan_recovers_the_missed_file(int sqliteCode)
    {
        using var f = new ShellFixture();
        await f.WriteAsync("seed.md", "# Seed");
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));
        var workspace = f.Shell.Workspace!;
        var problem = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        workspace.ProblemReported += (_, _) => problem.TrySetResult();
        workspace.ChangeProcessingHook = _ => throw new SqliteException("injected index failure", sqliteCode);
        await f.WriteAsync("missed.md", "# Recoverable zebra");
        await problem.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotNull(workspace.UpdatesPausedReason);
        Assert.Contains(f.Shell.Activities.Items, item => item.Severity == ActivitySeverity.Error);
        Assert.NotNull(f.Shell.Banner);

        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        workspace.FilesChanged += (_, _) => recovered.TrySetResult();
        workspace.ChangeProcessingHook = null;
        workspace.RequestRescan();
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Null(workspace.UpdatesPausedReason);
        Assert.Single(workspace.Search("zebra").Hits);
        Assert.Equal("# Recoverable zebra", await File.ReadAllTextAsync(Path.Combine(f.Docs, "missed.md")));
    }

    [Fact]
    public async Task Update_failure_pauses_with_a_reason_then_the_next_change_recovers_and_finds_missed_files()
    {
        using var f = new ShellFixture();
        await f.WriteAsync("seed.md", "# Seed");
        using var workspace = await WorkspaceSession.OpenAsync(f.Docs, f.Root.File("index"));
        var attempts = 0;
        workspace.ChangeProcessingHook = _ => ++attempts == 1 ? throw new InvalidDataException("index is damaged") : Task.CompletedTask;
        var problem = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        workspace.ProblemReported += (_, reason) => problem.TrySetResult(reason);

        await f.WriteAsync("missed.md", "# Missed zebra");
        Assert.Contains("index is damaged", await problem.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.NotNull(workspace.UpdatesPausedReason);
        Assert.Contains(workspace.Notices, n => n.Contains("Updates paused"));

        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        workspace.FilesChanged += (_, _) => recovered.TrySetResult();
        await f.WriteAsync("later.md", "# Later");
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Null(workspace.UpdatesPausedReason);
        Assert.Contains(workspace.Snapshot.Files, x => x.FullPath.EndsWith("missed.md", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(workspace.Snapshot.Files, x => x.FullPath.EndsWith("later.md", StringComparison.OrdinalIgnoreCase));
        Assert.Single(workspace.Search("zebra").Hits);
    }

    [Fact]
    public async Task Request_rescan_resumes_after_a_pause()
    {
        using var f = new ShellFixture();
        await f.WriteAsync("seed.md", "# Seed");
        using var workspace = await WorkspaceSession.OpenAsync(f.Docs, f.Root.File("index"));
        var fail = true;
        workspace.ChangeProcessingHook = _ => fail ? throw new IOException("locked") : Task.CompletedTask;
        var problem = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        workspace.ProblemReported += (_, _) => problem.TrySetResult();
        await f.WriteAsync("one.md", "# One");
        await problem.Task.WaitAsync(TimeSpan.FromSeconds(15));

        fail = false;
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        workspace.FilesChanged += (_, _) => recovered.TrySetResult();
        workspace.RequestRescan();
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Null(workspace.UpdatesPausedReason);
        Assert.Contains(workspace.Snapshot.Files, x => x.FullPath.EndsWith("one.md", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(11, "Press Rebuild")]
    [InlineData(26, "Press Rebuild")]
    [InlineData(13, "drive is full")]
    [InlineData(5, "busy")]
    [InlineData(6, "busy")]
    [InlineData(10, "Press Rescan")]
    public void Index_problem_text_matches_the_real_cause(int code, string expected)
    {
        var text = WorkspaceSession.ProblemText(new SqliteException("raw database wording", code));
        Assert.Contains(expected, text);
        Assert.DoesNotContain("database", text, StringComparison.OrdinalIgnoreCase);
        if (code is not (11 or 26)) Assert.DoesNotContain("damaged", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Index_failure_shows_an_error_banner_with_rescan_and_rebuild_actions()
    {
        using var f = new ShellFixture();
        await f.WriteAsync("seed.md", "# Seed");
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));
        var workspace = f.Shell.Workspace!;
        var banner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Shell.BannerChanged += (_, _) => { if (f.Shell.Banner is not null) banner.TrySetResult(); };
        workspace.ChangeProcessingHook = _ => throw new SqliteException("database disk image is malformed", 11);

        await f.WriteAsync("trigger.md", "# Trigger");
        await banner.Task.WaitAsync(TimeSpan.FromSeconds(15));

        var shown = f.Shell.Banner!;
        Assert.Equal(StatusSeverity.Error, shown.Severity);
        Assert.DoesNotContain("malformed", shown.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("database", shown.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SQLite", shown.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Press Rebuild", shown.Text);
        Assert.Contains(f.Shell.Activities.Items, a => a.Severity == ActivitySeverity.Error && (a.Phase + a.Details).Contains("malformed"));
        var rescanned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        workspace.ChangeProcessingHook = _ => { rescanned.TrySetResult(); return Task.CompletedTask; };
        Assert.Single(shown.Actions, a => a.Label == "Rescan").Invoke();
        await rescanned.Task.WaitAsync(TimeSpan.FromSeconds(15));
        workspace.ChangeProcessingHook = null;
        var rebuild = Assert.Single(shown.Actions, a => a.Label == "Rebuild");
        rebuild.Invoke();
        for (var i = 0; i < 1000 && f.Shell.Banner is not null; i++) await Task.Delay(10);
        Assert.Null(f.Shell.Banner);
        Assert.Contains("search index was rebuilt", f.Shell.StatusMessage);
    }

    [Fact]
    public async Task Notices_tolerate_concurrent_background_writers_and_readers()
    {
        var log = new NoticeLog();
        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(() => { for (var i = 0; i < 2000; i++) log.Add($"{w}:{i}"); }));
        var reader = Task.Run(() => { for (var i = 0; i < 500; i++) _ = string.Join(" ", log).Length; });
        await Task.WhenAll(writers.Append(reader));
        Assert.Equal(8000, log.Count);
    }
}
