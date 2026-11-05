using Tern.App.Shell;
using Tern.Domain.Text;
using Tern.Workspaces;

namespace Tern.App.Tests;

public sealed class PerformanceRegressionTests
{
    [Fact]
    public async Task Atomic_save_and_its_watcher_hints_preserve_the_existing_tree()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("note.md", "# Original");
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));
        var workspace = f.Shell.Workspace!;
        var tree = workspace.Snapshot.Tree;
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        workspace.FilesChanged += (_, _) => changed.TrySetResult();
        var tab = (await f.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(tab.CurrentText.Length, " changed")]);
        Assert.True(await f.Shell.SaveTabAsync(tab));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Same(tree, workspace.Snapshot.Tree);
        Assert.Equal("Original changed", workspace.Graph.GetDocument(path)!.Title);
    }

    [Fact]
    public async Task Saving_existing_file_reuses_tree_but_new_files_refresh_membership()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("note.md", "# Before");
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));
        var workspace = f.Shell.Workspace!;
        var tree = workspace.Snapshot.Tree;
        await File.WriteAllTextAsync(path, "# After with more text");
        await workspace.NotifySavedAsync(path, "# After with more text");
        Assert.Same(tree, workspace.Snapshot.Tree);
        Assert.Equal(new FileInfo(path).Length, Assert.Single(workspace.Snapshot.Files).Length);
        var added = await f.WriteAsync("new.md", "# New");
        await workspace.NotifySavedAsync(added, "# New");
        Assert.Equal(2, workspace.Snapshot.Files.Count);
        Assert.Single(workspace.Search("New").Hits);
    }

    [Fact]
    public async Task Latest_unsaved_graph_wins_and_cleared_work_cannot_overwrite_a_save()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("note.md", "# Disk");
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));
        var workspace = f.Shell.Workspace!;
        for (var i = 0; i < 50; i++) workspace.SetUnsavedText(path, "# Draft " + i);
        await workspace.GraphIdle;
        Assert.Equal("Draft 49", workspace.Graph.GetDocument(path)!.Title);
        workspace.ClearUnsavedText(path);
        workspace.ClearUnsavedText(path); // Clean-tab activation must not cancel the restore.
        await workspace.GraphIdle;
        Assert.Equal("Disk", workspace.Graph.GetDocument(path)!.Title);
        workspace.SetUnsavedText(path, "# Obsolete");
        await workspace.NotifySavedAsync(path, "# Saved");
        await workspace.GraphIdle;
        Assert.Equal("Saved", workspace.Graph.GetDocument(path)!.Title);
    }

    [Fact]
    public async Task Oversized_watcher_change_is_not_read_into_graph()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("note.md", "# Small");
        using var workspace = await WorkspaceSession.OpenAsync(f.Docs, f.Root.File("index"), new WorkspaceScanOptions { MaxFileSizeBytes = 1024 });
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        workspace.FilesChanged += (_, _) => changed.TrySetResult();
        await File.WriteAllTextAsync(path, new string('x', 4096));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(workspace.Graph.Contains(path));
        Assert.Empty(workspace.Snapshot.Files);
    }

    [Fact]
    public async Task Deletion_hint_preserves_an_unsaved_buffer_in_the_graph()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("note.md", "# Disk");
        using var workspace = await WorkspaceSession.OpenAsync(f.Docs, f.Root.File("index"));
        workspace.SetUnsavedText(path, "# Unsaved draft");
        await workspace.GraphIdle;
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        workspace.FilesChanged += (_, _) => changed.TrySetResult();
        File.Delete(path);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Unsaved draft", workspace.Graph.GetDocument(path)!.Title);
        Assert.Single(workspace.Search("Unsaved").Hits);
    }
}
