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
    public bool IsKnownWorkspace(string directory) => IsKnownWorkspace(directory, null);

    // Parent walks share one session read instead of re-reading the file for every folder level.
    private bool IsKnownWorkspace(string directory, Lazy<IReadOnlyList<string>>? savedRoots) =>
        File.Exists(Path.Combine(directory, WorkspaceDefinition.FileName))
        || Recent.Workspaces.Any(w => WorkspacePaths.PathsEqual(w.Path, directory))
        || OpenWorkspaces.Any(w => w.IsNamedWorkspace && WorkspacePaths.PathsEqual(w.Root, directory))
        || (savedRoots?.Value ?? new SessionStore(Paths.StateDirectory).Load().OpenWorkspaceRoots).Any(root => WorkspacePaths.PathsEqual(root, directory));

    private Lazy<IReadOnlyList<string>> SavedWorkspaceRoots() => new(() => new SessionStore(Paths.StateDirectory).Load().OpenWorkspaceRoots);

    /// <param name="treeReady">Runs once the file tree is visible, while links and search continue in the background.</param>
    public Task<bool> OpenDirectoryAsync(string directory, Action? treeReady = null) =>
        IsKnownWorkspace(directory) ? OpenWorkspaceAsync(directory, treeReady: treeReady) : OpenFolderAsync(directory, treeReady: treeReady);

    private async Task<bool> OpenWorkspaceFromRootAsync(string directory, IProgress<Search.IndexProgress>? progress, bool recordRecent,
        WorkspaceTurn? turn = null, Action? treeReady = null)
    {
        try
        {
            var root = WorkspacePaths.Normalize(directory);
            // A nested workspace belongs to the complete outer workspace. Open
            // that root so siblings and every branch are scanned together.
            // Missing Recent entries must still fail rather than opening a parent.
            var savedRoots = SavedWorkspaceRoots();
            if (Directory.Exists(root))
                for (var parent = Directory.GetParent(root); parent is not null; parent = parent.Parent)
                    if (IsKnownWorkspace(parent.FullName, savedRoots)) root = parent.FullName;
            var existing = OpenWorkspaces.FirstOrDefault(workspace => workspace.IsNamedWorkspace && WorkspacePaths.PathsEqual(workspace.Root, root));
            var revealed = false;
            void Reveal()
            {
                revealed = true;
                WorkspaceRevealPath = WorkspacePaths.Normalize(directory);
                OnPropertyChanged(nameof(WorkspaceRevealPath));
            }
            var opened = existing is not null ? SelectWorkspace(existing)
                : await OpenWorkspaceCoreAsync(root, progress, recordRecent, turn: turn, treeReady: () => { Reveal(); treeReady?.Invoke(); }).ConfigureAwait(true);
            if (existing is not null && recordRecent) Recent.AddWorkspace(existing.Root);
            // Revealing again after background indexing would undo folders the user collapsed meanwhile.
            if (opened && !revealed) Reveal();
            return opened;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            StatusMessage = "Could not open workspace: " + PlainProblem(ex);
            _dialogs.ShowMessage("Cannot open workspace", StatusMessage);
            return false;
        }
    }

    private Task<bool> SerializeWorkspacesAsync(Func<Task<bool>> action) => SerializeWorkspacesAsync(_ => action());

    private async Task<bool> SerializeWorkspacesAsync(Func<WorkspaceTurn, Task<bool>> action)
    {
        var turn = await AcquireWorkspaceTurnAsync().ConfigureAwait(true);
        try { return await action(turn).ConfigureAwait(true); }
        finally { turn.Release(); }
    }

    private async Task<WorkspaceTurn> AcquireWorkspaceTurnAsync()
    {
        var turn = new WorkspaceTurn();
        Task previous;
        lock (_workspaceGate) { previous = _workspaceTail; _workspaceTail = turn.Completed; }
        await previous.ConfigureAwait(true);
        return turn;
    }

    /// <summary>Exclusive use of the workspace list. Opening gives it up while indexing so other workspace commands stay available.</summary>
    private sealed class WorkspaceTurn
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completed => _completed.Task;
        public bool IsReleased => _completed.Task.IsCompleted;
        public void Release() => _completed.TrySetResult();
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
    public Task<bool> CreateWorkspaceAsync(string newFolderPath, Action? physicalCreationCompleted = null) => SerializeWorkspacesAsync(async () =>
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
            physicalCreationCompleted?.Invoke();
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
        { StatusMessage = "Could not create workspace: " + PlainProblem(ex); return false; }
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
                { StatusMessage = "The unused workspace staging folder could not be removed: " + PlainProblem(ex); }
            }
        }
    });

    /// <summary>
    /// Makes an existing folder a named workspace by writing <c>.tern-workspace.json</c> into it, then opening it with a full
    /// content index. No file is moved, renamed or rewritten; the descriptor is removed again if the workspace cannot be opened.
    /// </summary>
    public Task<bool> PromoteFolderAsync(string folderPath, string? name = null, Action? treeReady = null) => SerializeWorkspacesAsync(async turn =>
    {
        if (_disposed) return false;
        string? descriptor = null;
        var published = false;
        try
        {
            var path = WorkspacePaths.Normalize(folderPath);
            if (!Directory.Exists(path))
            { SetStatus("That folder no longer exists. Put it back or choose another folder.", StatusSeverity.Error); return false; }
            if (IsKnownWorkspace(path))
            {
                SetStatus("This folder is already a workspace.", StatusSeverity.Info);
                return await OpenWorkspaceFromRootAsync(path, null, recordRecent: true).ConfigureAwait(true);
            }
            var savedRoots = SavedWorkspaceRoots();
            for (var parent = Directory.GetParent(path); parent is not null; parent = parent.Parent)
                if (IsKnownWorkspace(parent.FullName, savedRoots))
                {
                    SetStatus($"This folder is already inside the workspace at {parent.FullName}, so its files are part of that workspace.", StatusSeverity.Warning);
                    return false;
                }
            var folderName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var workspaceName = string.IsNullOrWhiteSpace(name) ? (string.IsNullOrWhiteSpace(folderName) ? "Workspace" : folderName) : name.Trim();
            var replaces = OpenWorkspaces.Any(w => !w.IsNamedWorkspace && WorkspacePaths.PathsEqual(w.Root, path));
            if (!replaces && OpenWorkspaces.Count >= SessionStore.MaxWorkspaces)
            { SetStatus("Close a workspace before making another folder a workspace.", StatusSeverity.Warning); return false; }
            WorkspaceDefinition.Create(path, workspaceName);
            descriptor = Path.Combine(path, WorkspaceDefinition.FileName);
            var opened = await OpenWorkspaceCoreAsync(path, null, recordRecent: true, turn: turn, treeReady: () =>
            {
                published = true;
                treeReady?.Invoke();
            }).ConfigureAwait(true);
            if (!opened)
            {
                // Closing during indexing keeps the workspace the user just created.
                if (!published) TryDelete(descriptor);
                return false;
            }
            descriptor = null;
            foreach (var outer in OpenWorkspaces.Where(w => !WorkspacePaths.PathsEqual(w.Root, path) && w.IsInside(path)))
                await outer.RescanTreeAsync().ConfigureAwait(true);
            WorkspaceRevealPath = path;
            OnPropertyChanged(nameof(WorkspaceRevealPath));
            OnPropertyChanged(nameof(Workspace));
            SetStatus($"{workspaceName} is now a workspace. Content search, backlinks, link checks and tasks are available; no files were moved.", StatusSeverity.Info);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (descriptor is not null) TryDelete(descriptor);
            SetStatus("Could not make this folder a workspace: " + PlainProblem(ex) + " Check that you can write to the folder.", StatusSeverity.Error);
            return false;
        }

        static void TryDelete(string file)
        {
            try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    });

    private void OnWorkspaceProblem(object? sender, WorkspaceProblem problem)
    {
        if (sender is not WorkspaceSession workspace) return;
        var reason = problem.Reason;
        var paused = workspace.UpdatesPausedReason is not null;
        var text = (paused ? reason : "Index problem: " + reason.Replace("Indexing problem: ", string.Empty)) + " (" + workspace.Name + ")";
        SetStatus(text, StatusSeverity.Error);
        ShowBanner(text, StatusSeverity.Error, problem.Details,
            new BannerAction("Rescan", () => workspace.RequestRescan()),
            new BannerAction("Rebuild", () => _ = RebuildWorkspaceIndexAsync(workspace)));
    }

    private async Task RebuildWorkspaceIndexAsync(WorkspaceSession workspace)
    {
        try
        {
            using var operation = BeginStatusOperation("Rebuilding search index", workspace.Root);
            try
            {
                var progress = new Progress<Search.IndexProgress>(p => operation.Report("Indexing workspace", p.Processed, p.Total));
                await Task.Run(() => workspace.RebuildSearchAsync(progress)).ConfigureAwait(true);
                workspace.RequestRescan();
                DismissBanner();
                SetStatus("This workspace's search index was rebuilt. Documents and other indexes were kept.", StatusSeverity.Info);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            { operation.Fail(ex.Message); }
        }
        catch (ObjectDisposedException) { }
    }

    private void DetachWorkspace(WorkspaceSession workspace)
    {
        if (!OpenWorkspaces.Remove(workspace)) return;
        workspace.ProblemDetailsReported -= OnWorkspaceProblem;
        if (ReferenceEquals(Workspace, workspace)) Workspace = OpenWorkspaces.LastOrDefault();
        workspace.Dispose();
        OnPropertyChanged(nameof(HasWorkspace));
        OnPropertyChanged(nameof(OpenWorkspaces));
    }
}
