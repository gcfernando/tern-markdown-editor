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
            async request =>
            {
                if (_windowClosed) return "The main window closed. Nothing was created.";
                if (Validate(request.Parent) is { } problem) return problem;
                return await PerformCreationAsync(kind, request, Owner(request.Parent));
            }).ShowDialog<CreatedItemRequest?>(this);
    }

    private async Task<string?> PerformCreationAsync(CreateItemKind kind, CreatedItemRequest request, WorkspaceSession? owner)
    {
        if (owner is not null && !_shell.OpenWorkspaces.Contains(owner)) return "The destination was closed. Choose another location.";
        using var operation = _shell.BeginStatusOperation("Creating " + kind, request.Parent);
        if (kind == CreateItemKind.Workspace)
        {
            if (!await CreateWorkspaceAtAsync(request.Parent, request.Name)) { operation.Fail(_shell.StatusMessage); return _shell.StatusMessage; }
            return null;
        }
        var operations = owner?.FileOperations ?? new WorkspaceFileOperations(request.Parent);
        var result = await Task.Run(() => kind == CreateItemKind.Folder
            ? operations.CreateFolder(request.Parent, request.Name) : operations.CreateFile(request.Parent, request.Name));
        if (!result.Succeeded)
        {
            var error = result.Message ?? "The location is unavailable. Your name and destination are kept for retry.";
            operation.Fail(error);
            return error;
        }
        if (owner is null) _outsideCreationParent = request.Parent;
        // The requested physical creation is now complete. A later display failure must not invite another creation.
        try
        {
            if (kind == CreateItemKind.Folder && owner is null) await _shell.OpenDirectoryAsync(result.Path!);
            if (kind == CreateItemKind.File)
            {
                _shell.Recent.AddFile(result.Path!);
                await _shell.OpenFileAsync(result.Path!);
            }
            foreach (var root in _shell.OpenWorkspaces.Where(w => w.IsInside(result.Path!)).ToArray()) await root.RescanTreeAsync();
            if (!_windowClosed) { RefreshWorkspace(); await ShowNavAsync("Files"); }
            _shell.StatusMessage = "Created " + result.Path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { _shell.StatusMessage = "Created " + result.Path + ". Refresh/open was unavailable: " + ex.Message; }
        return null;
    }
}
