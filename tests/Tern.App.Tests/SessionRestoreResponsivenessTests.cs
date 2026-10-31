using Tern.App.Shell;
using Tern.Domain.Text;
using Tern.Filesystem;
using Tern.Workspaces;

namespace Tern.App.Tests;

public sealed class SessionRestoreResponsivenessTests
{
    [Fact]
    public async Task Workspace_preparation_overlaps_a_paused_background_document_while_active_document_stays_editable()
    {
        using var fixture = new ShellFixture();
        var (background, active) = await PrepareSessionAsync(fixture);
        var recentPath = Path.Combine(fixture.Shell.Paths.StateDirectory, RecentItemsStore.FileName);
        var recentBefore = await File.ReadAllTextAsync(recentPath);
        using var restored = new ShellViewModel(fixture.Shell.Paths, new ScriptedDialogs(), new RecordingLauncher());
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var files = FilesFor(restored);
        files.AfterReadForLoadAsync = async path =>
        {
            if (!WorkspacePaths.PathsEqual(path, background)) return;
            paused.TrySetResult();
            await release.Task;
        };
        restored.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.Workspace) && restored.Workspace is not null) prepared.TrySetResult();
        };
        var ready = false;
        var pending = restored.RestoreSessionAsync(() =>
        {
            ready = true;
            Assert.Equal(active, restored.ActiveTab!.FilePath);
            Assert.Single(restored.Tabs);
            Assert.Null(restored.Workspace);
            restored.ActiveTab.ApplyEdits([TextEdit.Insert(restored.ActiveTab.CurrentText.Length, "\nEarly edit\n")]);
        });
        try
        {
            await Task.WhenAll(paused.Task, prepared.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(ready);
            Assert.False(pending.IsCompleted);
            Assert.Single(restored.Tabs);
            Assert.Equal(active, restored.ActiveTab!.FilePath);
            Assert.Equal(fixture.Docs, Assert.Single(restored.OpenWorkspaces).Root);
        }
        finally
        {
            release.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            files.AfterReadForLoadAsync = null;
        }
        Assert.Equal(new[] { background, active }, restored.Tabs.Select(t => t.FilePath));
        Assert.Equal(active, restored.ActiveTab!.FilePath);
        Assert.Equal("# Active\n\nEarly edit\n", restored.ActiveTab.CurrentText);
        Assert.True(restored.ActiveTab.IsDirty);
        Assert.Equal("# Active\n", await File.ReadAllTextAsync(active));
        Assert.Equal(fixture.Docs, restored.Workspace!.Root);
        Assert.Empty(restored.Recent.Files);
        Assert.Empty(restored.Recent.Workspaces);
        Assert.Equal(recentBefore, await File.ReadAllTextAsync(recentPath));
    }

    [Fact]
    public async Task Disposal_during_overlapping_restore_does_not_attach_the_paused_document_or_retain_workspace_handles()
    {
        using var fixture = new ShellFixture();
        var (background, active) = await PrepareSessionAsync(fixture);
        using var restored = new ShellViewModel(fixture.Shell.Paths, new ScriptedDialogs(), new RecordingLauncher());
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var files = FilesFor(restored);
        files.AfterReadForLoadAsync = async path =>
        {
            if (!WorkspacePaths.PathsEqual(path, background)) return;
            paused.TrySetResult();
            await release.Task;
        };
        restored.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.Workspace) && restored.Workspace is not null) prepared.TrySetResult();
        };
        var pending = restored.RestoreSessionAsync();
        try
        {
            await Task.WhenAll(paused.Task, prepared.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(active, Assert.Single(restored.Tabs).FilePath);
            restored.Dispose();
            Assert.Empty(restored.OpenWorkspaces);
        }
        finally
        {
            release.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            files.AfterReadForLoadAsync = null;
        }
        Assert.Empty(restored.OpenWorkspaces);
        Assert.Null(restored.Workspace);
        Assert.Equal(active, Assert.Single(restored.Tabs).FilePath);
        Assert.Empty(restored.Recent.Files);
        Assert.Empty(restored.Recent.Workspaces);
        var database = Path.Combine(restored.Paths.IndexDirectory, WorkspaceSession.IndexFileName(fixture.Docs));
        using var exclusive = new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.True(exclusive.Length > 0);
    }

    private static DocumentFileService FilesFor(ShellViewModel shell)
    {
        // Reuse the existing filesystem interleaving seam; no production restoration abstraction is introduced.
        var field = typeof(ShellViewModel).GetField("_files", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<DocumentFileService>(field.GetValue(shell));
    }

    private static async Task<(string Background, string Active)> PrepareSessionAsync(ShellFixture fixture)
    {
        var background = await fixture.WriteAsync("background.md", "# Background\n");
        var active = await fixture.WriteAsync("active.md", "# Active\n");
        await fixture.Shell.OpenWorkspaceAsync(fixture.Docs);
        await fixture.Shell.OpenFileAsync(background);
        await fixture.Shell.OpenFileAsync(active);
        fixture.Shell.ClearRecentHistory();
        Assert.True(fixture.Shell.SaveSession());
        fixture.Shell.Dispose();
        return (background, active);
    }

    [Fact]
    public async Task Active_saved_document_is_ready_before_workspace_indexing_and_original_tab_order_returns()
    {
        using var fixture = new ShellFixture();
        var first = await fixture.WriteAsync("first.md", "# First\n");
        var active = await fixture.WriteAsync("active.md", "# Active\n");
        await fixture.Shell.OpenWorkspaceAsync(fixture.Docs);
        await fixture.Shell.OpenFileAsync(first);
        await fixture.Shell.OpenFileAsync(active);
        Assert.True(fixture.Shell.SaveSession());
        using var restored = new ShellViewModel(fixture.Shell.Paths, new ScriptedDialogs(), new RecordingLauncher());
        var ready = false;
        await restored.RestoreSessionAsync(() =>
        {
            ready = true;
            Assert.Equal(active, restored.ActiveTab!.FilePath);
            Assert.Null(restored.Workspace);
            Assert.Single(restored.Tabs);
            Assert.False(restored.IsBusy);
        });
        Assert.True(ready);
        Assert.Equal(new[] { first, active }, restored.Tabs.Select(t => t.FilePath));
        Assert.Equal(active, restored.ActiveTab!.FilePath);
        Assert.NotNull(restored.Workspace);
    }
}
