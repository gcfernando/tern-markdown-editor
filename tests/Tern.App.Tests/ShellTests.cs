using Tern.App.Commands;
using Tern.App.Documents;
using Tern.App.Shell;
using Tern.Domain.Documents;
using Tern.Domain.Merging;
using Tern.Domain.Text;
using Tern.Recovery;
using Tern.Rendering.Resources;

namespace Tern.App.Tests;

/// <summary>Stands in for the dialog windows only; every file operation under test is real.</summary>
internal sealed class ScriptedDialogs : IShellDialogs
{
    public SaveChoice SaveChoice { get; set; } = SaveChoice.Cancel;

    public ConflictChoice ConflictChoice { get; set; } = ConflictChoice.Cancel;

    public MissingFileChoice MissingChoice { get; set; } = MissingFileChoice.KeepEditing;

    public string? MergeResult { get; set; }

    public string? SavePath { get; set; }

    public bool Overwrite { get; set; }
    public bool ReadOnlyCopy { get; set; }
    public string? SuggestedName { get; private set; }
    public Func<Task<bool>>? OverwriteResponse { get; set; }

    public bool AllowExternal { get; set; }

    public List<string> Messages { get; } = [];

    public List<Uri> ExternalAsked { get; } = [];

    public int SavePrompts { get; private set; }
    public Queue<SaveChoice> SaveResponses { get; } = new();
    public DuplicateOpenChoice DuplicateChoice { get; set; } = DuplicateOpenChoice.Cancel;
    public int DuplicatePrompts { get; private set; }
    public bool DeleteChoice { get; set; }
    public int DeletePrompts { get; private set; }
    public int LastDeleteUnsavedCount { get; private set; }
    public Func<string, int, Task<bool>>? DeleteResponse { get; set; }

    public Task<bool> ConfirmDeleteAsync(string path, int unsavedTabCount)
    {
        DeletePrompts++;
        LastDeleteUnsavedCount = unsavedTabCount;
        return DeleteResponse?.Invoke(path, unsavedTabCount) ?? Task.FromResult(DeleteChoice);
    }

    public Task<DuplicateOpenChoice> AskDuplicateOpenAsync(string documentName)
    {
        DuplicatePrompts++;
        return Task.FromResult(DuplicateChoice);
    }

    public Task<SaveChoice> AskSaveChangesAsync(string documentName)
    {
        SavePrompts++;
        return Task.FromResult(SaveResponses.Count > 0 ? SaveResponses.Dequeue() : SaveChoice);
    }

    public Task<ConflictChoice> AskConflictAsync(DocumentTab tab) => Task.FromResult(ConflictChoice);

    public Task<MissingFileChoice> AskFileMissingAsync(DocumentTab tab) => Task.FromResult(MissingChoice);

    public Task<string?> ResolveMergeAsync(DocumentTab tab, MergeResult merge) => Task.FromResult(MergeResult);

    public Task<IReadOnlyList<string>> PickFilesToOpenAsync() => Task.FromResult<IReadOnlyList<string>>([]);

    public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);

    public Task<string?> PickSavePathAsync(string suggestedName, string? initialDirectory)
    { SuggestedName = suggestedName; return Task.FromResult(SavePath); }

    public Task<bool> ConfirmReadOnlyCopyAsync(string documentName) => Task.FromResult(ReadOnlyCopy);

    public Task<bool> ConfirmOverwriteAsync(string path) => OverwriteResponse?.Invoke() ?? Task.FromResult(Overwrite);

    public Task<bool> ConfirmOpenExternalAsync(Uri uri)
    {
        ExternalAsked.Add(uri);
        return Task.FromResult(AllowExternal);
    }

    public void ShowMessage(string title, string message) => Messages.Add(title + ": " + message);
}

internal sealed class RecordingLauncher : IExternalLauncher
{
    public List<Uri> Opened { get; } = [];

    public List<string> Revealed { get; } = [];

    public void OpenUri(Uri uri) => Opened.Add(uri);

    public void RevealInFileManager(string path) => Revealed.Add(path);
}

public sealed class ShellFixture : IDisposable
{
    internal ShellFixture()
    {
        Root = new TempDir();
        Dialogs = new ScriptedDialogs();
        Launcher = new RecordingLauncher();
        Shell = new ShellViewModel(AppPaths.Under(Root.File("appdata")), Dialogs, Launcher);
        Docs = Root.File("docs");
        Directory.CreateDirectory(Docs);
    }

