using Tern.Workspaces;

namespace Tern.App.Tests;

public sealed class NestedWorkspaceRootTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Opening_nested_workspace_loads_complete_outer_root_and_its_saved_context(bool implicitOpen)
    {
        using var fixture = new ShellFixture();
        var first = await fixture.WriteAsync("WORKSPACE/FILES1.md", "# First");
        var second = await fixture.WriteAsync("WORKSPACE/FILES2.md", "# Second");
        var nestedNote = await fixture.WriteAsync("WORKSPACE/FOLDER1/WORKSPACE2/Nested.md", "# Nested");
        var otherBranch = await fixture.WriteAsync("WORKSPACE/OTHER/Sibling.md", "# Sibling");
        var root = Path.GetDirectoryName(first)!;
        var nested = Path.GetDirectoryName(nestedNote)!;
        WorkspaceDefinition.Create(root, "WORKSPACE");
        WorkspaceDefinition.Create(nested, "WORKSPACE2");
        WorkspaceDefinition.SaveWorkingContext(root, "WORKSPACE", new WorkspaceWorkingContext([new("FILES1.md", 3, 0)], "FILES1.md", "Reading"));
        WorkspaceDefinition.SaveWorkingContext(nested, "WORKSPACE2", new WorkspaceWorkingContext([new("Nested.md", 0, 0)], "Nested.md", "Split"));
        Assert.True(implicitOpen ? await fixture.Shell.OpenDirectoryAsync(nested) : await fixture.Shell.OpenWorkspaceAsync(nested));
        var workspace = Assert.Single(fixture.Shell.OpenWorkspaces);
        Assert.Equal(root, workspace.Root);
        Assert.Equal("WORKSPACE", workspace.Name);
        Assert.True(WorkspacePaths.PathsEqual(first, fixture.Shell.ActiveTab!.FilePath!));
        Assert.Equal(3, fixture.Shell.GetViewState(WorkspacePaths.Normalize(first))!.CaretOffset);
        Assert.Equal(4, workspace.Snapshot.Files.Count);
        foreach (var path in new[] { first, second, nestedNote, otherBranch })
            Assert.Contains(workspace.Snapshot.Files, file => WorkspacePaths.PathsEqual(file.FullPath, path));
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(nested));
        Assert.Same(workspace, Assert.Single(fixture.Shell.OpenWorkspaces));
        Assert.Single(fixture.Shell.Tabs);
    }

    [Fact]
    public async Task Topmost_workspace_is_used_across_several_named_ancestors()
    {
        using var fixture = new ShellFixture();
        var leaf = await fixture.WriteAsync("Root/Branch/Middle/Folder/Leaf/note.md", "note");
        var root = Path.Combine(fixture.Docs, "Root");
        var middle = Path.Combine(root, "Branch", "Middle");
        var nested = Path.GetDirectoryName(leaf)!;
        foreach (var directory in new[] { root, middle, nested }) WorkspaceDefinition.Create(directory, Path.GetFileName(directory));
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(nested));
        Assert.Equal(root, Assert.Single(fixture.Shell.OpenWorkspaces).Root);
    }

    [Fact]
    public async Task Plain_ancestors_do_not_expand_a_workspace_to_the_drive_root()
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("Plain/OnlyWorkspace/note.md", "note");
        var root = Path.GetDirectoryName(path)!;
        WorkspaceDefinition.Create(root, "OnlyWorkspace");
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(root));
        Assert.Equal(root, Assert.Single(fixture.Shell.OpenWorkspaces).Root);
    }

    [Fact]
    public async Task Complete_scan_continues_through_deeper_workspaces_without_a_fixed_nesting_limit()
    {
        using var fixture = new ShellFixture();
        var root = Path.Combine(fixture.Docs, "Root");
        var current = root;
        string? selected = null;
        var files = new List<string>();
        for (var level = 0; level < 24; level++)
        {
            Directory.CreateDirectory(current);
            WorkspaceDefinition.Create(current, "Workspace " + level);
            var path = Path.Combine(current, "note.md");
            await File.WriteAllTextAsync(path, "# Level " + level);
            files.Add(path);
            if (level == 5) selected = current;
            current = Path.Combine(current, "d" + level);
        }
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(selected!));
        var workspace = Assert.Single(fixture.Shell.OpenWorkspaces);
        Assert.Equal(root, workspace.Root);
        Assert.Equal(files.Count, workspace.Snapshot.Files.Count);
        foreach (var path in files) Assert.Contains(workspace.Snapshot.Files, file => WorkspacePaths.PathsEqual(file.FullPath, path));
    }
}
