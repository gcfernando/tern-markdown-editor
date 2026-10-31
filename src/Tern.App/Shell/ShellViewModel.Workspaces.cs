using System.Collections.ObjectModel;
using Tern.Workspaces;

namespace Tern.App.Shell;

public sealed partial class ShellViewModel
{
    private readonly Lock _workspaceGate = new();
    private Task _workspaceTail = Task.CompletedTask;
    public ObservableCollection<WorkspaceSession> OpenWorkspaces { get; } = [];
    public string? WorkspaceRevealPath { get; private set; }

    /// <summary>Implicit directory entry points preserve named/legacy contexts and browse all other directories as folders.</summary>
    public bool IsKnownWorkspace(string directory) =>
        File.Exists(Path.Combine(directory, WorkspaceDefinition.FileName))
        || Recent.Workspaces.Any(w => WorkspacePaths.PathsEqual(w.Path, directory))
        || OpenWorkspaces.Any(w => w.IsNamedWorkspace && WorkspacePaths.PathsEqual(w.Root, directory))
        || new SessionStore(Paths.StateDirectory).Load().OpenWorkspaceRoots.Any(root => WorkspacePaths.PathsEqual(root, directory));

    public Task<bool> OpenDirectoryAsync(string directory) =>
        IsKnownWorkspace(directory) ? OpenWorkspaceAsync(directory) : OpenFolderAsync(directory);

    private async Task<bool> OpenWorkspaceFromRootAsync(string directory, IProgress<Search.IndexProgress>? progress, bool recordRecent)
    {
        try
        {
            var root = WorkspacePaths.Normalize(directory);
            // A nested workspace belongs to the complete outer workspace. Open
            // that root so siblings and every branch are scanned together.
            // Missing Recent entries must still fail rather than opening a parent.
            if (Directory.Exists(root))
                for (var parent = Directory.GetParent(root); parent is not null; parent = parent.Parent)
                    if (IsKnownWorkspace(parent.FullName)) root = parent.FullName;
            var existing = OpenWorkspaces.FirstOrDefault(workspace => workspace.IsNamedWorkspace && WorkspacePaths.PathsEqual(workspace.Root, root));
            var opened = existing is not null ? SelectWorkspace(existing)
                : await OpenWorkspaceCoreAsync(root, progress, recordRecent).ConfigureAwait(true);
            if (existing is not null && recordRecent) Recent.AddWorkspace(existing.Root);
            if (opened)
            {
                WorkspaceRevealPath = WorkspacePaths.Normalize(directory);
                OnPropertyChanged(nameof(WorkspaceRevealPath));
            }
            return opened;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            StatusMessage = "Could not open workspace: " + ex.Message;
            _dialogs.ShowMessage("Cannot open workspace", StatusMessage);
            return false;
        }
    }

    private async Task<bool> SerializeWorkspacesAsync(Func<Task<bool>> action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (_workspaceGate) { previous = _workspaceTail; _workspaceTail = completed.Task; }
        try { await previous.ConfigureAwait(true); return await action().ConfigureAwait(true); }
        finally { completed.TrySetResult(); }
    }

    /// <summary>Selects the workspace used by explorer, workspace search and creation commands; documents stay open.</summary>
    public bool SelectWorkspace(WorkspaceSession workspace)
    {
        if (!OpenWorkspaces.Contains(workspace)) return false;
        if (Workspace is { } previous) previous.PreferredViewMode = ViewMode;
        Workspace = workspace;
        ViewMode = workspace.PreferredViewMode;
        OnPropertyChanged(nameof(HasWorkspace));
        return true;
    }

    /// <summary>Nested roots are allowed; the most specific root owns a document for workspace-close decisions.</summary>
    public WorkspaceSession? WorkspaceForPath(string path) => OpenWorkspaces
        .Where(workspace => workspace.IsInside(path)).OrderByDescending(workspace => workspace.Root.Length).FirstOrDefault();

    /// <summary>Creates a new directory with a portable workspace descriptor. Existing destinations are never overwritten.</summary>
    public Task<bool> CreateWorkspaceAsync(string newFolderPath) => SerializeWorkspacesAsync(async () =>
    {
        if (_disposed) return false;
        if (OpenWorkspaces.Count >= SessionStore.MaxWorkspaces)
        { StatusMessage = "Close a workspace before creating another one."; return false; }
        string? staging = null;
        string? created = null;
        try
        {
            var path = WorkspacePaths.Normalize(newFolderPath);
            var parent = Path.GetDirectoryName(path);
            var name = Path.GetFileName(path);
            if (parent is null || !Directory.Exists(parent) || !FileNameValidator.Validate(name).IsValid)
            { StatusMessage = "Choose an existing parent folder and a valid new workspace name."; return false; }
            if (Directory.Exists(path) || File.Exists(path))
            { StatusMessage = "That location already exists. Use Open Workspace for an existing workspace, or Open Folder to browse a directory."; return false; }
            staging = Path.Combine(parent, ".tern-workspace-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            WorkspaceDefinition.Create(staging, name);
            // Directory.Move has exclusive destination semantics, unlike CreateDirectory's reuse behavior.
            Directory.Move(staging, path);
            staging = null;
            created = path;
            var opened = await OpenWorkspaceCoreAsync(path, null, recordRecent: true).ConfigureAwait(true);
            if (opened)
            {
                foreach (var parentWorkspace in OpenWorkspaces.Where(w => !WorkspacePaths.PathsEqual(w.Root, path) && w.IsInside(path)))
                    await parentWorkspace.RescanTreeAsync().ConfigureAwait(true);
                OnPropertyChanged(nameof(Workspace));
            }
            if (!opened) StatusMessage = "The empty workspace folder was created, but could not be opened. It remains at " + created;
            return opened;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { StatusMessage = "Could not create workspace: " + ex.Message; return false; }
        finally
        {
            if (staging is not null && Directory.Exists(staging))
            {
                try
                {
                    var descriptor = Path.Combine(staging, WorkspaceDefinition.FileName);
                    if (File.Exists(descriptor)) File.Delete(descriptor);
                    Directory.Delete(staging, recursive: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { StatusMessage = "The unused workspace staging folder could not be removed: " + ex.Message; }
            }
        }
    });

    private void DetachWorkspace(WorkspaceSession workspace)
    {
        if (!OpenWorkspaces.Remove(workspace)) return;
        if (ReferenceEquals(Workspace, workspace)) Workspace = OpenWorkspaces.LastOrDefault();
        workspace.Dispose();
        OnPropertyChanged(nameof(HasWorkspace));
        OnPropertyChanged(nameof(OpenWorkspaces));
    }
}