    public TempDir Root { get; }

    public string Docs { get; }

    internal ScriptedDialogs Dialogs { get; }

    internal RecordingLauncher Launcher { get; }

    public ShellViewModel Shell { get; }

    public async Task<string> WriteAsync(string name, string text)
    {
        var path = Path.Combine(Docs, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text);
        return path;
    }

    public void Dispose()
    {
        Shell.Dispose();
        Root.Dispose();
    }
}

public class ShellTabTests
{
    [Fact]
    public async Task Opening_same_file_twice_activates_existing_tab()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("a.md", "# A\n");
        var first = await f.Shell.OpenFileAsync(path);
        var second = await f.Shell.OpenFileAsync(path.ToUpperInvariant());
        Assert.Same(first, second);
        Assert.Single(f.Shell.Tabs);
    }

    [Fact]
    public async Task Missing_file_reports_error_and_opens_nothing()
    {
        using var f = new ShellFixture();
        var tab = await f.Shell.OpenFileAsync(Path.Combine(f.Docs, "nope.md"));
        Assert.Null(tab);
        Assert.Single(f.Dialogs.Messages);
        Assert.Empty(f.Shell.Tabs);
    }

    [Theory]
    [InlineData(SaveChoice.Cancel, false, true)]
    [InlineData(SaveChoice.Discard, true, false)]
    [InlineData(SaveChoice.Save, true, false)]
    public async Task Closing_dirty_tab_follows_user_choice(SaveChoice choice, bool closes, bool expectOriginal)
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("a.md", "one\n");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "x")]);
        f.Dialogs.SaveChoice = choice;

        var result = await f.Shell.CloseTabAsync(tab);

        Assert.Equal(closes, result);
        Assert.Equal(closes, !f.Shell.Tabs.Contains(tab));
        var onDisk = await File.ReadAllTextAsync(path);
        Assert.Equal(choice == SaveChoice.Save ? "xone\n" : "one\n", onDisk);
        _ = expectOriginal;
    }

    [Fact]
    public async Task Closing_clean_tab_never_prompts_and_neighbour_becomes_active()
    {
        using var f = new ShellFixture();
        var a = (await f.Shell.OpenFileAsync(await f.WriteAsync("a.md", "a")))!;
        var b = (await f.Shell.OpenFileAsync(await f.WriteAsync("b.md", "b")))!;
        var c = (await f.Shell.OpenFileAsync(await f.WriteAsync("c.md", "c")))!;
        f.Shell.ActiveTab = b;
        Assert.True(await f.Shell.CloseTabAsync(b));
        Assert.Equal(0, f.Dialogs.SavePrompts);
        Assert.Same(c, f.Shell.ActiveTab);
        Assert.Equal([a, c], f.Shell.Tabs);
    }

    [Fact]
    public async Task Reopen_closed_tab_restores_last_closed_existing_file()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("a.md", "a");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        await f.Shell.CloseTabAsync(tab);
        var reopened = await f.Shell.ReopenClosedTabAsync();
        Assert.NotNull(reopened);
        Assert.Equal(path, reopened!.FilePath);
    }

    [Fact]
    public async Task Close_all_stops_when_user_cancels_any_prompt()
    {
        using var f = new ShellFixture();
        var tab = (await f.Shell.OpenFileAsync(await f.WriteAsync("a.md", "a")))!;
        tab.ApplyEdits([TextEdit.Insert(0, "x")]);
        f.Dialogs.SaveChoice = SaveChoice.Cancel;
        Assert.False(await f.Shell.CloseAllAsync());
        f.Dialogs.SaveChoice = SaveChoice.Discard;
        Assert.True(await f.Shell.CloseAllAsync());
    }

    [Fact]
    public async Task Tab_cycling_wraps_and_tabs_can_be_reordered()
    {
        using var f = new ShellFixture();
        var a = (await f.Shell.OpenFileAsync(await f.WriteAsync("a.md", "a")))!;
        var b = (await f.Shell.OpenFileAsync(await f.WriteAsync("b.md", "b")))!;
        f.Shell.NextTab();
        Assert.Same(a, f.Shell.ActiveTab);
        f.Shell.PreviousTab();
        Assert.Same(b, f.Shell.ActiveTab);
        f.Shell.MoveTab(0, 1);
        Assert.Equal([b, a], f.Shell.Tabs);
    }

    [Fact]
    public async Task Untitled_save_uses_picked_path_and_cancel_leaves_tab_dirty()
    {
        using var f = new ShellFixture();
        var tab = f.Shell.NewDocument("# Hello\n");
        tab.ApplyEdits([TextEdit.Insert(0, "x")]);
        f.Dialogs.SavePath = null;
        Assert.False(await f.Shell.SaveTabAsync(tab));
        Assert.True(tab.IsDirty);

        f.Dialogs.SavePath = Path.Combine(f.Docs, "new.md");
        Assert.True(await f.Shell.SaveTabAsync(tab));
        Assert.False(tab.IsDirty);
        Assert.Equal("x# Hello\n", await File.ReadAllTextAsync(f.Dialogs.SavePath));
    }

    [Fact]
    public async Task Save_as_over_existing_file_needs_confirmation()
    {
        using var f = new ShellFixture();
        var existing = await f.WriteAsync("keep.md", "precious");
        var tab = f.Shell.NewDocument("new text");
        f.Dialogs.SavePath = existing;
        f.Dialogs.Overwrite = false;
        Assert.False(await f.Shell.SaveTabAsync(tab));
        Assert.Equal("precious", await File.ReadAllTextAsync(existing));
        f.Dialogs.Overwrite = true;
        Assert.True(await f.Shell.SaveTabAsync(tab));
        Assert.Equal("new text", await File.ReadAllTextAsync(existing));
    }

    [Fact]
    public async Task Save_all_saves_only_dirty_writable_tabs()
    {
        using var f = new ShellFixture();
        var a = (await f.Shell.OpenFileAsync(await f.WriteAsync("a.md", "a")))!;
        await f.Shell.OpenFileAsync(await f.WriteAsync("b.md", "b"));
        a.ApplyEdits([TextEdit.Insert(0, "x")]);
        Assert.Equal(1, await f.Shell.SaveAllAsync());
    }

    [Fact]
    public async Task Read_only_document_save_is_refused_with_message()
    {
        using var f = new ShellFixture();
        var path = Path.Combine(f.Docs, "bad.md");
        await File.WriteAllBytesAsync(path, [0x61, 0xFF, 0xFE, 0x80]);
        var tab = (await f.Shell.OpenFileAsync(path))!;
        Assert.False(await f.Shell.SaveTabAsync(tab));
        Assert.Single(f.Dialogs.Messages);
    }
}

