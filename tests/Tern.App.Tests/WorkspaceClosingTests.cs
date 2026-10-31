using Tern.App.Shell;
using Tern.Domain.Text;
using Tern.Workspaces;

namespace Tern.App.Tests;

public sealed class WorkspaceClosingTests
{
    [Theory]
    [InlineData(SaveChoice.Cancel, false)]
    [InlineData(SaveChoice.Discard, true)]
    [InlineData(SaveChoice.Save, true)]
    public async Task Workspace_close_respects_save_choices_and_keeps_unrelated_tabs(SaveChoice choice, bool closes)
    {
        using var f = new ShellFixture();
        var cleanPath = await f.WriteAsync("clean.md", "clean");
        var dirtyPath = await f.WriteAsync("dirty.md", "original");
        await f.Shell.OpenWorkspaceAsync(f.Docs);
        var workspace = f.Shell.Workspace;
        var clean = await f.Shell.OpenFileAsync(cleanPath);
        var dirty = (await f.Shell.OpenFileAsync(dirtyPath))!;
        dirty.ApplyEdits([TextEdit.Insert(0, "changed ")]);
        var outside = f.Root.File("outside.md");
        await File.WriteAllTextAsync(outside, "outside");
        var standalone = await f.Shell.OpenFileAsync(outside);
        var untitled = f.Shell.NewDocument();
        f.Dialogs.SaveChoice = choice;

        Assert.Equal(closes, await f.Shell.CloseWorkspaceAsync());
        Assert.Contains(standalone!, f.Shell.Tabs);
        Assert.Contains(untitled, f.Shell.Tabs);
        if (closes)
        {
            Assert.Null(f.Shell.Workspace);
            Assert.DoesNotContain(clean!, f.Shell.Tabs);
            Assert.DoesNotContain(dirty, f.Shell.Tabs);
        }
        else
        {
            Assert.Same(workspace, f.Shell.Workspace);
            Assert.Contains(clean!, f.Shell.Tabs);
            Assert.Contains(dirty, f.Shell.Tabs);
        }
        Assert.Equal(choice == SaveChoice.Save ? "changed original" : "original", await File.ReadAllTextAsync(dirtyPath));
        Assert.True(File.Exists(cleanPath));
        Assert.True(Directory.Exists(f.Docs));
    }

    [Fact]
    public async Task Folder_close_matches_path_boundaries_and_does_not_close_siblings()
    {
        using var f = new ShellFixture();
        await f.Shell.OpenWorkspaceAsync(f.Docs);
        var insidePath = await f.WriteAsync("notes/inside.md", "inside");
        var siblingPath = await f.WriteAsync("notes-archive/outside.md", "outside");
        var inside = await f.Shell.OpenFileAsync(insidePath);
        var sibling = await f.Shell.OpenFileAsync(siblingPath);
        Assert.True(await f.Shell.CloseFolderDocumentsAsync(Path.Combine(f.Docs, "notes")));
        Assert.DoesNotContain(inside!, f.Shell.Tabs);
        Assert.Contains(sibling!, f.Shell.Tabs);
        Assert.NotNull(f.Shell.Workspace);
        Assert.True(File.Exists(insidePath));
    }

    [Fact]
    public async Task Failed_session_write_keeps_documents_open()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("a.md", "original");
        var tab = await f.Shell.OpenFileAsync(path);
        Directory.CreateDirectory(Path.Combine(f.Shell.Paths.StateDirectory, SessionStore.FileName));
        var layout = new WindowLayoutHint(0, 0, 1440, 900, false, 260, true);
        Assert.False(await f.Shell.PrepareExitAsync(layout));
        Assert.Contains(tab!, f.Shell.Tabs);
        Assert.Contains("session could not be saved", f.Shell.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Exit_preserves_reopenable_session_and_cancel_keeps_all_tabs()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("a.md", "original");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "changed ")]);
        var layout = new WindowLayoutHint(0, 0, 1440, 900, false, 260, true);
        Assert.False(await f.Shell.PrepareExitAsync(layout));
        Assert.Contains(tab, f.Shell.Tabs);
        f.Dialogs.SaveChoice = SaveChoice.Discard;
        Assert.True(await f.Shell.PrepareExitAsync(layout));
        Assert.Empty(f.Shell.Tabs);
        var session = new SessionStore(f.Shell.Paths.StateDirectory).Load();
        Assert.Equal(path, Assert.Single(session.Tabs).Path);
        Assert.Equal("original", await File.ReadAllTextAsync(path));
    }
}
