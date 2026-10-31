using Tern.App.Commands;
using Tern.App.Documents;
using Tern.App.Shell;
using Tern.Domain.Text;
using Tern.Workspaces;

namespace Tern.App.Tests;

public sealed class IssueLifecycleTests
{
    [Fact]
    public async Task Explicit_states_follow_document_and_operation_transitions()
    {
        using var fixture = new ShellFixture();
        Assert.Equal(DocumentState.NoDocument, fixture.Shell.ActiveDocumentState);
        var draft = fixture.Shell.NewDocument();
        Assert.Equal(DocumentState.NewEmpty, fixture.Shell.ActiveDocumentState);
        draft.ApplyEdits([TextEdit.Insert(0, "text")]);
        Assert.Equal(DocumentState.NewModified, fixture.Shell.ActiveDocumentState);
        fixture.Dialogs.SavePath = Path.Combine(fixture.Docs, "new.md");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Shell.BeforeSaveAsWrite = () => { reached.SetResult(); return release.Task; };
        var save = fixture.Shell.SaveTabAsAsync(draft);
        await reached.Task;
        Assert.Equal(DocumentState.Saving, fixture.Shell.ActiveDocumentState);
        release.SetResult();
        Assert.True(await save);
        Assert.Equal(DocumentState.SavedClean, fixture.Shell.ActiveDocumentState);
        draft.ApplyEdits([TextEdit.Delete(0, 4)]);
        Assert.Equal(DocumentState.SavedModified, fixture.Shell.ActiveDocumentState);
        draft.Session.SetReadOnly(true);
        Assert.Equal(DocumentState.ReadOnly, fixture.Shell.ActiveDocumentState);
        Assert.Equal(DocumentState.Inaccessible, DocumentStateResolver.Resolve(draft.Session, inaccessible: true));
        Assert.Equal(DocumentState.Missing, DocumentStateResolver.Resolve(draft.Session, missing: true));
        Assert.Equal(DocumentState.Loading, DocumentStateResolver.Resolve(null, DocumentOperation.Loading));
        Assert.Null(await fixture.Shell.OpenFileAsync(Path.Combine(fixture.Docs, "absent.md")));
        Assert.Equal(DocumentState.ReadOnly, fixture.Shell.ActiveDocumentState);
    }

    [Fact]
    public void Cache_reset_preserves_unknown_files_and_subfolders()
    {
        using var fixture = new ShellFixture();
        var cache = fixture.Shell.Paths.IndexDirectory;
        Directory.CreateDirectory(Path.Combine(cache, "attachments"));
        var userFile = Path.Combine(cache, "my-note.md");
        File.WriteAllText(userFile, "keep");
        var attachment = Path.Combine(cache, "attachments", "photo.png");
        File.WriteAllBytes(attachment, [1, 2]);
        var ownedCache = Path.Combine(cache, "index-0123456789abcdef.db");
        File.WriteAllText(ownedCache, "cache");
        Assert.True(fixture.Shell.ResetApplication(new ResetOptions(RecentHistory: false, Caches: true)));
        Assert.Equal("keep", File.ReadAllText(userFile));
        Assert.True(File.Exists(attachment));
        Assert.False(File.Exists(ownedCache));
    }

