using Tern.App.Documents;
using Tern.Domain.Documents;
using Tern.Filesystem;
using Tern.Workspaces;

namespace Tern.App.Shell;

public sealed partial class ShellViewModel
{
    /// <summary>Folders of the workspace that owns <paramref name="path"/> that it could be moved into, excluding itself and its own subfolders.</summary>
    public IReadOnlyList<string> MoveDestinations(string path)
    {
        var full = Path.GetFullPath(path);
        if (WorkspaceForPath(full) is not { } workspace) return [];
        var isFolder = Directory.Exists(full);
        var folders = new List<string> { workspace.Root };
        folders.AddRange(workspace.Snapshot.Files.Select(f => Path.GetDirectoryName(f.FullPath)!).Distinct(WorkspacePaths.Comparer));
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System, IgnoreInaccessible = true };
            folders.AddRange(Directory.EnumerateDirectories(workspace.Root, "*", options).Where(d => !Path.GetFileName(d).StartsWith('.')).Take(5000));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var current = Path.GetDirectoryName(full);
        return [.. folders.Select(Path.GetFullPath).Distinct(WorkspacePaths.Comparer)
            .Where(d => workspace.IsInside(d + Path.DirectorySeparatorChar) || SamePath(d, workspace.Root))
            .Where(d => !SamePath(d, current ?? string.Empty))
            .Where(d => !isFolder || !(SamePath(d, full) || d.StartsWith(full.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .Order(WorkspacePaths.Comparer)];
    }

    /// <summary>Moves a file or folder into another folder of its workspace, repairing links and migrating everything keyed by its path.</summary>
    public Task<bool> MovePathAsync(string path, string destinationDirectory) => RelocateAsync(path, IdentityChangeKind.Move,
        full => Path.Combine(Path.GetFullPath(destinationDirectory), Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar))),
        (workspace, full) => workspace.FileOperations.MoveAsync(full, destinationDirectory, CollisionPolicy.Fail));

    /// <summary>Renames a folder, repairing links and migrating everything keyed by the paths inside it.</summary>
    public Task<bool> RenameFolderAsync(string folderPath, string newName) => RelocateAsync(folderPath, IdentityChangeKind.Rename,
        full => Path.Combine(Path.GetDirectoryName(full.TrimEnd(Path.DirectorySeparatorChar)) ?? full, newName),
        (workspace, full) => workspace.FileOperations.RenameAsync(full, newName), requireFolder: true);

