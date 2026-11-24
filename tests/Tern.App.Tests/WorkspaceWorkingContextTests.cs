using Tern.App.Settings;
using Tern.App.Shell;
using Tern.Domain.Text;
using Tern.Workspaces;

namespace Tern.App.Tests;

public sealed class WorkspaceWorkingContextTests
{
    [Fact]
    public async Task Opening_named_workspace_keeps_current_unsaved_buffer_and_view_of_already_open_document()
    {
        using var fixture = new ShellFixture();
        var root = Path.Combine(fixture.Docs, "Company");
        Directory.CreateDirectory(root);
        var path = await fixture.WriteAsync("Company/live.md", "saved");
        WorkspaceDefinition.Create(root, "Company");
        WorkspaceDefinition.SaveWorkingContext(root, "Company", new WorkspaceWorkingContext([new("live.md", 1, 10)], "live.md", "Reading"));
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "keep live ")]);
        fixture.Shell.RememberViewState(tab, 8, 90);
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(root));
        Assert.Same(tab, Assert.Single(fixture.Shell.Tabs));
        Assert.Equal("keep live saved", tab.CurrentText);
        Assert.True(tab.IsDirty);
        Assert.Equal(new FileViewState(8, 90), fixture.Shell.GetViewState(tab.FilePath!));
    }

    [Fact]
    public async Task Existing_custom_metadata_is_never_rewritten_on_close_even_when_it_is_unsupported()
    {
        using var fixture = new ShellFixture();
        var root = Path.Combine(fixture.Docs, "Company");
        Directory.CreateDirectory(root);
        var descriptor = Path.Combine(root, WorkspaceDefinition.FileName);
        const string custom = "{\"Version\":1,\"Name\":\"Company\",\"CustomMetadata\":{\"Preserve\":true}}";
        await File.WriteAllTextAsync(descriptor, custom);
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(root));
        Assert.True(await fixture.Shell.CloseWorkspaceAsync());
        Assert.Equal(custom, await File.ReadAllTextAsync(descriptor));
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(root));
        const string unsupported = "{\"Version\":99,\"Name\":\"Do not replace\"}";
        await File.WriteAllTextAsync(descriptor, unsupported);
        Assert.True(await fixture.Shell.CloseWorkspaceAsync());
        Assert.Empty(fixture.Shell.OpenWorkspaces);
        Assert.Equal(unsupported, await File.ReadAllTextAsync(descriptor));
    }

    [Fact]
    public async Task Legacy_in_file_context_is_migrated_once_to_local_state_preserving_other_metadata()
    {
        using var fixture = new ShellFixture();
        var root = Path.Combine(fixture.Docs, "Company");
        Directory.CreateDirectory(root);
        var path = await fixture.WriteAsync("Company/live.md", "saved");
        var descriptor = Path.Combine(root, WorkspaceDefinition.FileName);
        await File.WriteAllTextAsync(descriptor, "{\"Version\":1,\"Name\":\"Company\",\"CustomMetadata\":{\"Preserve\":true},\"WorkingContext\":{\"Tabs\":[{\"Path\":\"live.md\",\"CaretOffset\":2,\"ScrollOffset\":5}],\"ActiveTab\":\"live.md\",\"ViewMode\":\"Reading\"}}");
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(root));
        Assert.True(WorkspacePaths.PathsEqual(path, Assert.Single(fixture.Shell.Tabs).FilePath!));
        Assert.Equal(ViewMode.Reading, fixture.Shell.ViewMode);
        var store = new WorkspaceContextStore(fixture.Shell.Paths.StateDirectory);
        Assert.Equal("live.md", Assert.Single(store.Load(root)!.Tabs).Path);
        var shared = await File.ReadAllTextAsync(descriptor);
        Assert.DoesNotContain("WorkingContext", shared, StringComparison.Ordinal);
        Assert.Contains("\"Preserve\":true", shared, StringComparison.Ordinal);
        Assert.Equal("Company", WorkspaceDefinition.Load(root)!.Name);
    }

    [Fact]
    public async Task Legacy_context_is_left_in_a_read_only_workspace_file_and_still_restores()
    {
        using var fixture = new ShellFixture();
        var root = Path.Combine(fixture.Docs, "Company");
        Directory.CreateDirectory(root);
        var path = await fixture.WriteAsync("Company/live.md", "saved");
        WorkspaceDefinition.Create(root, "Company");
        WorkspaceDefinition.SaveWorkingContext(root, "Company", new WorkspaceWorkingContext([new("live.md", 1, 0)], "live.md", "Source"));
        var descriptor = Path.Combine(root, WorkspaceDefinition.FileName);
        var before = await File.ReadAllBytesAsync(descriptor);
        File.SetAttributes(descriptor, FileAttributes.ReadOnly);
        try
        {
            Assert.True(await fixture.Shell.OpenWorkspaceAsync(root));
            Assert.True(WorkspacePaths.PathsEqual(path, Assert.Single(fixture.Shell.Tabs).FilePath!));
            Assert.True(await fixture.Shell.CloseWorkspaceAsync());
            Assert.Equal(before, await File.ReadAllBytesAsync(descriptor));
            Assert.NotNull(new WorkspaceContextStore(fixture.Shell.Paths.StateDirectory).Load(root));
        }
        finally { File.SetAttributes(descriptor, FileAttributes.Normal); }
    }

    [Fact]
    public async Task Closing_and_exiting_never_write_into_the_workspace_folder()
    {
        using var fixture = new ShellFixture();
        var root = Path.Combine(fixture.Docs, "Company");
        Assert.True(await fixture.Shell.CreateWorkspaceAsync(root));
        await fixture.Shell.OpenFileAsync(await fixture.WriteAsync("Company/a.md", "a"));
        var descriptor = Path.Combine(root, WorkspaceDefinition.FileName);
        var before = await File.ReadAllBytesAsync(descriptor);
        var files = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Order().ToArray();
        Assert.True(await fixture.Shell.PrepareExitAsync(new WindowLayoutHint(0, 0, 800, 600, false, 260, true)));
        Assert.Equal(before, await File.ReadAllBytesAsync(descriptor));
        Assert.Equal(files, Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Order().ToArray());
        Assert.NotNull(new WorkspaceContextStore(fixture.Shell.Paths.StateDirectory).Load(root));
    }


    [Fact]
    public async Task Explicit_named_workspace_reopens_its_saved_documents_active_view_and_recorded_positions()
    {
        using var fixture = new ShellFixture();
        var root = Path.Combine(fixture.Docs, "Company");
        Assert.True(await fixture.Shell.CreateWorkspaceAsync(root));
        var first = await fixture.WriteAsync("Company/first.md", "first");
        var second = await fixture.WriteAsync("Company/second.md", "second");
        var firstTab = (await fixture.Shell.OpenFileAsync(first))!;
        var secondTab = (await fixture.Shell.OpenFileAsync(second))!;
        fixture.Shell.RememberViewState(firstTab, 2, 20);
        fixture.Shell.RememberViewState(secondTab, 3, 40);
        fixture.Shell.ActiveTab = firstTab;
        fixture.Shell.ViewMode = ViewMode.Reading;
        Assert.True(await fixture.Shell.CloseWorkspaceAsync());
        Assert.Empty(fixture.Shell.Tabs);
        var context = Assert.IsType<WorkspaceWorkingContext>(new WorkspaceContextStore(fixture.Shell.Paths.StateDirectory).Load(root));
        Assert.Equal(new[] { "first.md", "second.md" }, context.Tabs.Select(tab => tab.Path));
        Assert.Equal("first.md", context.ActiveTab);
        Assert.Equal("Reading", context.ViewMode);
        fixture.Shell.ViewMode = ViewMode.Source;
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(root));
        Assert.Equal(2, fixture.Shell.Tabs.Count);
        Assert.True(WorkspacePaths.PathsEqual(first, fixture.Shell.ActiveTab!.FilePath!));
        Assert.Equal(ViewMode.Reading, fixture.Shell.ViewMode);
        Assert.Equal(new FileViewState(2, 20), fixture.Shell.GetViewState(fixture.Shell.Tabs[0].FilePath!));
        Assert.Equal(new FileViewState(3, 40), fixture.Shell.GetViewState(fixture.Shell.Tabs[1].FilePath!));
    }

    [Fact]
    public async Task Open_folder_refuses_named_workspace_without_restoring_or_rewriting_its_context()
    {
        using var fixture = new ShellFixture();
        var root = Path.Combine(fixture.Docs, "Company");
        Assert.True(await fixture.Shell.CreateWorkspaceAsync(root));
        var path = await fixture.WriteAsync("Company/saved.md", "saved");
        await fixture.Shell.OpenFileAsync(path);
        fixture.Shell.ViewMode = ViewMode.Reading;
        Assert.True(await fixture.Shell.CloseWorkspaceAsync());
        var descriptor = Path.Combine(root, WorkspaceDefinition.FileName);
        var original = await File.ReadAllBytesAsync(descriptor);
        fixture.Shell.ViewMode = ViewMode.Source;
        Assert.False(await fixture.Shell.OpenFolderAsync(root));
        Assert.Empty(fixture.Shell.OpenWorkspaces);
        Assert.Empty(fixture.Shell.Tabs);
        Assert.False(fixture.Shell.HasWorkspace);
        Assert.Equal(ViewMode.Source, fixture.Shell.ViewMode);
        Assert.True(await fixture.Shell.CloseWorkspaceAsync());
        Assert.Equal(original, await File.ReadAllBytesAsync(descriptor));
    }

    [Fact]
    public async Task Cancel_does_not_save_context_or_close_dirty_documents()
    {
        using var fixture = new ShellFixture();
        var root = Path.Combine(fixture.Docs, "Company");
        Assert.True(await fixture.Shell.CreateWorkspaceAsync(root));
        var path = await fixture.WriteAsync("Company/unsaved.md", "saved");
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "keep ")]);
        var descriptor = Path.Combine(root, WorkspaceDefinition.FileName);
        var original = await File.ReadAllBytesAsync(descriptor);
        fixture.Dialogs.SaveChoice = SaveChoice.Cancel;
        Assert.False(await fixture.Shell.CloseWorkspaceAsync());
        Assert.Contains(tab, fixture.Shell.Tabs);
        Assert.True(tab.IsDirty);
        Assert.Equal(original, await File.ReadAllBytesAsync(descriptor));
    }

    [Fact]
    public async Task Context_store_failure_offers_retry_close_anyway_or_cancel_and_keeps_tabs_on_cancel()
    {
        using var fixture = new ShellFixture();
        var root = Path.Combine(fixture.Docs, "Company");
        Assert.True(await fixture.Shell.CreateWorkspaceAsync(root));
        var path = await fixture.WriteAsync("Company/keep.md", "saved");
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        var descriptor = Path.Combine(root, WorkspaceDefinition.FileName);
        var original = await File.ReadAllBytesAsync(descriptor);
        var blocker = Path.Combine(fixture.Shell.Paths.StateDirectory, WorkspaceContextStore.DirectoryName);
        Directory.CreateDirectory(fixture.Shell.Paths.StateDirectory);
        await File.WriteAllTextAsync(blocker, "not a directory"); // Deterministic unwritable context location on every platform.
        fixture.Dialogs.MetadataResponses.Enqueue(MetadataFailureChoice.Cancel);
        Assert.False(await fixture.Shell.CloseWorkspaceAsync());
        Assert.Single(fixture.Shell.OpenWorkspaces);
        Assert.Contains(tab, fixture.Shell.Tabs);
        Assert.Equal("saved", tab.CurrentText);
        Assert.Equal(original, await File.ReadAllBytesAsync(descriptor));
        Assert.Contains("No documents were closed", fixture.Shell.StatusMessage, StringComparison.Ordinal);
        Assert.Single(fixture.Dialogs.MetadataPrompts);

        fixture.Dialogs.MetadataResponses.Enqueue(MetadataFailureChoice.Continue);
        Assert.True(await fixture.Shell.CloseWorkspaceAsync());
        Assert.Empty(fixture.Shell.OpenWorkspaces);
        Assert.DoesNotContain(tab, fixture.Shell.Tabs);
        Assert.Equal(original, await File.ReadAllBytesAsync(descriptor));
    }

    [Fact]
    public async Task Closing_all_saves_separate_contexts_and_excludes_documents_owned_by_nested_workspace()
    {
        using var fixture = new ShellFixture();
        var company = Path.Combine(fixture.Docs, "Company");
        var hr = Path.Combine(company, "HR");
        Assert.True(await fixture.Shell.CreateWorkspaceAsync(company));
        var first = await fixture.WriteAsync("Company/company.md", "company");
        var companyTab = (await fixture.Shell.OpenFileAsync(first))!;
        fixture.Shell.ViewMode = ViewMode.Source;
        Assert.True(await fixture.Shell.CreateWorkspaceAsync(hr));
        var nested = await fixture.WriteAsync("Company/HR/people.md", "people");
        await fixture.Shell.OpenFileAsync(nested);
        fixture.Shell.ViewMode = ViewMode.Reading;
        fixture.Shell.ActiveTab = companyTab;
        Assert.Equal(ViewMode.Source, fixture.Shell.ViewMode);
        Assert.True(await fixture.Shell.CloseAllWorkspacesAsync());
        var store = new WorkspaceContextStore(fixture.Shell.Paths.StateDirectory);
        var parentState = store.Load(company)!;
        var childState = store.Load(hr)!;
        Assert.Equal("company.md", Assert.Single(parentState.Tabs).Path);
        Assert.Equal("people.md", Assert.Single(childState.Tabs).Path);
        Assert.Equal("Source", parentState.ViewMode);
        Assert.Equal("Reading", childState.ViewMode);
    }

    [Fact]
    public async Task Global_session_restore_does_not_add_workspace_context_tabs_a_second_time()
    {
        using var fixture = new ShellFixture();
        var root = Path.Combine(fixture.Docs, "Company");
        Assert.True(await fixture.Shell.CreateWorkspaceAsync(root));
        var first = await fixture.WriteAsync("Company/first.md", "one");
        var second = await fixture.WriteAsync("Company/second.md", "two");
        await fixture.Shell.OpenFileAsync(first);
        await fixture.Shell.OpenFileAsync(second);
        Assert.True(await fixture.Shell.CloseWorkspaceAsync());
        // A restart must stop the previous shell's delayed session writer first.
        fixture.Shell.Dispose();
        var store = new SessionStore(fixture.Shell.Paths.StateDirectory);
        Assert.True(store.Save(new SessionSnapshot(root, [new TabState(first, 0, 0)], first, null, new Dictionary<string, FileViewState>())
        { OpenWorkspaceRoots = [root] }));
        using var restarted = new ShellViewModel(fixture.Shell.Paths, fixture.Dialogs, new RecordingLauncher());
        await restarted.RestoreSessionAsync();
        Assert.True(WorkspacePaths.PathsEqual(first, Assert.Single(restarted.Tabs).FilePath!));
    }

    [Fact]
    public async Task Invalid_or_outside_saved_paths_are_ignored_while_valid_saved_documents_reopen()
    {
        using var fixture = new ShellFixture();
        var root = Path.Combine(fixture.Docs, "Company");
        Directory.CreateDirectory(root);
        var valid = await fixture.WriteAsync("Company/valid.md", "valid");
        var outside = await fixture.WriteAsync("outside.md", "outside");
        WorkspaceDefinition.Create(root, "Company");
        WorkspaceDefinition.SaveWorkingContext(root, "Company", new WorkspaceWorkingContext(
            [new("valid.md", 2, 30), new("../outside.md", 0, 0), new(outside, 0, 0), new("missing.md", 0, 0)], "../outside.md", "Reading"));
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(root));
        Assert.True(WorkspacePaths.PathsEqual(valid, Assert.Single(fixture.Shell.Tabs).FilePath!));
        Assert.Contains(fixture.Shell.Workspace!.Notices, notice => notice.Contains("unavailable", StringComparison.Ordinal));
        Assert.Equal("outside", await File.ReadAllTextAsync(outside));
    }
}
