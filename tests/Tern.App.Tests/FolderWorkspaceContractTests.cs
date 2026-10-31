using Tern.App.Commands;
using Tern.App.Shell;
using Tern.Workspaces;

namespace Tern.App.Tests;

public sealed class FolderWorkspaceContractTests
{
    [Fact]
    public async Task Standalone_draft_remains_local_and_renameable_without_a_saved_path()
    {
        using var fixture = new ShellFixture();
        await fixture.Shell.OpenWorkspaceAsync(fixture.Docs);
        var draft = fixture.Shell.NewDocument();
        draft.IsStandaloneDraft = true;
        Assert.False(draft.IsExternalDraft);
        Assert.True(draft.CanRename);
        Assert.True(await fixture.Shell.RenameDocumentAsync(draft, "outside.md"));
        Assert.Null(draft.FilePath);
        Assert.Equal("outside.md", draft.DisplayName);
    }

    [Fact]
    public void Partial_reset_explains_completed_categories_when_session_storage_fails()
    {
        using var fixture = new ShellFixture();
        fixture.Shell.Settings.EditorFontSize = 24;
        Directory.CreateDirectory(Path.Combine(fixture.Shell.Paths.StateDirectory, SessionStore.FileName));
        Assert.False(fixture.Shell.ResetApplication(new ResetOptions(RecentHistory: false, Preferences: true, SessionState: true)));
        Assert.Equal(new Tern.App.Settings.AppSettings().EditorFontSize, fixture.Shell.Settings.EditorFontSize);
        Assert.Contains("Could not reset session state", fixture.Shell.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("Already reset: preferences", fixture.Shell.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Implicit_directory_open_keeps_plain_folders_plain_and_recognizes_named_workspaces()
    {
        using var fixture = new ShellFixture();
        await fixture.WriteAsync("note.md", "content");
        Assert.True(await fixture.Shell.OpenDirectoryAsync(fixture.Docs));
        Assert.False(fixture.Shell.HasWorkspace);
        Assert.False(File.Exists(Path.Combine(fixture.Docs, WorkspaceDefinition.FileName)));
        Assert.True(await fixture.Shell.CloseAllWorkspacesAsync());
        var named = Path.Combine(fixture.Docs, "Company");
        Assert.True(await fixture.Shell.CreateWorkspaceAsync(named));
        Assert.True(await fixture.Shell.CloseAllWorkspacesAsync(), fixture.Shell.StatusMessage);
        fixture.Shell.ClearRecentHistory();
        Assert.True(await fixture.Shell.OpenDirectoryAsync(named));
        Assert.True(fixture.Shell.HasWorkspace);
        Assert.Equal("Company", fixture.Shell.Workspace!.Name);
    }

    [Fact]
    public async Task Plain_folder_populates_tree_without_metadata_or_workspace_search_and_restores_as_folder()
    {
        using var fixture = new ShellFixture();
        var document = await fixture.WriteAsync("Company/HR/note.md", "# People");
        Assert.True(await fixture.Shell.OpenFolderAsync(fixture.Docs));
        Assert.False(fixture.Shell.HasWorkspace);
        var folder = Assert.Single(fixture.Shell.OpenWorkspaces);
        Assert.False(folder.IsNamedWorkspace);
        Assert.Contains(folder.Snapshot.Files, file => WorkspacePaths.PathsEqual(file.FullPath, document));
        Assert.False(File.Exists(Path.Combine(fixture.Docs, WorkspaceDefinition.FileName)));
        Assert.Empty(fixture.Shell.Recent.Workspaces);
        Assert.False(DocumentCommandPolicy.CanExecute(CommandIds.FindInWorkspace, fixture.Shell.GetCommandContext()));
        Assert.True(DocumentCommandPolicy.CanExecute(CommandIds.NewFolder, fixture.Shell.GetCommandContext()));
        Assert.True(fixture.Shell.SaveSession());
        var saved = new SessionStore(fixture.Shell.Paths.StateDirectory).Load();
        Assert.Empty(saved.OpenWorkspaceRoots);
        Assert.Equal(fixture.Docs, Assert.Single(saved.OpenFolderRoots));
        Assert.True(await fixture.Shell.CloseAllWorkspacesAsync());
        await fixture.Shell.RestoreSessionAsync();
        Assert.False(Assert.Single(fixture.Shell.OpenWorkspaces).IsNamedWorkspace);
        Assert.False(fixture.Shell.HasWorkspace);
    }

    [Fact]
    public async Task Named_workspace_has_portable_identity_and_nested_workspace_reopens_without_changing_names()
    {
        using var fixture = new ShellFixture();
        var company = Path.Combine(fixture.Docs, "Company");
        var hr = Path.Combine(company, "HR");
        Assert.True(await fixture.Shell.CreateWorkspaceAsync(company));
        Assert.True(await fixture.Shell.CreateWorkspaceAsync(hr));
        Assert.Equal("Company", WorkspaceDefinition.Load(company)!.Name);
        Assert.Equal("HR", WorkspaceDefinition.Load(hr)!.Name);
        Assert.Equal(hr, fixture.Shell.Workspace!.Root);
        Assert.True(fixture.Shell.HasWorkspace);
        Assert.True(await fixture.Shell.CloseAllWorkspacesAsync());
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(company));
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(hr));
        Assert.Equal("Company", fixture.Shell.Workspace!.Name);
        Assert.Single(fixture.Shell.OpenWorkspaces);
        Assert.Equal("HR", WorkspaceDefinition.Load(hr)!.Name);
        Assert.True(Directory.Exists(company));
        Assert.True(Directory.Exists(hr));
    }

    [Fact]
    public async Task Corrupt_named_descriptor_cannot_be_bypassed_by_opening_as_a_plain_folder()
    {
        using var fixture = new ShellFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Docs, WorkspaceDefinition.FileName), "{ broken");
        Assert.False(await fixture.Shell.OpenWorkspaceAsync(fixture.Docs));
        Assert.Empty(fixture.Shell.OpenWorkspaces);
        Assert.False(await fixture.Shell.OpenFolderAsync(fixture.Docs));
        Assert.Empty(fixture.Shell.OpenWorkspaces);
        Assert.Contains("Use Open Workspace", fixture.Shell.StatusMessage);
        Assert.False(fixture.Shell.HasWorkspace);
        Assert.Equal("{ broken", await File.ReadAllTextAsync(Path.Combine(fixture.Docs, WorkspaceDefinition.FileName)));
    }

    [Fact]
    public void Empty_reset_is_rejected_and_unknown_remote_cache_content_is_preserved()
    {
        using var fixture = new ShellFixture();
        Assert.False(fixture.Shell.ResetApplication(new ResetOptions(RecentHistory: false)));
        var cache = Path.Combine(fixture.Shell.Paths.StateDirectory, "remote-cache");
        var hash = new string('a', 64);
        var managed = Path.Combine(cache, hash + "-" + new string('b', 32));
        Directory.CreateDirectory(managed);
        File.WriteAllText(Path.Combine(managed, "cached.md"), "cached");
        File.WriteAllText(Path.Combine(cache, hash + ".current"), Path.GetFileName(managed));
        var user = Path.Combine(cache, "keep-me.md");
        File.WriteAllText(user, "user content");
        Assert.True(fixture.Shell.ResetApplication(new ResetOptions(RecentHistory: false, RemoteCache: true)));
        Assert.False(Directory.Exists(managed));
        Assert.False(File.Exists(Path.Combine(cache, hash + ".current")));
        Assert.Equal("user content", File.ReadAllText(user));
    }

    [Fact]
    public async Task Search_cache_reset_requires_browsed_folders_closed_and_preserves_other_selected_categories()
    {
        using var fixture = new ShellFixture();
        var note = await fixture.WriteAsync("keep.md", "keep");
        await fixture.Shell.OpenFileAsync(note);
        await fixture.Shell.OpenFolderAsync(fixture.Docs);
        Assert.False(fixture.Shell.ResetApplication(new ResetOptions(RecentHistory: true, Caches: true)));
        Assert.Single(fixture.Shell.Recent.Files);
        Assert.Contains("Nothing was reset", fixture.Shell.StatusMessage, StringComparison.Ordinal);
        Assert.Equal("keep", await File.ReadAllTextAsync(note));
    }

    [Fact]
    public async Task Open_cached_document_blocks_remote_reset_before_other_categories_change()
    {
        using var fixture = new ShellFixture();
        var cache = Path.Combine(fixture.Shell.Paths.StateDirectory, "remote-cache", new string('a', 64));
        Directory.CreateDirectory(cache);
        var path = Path.Combine(cache, "document.md");
        await File.WriteAllTextAsync(path, "cached");
        await fixture.Shell.OpenFileAsync(path);
        Assert.False(fixture.Shell.ResetApplication(new ResetOptions(RemoteCache: true)));
        Assert.Single(fixture.Shell.Recent.Files);
        Assert.Equal("cached", await File.ReadAllTextAsync(path));
    }
}
