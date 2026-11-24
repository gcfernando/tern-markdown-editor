using Tern.App.Shell;
using Tern.Workspaces;

namespace Tern.App.Tests;

public sealed class LiveSessionPersistenceTests
{
    [Fact]
    public async Task Clean_tabs_and_selection_are_restorable_without_a_clean_exit_or_explicit_save_session()
    {
        using var f = new ShellFixture();
        var first = await f.WriteAsync("first.md", "# First");
        var second = await f.WriteAsync("second.md", "# Second");
        var third = await f.WriteAsync("third.md", "# Third");
        var firstTab = (await f.Shell.OpenFileAsync(first))!;
        await f.Shell.OpenFileAsync(second);
        await f.Shell.OpenFileAsync(third);
        f.Shell.SelectTab(firstTab, TabActivationReason.Programmatic);
        var store = new SessionStore(f.Shell.Paths.StateDirectory);
        await Until(() => store.Load().Tabs.Count == 3 && store.Load().ActiveTab == first);
        using var restarted = new ShellViewModel(f.Shell.Paths, new ScriptedDialogs(), new RecordingLauncher());
        await restarted.RestoreSessionAsync();
        Assert.Equal(new[] { first, second, third }, restarted.Tabs.Select(tab => tab.FilePath));
        Assert.Equal(first, restarted.ActiveTab!.FilePath);
        Assert.All(restarted.Tabs, tab => Assert.False(tab.IsDirty));
    }

    [Fact]
    public async Task Closed_tabs_and_folder_sections_are_updated_in_live_session_state()
    {
        using var f = new ShellFixture();
        var first = (await f.Shell.OpenFileAsync(await f.WriteAsync("first.md", "# First")))!;
        var second = (await f.Shell.OpenFileAsync(await f.WriteAsync("second.md", "# Second")))!;
        Assert.True(await f.Shell.OpenFolderAsync(f.Docs));
        var store = new SessionStore(f.Shell.Paths.StateDirectory);
        await Until(() => store.Load().Tabs.Count == 2 && store.Load().OpenFolderRoots.Count == 1);
        Assert.True(await f.Shell.CloseTabAsync(first));
        await Until(() => store.Load().Tabs.Count == 1);
        Assert.Equal(second.FilePath, Assert.Single(store.Load().Tabs).Path);
        Assert.True(await f.Shell.CloseAllWorkspacesAsync());
        await Until(() => store.Load().OpenFolderRoots.Count == 0 && store.Load().Tabs.Count == 0);
    }

    [Fact]
    public async Task Live_saves_do_not_overwrite_a_restore_that_is_still_loading_background_tabs()
    {
        using var f = new ShellFixture();
        var first = await f.WriteAsync("first.md", "# First");
        var second = await f.WriteAsync("second.md", "# Second");
        var store = new SessionStore(f.Shell.Paths.StateDirectory);
        Assert.True(store.Save(new SessionSnapshot(null, [new(first, 0, 0), new(second, 0, 0)], first, null,
            new Dictionary<string, FileViewState>())));
        using var restarted = new ShellViewModel(f.Shell.Paths, new ScriptedDialogs(), new RecordingLauncher());
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        restarted.FileService.AfterReadForLoadAsync = async path =>
        {
            if (path != second) return;
            paused.TrySetResult();
            await release.Task;
        };
        var restoration = restarted.RestoreSessionAsync();
        try
        {
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(800);
            Assert.Equal(2, store.Load().Tabs.Count);
            Assert.Single(restarted.Tabs);
        }
        finally { release.TrySetResult(); await restoration; }
        Assert.Equal(2, restarted.Tabs.Count);
    }

    [Fact]
    public async Task Exit_snapshot_keeps_tabs_and_geometry_even_after_tabs_are_detached()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("exit.md", "# Exit");
        await f.Shell.OpenFileAsync(path);
        var layout = new WindowLayoutHint(100, 100, 1200, 800, false, 250, true);
        Assert.True(await f.Shell.PrepareExitAsync(layout));
        Assert.Empty(f.Shell.Tabs);
        await Task.Delay(800);
        var snapshot = new SessionStore(f.Shell.Paths.StateDirectory).Load();
        Assert.Equal(path, Assert.Single(snapshot.Tabs).Path);
        Assert.Equal(layout, snapshot.Window);
    }

    [Fact]
    public async Task Live_saves_preserve_existing_window_layout_and_reset_cancels_pending_writes()
    {
        using var f = new ShellFixture();
        var layout = new WindowLayoutHint(100, 100, 1200, 800, false, 250, true);
        Assert.True(f.Shell.SaveSession(layout));
        var first = await f.WriteAsync("first.md", "# First");
        await f.Shell.OpenFileAsync(first);
        var store = new SessionStore(f.Shell.Paths.StateDirectory);
        await Until(() => store.Load().Tabs.Count == 1);
        Assert.Equal(layout, store.Load().Window);
        await f.Shell.OpenFileAsync(await f.WriteAsync("second.md", "# Second"));
        Assert.True(f.Shell.ResetApplication(new ResetOptions(RecentHistory: false, SessionState: true)));
        await Task.Delay(800);
        Assert.Empty(store.Load().Tabs);
        Assert.Equal(2, f.Shell.Tabs.Count);
    }

    [Fact]
    public async Task Disposal_cancels_pending_live_writes_without_overwriting_the_persisted_session()
    {
        using var f = new ShellFixture();
        var first = await f.WriteAsync("first.md", "# First");
        await f.Shell.OpenFileAsync(first);
        var store = new SessionStore(f.Shell.Paths.StateDirectory);
        await Until(() => store.Load().Tabs.Count == 1);
        await f.Shell.OpenFileAsync(await f.WriteAsync("second.md", "# Second"));
        f.Shell.Dispose();
        await Task.Delay(800);
        Assert.Equal(first, Assert.Single(store.Load().Tabs).Path);
    }

    [Fact]
    public async Task Failed_live_persistence_is_reported_without_affecting_open_documents()
    {
        using var f = new ShellFixture();
        Directory.CreateDirectory(f.Shell.Paths.StateDirectory);
        var sessionPath = Path.Combine(f.Shell.Paths.StateDirectory, SessionStore.FileName);
        await File.WriteAllTextAsync(sessionPath, "{}");
        using var locked = new FileStream(sessionPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var path = await f.WriteAsync("kept.md", "# Kept");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        await Until(() => f.Shell.StatusMessage.Contains("session could not be saved", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("# Kept", tab.CurrentText);
        Assert.Equal(StatusSeverity.Warning, f.Shell.StatusSeverity);
    }

    private static async Task Until(Func<bool> condition)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(25);
        Assert.True(condition(), "The live session state did not reach the expected persisted value.");
    }
}
