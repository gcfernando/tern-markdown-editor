using Tern.App.Shell;
using Tern.Domain.Text;
using Tern.Workspaces;

namespace Tern.App.Tests;

public sealed class MultipleWorkspaceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleared_recent_history_stays_empty_across_restart_until_an_explicit_open(bool explicitlyOpenFile)
    {
        using var fixture = new ShellFixture();
        var workspacePath = await fixture.WriteAsync("workspace-note.md", "workspace text");
        var standalonePath = fixture.Root.File("standalone.md");
        await File.WriteAllTextAsync(standalonePath, "standalone text");
        await fixture.Shell.OpenWorkspaceAsync(fixture.Docs);
        await fixture.Shell.OpenFileAsync(workspacePath);
        await fixture.Shell.OpenFileAsync(standalonePath);
        Assert.Equal(2, fixture.Shell.Recent.Files.Count);
        Assert.Single(fixture.Shell.Recent.Workspaces);
        fixture.Shell.ClearRecentHistory();
        Assert.True(fixture.Shell.SaveSession());
        fixture.Shell.Dispose();

        using var restarted = new ShellViewModel(fixture.Shell.Paths, fixture.Dialogs, new RecordingLauncher());
        await restarted.RestoreSessionAsync();
        Assert.Equal(new[] { workspacePath, standalonePath }, restarted.Tabs.Select(t => t.FilePath));
        Assert.Equal(standalonePath, restarted.ActiveTab!.FilePath);
        Assert.Equal(fixture.Docs, Assert.Single(restarted.OpenWorkspaces).Root);
        Assert.Empty(restarted.Recent.Files);
        Assert.Empty(restarted.Recent.Workspaces);
        var persisted = new RecentItemsStore(restarted.Paths.StateDirectory);
        Assert.Empty(persisted.Files);
        Assert.Empty(persisted.Workspaces);

        if (explicitlyOpenFile)
        {
            await restarted.OpenFileAsync(workspacePath);
            Assert.Equal(workspacePath, Assert.Single(restarted.Recent.Files).Path);
            Assert.Empty(restarted.Recent.Workspaces);
        }
        else
        {
            await restarted.OpenWorkspaceAsync(fixture.Docs + Path.DirectorySeparatorChar);
            Assert.Empty(restarted.Recent.Files);
            Assert.Equal(fixture.Docs, Assert.Single(restarted.Recent.Workspaces).Path);
        }
        Assert.Equal(2, restarted.Tabs.Count);
        Assert.Single(restarted.OpenWorkspaces);
        Assert.Equal("workspace text", await File.ReadAllTextAsync(workspacePath));
        Assert.Equal("standalone text", await File.ReadAllTextAsync(standalonePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Malformed_session_paths_are_ignored_individually_and_valid_documents_still_restore(bool invalidActiveTab)
    {
        using var fixture = new ShellFixture();
        var valid = await fixture.WriteAsync("preserved.md", "safe content");
        var invalidPaths = new List<string> { "\0invalid.md" };
        if (OperatingSystem.IsWindows()) invalidPaths.Add(Path.Combine(fixture.Docs, "invalid|name.md"));
        var stateFile = Path.Combine(fixture.Shell.Paths.StateDirectory, SessionStore.FileName);
        Directory.CreateDirectory(fixture.Shell.Paths.StateDirectory);
        var store = new SessionStore(fixture.Shell.Paths.StateDirectory);
        foreach (var invalid in invalidPaths)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                version = 1,
                lastWorkspace = invalid,
                openWorkspaces = new[] { invalid, fixture.Docs, fixture.Docs + Path.DirectorySeparatorChar },
                activeTab = invalidActiveTab ? invalid : valid,
                tabs = new[] { new { path = invalid, caret = 0, scroll = 0 }, new { path = valid, caret = 7, scroll = 12 }, new { path = invalid + "more", caret = 0, scroll = 0 } },
                fileViews = new[] { new { path = invalid, caret = 0, scroll = 0 }, new { path = valid, caret = 7, scroll = 12 } },
            });
            await File.WriteAllTextAsync(stateFile, json);
            var loaded = store.Load();
            Assert.Equal(valid, Assert.Single(loaded.Tabs).Path);
            Assert.Equal(invalidActiveTab ? null : valid, loaded.ActiveTab);
            Assert.Equal(valid, Assert.Single(loaded.FileViews).Key);
            Assert.Equal(new FileViewState(7, 12), loaded.FileViews[valid]);
            Assert.Null(loaded.LastWorkspace);
            Assert.Equal(WorkspacePaths.Normalize(fixture.Docs), Assert.Single(loaded.OpenWorkspaceRoots));
            Assert.Equal(json, await File.ReadAllTextAsync(stateFile));
        }
        await fixture.Shell.RestoreSessionAsync();
        Assert.Equal(valid, Assert.Single(fixture.Shell.Tabs).FilePath);
        Assert.Equal("safe content", fixture.Shell.ActiveTab!.CurrentText);
        Assert.Single(fixture.Shell.OpenWorkspaces);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Root_spelling_with_or_without_trailing_separator_reuses_session_and_preserves_tabs(bool trailingFirst)
    {
        using var fixture = new ShellFixture();
        var root = WorkspacePaths.Normalize(fixture.Docs);
        var trailing = root + Path.DirectorySeparatorChar;
        var firstSpelling = trailingFirst ? trailing : root;
        var secondSpelling = trailingFirst ? root : trailing;
        var path = await fixture.WriteAsync("retained.md", "original");
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(firstSpelling));
        var workspace = fixture.Shell.Workspace!;
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "unsaved ")]);
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(secondSpelling));
        if (OperatingSystem.IsWindows()) Assert.True(await fixture.Shell.OpenWorkspaceAsync(trailing.ToUpperInvariant()));
        Assert.Same(workspace, Assert.Single(fixture.Shell.OpenWorkspaces));
        Assert.Same(workspace, fixture.Shell.Workspace);
        Assert.Equal(root, workspace.Root);
        Assert.Same(tab, Assert.Single(fixture.Shell.Tabs));
        Assert.True(tab.IsDirty);
        Assert.Equal("unsaved original", tab.CurrentText);
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Equal(WorkspaceSession.IndexFileName(root), WorkspaceSession.IndexFileName(trailing));
        Assert.Single(Directory.EnumerateFiles(fixture.Shell.Paths.IndexDirectory, "index-*.db"));
        Assert.True(fixture.Shell.SaveSession());
        Assert.Equal(root, Assert.Single(new SessionStore(fixture.Shell.Paths.StateDirectory).Load().OpenWorkspaceRoots));
    }

    [Fact]
    public async Task Restoring_legacy_duplicate_workspace_spellings_creates_one_session_and_canonical_active_root()
    {
        using var fixture = new ShellFixture();
        var root = WorkspacePaths.Normalize(fixture.Docs);
        var trailing = root + Path.DirectorySeparatorChar;
        var snapshot = new SessionSnapshot(trailing, [], null, null, new Dictionary<string, FileViewState>())
        { OpenWorkspaceRoots = [trailing, root, Path.Combine(root, ".")] };
        var store = new SessionStore(fixture.Shell.Paths.StateDirectory);
        Assert.True(store.Save(snapshot));
        Assert.Equal(root, Assert.Single(store.Load().OpenWorkspaceRoots));
        Assert.True(WorkspacePaths.PathsEqual(root, store.Load().LastWorkspace!));
        await fixture.Shell.RestoreSessionAsync();
        Assert.Equal(root, Assert.Single(fixture.Shell.OpenWorkspaces).Root);
        Assert.Equal(root, fixture.Shell.Workspace!.Root);
        Assert.Single(Directory.EnumerateFiles(fixture.Shell.Paths.IndexDirectory, "index-*.db"));
    }

    [Fact]
    public async Task Close_all_preserves_dirty_read_only_buffer_when_user_cancels()
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("readonly.md", "original");
        await fixture.Shell.OpenWorkspaceAsync(fixture.Docs);
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "unsaved ")]);
        tab.Session.SetReadOnly(true);
        fixture.Dialogs.SaveChoice = SaveChoice.Cancel;
        Assert.False(await fixture.Shell.CloseAllWorkspacesAsync());
        Assert.Single(fixture.Shell.OpenWorkspaces);
        Assert.Contains(tab, fixture.Shell.Tabs);
        Assert.Equal("unsaved original", tab.CurrentText);
        Assert.Equal(1, fixture.Dialogs.SavePrompts);
    }

    [Fact]
    public async Task Nested_workspace_rename_plan_includes_parent_workspace_backlinks()
    {
        using var fixture = new ShellFixture();
        var source = await fixture.WriteAsync("source.md", "[Target](nested/target.md)\n");
        var target = await fixture.WriteAsync("nested/target.md", "# Target\n");
        await fixture.Shell.OpenWorkspaceAsync(fixture.Docs);
        await fixture.Shell.OpenWorkspaceAsync(Path.GetDirectoryName(target)!);
        var tab = (await fixture.Shell.OpenFileAsync(target))!;
        Assert.True(await fixture.Shell.RenameDocumentAsync(tab, "renamed.md"));
        var plan = Assert.IsType<RepairPlan>(fixture.Shell.LastRenameRepairPlan);
        Assert.Contains(plan.Files, f => f.Path == source && f.ResultText.Contains("nested/renamed.md", StringComparison.Ordinal));
        Assert.Equal("[Target](nested/target.md)\n", await File.ReadAllTextAsync(source));
        var result = await fixture.Shell.ApplyRenameRepairsAsync(plan);
        Assert.True(result.Succeeded);
        Assert.Equal("[Target](nested/renamed.md)\n", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task Opening_and_selecting_workspaces_preserves_documents_and_reuses_root_identity()
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("first.md", "original");
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(fixture.Docs));
        var first = fixture.Shell.Workspace!;
        var document = (await fixture.Shell.OpenFileAsync(path))!;
        document.ApplyEdits([TextEdit.Insert(0, "unsaved ")]);
        var otherRoot = fixture.Root.File("second");
        Directory.CreateDirectory(otherRoot);
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(otherRoot));
        Assert.Equal(2, fixture.Shell.OpenWorkspaces.Count);
        Assert.Contains(document, fixture.Shell.Tabs);
        Assert.True(document.IsDirty);
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(fixture.Docs));
        Assert.Equal(2, fixture.Shell.OpenWorkspaces.Count);
        Assert.Same(first, fixture.Shell.Workspace);
        Assert.Equal("unsaved original", document.CurrentText);
        Assert.Equal("original", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Close_all_finishes_decisions_before_detaching_and_preserves_standalone_work()
    {
        using var fixture = new ShellFixture();
        var firstPath = await fixture.WriteAsync("first.md", "one");
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(fixture.Docs));
        var first = (await fixture.Shell.OpenFileAsync(firstPath))!;
        first.ApplyEdits([TextEdit.Insert(0, "dirty ")]);
        var otherRoot = fixture.Root.File("second");
        Directory.CreateDirectory(otherRoot);
        var secondPath = Path.Combine(otherRoot, "second.md");
        await File.WriteAllTextAsync(secondPath, "two");
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(otherRoot));
        var second = (await fixture.Shell.OpenFileAsync(secondPath))!;
        second.ApplyEdits([TextEdit.Insert(0, "dirty ")]);
        var standalonePath = fixture.Root.File("standalone.md");
        await File.WriteAllTextAsync(standalonePath, "independent");
        var standalone = (await fixture.Shell.OpenFileAsync(standalonePath))!;
        standalone.ApplyEdits([TextEdit.Insert(0, "keep ")]);
        var draft = fixture.Shell.NewDocument("also keep");
        fixture.Dialogs.SaveResponses.Enqueue(SaveChoice.Discard);
        fixture.Dialogs.SaveResponses.Enqueue(SaveChoice.Cancel);
        Assert.False(await fixture.Shell.CloseAllWorkspacesAsync());
        Assert.Equal(2, fixture.Shell.OpenWorkspaces.Count);
        Assert.Equal(4, fixture.Shell.Tabs.Count);
        fixture.Dialogs.SaveChoice = SaveChoice.Discard;
        Assert.True(await fixture.Shell.CloseAllWorkspacesAsync());
        Assert.Empty(fixture.Shell.OpenWorkspaces);
        Assert.Null(fixture.Shell.Workspace);
        Assert.Equal(2, fixture.Shell.Tabs.Count);
        Assert.Contains(standalone, fixture.Shell.Tabs);
        Assert.Contains(draft, fixture.Shell.Tabs);
        Assert.True(standalone.IsDirty);
        Assert.True(draft.IsDirty);
        Assert.Equal("one", await File.ReadAllTextAsync(firstPath));
        Assert.Equal("two", await File.ReadAllTextAsync(secondPath));
    }

    [Fact]
    public async Task Closing_root_loaded_from_nested_workspace_closes_all_its_document_tabs()
    {
        using var fixture = new ShellFixture();
        var parentPath = await fixture.WriteAsync("parent.md", "parent");
        var nestedPath = await fixture.WriteAsync("nested/child.md", "child");
        await fixture.Shell.OpenWorkspaceAsync(fixture.Docs);
        var parent = fixture.Shell.Workspace!;
        await fixture.Shell.OpenWorkspaceAsync(Path.GetDirectoryName(nestedPath)!);
        var nested = fixture.Shell.Workspace!;
        Assert.Same(parent, nested);
        var parentTab = await fixture.Shell.OpenFileAsync(parentPath);
        var childTab = await fixture.Shell.OpenFileAsync(nestedPath);
        Assert.True(await fixture.Shell.CloseWorkspaceAsync(parent));
        Assert.Null(fixture.Shell.Workspace);
        Assert.DoesNotContain(parentTab!, fixture.Shell.Tabs);
        Assert.DoesNotContain(childTab!, fixture.Shell.Tabs);
        Assert.Empty(fixture.Shell.OpenWorkspaces);
        Assert.Equal("child", await File.ReadAllTextAsync(nestedPath));
    }

    [Fact]
    public async Task Create_workspace_uses_new_directory_and_refuses_existing_content()
    {
        using var fixture = new ShellFixture();
        var destination = fixture.Root.File("created");
        Assert.True(await fixture.Shell.CreateWorkspaceAsync(destination));
        Assert.Equal(destination, fixture.Shell.Workspace!.Root);
        var userFile = Path.Combine(destination, "keep.md");
        await File.WriteAllTextAsync(userFile, "preserve");
        Assert.False(await fixture.Shell.CreateWorkspaceAsync(destination));
        Assert.Equal("preserve", await File.ReadAllTextAsync(userFile));
        Assert.Single(fixture.Shell.OpenWorkspaces);
        Assert.False(await fixture.Shell.CreateWorkspaceAsync(fixture.Root.File("missing-parent/child")));
        Assert.False(Directory.Exists(fixture.Root.File("missing-parent")));
    }

    [Fact]
    public async Task Session_roundtrip_restores_all_workspace_roots_and_active_workspace()
    {
        using var fixture = new ShellFixture();
        await fixture.Shell.OpenWorkspaceAsync(fixture.Docs);
        var first = fixture.Shell.Workspace!;
        var secondRoot = fixture.Root.File("second");
        Directory.CreateDirectory(secondRoot);
        await fixture.Shell.OpenWorkspaceAsync(secondRoot);
        Assert.True(fixture.Shell.SelectWorkspace(first));
        Assert.True(fixture.Shell.SaveSession());
        var snapshot = new SessionStore(fixture.Shell.Paths.StateDirectory).Load();
        Assert.Equal(2, snapshot.OpenWorkspaceRoots.Count);
        Assert.Equal(first.Root, snapshot.LastWorkspace);
        Assert.True(await fixture.Shell.CloseAllWorkspacesAsync());
        await fixture.Shell.RestoreSessionAsync();
        Assert.Equal(2, fixture.Shell.OpenWorkspaces.Count);
        Assert.Equal(first.Root, fixture.Shell.Workspace!.Root);
    }

    [Fact]
    public async Task Legacy_single_workspace_session_remains_restorable()
    {
        using var fixture = new ShellFixture();
        var store = new SessionStore(fixture.Shell.Paths.StateDirectory);
        Assert.True(store.Save(new SessionSnapshot(fixture.Docs, [], null, null, new Dictionary<string, FileViewState>())));
        await fixture.Shell.RestoreSessionAsync();
        Assert.Equal(fixture.Docs, Assert.Single(fixture.Shell.OpenWorkspaces).Root);
    }
}
