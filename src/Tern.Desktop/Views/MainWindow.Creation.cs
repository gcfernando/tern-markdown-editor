using Avalonia.Controls;
using Tern.App.Shell;
using Tern.Workspaces;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private async Task ShowCreationFormAsync(CreateItemKind kind, string? initialParent = null, WorkspaceSession? initialOwner = null)
    {
        var roots = _shell.OpenWorkspaces.ToArray();
        initialOwner ??= _shell.Workspace;
        initialParent ??= initialOwner?.Root ?? OutsideCreationParent();
        var locations = roots.Select(w => new CreationDestination((w.IsNamedWorkspace ? "Workspace · " : "Folder · ") + w.Name + " — " + w.Root, w.Root)).ToList();
        if (initialOwner is not null && !WorkspacePaths.PathsEqual(initialParent, initialOwner.Root))
            locations.Insert(0, new CreationDestination("Selected folder · " + initialParent, initialParent));
        locations.Add(new CreationDestination("Other folder…", OutsideCreationParent(), true));
        WorkspaceSession? Owner(string parent) => roots.OrderByDescending(w => w.Root.Length).FirstOrDefault(w => WorkspacePaths.PathsEqual(parent, w.Root) || w.IsInside(parent));
        string? Validate(string parent)
        {
            if (Owner(parent) is { } owner) return _shell.OpenWorkspaces.Contains(owner) ? null : "This location was closed. Choose another destination; nothing was created.";
            return OutsideCreationError(parent);
        }
        await new CreateItemWindow(kind, initialParent, initialOwner is null, Validate, locations,
            async (request, physicalCreationCompleted) =>
            {
                if (_windowClosed) return "The main window closed. Nothing was created.";
                if (Validate(request.Parent) is { } problem) return problem;
                return await PerformCreationAsync(kind, request, Owner(request.Parent), physicalCreationCompleted);
            }).ShowDialog<CreatedItemRequest?>(this);
    }

    private async void OnImportWorkspaceFilesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: Tern.Desktop.ViewModels.TreeRow row }) return;
        var owner = _shell.OpenWorkspaces.FirstOrDefault(workspace => WorkspacePaths.PathsEqual(workspace.Root, row.FullPath));
        if (owner is null) { _shell.SetStatus("The destination was closed. Reopen it before importing.", StatusSeverity.Warning); return; }
        await _shell.ImportFilesToWorkspaceAsync(owner);
        if (!_windowClosed) { RefreshWorkspace(); await ShowNavAsync("Files"); }
    }

    private void OnLearnWorkspacesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        HelpWindow.ShowFor(this, () => CreateHelpWindow(Tern.App.Commands.CommandIds.CreateWorkspace), Tern.App.Commands.CommandIds.CreateWorkspace);

    private async Task<string?> PerformCreationAsync(CreateItemKind kind, CreatedItemRequest request, WorkspaceSession? owner, Action physicalCreationCompleted)
    {
        if (owner is not null && !_shell.OpenWorkspaces.Contains(owner)) return "The destination was closed. Choose another location.";
        using var operation = _shell.BeginStatusOperation("Creating " + kind, request.Parent);
        if (kind == CreateItemKind.Workspace)
        {
            if (!await CreateWorkspaceAtAsync(request.Parent, request.Name, physicalCreationCompleted)) { operation.Fail(_shell.StatusMessage); return _shell.StatusMessage; }
            return null;
        }
        var operations = owner?.FileOperations ?? new WorkspaceFileOperations(request.Parent);
        var format = _shell.Settings.NewFileFormat;
        var result = await Task.Run(() => kind == CreateItemKind.Folder
            ? operations.CreateFolder(request.Parent, request.Name) : operations.CreateFile(request.Parent, request.Name, format: format));
        if (!result.Succeeded)
        {
            var error = result.Message ?? "The location is unavailable. Your name and destination are kept for retry.";
            operation.Fail(error);
            return error;
        }
        if (owner is null) _outsideCreationParent = request.Parent;
        physicalCreationCompleted();
        // The requested physical creation is now complete. A later display failure must not invite another creation.
        try
        {
            if (kind == CreateItemKind.Folder && owner is null) await _shell.OpenDirectoryAsync(result.Path!);
            if (kind == CreateItemKind.File)
            {
                _shell.Recent.AddFile(result.Path!);
                await _shell.OpenFileAsync(result.Path!, createdFile: result.CreatedFile);
            }
            foreach (var root in _shell.OpenWorkspaces.Where(w => w.IsInside(result.Path!)).ToArray()) await root.RescanTreeAsync();
            if (!_windowClosed) { RefreshWorkspace(); await ShowNavAsync("Files"); }
            _shell.SetStatus("Created " + result.Path, Tern.App.Shell.StatusSeverity.Success);
            if (!_windowClosed && kind == CreateItemKind.File && !OwnedWindows.Any()
                && _shell.ActiveTab?.FilePath is { } activePath && WorkspacePaths.PathsEqual(activePath, result.Path!))
            {
                if (_shell.ViewMode == Tern.App.Settings.ViewMode.Reading) await RunCommandAsync(Tern.App.Commands.CommandIds.ViewSource);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (!_windowClosed && !OwnedWindows.Any() && _shell.ActiveTab?.FilePath is { } path
                        && WorkspacePaths.PathsEqual(path, result.Path!))
                        Editor.TextArea.Focus();
                }, Avalonia.Threading.DispatcherPriority.Loaded);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { _shell.StatusMessage = "Created " + result.Path + ". Refresh/open was unavailable: " + _shell.PlainProblem(ex); }
        return null;
    }
}
