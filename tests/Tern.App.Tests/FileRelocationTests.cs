using Tern.App.Shell;
using Tern.Domain.Text;
using Tern.Recovery;
using Tern.Workspaces;

namespace Tern.App.Tests;

/// <summary>Move, Duplicate, folder rename and folder recycle must migrate every identity keyed by a path (SB-06).</summary>
public sealed class FileRelocationTests
{
    [Fact]
    public async Task Moving_a_linked_note_repairs_links_and_carries_tabs_history_recent_and_view_state()
    {
        using var fixture = new ShellFixture();
        var source = await fixture.WriteAsync("index.md", "[note](note.md)\n");
        var note = await fixture.WriteAsync("note.md", "# Note\n");
        Directory.CreateDirectory(Path.Combine(fixture.Docs, "archive"));
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(fixture.Docs));
        var tab = (await fixture.Shell.OpenFileAsync(note))!;
        fixture.Shell.RememberViewState(tab, 3, 40);
        await fixture.Shell.HistoryStore.CaptureAsync(note, "# Old\n");
        Assert.Contains(Path.Combine(fixture.Docs, "archive"), fixture.Shell.MoveDestinations(note));

        Assert.True(await fixture.Shell.MovePathAsync(note, Path.Combine(fixture.Docs, "archive")), fixture.Shell.StatusMessage);
        var movedActivity = Assert.Single(fixture.Shell.Activities.Items, item => item.Kind == "Moving Library item");
        Assert.Equal("Moved; link changes await review", movedActivity.Phase);
        Assert.Equal(ActivityState.Completed, movedActivity.State);
        Assert.Null(movedActivity.Cancel);