    private Task<bool> RelocateAsync(string path, IdentityChangeKind kind, Func<string, string> predictTarget,
        Func<WorkspaceSession, string, Task<FileOperationResult>> operate, bool requireFolder = false) =>
        TrackActivityAsync(kind == IdentityChangeKind.Move ? "Moving Library item" : "Renaming Library folder", path,
        activity => SerializeIdentityAsync(async () =>
    {
        LastRenameRepairPlan = null;
        if (_disposed) return false;
        var verb = kind == IdentityChangeKind.Move ? "move" : "rename";
        var changed = false;
        try
        {
            activity.Report("Validating the item and destination");
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            var isFolder = Directory.Exists(full);
            if (!isFolder && (requireFolder || !File.Exists(full)))
            { SetStatus("Choose an existing " + (requireFolder ? "folder" : "file or folder") + " to " + verb + ".", StatusSeverity.Warning); return false; }
            if (WorkspaceForPath(full) is not { } workspace)
            { SetStatus("Only items inside an open workspace can be " + verb + "d.", StatusSeverity.Warning); return false; }
            if (FolderBlockedReason(full, isFolder, verb) is { } blocked) { SetStatus(blocked, StatusSeverity.Warning); return false; }
            var target = Path.GetFullPath(predictTarget(full));
            var caseOnly = SamePath(full, target) && !string.Equals(full, target, StringComparison.Ordinal);
            if (!caseOnly && SamePath(full, target)) return true;
            if (!caseOnly && (File.Exists(target) || Directory.Exists(target)))
            { SetStatus("That name already exists in the destination. Choose another name or folder.", StatusSeverity.Warning); return false; }
            activity.Report("Planning affected links; no files changed");
            var repairs = await PlanRepairsAsync(full, target).ConfigureAwait(true);
            activity.Report("Updating the file-system location");
            var result = await operate(workspace, full).ConfigureAwait(true);
            if (!result.Succeeded || result.Path is null)
            { activity.Fail(result.Message ?? "The file-system operation did not succeed."); SetStatus("Could not " + verb + ": " + result.Message, StatusSeverity.Error); return false; }
            changed = true;
            activity.Report("Location changed; updating path references and history");
            var warnings = await ApplyIdentityChangeAsync(new IdentityChange(full, result.Path, kind, isFolder)).ConfigureAwait(true);
            foreach (var tab in Tabs) OnTabStateChanged(tab.Session, EventArgs.Empty);
            LastRenameRepairPlan = repairs;
            activity.Report("Location changed; refreshing workspace files and index");
            warnings = [.. warnings, .. await RescanAffectedAsync(full, result.Path).ConfigureAwait(true)];
            if (!SaveSession()) warnings = [.. warnings, "The updated session could not be saved: " + _sessionStore.LastSaveError?.Message];
            var name = Path.GetFileName(result.Path);
            var message = (kind == IdentityChangeKind.Move ? "Moved " : "Renamed ") + name
                + (repairs is { TotalEdits: > 0 } ? ". Review the proposed link updates before applying them." : ".")
                + (warnings.Count > 0 ? " " + string.Join(" ", warnings) : string.Empty);
            SetStatus(message, warnings.Count > 0 ? StatusSeverity.Warning : StatusSeverity.Success);
            activity.Complete(message, warnings.Count > 0,
                (kind == IdentityChangeKind.Move ? "Moved" : "Renamed") +
                (warnings.Count > 0 ? " with warning" : repairs is { TotalEdits: > 0 } ? "; link changes await review" : ""));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { activity.Fail((changed ? "The location changed, but follow-up work failed: " : "") + ex.Message); SetStatus("Could not " + verb + ": " + Tern.App.Shell.UserProblem.Plain(ex), StatusSeverity.Error); return false; }
    }, parentActivity: activity));

    /// <summary>Copies a file or folder next to itself ("name copy.ext"). The copy is a new document: it gets no history, but keeps linked-image permissions.</summary>
    public Task<bool> DuplicatePathAsync(string path) => TrackActivityAsync("Duplicating Library item", path,
        activity => SerializeIdentityAsync(async () =>
    {
        LastRenameRepairPlan = null;
        if (_disposed) return false;
        var copied = false;
        try
        {
            activity.Report("Validating the source to duplicate");
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            var isFolder = Directory.Exists(full);
            if (!isFolder && !File.Exists(full)) { SetStatus("Choose an existing file or folder to duplicate.", StatusSeverity.Warning); return false; }
            if (WorkspaceForPath(full) is not { } workspace)
            { SetStatus("Only items inside an open workspace can be duplicated.", StatusSeverity.Warning); return false; }
            activity.Report("Copying the selected item");
            var result = workspace.FileOperations.Duplicate(full);
            if (!result.Succeeded || result.Path is null)
            { activity.Fail(result.Message ?? "The copy did not succeed."); SetStatus("Could not duplicate: " + result.Message, StatusSeverity.Error); return false; }
            copied = true;
            activity.Report("Copy created; updating references and linked-image permissions");
            var warnings = (await ApplyIdentityChangeAsync(new IdentityChange(full, result.Path, IdentityChangeKind.Duplicate, isFolder)).ConfigureAwait(true)).ToList();
            activity.Report("Copy created; refreshing workspace files and index");
            warnings.AddRange(await RescanAffectedAsync(result.Path).ConfigureAwait(true));
            SetStatus("Created " + Path.GetFileName(result.Path) + "." + (warnings.Count > 0 ? " " + string.Join(" ", warnings) : string.Empty),
                warnings.Count > 0 ? StatusSeverity.Warning : StatusSeverity.Success);
            activity.Complete(warnings.Count > 0 ? string.Join(" ", warnings) : null, warnings.Count > 0,
                warnings.Count > 0 ? "Duplicated with warning" : "Duplicated");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { activity.Fail((copied ? "The copy was created, but follow-up work failed: " : "") + ex.Message); SetStatus("Could not duplicate: " + Tern.App.Shell.UserProblem.Plain(ex), StatusSeverity.Error); return false; }
    }, parentActivity: activity));

    private string? FolderBlockedReason(string full, bool isFolder, string verb)
    {
        if (!isFolder) return null;
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            return "A link to a folder cannot be " + verb + "d from Tern. Use File Explorer.";
        if (OpenWorkspaces.Any(w => WorkspacePaths.PathsEqual(w.Root, full) || WorkspacePaths.IsUnder(full, w.Root)))
            return "This folder is, or contains, an open workspace. Close that workspace first.";
        return null;
    }

    private async Task<List<string>> RescanAffectedAsync(params string[] paths)
    {
        var warnings = new List<string>();
        foreach (var workspace in OpenWorkspaces.Where(w => paths.Any(w.IsInside)).ToList())
        {
            try { await workspace.RescanTreeAsync().ConfigureAwait(true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warnings.Add("Workspace refresh failed: " + PlainProblem(ex)); }
        }
        return warnings;
    }

    /// <summary>Recycles a confirmed folder with everything in it, then retires every Tern reference to the files it held.</summary>
    public Task<bool> DeleteFolderAsync(string folderPath, ILocalFileDeletion service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return TrackActivityAsync("Recycling Library folder", folderPath, activity => SerializeIdentityAsync(async () =>
        {
            if (_disposed) return false;
            LastRenameRepairPlan = null;
            string full;
            List<string> files;
            try
            {
                activity.Report("Validating the folder and affected documents");
                full = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar);
                if (!Directory.Exists(full)) { SetStatus("Choose an existing local folder.", StatusSeverity.Warning); return false; }
                if (WorkspaceForPath(full) is null) { SetStatus("Only folders inside an open workspace can be moved to the Recycle Bin.", StatusSeverity.Warning); return false; }
                if (FolderBlockedReason(full, true, "recycle") is { } blocked) { SetStatus(blocked, StatusSeverity.Warning); return false; }
                files = [.. Directory.EnumerateFiles(full, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }).Take(MaxIdentityFiles)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { activity.Fail(ex.Message); SetStatus("Tern could not check this folder, so it was not moved to the Recycle Bin. Check that it is available and try again: " + Tern.App.Shell.UserProblem.Plain(ex), StatusSeverity.Error); return false; }

            var targets = FolderTabs(full).Select(tab => new DeletionSnapshot(tab, tab.Session.Revision, tab.Session.Baseline, tab.FilePath, tab.RecoveredFromPath)).ToList();
            foreach (var target in targets.Where(t => t.FilePath is not null && t.Baseline is not null))
            {
                var disk = await _files.ReadBaselineAsync(target.FilePath!).ConfigureAwait(true);
                if (disk is null || !target.Baseline!.SameContentAs(disk))
                { SetStatus("A document in this folder changed on disk. Review or reload it before recycling the folder.", StatusSeverity.Warning); return false; }
            }
            activity.WaitForUser("Confirm folder recycling; no changes made");
            if (!await _dialogs.ConfirmDeleteFolderAsync(full, files.Count, targets.Count, targets.Count(t => t.Tab.IsDirty)).ConfigureAwait(true))
            { activity.Cancel("Canceled; the folder, documents and application references were kept."); return false; }
            activity.Report("Revalidating the confirmed folder and open edits");

            try
            {
                if (_disposed || !Directory.Exists(full) || !FolderSnapshotsMatch(full, targets))
                { SetStatus("The folder or its open edits changed during confirmation. Nothing was deleted; review and try again.", StatusSeverity.Warning); return false; }
                activity.Report("Handing the folder to the Windows Recycle Bin");
                await service.MoveToRecycleBinAsync(full).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                if (ex is OperationCanceledException) activity.Cancel("Windows recycling stopped; application references were kept. Verify the folder in Explorer.");
                else activity.Fail(ex.Message); SetStatus("The folder was not moved to the Recycle Bin. Close any program using it and try again: " + Tern.App.Shell.UserProblem.Plain(ex), StatusSeverity.Error); return false;
            }
            if (Directory.Exists(full))
            { activity.Fail("The folder still exists; application references were kept."); SetStatus("The folder is still there. Close any program using it and try again.", StatusSeverity.Error); return false; }
            if (_disposed) { activity.Complete("The folder was recycled; application shutdown interrupted reference cleanup.", true, "Recycled; cleanup interrupted"); return true; }
            if (!FolderSnapshotsMatch(full, targets))
            { activity.Complete("Save the newer open edits to another location.", true, "Folder recycled; newer edits kept"); SetStatus("The folder was recycled, but newer open edits were kept. Save those documents to another location.", StatusSeverity.Warning); return true; }

            activity.Report("Folder recycled; preserving recovery and updating application references");
            var warnings = new List<string>();
            if (targets.Any(t => t.Tab.IsDirty)) await _recovery.FlushAllAsync().ConfigureAwait(true);
            var keptRecovery = new HashSet<Guid>(targets.Where(t => t.Tab.IsDirty).Select(t => t.Tab.Session.Id));
            foreach (var target in targets) RemoveTab(target.Tab, retireRecovery: false);
            foreach (var key in _viewStates.Keys.Where(k => MapPath(k, full, full, true) is not null).ToList()) _viewStates.Remove(key);
            var retainedClosed = _closedTabs.Where(closed => MapPath(closed, full, full, true) is null).Reverse().ToList();
            _closedTabs.Clear();
            foreach (var closed in retainedClosed) _closedTabs.Push(closed);
            foreach (var kind in new[] { RecentKind.File, RecentKind.Workspace })
                foreach (var entry in Recent.Get(kind).Where(e => MapPath(e.Path, full, full, true) is not null).ToList()) Recent.Remove(kind, entry.Path);
            if (Recent.LastSaveError is { } recentError) warnings.Add("Recent could not be persisted: " + recentError.Message);
            try
            {
                // Unsaved edits the user confirmed discarding stay recoverable alongside the trashed history (R-01).
                var clean = targets.Where(t => !keptRecovery.Contains(t.Tab.Session.Id)).Select(t => t.Tab.Session.Id).ToHashSet();
                foreach (var entry in RecoveryStore.Scan().Entries.Where(e => clean.Contains(e.Record.SessionId))) RecoveryStore.Retire(entry.Record.SessionId);
                foreach (var id in clean) RecoveryStore.Retire(id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { warnings.Add("Recovery history could not be cleared: " + PlainProblem(ex)); }
            try { foreach (var file in files) await HistoryStore.TrashAsync(file).ConfigureAwait(true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warnings.Add("Saved-text history could not be kept for restoring: " + PlainProblem(ex)); }
            if (!_linkedImages.Forget(full, isFolder: true)) warnings.Add("Linked-image permissions could not be cleared: " + _linkedImages.LastSaveError);
            foreach (var workspace in OpenWorkspaces.Where(w => w.IsInside(full)))
            {
                foreach (var file in files) workspace.ClearUnsavedText(file);
                workspace.Graph.Remove(full);
            }
            activity.Report("Folder recycled; refreshing workspace files and index");
            warnings.AddRange(await RescanAffectedAsync(full).ConfigureAwait(true));
            if (!SaveSession()) warnings.Add("The updated session could not be saved: " + _sessionStore.LastSaveError?.Message);
            SetStatus(warnings.Count == 0
                ? "Moved the folder to the Windows Recycle Bin. Its documents' history is kept for 30 days and restored if they come back."
                : "The folder was moved to the Recycle Bin. " + string.Join(" ", warnings), warnings.Count == 0 ? StatusSeverity.Success : StatusSeverity.Warning);
            activity.Complete(warnings.Count > 0 ? string.Join(" ", warnings) : null, warnings.Count > 0,
                warnings.Count > 0 ? "Folder recycled with warning" : "Folder recycled");
            return true;
        }, parentActivity: activity));
    }

    private List<DocumentTab> FolderTabs(string folder) => Tabs.Where(tab =>
        tab.FilePath is { } file && MapPath(file, folder, folder, true) is not null
        || tab.FilePath is null && tab.RecoveredFromPath is { } recovered && MapPath(recovered, folder, folder, true) is not null).ToList();

    private bool FolderSnapshotsMatch(string folder, IReadOnlyList<DeletionSnapshot> snapshots)
    {
        var current = FolderTabs(folder);
        return current.Count == snapshots.Count && snapshots.All(s => current.Contains(s.Tab)
            && s.Tab.Session.IsCurrent(s.Revision) && s.Tab.Session.Baseline == s.Baseline
            && s.Tab.FilePath == s.FilePath && s.Tab.RecoveredFromPath == s.RecoveredFromPath);
    }
}
