using Tern.Markdown;
using Tern.Rendering.Resources;
using Tern.Workspaces;

namespace Tern.Security.Tests;

public class PathTraversalTests
{
    [Theory]
    [InlineData("..\\evil.md")]
    [InlineData("../evil.md")]
    [InlineData("sub/evil.md")]
    [InlineData("sub\\evil.md")]
    [InlineData("C:\\evil.md")]
    [InlineData("\\\\server\\share\\evil.md")]
    [InlineData("evil.md:stream")]
    [InlineData("CON")]
    [InlineData("nul.md")]
    [InlineData("COM1.md")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("bad|name.md")]
    [InlineData("bad\0name.md")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData(".")]
    public void Hostile_names_are_refused_when_creating_files_and_folders(string name)
    {
        using var box = new Sandbox();
        var ops = new WorkspaceFileOperations(box.Workspace);

        var file = ops.CreateFile(box.Workspace, name);
        var folder = ops.CreateFolder(box.Workspace, name);

        Assert.False(file.Succeeded);
        Assert.False(folder.Succeeded);
        Assert.False(File.Exists(Path.Combine(box.Root, "evil.md")));
        Assert.Empty(Directory.GetFileSystemEntries(box.Workspace));
    }

    [Fact]
    public void Overlong_names_are_refused()
    {
        using var box = new Sandbox();
        var ops = new WorkspaceFileOperations(box.Workspace);
        Assert.False(ops.CreateFile(box.Workspace, new string('a', 300) + ".md").Succeeded);
    }

    [Fact]
    public async Task Operations_outside_the_workspace_are_refused_even_with_dot_dot_segments()
    {
        using var box = new Sandbox();
        box.Write("sub\\a.md");
        var ops = new WorkspaceFileOperations(box.Workspace);
        var sneaky = Path.Combine(box.Workspace, "sub", "..", "..", "outside");

        Assert.Equal(FileOperationError.OutsideWorkspace, ops.CreateFile(sneaky, "x.md").Error);
        Assert.Equal(FileOperationError.OutsideWorkspace, ops.CreateFolder(sneaky, "x").Error);
        Assert.Equal(FileOperationError.OutsideWorkspace, (await ops.MoveAsync(Path.Combine(box.Workspace, "sub", "a.md"), sneaky)).Error);
        Assert.Equal(FileOperationError.OutsideWorkspace, (await ops.RenameAsync(Path.Combine(box.Workspace, "..", "outside", "secret.md"), "renamed.md")).Error);
        Assert.Equal(FileOperationError.OutsideWorkspace, ops.Duplicate(Path.Combine(box.Outside, "secret.md")).Error);

        Assert.True(File.Exists(Path.Combine(box.Outside, "secret.md")));
        Assert.Equal(new[] { "secret.md", "secret.png" }, Directory.GetFiles(box.Outside).Select(Path.GetFileName).Order().ToArray());
    }