        var moved = Path.Combine(fixture.Docs, "archive", "note.md");
        Assert.False(File.Exists(note));
        Assert.Equal(moved, tab.FilePath);
        Assert.Contains(fixture.Shell.Recent.Files, e => e.Path == WorkspacePaths.Normalize(moved));
        Assert.Equal(40, fixture.Shell.GetViewState(moved)?.ScrollOffset);
        Assert.Equal("# Old\n", Assert.Single(await fixture.Shell.HistoryStore.ReadAsync(moved)).Text);
        Assert.Empty(await fixture.Shell.HistoryStore.ReadAsync(note));
        var plan = Assert.IsType<RepairPlan>(fixture.Shell.LastRenameRepairPlan);
        Assert.Contains(plan.Files, f => f.Path == source && f.ResultText.Contains("archive/note.md", StringComparison.Ordinal));
        Assert.True((await fixture.Shell.ApplyRenameRepairsAsync(plan)).Succeeded);
        Assert.Equal(ActivityState.Completed,
            Assert.Single(fixture.Shell.Activities.Items, item => item.Kind == "Applying reviewed link repairs").State);
    }

    [Fact]
    public async Task Link_repairs_keep_each_closed_files_earlier_version_and_can_be_undone_only_while_unchanged()
    {
        using var fixture = new ShellFixture();
        var index = await fixture.WriteAsync("index.md", "[note](note.md)\n");
        var other = await fixture.WriteAsync("other.md", "[note](note.md)\n");
        var note = await fixture.WriteAsync("note.md", "# Note\n");
        Directory.CreateDirectory(Path.Combine(fixture.Docs, "archive"));
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(fixture.Docs));
        Assert.True(await fixture.Shell.MovePathAsync(note, Path.Combine(fixture.Docs, "archive")));
        var plan = Assert.IsType<RepairPlan>(fixture.Shell.LastRenameRepairPlan);
        Assert.True((await fixture.Shell.ApplyRenameRepairsAsync(plan)).Succeeded);

        foreach (var path in new[] { index, other })
        {
            Assert.Contains("archive/note.md", await File.ReadAllTextAsync(path));
            Assert.Contains(await fixture.Shell.HistoryStore.ReadAsync(path), v => v.Text == "[note](note.md)\n");
        }

        Assert.Equal(2, fixture.Shell.LinkRepairUndoCount);
        await File.WriteAllTextAsync(other, "edited elsewhere\n");
        var (restored, skipped) = await fixture.Shell.UndoLinkRepairsAsync();
        Assert.Equal((1, 1), (restored, skipped));
        Assert.Equal("[note](note.md)\n", await File.ReadAllTextAsync(index));
        Assert.Equal("edited elsewhere\n", await File.ReadAllTextAsync(other));
        var partial = Assert.Single(fixture.Shell.Activities.Items, item => item.Kind == "Undoing reviewed link repairs");
        Assert.Equal(ActivityState.Completed, partial.State);
        Assert.Equal(ActivitySeverity.Warning, partial.Severity);
        Assert.Equal("1 restored; 1 skipped.", partial.Details);
    }

    [Fact]
    public async Task Moving_a_dirty_document_keeps_its_edits_and_rewrites_crash_recovery_path()
    {
        using var fixture = new ShellFixture();
        var note = await fixture.WriteAsync("note.md", "# Note\n");
        Directory.CreateDirectory(Path.Combine(fixture.Docs, "sub"));
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(fixture.Docs));
        var tab = (await fixture.Shell.OpenFileAsync(note))!;
        tab.ApplyEdits([TextEdit.Insert(0, "edit ")]);

        Assert.True(await fixture.Shell.MovePathAsync(note, Path.Combine(fixture.Docs, "sub")));

        var moved = Path.Combine(fixture.Docs, "sub", "note.md");
        Assert.True(tab.IsDirty);
        Assert.Equal("edit # Note\n", tab.CurrentText);
        var entry = fixture.Shell.RecoveryStore.Scan().Entries.Single(e => e.Record.SessionId == tab.Session.Id);
        Assert.Equal(moved, entry.Record.OriginalPath);
    }

    [Fact]
    public async Task Renaming_a_folder_migrates_nested_files_and_repairs_links()
    {
        using var fixture = new ShellFixture();
        var index = await fixture.WriteAsync("index.md", "[a](Notes/a.md)\n");
        var a = await fixture.WriteAsync("Notes/a.md", "# A\n");
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(fixture.Docs));
        var tab = (await fixture.Shell.OpenFileAsync(a))!;
        await fixture.Shell.HistoryStore.CaptureAsync(a, "# Older\n");

        Assert.True(await fixture.Shell.RenameFolderAsync(Path.Combine(fixture.Docs, "Notes"), "Journal"), fixture.Shell.StatusMessage);

        var moved = Path.Combine(fixture.Docs, "Journal", "a.md");
        Assert.Equal(moved, tab.FilePath);
        Assert.Single(await fixture.Shell.HistoryStore.ReadAsync(moved));
        var plan = Assert.IsType<RepairPlan>(fixture.Shell.LastRenameRepairPlan);
        Assert.Contains(plan.Files, f => f.Path == index && f.ResultText.Contains("Journal/a.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Moving_or_renaming_never_overwrites_and_refuses_open_workspace_roots()
    {
        using var fixture = new ShellFixture();
        var note = await fixture.WriteAsync("note.md", "mine");
        await fixture.WriteAsync("dest/note.md", "theirs");
        await fixture.WriteAsync("inner/x.md", "x");
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(fixture.Docs));
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(Path.Combine(fixture.Docs, "inner")));

        Assert.False(await fixture.Shell.MovePathAsync(note, Path.Combine(fixture.Docs, "dest")));
        Assert.Equal("theirs", await File.ReadAllTextAsync(Path.Combine(fixture.Docs, "dest", "note.md")));
        Assert.True(File.Exists(note));
        var outside = Path.Combine(fixture.Root.Path, "other");
        Directory.CreateDirectory(outside);
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(outside));
        Assert.False(await fixture.Shell.RenameFolderAsync(outside, "renamed"), fixture.Shell.StatusMessage);
        Assert.True(Directory.Exists(outside));
        Assert.False(await fixture.Shell.RenameFolderAsync(fixture.Docs, "renamed"), fixture.Shell.StatusMessage);
        Assert.True(Directory.Exists(fixture.Docs));
    }

    [Fact]
    public async Task Duplicate_creates_a_copy_with_linked_image_permissions_but_no_history()
    {
        using var fixture = new ShellFixture();
        var note = await fixture.WriteAsync("note.md", "# Note\n");
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(fixture.Docs));
        await fixture.Shell.HistoryStore.CaptureAsync(note, "# Older\n");

        Assert.True(await fixture.Shell.DuplicatePathAsync(note), fixture.Shell.StatusMessage);

        var copy = Path.Combine(fixture.Docs, "note copy.md");
        Assert.Equal("# Note\n", await File.ReadAllTextAsync(copy));
        Assert.Empty(await fixture.Shell.HistoryStore.ReadAsync(copy));
        Assert.Single(await fixture.Shell.HistoryStore.ReadAsync(note));
        Assert.True(await fixture.Shell.DuplicatePathAsync(note));
        Assert.True(File.Exists(Path.Combine(fixture.Docs, "note copy 2.md")));
    }

    [Fact]
    public async Task Recycling_a_folder_requires_confirmation_then_clears_tabs_history_and_recent()
    {
        using var fixture = new ShellFixture();
        var a = await fixture.WriteAsync("Old/a.md", "# A\n");
        await fixture.WriteAsync("keep.md", "keep");
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(fixture.Docs));
        var tab = (await fixture.Shell.OpenFileAsync(a))!;
        await fixture.Shell.HistoryStore.CaptureAsync(a, "# Older\n");
        var recycle = new FolderRecycle(fixture.Root.File("fake-recycle"));

        Assert.False(await fixture.Shell.DeleteFolderAsync(Path.Combine(fixture.Docs, "Old"), recycle));
        Assert.Equal(0, recycle.Calls);
        Assert.True(File.Exists(a));
        Assert.Equal(ActivityState.Canceled,
            Assert.Single(fixture.Shell.Activities.Items, item => item.Kind == "Recycling Library folder").State);

        fixture.Dialogs.DeleteFolderChoice = true;
        Assert.True(await fixture.Shell.DeleteFolderAsync(Path.Combine(fixture.Docs, "Old"), recycle), fixture.Shell.StatusMessage);

        Assert.Equal(1, fixture.Dialogs.LastFolderFileCount);
        Assert.False(Directory.Exists(Path.Combine(fixture.Docs, "Old")));
        Assert.DoesNotContain(tab, fixture.Shell.Tabs);
        Assert.Empty(fixture.Shell.Recent.Files.Where(e => e.Path.Contains("Old", StringComparison.OrdinalIgnoreCase)));
        Assert.Empty(await fixture.Shell.HistoryStore.ReadAsync(a));
        Assert.True(File.Exists(Path.Combine(fixture.Docs, "keep.md")));
    }

    [Fact]
    public async Task Failed_folder_recycle_keeps_everything()
    {
        using var fixture = new ShellFixture();
        var a = await fixture.WriteAsync("Old/a.md", "# A\n");
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(fixture.Docs));
        var tab = (await fixture.Shell.OpenFileAsync(a))!;
        fixture.Dialogs.DeleteFolderChoice = true;

        Assert.False(await fixture.Shell.DeleteFolderAsync(Path.Combine(fixture.Docs, "Old"),
            new FolderRecycle(fixture.Root.File("fake-recycle")) { Failure = new IOException("no bin") }));

        Assert.Contains(tab, fixture.Shell.Tabs);
        Assert.True(File.Exists(a));
    }

    [Fact]
    public async Task Save_As_copies_view_state_and_keeps_the_original_history()
    {
        using var fixture = new ShellFixture();
        var note = await fixture.WriteAsync("note.md", "# Note\n");
        var tab = (await fixture.Shell.OpenFileAsync(note))!;
        fixture.Shell.RememberViewState(tab, 2, 9);
        await fixture.Shell.HistoryStore.CaptureAsync(note, "# Older\n");
        var copy = Path.Combine(fixture.Docs, "copy.md");
        fixture.Dialogs.SavePath = copy;

        Assert.True(await fixture.Shell.SaveTabAsAsync(tab));

        Assert.Equal(9, fixture.Shell.GetViewState(copy)?.ScrollOffset);
        Assert.Equal(9, fixture.Shell.GetViewState(note)?.ScrollOffset);
        Assert.Single(await fixture.Shell.HistoryStore.ReadAsync(note));
    }

    private sealed class FolderRecycle(string directory) : ILocalFileDeletion
    {
        public int Calls { get; private set; }
        public Exception? Failure { get; init; }

        public Task MoveToRecycleBinAsync(string path)
        {
            Calls++;
            if (Failure is { } failure) throw failure;
            Directory.CreateDirectory(directory);
            Directory.Move(path, Path.Combine(directory, Guid.NewGuid().ToString("N")));
            return Task.CompletedTask;
        }
    }
}
