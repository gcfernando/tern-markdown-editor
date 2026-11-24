using Tern.App.Documents;
using Tern.Domain.Documents;
using Tern.Filesystem;
using Tern.Workspaces;

namespace Tern.App.Shell;

public sealed partial class ShellViewModel
{
    /// <summary>Recycles a confirmed local namespace entry before retiring its application references.</summary>
    public Task<bool> DeleteFileAsync(string path, ILocalFileDeletion service) =>
        TrackActivityAsync("Recycling Library file", path, activity => DeleteFileWithProgressAsync(path, service, activity));

    private async Task<bool> DeleteFileWithProgressAsync(string path, ILocalFileDeletion service, ActivityScope activity)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (_disposed) return false;
        string full;
        DiskBaseline? disk;
        string physical;
        try
        {
            activity.Report("Validating the local file and its disk baseline");
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile)
            { return RejectRecycling("Only local files can be moved to the Recycle Bin."); }
            full = Path.GetFullPath(path);
            if (Directory.Exists(full) || !File.Exists(full))
            { return RejectRecycling("Choose an existing local file, not a folder or online address."); }
            disk = await _files.ReadBaselineAsync(full).ConfigureAwait(true);
            physical = FileIdentity.ResolvePhysicalPath(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception)
        { return RejectRecycling("Could not verify the file for recycling: " + UserProblem.Plain(ex), ex.Message); }
        if (disk is null) { return RejectRecycling("The file no longer exists."); }
        var targets = DeletionTabs(full).Select(tab => new DeletionSnapshot(tab, tab.Session.Revision, tab.Session.Baseline, tab.FilePath, tab.RecoveredFromPath)).ToList();
        if (targets.Any(target => target.Baseline is { } baseline && !baseline.SameContentAs(disk)))
        { return RejectRecycling("The file changed on disk. Review or reload it before deleting."); }
        activity.WaitForUser("Confirm file recycling; no changes made");
        if (!await _dialogs.ConfirmDeleteAsync(full, targets.Count(target => target.Tab.IsDirty)).ConfigureAwait(true))
        { activity.Cancel("Canceled; the file, drafts and application references were kept."); return false; }