    [Fact]
    public async Task Case_only_rename_preserves_dirty_buffer_and_saved_history()
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("notes.md", "original");
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        await fixture.Shell.HistoryStore.CaptureAsync(path, "earlier version");
        tab.ApplyEdits([TextEdit.Insert(8, " dirty")]);
        Assert.True(await fixture.Shell.RenameDocumentAsync(tab, "Notes.md"));
        Assert.EndsWith("Notes.md", tab.FilePath, StringComparison.Ordinal);
        Assert.True(tab.IsDirty);
        Assert.Equal("original dirty", tab.CurrentText);
        Assert.Equal("original", await File.ReadAllTextAsync(tab.FilePath!));
        Assert.Equal("Notes.md", Path.GetFileName(Directory.GetFiles(fixture.Docs).Single()));
        Assert.Equal("earlier version", Assert.Single(await fixture.Shell.HistoryStore.ReadAsync(tab.FilePath!)).Text);
    }

    [Fact]
    public async Task Rename_link_plan_is_reviewed_and_does_not_overwrite_changed_open_buffers()
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("target.md", "# Target");
        var source = await fixture.WriteAsync("source.md", "[Target](target.md)\n");
        await fixture.Shell.OpenWorkspaceAsync(fixture.Docs);
        var target = (await fixture.Shell.OpenFileAsync(path))!;
        var sourceTab = (await fixture.Shell.OpenFileAsync(source))!;
        sourceTab.ApplyEdits([TextEdit.Insert(sourceTab.CurrentText.Length, "unsaved\n")]);
        Assert.True(await fixture.Shell.RenameDocumentAsync(target, "renamed.md"));
        var plan = Assert.IsType<RepairPlan>(fixture.Shell.LastRenameRepairPlan);
        Assert.True(plan.TotalEdits > 0);
        Assert.Contains("target.md", sourceTab.CurrentText, StringComparison.Ordinal);
        sourceTab.ApplyEdits([TextEdit.Insert(0, "newer edit\n")]);
        var result = await fixture.Shell.ApplyRenameRepairsAsync(plan);
        Assert.Contains(result.Files, f => f.Status == RepairStatus.Conflict);
        Assert.StartsWith("newer edit", sourceTab.CurrentText, StringComparison.Ordinal);
        Assert.Equal("[Target](target.md)\n", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task Save_policy_distinguishes_empty_draft_from_emptied_saved_document()
    {
        using var fixture = new ShellFixture();
        Assert.False(DocumentCommandPolicy.CanExecute(CommandIds.Save, fixture.Shell.GetCommandContext()));
        var draft = fixture.Shell.NewDocument();
        Assert.True(DocumentCommandPolicy.CanExecute(CommandIds.Save, fixture.Shell.GetCommandContext()));
        draft.ApplyEdits([TextEdit.Insert(0, "hello")]);
        Assert.True(DocumentCommandPolicy.CanExecute(CommandIds.Save, fixture.Shell.GetCommandContext()));
        var path = await fixture.WriteAsync("saved.md", "hello");
        var saved = (await fixture.Shell.OpenFileAsync(path))!;
        Assert.False(DocumentCommandPolicy.CanExecute(CommandIds.Save, fixture.Shell.GetCommandContext()));
        saved.ApplyEdits([TextEdit.Delete(0, 5)]);
        Assert.True(DocumentCommandPolicy.CanExecute(CommandIds.Save, fixture.Shell.GetCommandContext()));
        Assert.True(await fixture.Shell.SaveTabAsync(saved));
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Clear_history_removes_pins_persists_and_preserves_documents()
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("saved.md", "safe");
        await fixture.Shell.OpenFileAsync(path);
        fixture.Shell.Recent.SetPinned(RecentKind.File, path, true);
        fixture.Shell.ClearRecentHistory();
        Assert.Empty(fixture.Shell.Recent.Files);
        Assert.Empty(new RecentItemsStore(fixture.Shell.Paths.StateDirectory).Files);
        Assert.Single(fixture.Shell.Tabs);
        Assert.Equal("safe", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Rename_draft_does_not_create_file_and_physical_rename_preserves_unsaved_edits()
    {
        using var fixture = new ShellFixture();
        var draft = fixture.Shell.NewDocument("draft");
        Assert.True(await fixture.Shell.RenameDocumentAsync(draft, "Notes.md"));
        Assert.Null(draft.FilePath);
        Assert.Equal("Notes.md", draft.DisplayName);
        Assert.False(await fixture.Shell.RenameDocumentAsync(draft, "CON.md"));
        var path = await fixture.WriteAsync("old.md", "disk");
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(4, " edits")]);
        Assert.True(await fixture.Shell.RenameDocumentAsync(tab, "new.md"));
        Assert.True(tab.IsDirty);
        Assert.Equal("disk edits", tab.CurrentText);
        Assert.Equal("disk", await File.ReadAllTextAsync(tab.FilePath!));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Session_reset_survives_shutdown_persistence_without_losing_unsaved_work()
    {
        using var fixture = new ShellFixture();
        var draft = fixture.Shell.NewDocument("keep this");
        Assert.True(fixture.Shell.ResetApplication(new ResetOptions(SessionState: true)));
        Assert.True(fixture.Shell.SaveSession());
        Assert.Equal("keep this", draft.CurrentText);
        Assert.True(draft.IsDirty);
        Assert.Empty(new SessionStore(fixture.Shell.Paths.StateDirectory).Load().Tabs);
    }

    [Fact]
    public async Task Rename_refuses_collisions_but_allows_readonly_namespace_changes_without_reencoding()
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("old.md", "original");
        var destination = await fixture.WriteAsync("existing.md", "other");
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        Assert.False(await fixture.Shell.RenameDocumentAsync(tab, "existing.md"));
        Assert.Equal("other", await File.ReadAllTextAsync(destination));
        tab.Session.SetReadOnly(true);
        Assert.True(await fixture.Shell.RenameDocumentAsync(tab, "new.md"));
        Assert.Equal(Path.Combine(fixture.Docs, "new.md"), tab.FilePath);
        Assert.False(File.Exists(path));
        Assert.Equal("original", await File.ReadAllTextAsync(tab.FilePath!));
        Assert.True(tab.IsReadOnly);
        Assert.True(DocumentCommandPolicy.CanExecute(CommandIds.Rename, fixture.Shell.GetCommandContext()));
    }

    [Fact]
    public async Task Invalid_open_path_reports_error_without_losing_active_document()
    {
        using var fixture = new ShellFixture();
        var draft = fixture.Shell.NewDocument("keep");
        Assert.Null(await fixture.Shell.OpenFileAsync("bad\0path"));
        Assert.Same(draft, fixture.Shell.ActiveTab);
        Assert.Single(fixture.Dialogs.Messages);
    }

    [Fact]
    public async Task Save_all_allows_empty_drafts_and_saves_persisted_deletion_when_the_draft_picker_is_cancelled()
    {
        using var fixture = new ShellFixture();
        fixture.Shell.NewDocument();
        var path = await fixture.WriteAsync("saved.md", "delete");
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Delete(0, 6)]);
        Assert.Equal(1, await fixture.Shell.SaveAllAsync());
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(path));
        Assert.Equal(0, fixture.Dialogs.SavePrompts);
    }
}