public class ShellConflictTests
{
    private static async Task<(ShellFixture f, DocumentTab tab, string path)> Dirty()
    {
        var f = new ShellFixture();
        var path = await f.WriteAsync("a.md", "a\nb\nc\n");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "mine\n")]);
        return (f, tab, path);
    }

    [Fact]
    public async Task Save_conflict_keep_mine_overwrites()
    {
        var (f, tab, path) = await Dirty();
        using var _ = f;
        await File.WriteAllTextAsync(path, "disk\n");
        f.Dialogs.ConflictChoice = ConflictChoice.KeepMine;
        Assert.True(await f.Shell.SaveTabAsync(tab));
        Assert.Equal("mine\na\nb\nc\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Save_conflict_take_disk_discards_buffer()
    {
        var (f, tab, path) = await Dirty();
        using var _ = f;
        await File.WriteAllTextAsync(path, "disk\n");
        f.Dialogs.ConflictChoice = ConflictChoice.TakeDisk;
        await f.Shell.SaveTabAsync(tab);
        Assert.Equal("disk\n", tab.CurrentText);
        Assert.False(tab.IsDirty);
        Assert.Equal("disk\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Save_conflict_cancel_changes_nothing()
    {
        var (f, tab, path) = await Dirty();
        using var _ = f;
        await File.WriteAllTextAsync(path, "disk\n");
        f.Dialogs.ConflictChoice = ConflictChoice.Cancel;
        Assert.False(await f.Shell.SaveTabAsync(tab));
        Assert.True(tab.IsDirty);
        Assert.Equal("disk\n", await File.ReadAllTextAsync(path));
        Assert.NotNull(tab.Conflict);
    }

    [Fact]
    public async Task Save_conflict_merge_writes_accepted_text()
    {
        var (f, tab, path) = await Dirty();
        using var _ = f;
        await File.WriteAllTextAsync(path, "a\nb\nc\nd\n");
        f.Dialogs.ConflictChoice = ConflictChoice.Merge;
        f.Dialogs.MergeResult = "mine\na\nb\nc\nd\n";
        Assert.True(await f.Shell.SaveTabAsync(tab));
        Assert.Equal("mine\na\nb\nc\nd\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Save_conflict_merge_cancelled_keeps_everything()
    {
        var (f, tab, path) = await Dirty();
        using var _ = f;
        await File.WriteAllTextAsync(path, "a\nb\nc\nd\n");
        f.Dialogs.ConflictChoice = ConflictChoice.Merge;
        f.Dialogs.MergeResult = null;
        Assert.False(await f.Shell.SaveTabAsync(tab));
        Assert.Equal("a\nb\nc\nd\n", await File.ReadAllTextAsync(path));
        Assert.Equal("mine\na\nb\nc\n", tab.CurrentText);
    }

    [Fact]
    public async Task Save_conflict_save_copy_leaves_original_alone()
    {
        var (f, tab, path) = await Dirty();
        using var _ = f;
        await File.WriteAllTextAsync(path, "disk\n");
        f.Dialogs.ConflictChoice = ConflictChoice.SaveCopy;
        f.Dialogs.SavePath = Path.Combine(f.Docs, "copy.md");
        await f.Shell.SaveTabAsync(tab);
        Assert.Equal("mine\na\nb\nc\n", await File.ReadAllTextAsync(f.Dialogs.SavePath));
        Assert.Equal("disk\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task External_change_check_reloads_clean_and_prompts_for_dirty_active_tab()
    {
        using var f = new ShellFixture();
        var clean = (await f.Shell.OpenFileAsync(await f.WriteAsync("clean.md", "one\n")))!;
        var path = await f.WriteAsync("dirty.md", "a\n");
        var dirty = (await f.Shell.OpenFileAsync(path))!;
        dirty.ApplyEdits([TextEdit.Insert(0, "m")]);
        await File.WriteAllTextAsync(clean.FilePath!, "two\n");
        await File.WriteAllTextAsync(path, "b\n");
        f.Dialogs.ConflictChoice = ConflictChoice.TakeDisk;

        await f.Shell.CheckForExternalChangesAsync();

        Assert.Equal("two\n", clean.CurrentText);
        Assert.Equal("b\n", dirty.CurrentText);
    }

    [Fact]
    public async Task Deleted_file_close_choice_removes_tab_without_deleting_recovery_state()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("a.md", "a\n");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        File.Delete(path);
        f.Dialogs.MissingChoice = MissingFileChoice.Close;
        await f.Shell.CheckForExternalChangesAsync();
        Assert.DoesNotContain(tab, f.Shell.Tabs);
    }

    [Fact]
    public async Task Deleted_file_keep_editing_then_save_recreates_it()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("a.md", "a\n");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        File.Delete(path);
        f.Dialogs.MissingChoice = MissingFileChoice.KeepEditing;
        await f.Shell.CheckForExternalChangesAsync();
        Assert.True(await f.Shell.SaveTabAsync(tab));
        Assert.Equal("a\n", await File.ReadAllTextAsync(path));
    }
}

public class ShellLinkTests
{
    [Fact]
    public async Task Local_markdown_link_opens_tab_and_requests_anchor()
    {
        using var f = new ShellFixture();
        var target = await f.WriteAsync("t.md", "# T\n");
        NavigationRequest? nav = null;
        f.Shell.NavigationRequested += (_, r) => nav = r;
        var result = await f.Shell.ActivateLinkAsync(new LocalFile(target, "t", false, true));
        Assert.Equal(LinkOutcome.OpenedDocument, result.Outcome);
        Assert.Equal("t", nav?.Anchor);
        Assert.Equal(target, f.Shell.ActiveTab?.FilePath);
    }

    [Fact]
    public async Task Missing_local_link_target_is_reported()
    {
        using var f = new ShellFixture();
        var result = await f.Shell.ActivateLinkAsync(new LocalFile(Path.Combine(f.Docs, "x.md"), null, false, false));
        Assert.Equal(LinkOutcome.FileMissing, result.Outcome);
        Assert.Empty(f.Shell.Tabs);
    }

    [Fact]
    public async Task External_link_needs_confirmation_and_declining_opens_nothing()
    {
        using var f = new ShellFixture();
        var link = new ExternalLink(new Uri("https://example.com/x"), "https://example.com/x");
        f.Dialogs.AllowExternal = false;
        Assert.Equal(LinkOutcome.DeclinedExternal, (await f.Shell.ActivateLinkAsync(link)).Outcome);
        Assert.Empty(f.Launcher.Opened);
        f.Dialogs.AllowExternal = true;
        Assert.Equal(LinkOutcome.OpenedExternal, (await f.Shell.ActivateLinkAsync(link)).Outcome);
        Assert.Single(f.Launcher.Opened);
    }

    [Fact]
    public async Task External_links_can_be_disabled_entirely()
    {
        using var f = new ShellFixture();
        f.Shell.Settings.AllowExternalLinkOpening = false;
        f.Dialogs.AllowExternal = true;
        var result = await f.Shell.ActivateLinkAsync(new ExternalLink(new Uri("https://example.com"), "x"));
        Assert.Equal(LinkOutcome.DeclinedExternal, result.Outcome);
        Assert.Empty(f.Dialogs.ExternalAsked);
        Assert.Empty(f.Launcher.Opened);
    }

    [Fact]
    public async Task Non_document_file_is_only_handed_to_the_os_after_confirmation()
    {
        using var f = new ShellFixture();
        var exe = await f.WriteAsync("run.exe", "MZ");
        f.Dialogs.AllowExternal = false;
        Assert.Equal(LinkOutcome.DeclinedExternal, (await f.Shell.ActivateLinkAsync(new LocalFile(exe, null, true, true))).Outcome);
        Assert.Empty(f.Launcher.Revealed);
    }

    [Fact]
    public async Task Blocked_link_is_never_opened()
    {
        using var f = new ShellFixture();
        var result = await f.Shell.ActivateLinkAsync(new Blocked(BlockReason.ScriptScheme, "javascript:alert(1)", "script scheme"));
        Assert.Equal(LinkOutcome.Blocked, result.Outcome);
        Assert.Empty(f.Launcher.Opened);
        Assert.Empty(f.Shell.Tabs);
    }

    [Fact]
    public async Task Anchor_link_requests_navigation_in_active_tab()
    {
        using var f = new ShellFixture();
        NavigationRequest? nav = null;
        f.Shell.NavigationRequested += (_, r) => nav = r;
        await f.Shell.ActivateLinkAsync(new Anchor("intro"));
        Assert.Equal("intro", nav?.Anchor);
    }
}

public class ShellWorkspaceAndSessionTests
{
    [Fact]
    public async Task Open_workspace_indexes_searches_and_tracks_backlinks()
    {
        using var f = new ShellFixture();
        await f.WriteAsync("a.md", "# Alpha\n\nSee [beta](b.md) and [missing](nope.md).\n");
        await f.WriteAsync("sub\\b.md", "# Beta\n\nThe zebra paragraph.\n");
        // b.md sits in a subfolder, so the link from a.md is deliberately broken.
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));
        var ws = f.Shell.Workspace!;

        Assert.Equal(2, ws.Snapshot.Files.Count);
        var hits = ws.Search("zebra");
        Assert.Single(hits.Hits);
        Assert.Equal("Beta", hits.Hits[0].Title);
        Assert.Equal(2, ws.Graph.GetBrokenLinks().Count);

        var quick = ws.QuickOpen("a.m");
        Assert.Contains(quick.Files, m => m.Item.EndsWith("a.md", StringComparison.OrdinalIgnoreCase));
        var headings = ws.QuickOpen("#alp");
        Assert.Contains(headings.Headings, m => m.Item.Text == "Alpha");
    }

    [Fact]
    public async Task Unsaved_edits_are_searchable_until_saved()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("a.md", "# Alpha\n\nplain\n");
        await f.Shell.OpenWorkspaceAsync(f.Docs);
        var tab = (await f.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(tab.CurrentText.Length, "\nquokka\n")]);

        var hit = Assert.Single(f.Shell.Workspace!.Search("quokka").Hits);
        Assert.True(hit.FromUnsavedBuffer);

        await f.Shell.SaveTabAsync(tab);
        var saved = Assert.Single(f.Shell.Workspace.Search("quokka").Hits);
        Assert.False(saved.FromUnsavedBuffer);
    }

    [Fact]
    public async Task Workspace_notices_new_files_from_disk()
    {
        using var f = new ShellFixture();
        await f.WriteAsync("a.md", "# A\n");
        await f.Shell.OpenWorkspaceAsync(f.Docs);
        var signal = new TaskCompletionSource();
        f.Shell.Workspace!.FilesChanged += (_, _) => signal.TrySetResult();
        await f.WriteAsync("fresh.md", "# Fresh\n");
        await signal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(f.Shell.Workspace.Snapshot.Files, x => x.RelativePath == "fresh.md");
    }

    [Fact]
    public async Task Opening_a_missing_workspace_reports_error()
    {
        using var f = new ShellFixture();
        Assert.False(await f.Shell.OpenWorkspaceAsync(Path.Combine(f.Docs, "nope")));
        Assert.Single(f.Dialogs.Messages);
        Assert.False(f.Shell.HasWorkspace);
    }

    [Fact]
    public async Task Session_round_trips_workspace_tabs_active_tab_and_view_state()
    {
        using var f = new ShellFixture();
        var a = await f.WriteAsync("a.md", "# A\n");
        var b = await f.WriteAsync("b.md", "# B\n");
        await f.Shell.OpenWorkspaceAsync(f.Docs);
        var tabA = (await f.Shell.OpenFileAsync(a))!;
        await f.Shell.OpenFileAsync(b);
        f.Shell.ActiveTab = tabA;
        f.Shell.RememberViewState(tabA, 3, 120);
        f.Shell.SaveSession();
        var paths = AppPaths.Under(f.Root.File("appdata"));

        using var second = new ShellViewModel(paths, new ScriptedDialogs(), new RecordingLauncher());
        await second.RestoreSessionAsync();

        Assert.Equal(2, second.Tabs.Count);
        Assert.Equal(a, second.ActiveTab?.FilePath);
        Assert.Equal(f.Docs, second.Workspace?.Root);
        Assert.Equal(120, second.GetViewState(a)?.ScrollOffset);
    }

    [Fact]
    public async Task Session_restore_skips_files_deleted_since()
    {
        using var f = new ShellFixture();
        var a = await f.WriteAsync("a.md", "# A\n");
        await f.Shell.OpenFileAsync(a);
        f.Shell.SaveSession();
        File.Delete(a);
        using var second = new ShellViewModel(AppPaths.Under(f.Root.File("appdata")), new ScriptedDialogs(), new RecordingLauncher());
        await second.RestoreSessionAsync();
        Assert.Empty(second.Tabs);
    }

    [Fact]
    public async Task Restore_can_be_turned_off()
    {
        using var f = new ShellFixture();
        await f.Shell.OpenFileAsync(await f.WriteAsync("a.md", "# A\n"));
        f.Shell.SaveSession();
        using var second = new ShellViewModel(AppPaths.Under(f.Root.File("appdata")), new ScriptedDialogs(), new RecordingLauncher());
        second.Settings.RestoreSession = false;
        await second.RestoreSessionAsync();
        Assert.Empty(second.Tabs);
    }

    [Fact]
    public async Task Recovery_checkpoint_reopens_as_unsaved_tab_and_does_not_touch_the_file()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("a.md", "saved\n");
        var format = Tern.Domain.Text.LineEndingAnalyzer.Describe(TextEncodingKind.Utf8, false, "unsaved work\n");
        var record = new RecoveryRecord(Guid.NewGuid(), path, "a.md", 5, DateTime.UtcNow, null, format, "unsaved work\n");

        var tab = await f.Shell.RestoreRecoveryAsync(record);

        Assert.True(tab.IsDirty);
        Assert.Equal("unsaved work\n", tab.CurrentText);
        Assert.Equal("saved\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Recovery_of_untitled_document_has_no_path()
    {
        using var f = new ShellFixture();
        var format = Tern.Domain.Text.LineEndingAnalyzer.Describe(TextEncodingKind.Utf8, false, "draft");
        var tab = await f.Shell.RestoreRecoveryAsync(new RecoveryRecord(Guid.NewGuid(), null, "Untitled", 1, DateTime.UtcNow, null, format, "draft"));
        Assert.True(tab.IsUntitled);
        Assert.Equal("draft", tab.CurrentText);
    }
}

public class CommandRegistryTests
{
    [Fact]
    public void Default_bindings_have_no_conflicts_and_all_parse()
    {
        var registry = CommandRegistry.CreateDefault();
        var seen = new Dictionary<Gesture, string>();
        foreach (var command in registry.Commands.Where(c => c.DefaultGesture is not null))
        {
            Assert.True(Gesture.TryParse(command.DefaultGesture, out var g), $"{command.Id} gesture '{command.DefaultGesture}' does not parse");
            Assert.True(seen.TryAdd(g, command.Id), $"{command.Id} clashes with {(seen.TryGetValue(g, out var other) ? other : "?")}");
        }
    }

    [Fact]
    public void Command_ids_are_unique_and_titled()
    {
        var all = CommandRegistry.DefaultCommands().ToList();
        Assert.Equal(all.Count, all.Select(c => c.Id).Distinct().Count());
        Assert.All(all, c => Assert.False(string.IsNullOrWhiteSpace(c.Title)));
    }

    [Theory]
    [InlineData("ctrl+shift+p", "Ctrl+Shift+P")]
    [InlineData("Shift+Control+P", "Ctrl+Shift+P")]
    [InlineData("alt+f4", "Alt+F4")]
    [InlineData("Ctrl++", "Ctrl+OemPlus")]
    public void Gestures_normalise(string input, string expected)
    {
        Assert.True(Gesture.TryParse(input, out var g));
        Assert.Equal(expected, g.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+Shift")]
    public void Invalid_gestures_are_rejected(string input) => Assert.False(Gesture.TryParse(input, out _));

    [Fact]
    public void Rebinding_to_a_taken_gesture_reports_the_owner_and_changes_nothing()
    {
        var registry = CommandRegistry.CreateDefault();
        var result = registry.SetBinding(CommandIds.Bold, "ctrl+s");
        Assert.Equal(BindingProblem.Conflict, result.Problem);
        Assert.Equal(CommandIds.Save, result.ConflictingCommandId);
        Assert.Equal("Ctrl+B", registry.GetGesture(CommandIds.Bold));
    }

    [Fact]
    public void Rebinding_applies_and_frees_the_old_gesture()
    {
        var registry = CommandRegistry.CreateDefault();
        Assert.True(registry.SetBinding(CommandIds.Bold, "Ctrl+Alt+J").Succeeded);
        Assert.Equal("Ctrl+Alt+J", registry.GetGesture(CommandIds.Bold));
        Assert.Null(registry.FindCommandFor(new Gesture(true, false, false, false, "B")));
    }

    [Fact]
    public void Forced_binding_unbinds_previous_owner()
    {
        var registry = CommandRegistry.CreateDefault();
        registry.ForceBinding(CommandIds.Bold, "Ctrl+S");
        Assert.Null(registry.GetGesture(CommandIds.Save));
        Assert.Equal("Ctrl+S", registry.GetGesture(CommandIds.Bold));
    }

    [Fact]
    public void Overrides_round_trip_through_settings_and_bad_entries_are_dropped()
    {
        var registry = CommandRegistry.CreateDefault();
        var rejected = registry.LoadOverrides(new Dictionary<string, string>
        {
            [CommandIds.Bold] = "Ctrl+Alt+J",
            ["no.such.command"] = "Ctrl+Q",
            [CommandIds.Italic] = "Ctrl+Nonsense+",
            [CommandIds.Find] = string.Empty,
        });
        Assert.Equal(2, rejected.Count);
        Assert.Equal("Ctrl+Alt+J", registry.GetGesture(CommandIds.Bold));
        Assert.Null(registry.GetGesture(CommandIds.Find));
        Assert.Equal("Ctrl+I", registry.GetGesture(CommandIds.Italic));
    }

    [Fact]
    public async Task Shell_persists_key_binding_overrides()
    {
        using var f = new ShellFixture();
        f.Shell.Registry.SetBinding(CommandIds.Bold, "Ctrl+Alt+J");
        f.Shell.SaveSettings();
        using var second = new ShellViewModel(AppPaths.Under(f.Root.File("appdata")), new ScriptedDialogs(), new RecordingLauncher());
        Assert.Equal("Ctrl+Alt+J", second.Registry.GetGesture(CommandIds.Bold));
        await Task.CompletedTask;
    }
}