        return await SerializeIdentityAsync(async () =>
        {
            try
            {
                activity.Report("Revalidating the confirmed file and open edits");
                var current = await _files.ReadBaselineAsync(full).ConfigureAwait(true);
                if (_disposed || current != disk || !SamePath(physical, FileIdentity.ResolvePhysicalPath(full)) || !DeletionSnapshotsMatch(full, targets))
                { return RejectRecycling("The file or its open edits changed during confirmation. Nothing was deleted; review and try again."); }
                // Always pass the original namespace path, never a resolved symlink/hard-link target.
                activity.Report("Handing the file to the Windows Recycle Bin");
                await service.MoveToRecycleBinAsync(full).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                if (ex is OperationCanceledException)
                {
                    var message = "Windows recycling stopped; application references were kept. Verify the file in Explorer: " + UserProblem.Plain(ex);
                    activity.Cancel(message + " Details: " + ex.Message);
                    SetStatus(message, StatusSeverity.Warning);
                    return false;
                }
                return RejectRecycling((File.Exists(full)
                    ? "The file was not recycled; its application references were kept: "
                    : "The selected path is no longer present, but Windows recycling could not be confirmed. Application references were kept; verify the Recycle Bin: ") + UserProblem.Plain(ex), ex.Message);
            }
            if (File.Exists(full))
            { return RejectRecycling("The recycle operation did not remove the selected path. The file still exists; application references were kept."); }
            if (_disposed) { activity.Complete("The file was recycled; application shutdown interrupted reference cleanup.", true, "Recycled; cleanup interrupted"); return true; }

            // Edits made while Windows was recycling were not covered by the confirmation.
            // Keep their tabs/checkpoints rather than silently discarding this newer work.
            if (!DeletionSnapshotsMatch(full, targets))
            { activity.Complete("Save the newer open edits to another location.", true, "File recycled; newer edits kept"); SetStatus("The file was recycled, but newer open edits were kept. Save those documents to another location.", StatusSeverity.Warning); return true; }

            activity.Report("File recycled; preserving recovery and updating application references");
            var warnings = new List<string>();
            // Unsaved edits the user confirmed discarding stay recoverable alongside the trashed history (R-01).
            if (targets.Any(target => target.Tab.IsDirty)) await _recovery.FlushAllAsync().ConfigureAwait(true);
            var keptRecovery = new HashSet<Guid>(targets.Where(target => target.Tab.IsDirty).Select(target => target.Tab.Session.Id));
            foreach (var target in targets) RemoveTab(target.Tab, retireRecovery: false);
            foreach (var key in _viewStates.Keys.Where(key => SamePath(key, full)).ToList()) _viewStates.Remove(key);
            var retainedClosed = _closedTabs.Where(closed => !SamePath(closed, full)).Reverse().ToList();
            _closedTabs.Clear();
            foreach (var closed in retainedClosed) _closedTabs.Push(closed);
            Recent.Remove(RecentKind.File, full);
            if (Recent.LastSaveError is { } recentError) warnings.Add("Recent could not be persisted: " + recentError.Message);
            try
            {
                var clean = targets.Where(target => !keptRecovery.Contains(target.Tab.Session.Id)).Select(target => target.Tab.Session.Id).ToHashSet();
                foreach (var entry in RecoveryStore.Scan().Entries.Where(entry => clean.Contains(entry.Record.SessionId))) RecoveryStore.Retire(entry.Record.SessionId);
                foreach (var id in clean) RecoveryStore.Retire(id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { warnings.Add("Recovery history could not be cleared: " + PlainProblem(ex)); }
            try { await HistoryStore.TrashAsync(full).ConfigureAwait(true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warnings.Add("Saved-text history could not be kept for restoring: " + PlainProblem(ex)); }
            activity.Report("File recycled; refreshing workspace files and index");
            foreach (var workspace in OpenWorkspaces.Where(workspace => workspace.IsInside(full)))
            {
                workspace.ClearUnsavedText(full);
                workspace.Graph.Remove(full);
                try { await workspace.RescanTreeAsync().ConfigureAwait(true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warnings.Add("Workspace refresh failed: " + PlainProblem(ex)); }
            }
            LastRenameRepairPlan = null;
            if (!SaveSession()) warnings.Add("The updated session could not be saved: " + _sessionStore.LastSaveError?.Message);
            SetStatus(warnings.Count == 0
                ? "Moved the file to the Windows Recycle Bin. Its history is kept for 30 days and restored if the file comes back."
                : "The file was moved to the Recycle Bin. " + string.Join(" ", warnings),
                warnings.Count == 0 ? StatusSeverity.Success : StatusSeverity.Warning);
            activity.Complete(warnings.Count > 0 ? string.Join(" ", warnings) : null, warnings.Count > 0,
                warnings.Count > 0 ? "File recycled with warning" : "File recycled");
            return true;
        }, parentActivity: activity).ConfigureAwait(true);

        bool RejectRecycling(string message, string? details = null)
        {
            activity.Fail(details is null ? message : message + " Details: " + details);
            SetStatus(message, StatusSeverity.Error);
            return false;
        }
    }

    private List<DocumentTab> DeletionTabs(string path) => Tabs.Where(tab =>
        tab.FilePath is { } file && SamePath(file, path)
        || tab.FilePath is null && tab.RecoveredFromPath is { } recovered && SamePath(recovered, path)).ToList();

    private bool DeletionSnapshotsMatch(string path, IReadOnlyList<DeletionSnapshot> snapshots)
    {
        var current = DeletionTabs(path);
        return current.Count == snapshots.Count && snapshots.All(snapshot => current.Contains(snapshot.Tab)
            && snapshot.Tab.Session.IsCurrent(snapshot.Revision) && snapshot.Tab.Session.Baseline == snapshot.Baseline
            && snapshot.Tab.FilePath == snapshot.FilePath && snapshot.Tab.RecoveredFromPath == snapshot.RecoveredFromPath);
    }

    private sealed record DeletionSnapshot(DocumentTab Tab, long Revision, DiskBaseline? Baseline, string? FilePath, string? RecoveredFromPath);
}
