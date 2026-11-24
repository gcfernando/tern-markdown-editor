using Tern.App.Knowledge;
using Tern.App.Shell;
using Tern.Workspaces;

namespace Tern.App.Tests;

public sealed class KnowledgeSourceStateTests
{
    [Fact]
    public async Task Equivalent_path_separators_share_one_overlay_revision_and_source_validation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new ShellFixture();
        var written = await f.WriteAsync("notes/launch.md", "# Launch\nLaunch is on 14 April.");
        var canonical = WorkspacePaths.Normalize(written);
        var alternative = canonical.Replace('\\', '/');
        using var workspace = await WorkspaceSession.OpenAsync(f.Docs, f.Root.File("index"));
        var retrieval = new KnowledgeRetrieval();
        var source = Assert.Single((await retrieval.SearchAsync(workspace, "Launch", new())).Sources);
        var before = workspace.CaptureKnowledgeState(canonical);
        workspace.SetUnsavedText(alternative, "# Launch\nLaunch is on 15 April.");
        var after = workspace.CaptureKnowledgeState(canonical);
        Assert.True(after.OverlayRevision > before.OverlayRevision);
        Assert.Equal(after, workspace.CaptureKnowledgeState(alternative));
        Assert.Equal(before.Disk, after.Disk);
        await Assert.ThrowsAnyAsync<Exception>(() => retrieval.ValidateAsync(workspace, [source], new()));
        workspace.ClearUnsavedText(alternative);
        await retrieval.ValidateAsync(workspace, [source], new());
        Assert.Equal("# Launch\nLaunch is on 14 April.", await File.ReadAllTextAsync(canonical));
    }
}