    [Fact]
    public async Task A_sibling_folder_sharing_the_workspace_name_prefix_is_not_inside_it()
    {
        using var box = new Sandbox();
        var sibling = Path.Combine(box.Root, "ws-evil");
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "a.md"), "x");
        var ops = new WorkspaceFileOperations(box.Workspace);

        Assert.Equal(FileOperationError.OutsideWorkspace, (await ops.RenameAsync(Path.Combine(sibling, "a.md"), "b.md")).Error);
        Assert.False(WorkspacePaths.IsSameOrUnder(box.Workspace, sibling));
    }

    [Fact]
    public void Creating_a_file_inside_a_junction_that_points_outside_is_refused()
    {
        using var box = new Sandbox();
        var link = Path.Combine(box.Workspace, "portal");
        Assert.True(box.TryLinkDirectory(link, box.Outside), "Could not create a directory junction/symlink for the test.");
        var ops = new WorkspaceFileOperations(box.Workspace);

        var result = ops.CreateFile(link, "planted.md", "planted");

        Assert.False(result.Succeeded);
        Assert.False(File.Exists(Path.Combine(box.Outside, "planted.md")));
        Assert.False(ops.CreateFolder(link, "planted-dir").Succeeded);
        Assert.False(Directory.Exists(Path.Combine(box.Outside, "planted-dir")));
    }

    [Fact]
    public async Task Renaming_moving_or_copying_through_a_junction_cannot_reach_outside_files()
    {
        using var box = new Sandbox();
        var link = Path.Combine(box.Workspace, "portal");
        Assert.True(box.TryLinkDirectory(link, box.Outside));
        var inside = box.Write("mine.md");
        var ops = new WorkspaceFileOperations(box.Workspace);

        var viaLink = Path.Combine(link, "secret.md");
        Assert.False((await ops.RenameAsync(viaLink, "stolen.md")).Succeeded);
        Assert.False(ops.Duplicate(viaLink).Succeeded);
        Assert.False((await ops.MoveAsync(inside, link)).Succeeded);
        Assert.False((await ops.MoveAsync(viaLink, box.Workspace)).Succeeded);

        Assert.True(File.Exists(Path.Combine(box.Outside, "secret.md")));
        Assert.False(File.Exists(Path.Combine(box.Outside, "mine.md")));
        Assert.True(File.Exists(inside));
        Assert.False(File.Exists(Path.Combine(box.Outside, "stolen.md")));
        Assert.False(File.Exists(Path.Combine(box.Outside, "secret copy.md")));
    }

    [Fact]
    public async Task The_link_itself_can_still_be_renamed_without_touching_its_target()
    {
        using var box = new Sandbox();
        var link = Path.Combine(box.Workspace, "portal");
        Assert.True(box.TryLinkDirectory(link, box.Outside));
        var ops = new WorkspaceFileOperations(box.Workspace);

        var renamed = await ops.RenameAsync(link, "portal2");

        Assert.True(renamed.Succeeded);
        Assert.True(File.Exists(Path.Combine(box.Outside, "secret.md")));
    }

    [Fact]
    public async Task Scanning_does_not_list_files_behind_a_junction_to_outside_the_workspace()
    {
        using var box = new Sandbox();
        box.Write("a.md");
        Assert.True(box.TryLinkDirectory(Path.Combine(box.Workspace, "portal"), box.Outside));

        var snapshot = await new WorkspaceScanner().ScanAsync(box.Workspace);

        Assert.DoesNotContain(snapshot.Files, f => f.FullPath.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Scanning_survives_a_link_cycle()
    {
        using var box = new Sandbox();
        box.Write("a.md");
        var inner = Path.Combine(box.Workspace, "loop");
        Directory.CreateDirectory(inner);
        Assert.True(box.TryLinkDirectory(Path.Combine(inner, "back"), box.Workspace));

        var snapshot = await new WorkspaceScanner().ScanAsync(box.Workspace).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(snapshot.Files.Count < 10);
    }

    [Theory]
    [InlineData("../outside/secret.md")]
    [InlineData("..\\outside\\secret.md")]
    [InlineData("sub/../../outside/secret.md")]
    [InlineData("/../outside/secret.md")]
    public async Task Links_climbing_out_of_the_workspace_are_reported_as_outside(string destination)
    {
        using var box = new Sandbox();
        var source = box.Write("sub\\a.md", $"[x]({destination})\n");
        var graph = new LinkGraph(box.Workspace);
        await graph.LoadAsync([source], new DiskTextProvider());

        var link = Assert.Single(graph.GetOutgoing(source));

        Assert.True(link.Status is LinkStatus.OutsideWorkspace or LinkStatus.MissingFile, link.Status.ToString());
        Assert.DoesNotContain(graph.GetBacklinks(Path.Combine(box.Outside, "secret.md")), _ => true);
    }

    [Fact]
    public async Task An_absolute_link_to_an_outside_file_is_outside_the_workspace()
    {
        using var box = new Sandbox();
        var source = box.Write("a.md", $"[x]({new Uri(Path.Combine(box.Outside, "secret.md")).AbsolutePath})\n[y]({Path.Combine(box.Outside, "secret.md")})\n");
        var graph = new LinkGraph(box.Workspace);
        await graph.LoadAsync([source], new DiskTextProvider());

        Assert.All(graph.GetOutgoing(source), l => Assert.NotEqual(LinkStatus.Ok, l.Status));
    }

    // ---- Resolver: image and link destinations ------------------------------------------------------------------

    private static SafeResourceResolver ResolverFor(Sandbox box, string? documentDirectory = null) =>
        new(new ResourceResolverOptions { DocumentDirectory = documentDirectory ?? box.Workspace, WorkspaceRoot = box.Workspace });

    [Theory]
    [InlineData("../outside/secret.png")]
    [InlineData("..\\outside\\secret.png")]
    [InlineData("a/../../outside/secret.png")]
    [InlineData("%2e%2e/outside/secret.png")]
    [InlineData("%2E%2E%2Foutside%2Fsecret.png")]
    [InlineData("%252e%252e/outside/secret.png")]
    [InlineData("..%5Coutside%5Csecret.png")]
    [InlineData("....//outside/secret.png")]
    [InlineData("/outside/secret.png")]
    [InlineData("secret.png:evil")]
    [InlineData("secret.png::$DATA")]
    [InlineData("CON")]
    [InlineData("aux.png")]
    [InlineData("\\\\?\\C:\\Windows\\win.ini")]
    [InlineData("\\\\.\\pipe\\x")]
    [InlineData("\\\\server\\share\\x.png")]
    [InlineData("//server/share/x.png")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("C:win.ini")]
    public void Image_destinations_that_leave_the_workspace_are_blocked(string destination)
    {
        using var box = new Sandbox();
        var decision = ResolverFor(box).ResolveImage(destination);
        Assert.IsType<Blocked>(decision);
    }

    [Fact]
    public void A_traversal_that_stays_inside_the_workspace_is_allowed_and_canonical()
    {
        using var box = new Sandbox();
        box.Write("a\\b\\img.png", "x");
        var decision = ResolverFor(box, Path.Combine(box.Workspace, "a")).ResolveImage("b/../b/img.png");
        var local = Assert.IsType<LocalFile>(decision);
        Assert.Equal(Path.Combine(box.Workspace, "a", "b", "img.png"), local.CanonicalPath, ignoreCase: true);
    }

    [Fact]
    public void A_junction_that_points_outside_is_not_followed_for_links_or_images()
    {
        using var box = new Sandbox();
        Assert.True(box.TryLinkDirectory(Path.Combine(box.Workspace, "portal"), box.Outside));
        var resolver = ResolverFor(box);

        Assert.IsType<Blocked>(resolver.ResolveImage("portal/secret.png"));
        Assert.IsType<Blocked>(resolver.ResolveLink("portal/secret.md"));
        Assert.IsType<Blocked>(resolver.ResolveLink("portal\\secret.md"));
    }

    [Fact]
    public void A_file_symlink_that_points_outside_is_not_followed()
    {
        using var box = new Sandbox();
        Assert.True(box.TryLinkFile(Path.Combine(box.Workspace, "pic.png"), Path.Combine(box.Outside, "secret.png")),
            "UNMET prerequisite: enable file symlink creation for the path acceptance suite. This case was not exercised.");

        Assert.IsType<Blocked>(ResolverFor(box).ResolveImage("pic.png"));
    }

    [Fact]
    public void A_junction_that_points_inside_the_workspace_is_allowed()
    {
        using var box = new Sandbox();
        box.Write("real\\pic.png", "x");
        Assert.True(box.TryLinkDirectory(Path.Combine(box.Workspace, "alias"), Path.Combine(box.Workspace, "real")));

        var local = Assert.IsType<LocalFile>(ResolverFor(box).ResolveImage("alias/pic.png"));
        Assert.StartsWith(Path.Combine(box.Workspace, "real"), local.CanonicalPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Executable_and_active_content_targets_are_flagged_as_risky()
    {
        using var box = new Sandbox();
        foreach (var name in new[] { "run.exe", "page.html", "image.svg", "script.ps1", "shortcut.lnk", "x.bat" })
        {
            box.Write(name, "x");
            var local = Assert.IsType<LocalFile>(ResolverFor(box).ResolveLink(name));
            Assert.True(local.IsActiveContentRisk, name);
        }
    }

    [Fact]
    public void Absurdly_long_destinations_are_refused_without_exceptions()
    {
        using var box = new Sandbox();
        var resolver = ResolverFor(box);
        Assert.IsType<Blocked>(resolver.ResolveLink(new string('a', 100_000)));
        Assert.IsType<Blocked>(resolver.ResolveImage(string.Join("/", Enumerable.Repeat("..", 20_000))));
        Assert.IsType<Blocked>(resolver.ResolveLink("http://" + new string('a', 100_000)));
    }

    [Fact]
    public async Task Recovery_and_state_files_never_name_a_path_the_app_will_write_to()
    {
        // A recovery record's OriginalPath is only a label for the restored tab: restoring creates an unsaved buffer.
        using var box = new Sandbox();
        var store = new Tern.Recovery.RecoveryStore(Path.Combine(box.Root, "recovery"));
        var record = new Tern.Recovery.RecoveryRecord(
            Guid.NewGuid(), Path.Combine(box.Outside, "secret.md"), "..\\..\\evil.md", 1, DateTime.UtcNow, null,
            Tern.Domain.Text.TextFileFormat.NewFileDefault, "text");
        await store.WriteCheckpointAsync(record);
        Assert.Empty(Directory.GetFiles(box.Outside, "*.recovery*"));
        Assert.All(Directory.EnumerateFiles(Path.Combine(box.Root, "recovery"), "*", SearchOption.AllDirectories), f => Assert.StartsWith(Path.Combine(box.Root, "recovery"), f, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class DiskTextProvider : IDocumentTextProvider
    {
        public ValueTask<string?> ReadTextAsync(string path, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>(File.Exists(path) ? File.ReadAllText(path) : null);
    }
}
