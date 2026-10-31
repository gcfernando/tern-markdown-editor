using Tern.App.Documents;
using Tern.Domain.Documents;
using Tern.Filesystem;
using Tern.Workspaces;

namespace Tern.App.Shell;

public sealed partial class ShellViewModel
{
    /// <summary>Recycles a confirmed local namespace entry before retiring its application references.</summary>
    public Task<bool> DeleteFileAsync(string path, ILocalFileDeletion service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return SerializeIdentityAsync(async () =>
        {
            if (_disposed) return false;
            string full;
            DiskBaseline? disk;
            string physical;
            try
            {
                if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile)
                { StatusMessage = "Only local files can be moved to the Recycle Bin."; return false; }
                full = Path.GetFullPath(path);
                if (Directory.Exists(full) || !File.Exists(full))
                { StatusMessage = "Choose an existing local file, not a folder or online address."; return false; }
                disk = await _files.ReadBaselineAsync(full).ConfigureAwait(true);
                physical = FileIdentity.ResolvePhysicalPath(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception)
            { StatusMessage = "Could not verify the file for recycling: " + ex.Message; return false; }
            if (disk is null) { StatusMessage = "The file no longer exists."; return false; }
            var targets = DeletionTabs(full).Select(tab => new DeletionSnapshot(tab, tab.Session.Revision, tab.Session.Baseline, tab.FilePath, tab.RecoveredFromPath)).ToList();
            if (targets.Any(target => target.Baseline is { } baseline && !baseline.SameContentAs(disk)))
            { StatusMessage = "The file changed on disk. Review or reload it before deleting."; return false; }
            if (!await _dialogs.ConfirmDeleteAsync(full, targets.Count(target => target.Tab.IsDirty)).ConfigureAwait(true)) return false;

            try
            {
                var current = await _files.ReadBaselineAsync(full).ConfigureAwait(true);
                if (_disposed || current != disk || !SamePath(physical, FileIdentity.ResolvePhysicalPath(full)) || !DeletionSnapshotsMatch(full, targets))
                { StatusMessage = "The file or its open edits changed during confirmation. Nothing was deleted; review and try again."; return false; }
                // Always pass the original namespace path, never a resolved symlink/hard-link target.
                await service.MoveToRecycleBinAsync(full).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or NotSupportedException or System.ComponentModel.Win32Exception)
            { StatusMessage = "The file was not recycled; its application references were kept: " + ex.Message; return false; }
            if (File.Exists(full))
            { StatusMessage = "The recycle operation did not remove the selected path. Application references were kept."; return false; }
            if (_disposed) return true;

            // Edits made while Windows was recycling were not covered by the confirmation.
            // Keep their tabs/checkpoints rather than silently discarding this newer work.
            if (!DeletionSnapshotsMatch(full, targets))
            { StatusMessage = "The file was recycled, but newer open edits were kept. Save those documents to another location."; return true; }

            var warnings = new List<string>();
            foreach (var target in targets) RemoveTab(target.Tab, retireRecovery: false);
            foreach (var key in _viewStates.Keys.Where(key => SamePath(key, full)).ToList()) _viewStates.Remove(key);
            var retainedClosed = _closedTabs.Where(closed => !SamePath(closed, full)).Reverse().ToList();
            _closedTabs.Clear();
            foreach (var closed in retainedClosed) _closedTabs.Push(closed);
            Recent.Remove(RecentKind.File, full);
            if (Recent.LastSaveError is { } recentError) warnings.Add("Recent could not be persisted: " + recentError.Message);
            try
            {
                foreach (var target in targets) RecoveryStore.Retire(target.Tab.Session.Id);
                foreach (var entry in RecoveryStore.Scan().Entries.Where(entry => entry.Record.OriginalPath is { } original && SamePath(original, full)))
                {
                    if (entry.OwnerIsAlive && !targets.Any(target => target.Tab.Session.Id == entry.Record.SessionId))
                    { warnings.Add("Recovery checkpoints belonging to another active session were kept."); continue; }
                    RecoveryStore.Retire(entry.Record.SessionId);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { warnings.Add("Recovery history could not be cleared: " + ex.Message); }
            try { await HistoryStore.PurgeAsync(full).ConfigureAwait(true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warnings.Add("Saved-text history could not be cleared: " + ex.Message); }
            foreach (var workspace in OpenWorkspaces.Where(workspace => workspace.IsInside(full)))
            {
                workspace.ClearUnsavedText(full);
                workspace.Graph.Remove(full);
                try { await workspace.RescanTreeAsync().ConfigureAwait(true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warnings.Add("Workspace refresh failed: " + ex.Message); }
            }
            LastRenameRepairPlan = null;
            if (!SaveSession()) warnings.Add("The updated session could not be saved: " + _sessionStore.LastSaveError?.Message);
            StatusMessage = warnings.Count == 0
                ? "Moved the file to the Windows Recycle Bin and cleared its Tern references and history."
                : "The file was moved to the Recycle Bin. " + string.Join(" ", warnings);
            return true;
        });
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
