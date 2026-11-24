using Tern.App.Documents;
using Tern.App.Shell;
using Tern.Filesystem;
using Tern.Workspaces;
using Xunit;

namespace Tern.App.Tests;

public class UnreachableAndMovedFileTests
{
    private static string UnusedDriveRoot()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        return (char)Enumerable.Range('D', 'Z' - 'D' + 1).First(c => !used.Contains((char)c)) + @":\";
    }

    [Fact]
    public void Probe_reports_a_missing_volume_as_unreachable_and_a_missing_file_as_missing()
    {
        using var root = new TempDir();
        File.WriteAllText(root.File("x.md"), "x");
        Assert.Equal(FileAvailability.Present, FileAvailabilityProbe.Probe(root.File("x.md")));
        Assert.Equal(FileAvailability.Missing, FileAvailabilityProbe.Probe(root.File("gone.md")));
        Assert.Equal(FileAvailability.Missing, FileAvailabilityProbe.Probe(Path.Combine(root.File("no-folder"), "gone.md")));
        if (OperatingSystem.IsWindows())
            Assert.Equal(FileAvailability.Unreachable, FileAvailabilityProbe.Probe(UnusedDriveRoot() + @"share\note.md"));
    }

    [Fact]
    public async Task Unreachable_location_keeps_baseline_shows_unavailable_and_recovers_without_a_dialog()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("a.md", "a\n");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        var baseline = tab.Session.Baseline;
        var reachable = false;
        f.Shell.FileService.AvailabilityProbe = p => reachable ? FileAvailabilityProbe.Probe(p) : FileAvailability.Unreachable;
        var hidden = path + ".hidden";
        File.Move(path, hidden);

        await f.Shell.CheckForExternalChangesAsync(tab);
        Assert.True(tab.IsInaccessible);
        Assert.Null(tab.Conflict);
        Assert.Same(baseline, tab.Session.Baseline);
        Assert.Equal(0, f.Dialogs.MissingPrompts);
        Assert.Equal("Unavailable — will retry", DocumentProtection.For(tab, f.Shell.ActiveDocumentState).Label);

        tab.ApplyEdits([new Tern.Domain.Text.TextEdit(0, 0, "edit ")]);
        Assert.False(await f.Shell.SaveTabAsync(tab));
        Assert.Contains(f.Dialogs.Messages, m => m.Contains("unavailable"));
        Assert.Equal(0, f.Dialogs.ConflictPrompts + f.Dialogs.MissingPrompts);

        File.Move(hidden, path);
        reachable = true;
        await f.Shell.CheckForExternalChangesAsync(tab);
        Assert.False(tab.IsInaccessible);
        Assert.Null(tab.Conflict);
        Assert.True(await f.Shell.SaveTabAsync(tab));
        Assert.Equal("edit a\n", await File.ReadAllTextAsync(path));
        Assert.Equal(0, f.Dialogs.ConflictPrompts + f.Dialogs.MissingPrompts);
    }

    [Fact]
    public async Task Save_never_recreates_a_vanished_folder_without_consent()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync(Path.Combine("old", "a.md"), "a\n");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        Directory.Delete(Path.GetDirectoryName(path)!, true);
        tab.ApplyEdits([new Tern.Domain.Text.TextEdit(0, 0, "x")]);

        f.Dialogs.RecreateChoice = RecreateFolderChoice.Cancel;
        Assert.False(await f.Shell.SaveTabAsync(tab));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)!));
        Assert.Single(f.Dialogs.RecreatePrompts);

        f.Dialogs.RecreateChoice = RecreateFolderChoice.Recreate;
        Assert.True(await f.Shell.SaveTabAsync(tab));
        Assert.Equal("xa\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Renamed_folder_offers_follow_and_follow_moves_the_tab_with_its_unsaved_edits()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync(Path.Combine("old", "a.md"), "a\n");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([new Tern.Domain.Text.TextEdit(0, 0, "x")]);
        var oldFolder = Path.GetDirectoryName(path)!;
        var newFolder = Path.Combine(f.Docs, "new");
        Directory.Move(oldFolder, newFolder);

        await f.Shell.OfferFollowForMovedFilesAsync(new WorkspaceChangeSet([new WorkspaceChange(WorkspaceChangeKind.Renamed, newFolder, oldFolder, true)], false));
        Assert.True(f.Shell.TryGetMovedTarget(tab, out var target));
        Assert.Equal(Path.Combine(newFolder, "a.md"), target);
        Assert.Contains("was moved to", f.Shell.Banner!.Text);
        Assert.Contains(f.Shell.Banner.Actions, a => a.Label == "Follow");
        Assert.Contains(f.Shell.Banner.Actions, a => a.Label.StartsWith("Save here anyway"));

        Assert.True(await f.Shell.FollowMovedFileAsync(tab));
        Assert.Equal(target, tab.FilePath);
        Assert.True(tab.IsDirty);
        Assert.Null(tab.Conflict);
        Assert.True(await f.Shell.SaveTabAsync(tab));
        Assert.Equal("xa\n", await File.ReadAllTextAsync(target));
        Assert.False(Directory.Exists(oldFolder));
        Assert.Empty(f.Dialogs.RecreatePrompts);
    }
}
