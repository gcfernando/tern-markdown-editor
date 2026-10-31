using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Tern.App.Documents;
using Tern.App.Shell;
using Tern.Desktop.ViewModels;
using Tern.Workspaces;
using Tern.App.Commands;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private bool _syncingWorkspaces;
    private string? _documentWorkspaceRoot;

    private void RefreshDocumentWorkspaceContext()
    {
        var root = _shown is { } tab ? WorkspaceRootFor(tab) : null;
        if (string.Equals(root, _documentWorkspaceRoot, StringComparison.OrdinalIgnoreCase)) return;
        _documentWorkspaceRoot = root;
        RefreshPanels();
        RebuildPreview();
    }

    private string? WorkspaceRootFor(DocumentTab tab) => tab.FilePath is { } path
        ? _shell.WorkspaceForPath(path)?.Root
        : tab.IsRecovered && !tab.IsExternalDraft && tab.RecoveredFromPath is { } original ? _shell.WorkspaceForPath(original)?.Root
        : tab.IsExternalDraft || tab.IsStandaloneDraft || tab.IsRecovered ? null : _shell.Workspace?.Root;

    private void OnOpenWorkspacesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshWorkspace();

    private void OnWorkspaceSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_syncingWorkspaces && WorkspaceSelector.SelectedItem is WorkspaceSession workspace) _shell.SelectWorkspace(workspace);
    }

    private async void OnTreeWorkspaceCloseClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RememberCurrentDocumentView();
        if (sender is Control { DataContext: TreeRow row } && _shell.OpenWorkspaces.FirstOrDefault(w => w.Root == row.FullPath) is { } workspace)
            await _shell.CloseWorkspaceAsync(workspace);
    }

    private void RememberCurrentDocumentView()
    {
        if (_shown is not null) _shell.RememberViewState(_shown, Editor.CaretOffset, Editor.VerticalOffset);
    }

    internal async Task<bool> CreateWorkspaceAtAsync(string parent, string name)
    {
        var validation = FileNameValidator.Validate(name);
        if (!validation.IsValid) { _shell.StatusMessage = validation.Message ?? "Choose a valid workspace folder name."; return false; }
        var created = await _shell.CreateWorkspaceAsync(Path.Combine(parent, name));
        if (created) await ShowNavAsync("Files");
        return created;
    }

    private async Task CreateWorkspaceFromPickerAsync()
    {
        if (!StorageProvider.CanPickFolder) { _shell.StatusMessage = "A Windows folder picker is required to choose the workspace's parent folder."; return; }
        var parents = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose the parent folder for a NEW workspace", AllowMultiple = false });
        if (parents.Count == 0 || parents[0].TryGetLocalPath() is not { } parent) return;
        var name = await new FileNameWindow(true, "Create a new workspace folder inside " + parent, initialName: "New workspace", dialogTitle: "Create workspace").ShowDialog<string?>(this);
        if (name is not null) await CreateWorkspaceAtAsync(parent, name);
    }

    private async Task OpenExistingWorkspaceAsync()
    {
        if (_dialogs is null) return;
        if (await _dialogs.PickFolderAsync("Open Workspace — choose a named workspace folder") is not { } folder) return;
        // Legacy workspaces were directory roots recorded in application history, with no portable descriptor.
        if (!_shell.IsKnownWorkspace(folder))
        {
            _shell.StatusMessage = "This is a plain folder. Choose Open Folder to browse it, or Create Workspace to create a named workspace in a new directory.";
            _dialogs.ShowMessage("Choose a workspace", _shell.StatusMessage);
            return;
        }
        if (await _shell.OpenWorkspaceAsync(folder)) await ShowNavAsync("Files");
    }

    private void OnExpandAllRootsClick(object? sender, RoutedEventArgs e)
    {
        var expand = (sender as Control)?.Tag as string == "expand";
        foreach (var row in _treeRoots.SelectMany(r => r.Flatten()).Where(r => r.IsDirectory)) row.IsExpanded = expand;
        _shell.StatusMessage = expand ? "All Library folders expanded." : "All Library folders collapsed.";
    }

    private async void OnTreeCreateClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Control { DataContext: TreeRow { IsDirectory: true } row, Tag: string action }) return;
        var owner = _shell.OpenWorkspaces.FirstOrDefault(w => WorkspacePaths.PathsEqual(w.Root, row.FullPath)) ?? _shell.WorkspaceForPath(row.FullPath);
        if (owner is null) return;
        _shell.SelectWorkspace(owner);
        FileTree.SelectedItem = row;
        if (action == "workspace")
        {
            var name = await new FileNameWindow(true, "Create a named workspace in a NEW directory inside " + row.FullPath,
                initialName: "New workspace", dialogTitle: "Create workspace").ShowDialog<string?>(this);
            if (name is not null) await CreateWorkspaceAtAsync(row.FullPath, name);
        }
        else await CreateWorkspaceEntryAsync(action == "folder");
    }

    private bool _choosingCreationTarget;
    private string? _outsideCreationParent;

    private async void OnGlobalAddCommandClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Control { Tag: string id } || _choosingCreationTarget || _shell.IsBusy) return;
        _choosingCreationTarget = true;
        try { await CreateFromGlobalAddAsync(id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            if (!_windowClosed)
            {
                _shell.StatusMessage = "Creation could not finish: " + ex.Message;
                _dialogs?.ShowMessage("Check the creation location", _shell.StatusMessage + " Check that location before retrying.");
            }
        }
        finally { _choosingCreationTarget = false; }
    }

    private async Task CreateFromGlobalAddAsync(string id)
    {
        if (id is not (CommandIds.NewFile or CommandIds.NewWorkspaceFile or CommandIds.NewFolder or CommandIds.CreateWorkspace)) return;
        // Capture once, before any dialog. A tab/selection change cannot redirect creation.
        var selected = _shell.Workspace;
        var kind = id == CommandIds.CreateWorkspace ? CreateItemKind.Workspace : id == CommandIds.NewFolder ? CreateItemKind.Folder : CreateItemKind.File;
        var label = kind switch { CreateItemKind.Workspace => "Create workspace", CreateItemKind.Folder => "Create folder", _ => "Create document" };
        var choice = selected is null ? AddDestinationChoice.Outside
            : await AddDestinationWindow.AskAsync(this, selected.Name, selected.Root, selected.IsNamedWorkspace, label);
        if (choice == AddDestinationChoice.Cancel || _windowClosed) return;
        var outside = choice == AddDestinationChoice.Outside;
        if (!outside && (selected is null || !_shell.OpenWorkspaces.Contains(selected)))
        { _shell.StatusMessage = "The selected location was closed. Nothing was created; choose a destination again."; return; }
        if (outside && id == CommandIds.NewFile)
        {
            _shell.NewDocument(standalone: true);
            RefreshLibraryContents();
            await ShowNavAsync("Files");
            _shell.StatusMessage = "New document outside workspaces. Save As chooses its location.";
            return;
        }
        var parent = outside ? OutsideCreationParent() : selected!.Root;
        var request = await new CreateItemWindow(kind, parent, outside,
            candidate => outside ? OutsideCreationError(candidate) : null).ShowDialog<CreatedItemRequest?>(this);
        if (request is null || _windowClosed) return;
        if (!outside && (selected is null || !_shell.OpenWorkspaces.Contains(selected)
            || !WorkspacePaths.PathsEqual(request.Parent, selected.Root)))
        { _shell.StatusMessage = "The selected location changed. Nothing was created; choose a destination again."; return; }
        if (outside && OutsideCreationError(request.Parent) is { } problem)
        { _shell.StatusMessage = problem; _dialogs?.ShowMessage("Choose an outside location", problem); return; }
        using var activity = _shell.BeginStatusOperation();
        _shell.StatusMessage = label + "…";
        if (kind == CreateItemKind.Workspace)
        {
            if (await CreateWorkspaceAtAsync(request.Parent, request.Name) && outside) _outsideCreationParent = request.Parent;
            return;
        }
        var operations = outside ? new WorkspaceFileOperations(request.Parent) : selected!.FileOperations;
        var result = await Task.Run(() => kind == CreateItemKind.Folder
            ? operations.CreateFolder(request.Parent, request.Name) : operations.CreateFile(request.Parent, request.Name));
        if (!result.Succeeded)
        {
            _shell.StatusMessage = "Could not create item: " + (result.Message ?? "The location is unavailable.");
            _dialogs?.ShowMessage("Could not create item", _shell.StatusMessage); return;
        }
        if (outside) _outsideCreationParent = request.Parent;
        if (kind == CreateItemKind.Folder && outside)
            await _shell.OpenFolderAsync(result.Path!);
        else if (kind == CreateItemKind.File)
        {
            // Keep the created file discoverable even if opening is subsequently cancelled.
            _shell.Recent.AddFile(result.Path!);
            await _shell.OpenFileAsync(result.Path!);
        }
        foreach (var root in _shell.OpenWorkspaces.Where(w => w.IsInside(result.Path!)).ToArray())
            await root.RescanTreeAsync();
        if (selected is not null && _shell.OpenWorkspaces.Contains(selected)) _shell.SelectWorkspace(selected);
        RefreshWorkspace();
        await ShowNavAsync("Files");
        _shell.StatusMessage = "Created " + result.Path;
    }

    private string OutsideCreationParent()
    {
        if (_outsideCreationParent is { } previous && Directory.Exists(previous) && OutsideCreationError(previous) is null) return previous;
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Directory.Exists(documents) && OutsideCreationError(documents) is null ? documents : string.Empty;
    }

    private string? OutsideCreationError(string parent)
    {
        try
        {
            var physical = Tern.Filesystem.FileIdentity.ResolvePhysicalPath(parent);
            return _shell.OpenWorkspaces.Any(w => w.IsNamedWorkspace && (WorkspacePaths.PathsEqual(w.Root, parent) || w.IsInside(parent)
                || WorkspacePaths.IsSameOrUnder(Tern.Filesystem.FileIdentity.ResolvePhysicalPath(w.Root), physical)))
                ? "This parent is inside an open workspace. Choose a separate folder, or cancel and use the selected workspace." : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return "Cannot use this parent folder: " + ex.Message; }
    }
}
