using Tern.App.Documents;
using Tern.Recovery;
using Tern.Workspaces;

namespace Tern.App.Shell;

public sealed partial class ShellViewModel
{
    private string[] _privateRoots = [];
    public RecoveryStore DiscardedDrafts { get; }

    private void RememberPrivateRoot(string root) =>
        _privateRoots = _privateRoots.Append(root).Distinct(WorkspacePaths.Comparer).ToArray();

    private bool IsPrivatePath(string path)
    {
        var full = Path.GetFullPath(path);
        return _privateRoots.Any(root => WorkspacePaths.Comparer.Equals(full, root)
            || full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
    }

    public bool IsPrivateDocument(DocumentTab tab) => tab.FilePath is { } path ? IsPrivatePath(path)
        : tab.DraftWorkspaceRoot is { } root && IsPrivatePath(root);

    private async Task ClearWorkspaceCopiesAsync(WorkspaceSession workspace)
    {
        await HistoryStore.PurgeWorkspaceAsync(workspace.Root).ConfigureAwait(true);
        foreach (var file in workspace.Snapshot.Files) await HistoryStore.PurgeAsync(file.FullPath).ConfigureAwait(true);
        foreach (var store in new[] { RecoveryStore, DiscardedDrafts })
        {
            var entries = store.Scan();
            foreach (var entry in entries.Entries.Where(entry => entry.Record.OriginalPath is { } path && workspace.IsInside(path)))
                if (!store.Retire(entry.Record.SessionId)) throw new IOException("An old secondary copy could not be removed from " + store.DirectoryPath);
            if (entries.Corrupt.Count > 0)
                throw new IOException("Private mode is active, but unreadable older recovery copies cannot be associated with a workspace. Review recovery storage before assuming all old copies were removed. Unreadable copies were kept.");
        }
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            File.Delete(Path.Combine(Paths.IndexDirectory, WorkspaceSession.IndexFileName(workspace.Root)) + suffix);
    }

    public async Task<bool> ClearHistoryAsync(DocumentTab? tab = null, WorkspaceSession? workspace = null)
    {
        try
        {
            if (workspace is not null)
            {
                await HistoryStore.PurgeWorkspaceAsync(workspace.Root).ConfigureAwait(true);
                foreach (var file in workspace.Snapshot.Files) await HistoryStore.PurgeAsync(file.FullPath).ConfigureAwait(true);
            }
            else if (tab?.FilePath is { } path) await HistoryStore.PurgeAsync(path).ConfigureAwait(true);
            else { SetStatus("Open a saved document or workspace before clearing its history.", StatusSeverity.Info); return false; }
            SetStatus("Saved-text history cleared. Source files are unchanged; later saves may create new versions.", StatusSeverity.Success);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { SetStatus("History could not be completely cleared: " + PlainProblem(ex), StatusSeverity.Error); return false; }
    }

    public async Task<bool> SetWorkspacePrivateAsync(WorkspaceSession workspace, bool isPrivate)
    {
        if (!OpenWorkspaces.Contains(workspace) || !workspace.IsNamedWorkspace)
        { SetStatus("Open a named workspace first.", StatusSeverity.Info); return false; }
        if (Tabs.Any(tab => tab.IsDirty && (tab.FilePath is { } path && workspace.IsInside(path)
            || tab.DraftWorkspaceRoot is { } root && WorkspacePaths.Comparer.Equals(root, workspace.Root))))
        { SetStatus("Save or close this workspace's unsaved notes first. Its privacy setting was not changed.", StatusSeverity.Warning); return false; }
        var rootPath = workspace.Root;
        if (!await CloseWorkspaceAsync(workspace).ConfigureAwait(true)) return false;
        try
        {
            WorkspaceDefinition.SetPrivate(rootPath, isPrivate, workspace.Name);
            if (isPrivate) RememberPrivateRoot(rootPath);
            else _privateRoots = _privateRoots.Where(root => !WorkspacePaths.Comparer.Equals(root, rootPath)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            await OpenWorkspaceAsync(rootPath).ConfigureAwait(true);
            SetStatus("Workspace privacy could not be saved: " + PlainProblem(ex), StatusSeverity.Error);
            return false;
        }
        var opened = await OpenWorkspaceAsync(rootPath).ConfigureAwait(true);
        if (opened) SetStatus(isPrivate
            ? "Private workspace: search is in memory; History, recovery and discarded-draft copies are disabled. Original files are not encrypted."
            : "Workspace secondary copies are enabled. Later saves and edits can create protected History and recovery copies.", StatusSeverity.Info);
        return opened;
    }

    public RecoveryScan RecentlyDiscarded()
    {
        var scan = DiscardedDrafts.Scan();
        var cutoff = DateTime.UtcNow.AddDays(-7);
        foreach (var entry in scan.Entries.Where(entry => entry.Record.SavedAtUtc < cutoff))
            if (!DiscardedDrafts.Retire(entry.Record.SessionId))
                SetStatus("Tern could not remove an old discarded draft. Check that you can write to Tern's data folder.", StatusSeverity.Warning);
        return new RecoveryScan(scan.Entries.Where(entry => entry.Record.SavedAtUtc >= cutoff).ToArray(), scan.Corrupt);
    }

    public DocumentTab ReopenDiscarded(RecoveryRecord record)
    {
        var tab = NewDocument(record.Text, standalone: true, preservedFormat: record.Format);
        tab.IsRecovered = true;
        tab.RecoveryName = record.DisplayName;
        tab.RecoveredFromPath = record.OriginalPath;
        tab.IsExternalDraft = record.IsExternalDraft;
        tab.SourceUrl = record.SourceUrl;
        SetStatus("Discarded draft opened as an unsaved copy. Save As chooses its destination; the original is unchanged.", StatusSeverity.Info);
        return tab;
    }

    private async Task<bool> KeepDiscardedAsync(DocumentTab tab)
    {
        if (IsPrivateDocument(tab)) return true;
        var snapshot = tab.Session.GetSnapshot();
        try
        {
            var record = new RecoveryRecord(tab.Session.Id, tab.FilePath ?? tab.RecoveredFromPath, tab.DisplayName, snapshot.Revision,
                DateTime.UtcNow, tab.Session.Baseline?.Sha256, tab.Session.Format, snapshot.Text, tab.IsExternalDraft, tab.SourceUrl);
            await DiscardedDrafts.WriteCheckpointAsync(record).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SetStatus("The discarded-draft safety copy could not be written. The document remains open. Retry closing after fixing storage, or save a separate copy. " + PlainProblem(ex), StatusSeverity.Error); return false; }
    }
}
