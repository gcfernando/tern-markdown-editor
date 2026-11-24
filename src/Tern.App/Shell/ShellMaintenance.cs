using Tern.App.Commands;
using Tern.App.Settings;
using Tern.App.Documents;
using Tern.Domain.Text;
using Tern.Workspaces;

namespace Tern.App.Shell;

public sealed record ResetOptions(bool RecentHistory = true, bool Preferences = false, bool SessionState = false, bool Caches = false, bool RemoteCache = false);

public sealed partial class ShellViewModel
{
    public RepairPlan? LastRenameRepairPlan { get; private set; }
    private bool _sessionReset;
    public bool IsBusy { get; private set; }
    public string? LinkedImagePermissionError => _linkedImages.LastSaveError;

    public IReadOnlyList<ResolvedLink> GetBrokenLinks(WorkspaceSession workspace) => [.. workspace.Graph.GetBrokenLinks()
        .Where(link => !LinkedImageAccess.IsAvailable(link.SourcePath, link.Link,
            Tabs.FirstOrDefault(tab => tab.FilePath is { } path && SamePath(path, link.SourcePath))?.LinkedImageFiles
                ?? _linkedImages.Get(link.SourcePath)))];
    private DocumentOperation _documentOperation;
    private DocumentTab? _operationDocument;
    public DocumentState ActiveDocumentState => DocumentStateResolver.Resolve(ActiveTab?.Session,
        _documentOperation == DocumentOperation.Loading || ReferenceEquals(_operationDocument, ActiveTab)
            ? _documentOperation : DocumentOperation.None,
        ActiveTab?.Conflict?.Kind == Tern.Filesystem.ConflictKind.DeletedOnDisk
            || ActiveTab?.Session is { HasFile: true, Baseline: null },
        ActiveTab?.IsInaccessible == true,
        ActiveTab?.IsChangedOnDisk == true);

    partial void OnActiveTabChanged(DocumentTab? value)
    {
        if (value?.FilePath is { } path && WorkspaceForPath(path) is { } owner)
        {
            owner.LastActiveDocumentPath = path;
            SelectWorkspace(owner);
        }
        if (value is not null) OnTabStateChanged(value.Session, EventArgs.Empty);
        OnPropertyChanged(nameof(ActiveDocumentState));
        QueueSessionSave();
    }

    private void OnDocumentMetadataChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentTab.FilePath)) QueueSessionSave();
        if (sender is DocumentTab { FilePath: { } path, LinkedImageFiles.Count: > 0 } tab
            && e.PropertyName is nameof(DocumentTab.FilePath) or nameof(DocumentTab.LinkedImageFiles))
        {
            if (!_linkedImages.Save(path, tab.LinkedImageFiles))
                StatusMessage = "The image link works now, but its selected-file permission could not be saved: " + _linkedImages.LastSaveError;
        }
        if (ReferenceEquals(sender, ActiveTab)) OnPropertyChanged(nameof(ActiveDocumentState));
    }

    public DocumentCommandContext GetCommandContext(bool canUndo = false, bool canRedo = false) =>
        new(ActiveTab?.Session, HasWorkspace, Tabs.Count, canUndo, canRedo, IsBusy,
            Tabs.Any(t => DocumentCommandPolicy.CanExecute(CommandIds.Save, new(t.Session))), Workspace is not null, Workspace is { IsNamedWorkspace: false },
            ActiveTab is { IsRecovered: true, IsExternalDraft: false, RecoveredFromPath: not null });

    public bool RemoveRecent(RecentKind kind, string path) => Recent.Remove(kind, path);

    public void ClearRecentHistory(bool includePinned = false)
    {
        Recent.ClearAll(includePinned);
        _closedTabs.Clear();
        if (Recent.LastSaveError is { } error)
            SetStatus("Recent history is cleared for now, but Tern could not save that change. Try again, or check that your user folder is not full: " + PlainProblem(error), StatusSeverity.Warning);
        else SetStatus(includePinned
            ? "Recent history cleared, including pinned references. Documents and workspaces are unchanged."
            : "Unpinned recent history and the Reopen Closed Tab list cleared. Pinned references, documents and workspaces are unchanged.", StatusSeverity.Info);
    }

    /// <summary>Resets only selected application metadata. Open buffers and recovery copies are never removed.</summary>
    public bool ResetApplication(ResetOptions options)
    {
        var completed = new List<string>();
        bool Fail(string message)
        {
            StatusMessage = message + (completed.Count > 0 ? " Already reset: " + string.Join(", ", completed) + ". The remaining categories were not reset." : string.Empty);
            return false;
        }
        if (IsBusy || IsIndexing || HasActiveStatusOperations)
        { StatusMessage = "Wait for loading, saving, downloading and indexing to finish before resetting application data. Nothing was reset."; return false; }
        if (!(options.RecentHistory || options.Preferences || options.SessionState || options.Caches || options.RemoteCache))
        { StatusMessage = "Select at least one category to reset. Nothing was changed."; return false; }
        if (options.RemoteCache)
        {
            var remoteCache = Path.GetFullPath(Path.Combine(Paths.StateDirectory, "remote-cache"));
            if (Tabs.Any(t => (t.ResourceDocumentPath ?? t.FilePath) is { } path && (SamePath(path, remoteCache)
                || path.StartsWith(remoteCache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
            { StatusMessage = "Close cached online documents before clearing remote downloads. Nothing was reset."; return false; }
        }
        if (options.Caches)
        {
            // Index files belong to the live workspace; deleting an open database is unsafe.
            StatusMessage = "Close all workspaces and browsed folders before clearing search indexes. Nothing was reset.";
            if (OpenWorkspaces.Count > 0) return false;
            try
            {
                var cache = Path.GetFullPath(Paths.IndexDirectory);
                // Only the dedicated index directory is a disposable cache, never an arbitrary configured root.
                if (!string.Equals(Path.GetFileName(cache), "index", StringComparison.OrdinalIgnoreCase)
                    || Tabs.Any(t => t.FilePath is { } path && (SamePath(path, cache)
                        || path.StartsWith(cache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
                { StatusMessage = "The configured index location is not safe to clear."; return false; }
                if (Directory.Exists(cache))
                {
                    for (var directory = new DirectoryInfo(cache); directory is not null; directory = directory.Parent)
                        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        { StatusMessage = "Search cache reset is unavailable through a linked directory."; return false; }
                    // Delete only the known flat SQLite index files. Unknown content and subfolders are user-owned.
                    foreach (var file in Directory.EnumerateFiles(cache))
                    {
                        var filename = Path.GetFileName(file);
                        if (!System.Text.RegularExpressions.Regex.IsMatch(filename, @"^index-[0-9a-f]{16}\.db(?:-wal|-shm|-journal)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)) continue;
                        if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) continue;
                        File.Delete(file);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return Fail("Search cache reset did not complete; some index files may remain: " + ex.Message); }
            completed.Add("search indexes");
        }
        if (options.RemoteCache)
        {
            var cache = Path.GetFullPath(Path.Combine(Paths.StateDirectory, "remote-cache"));
            try
            {
                if (Directory.Exists(cache))
                {
                    for (var directory = new DirectoryInfo(cache); directory is not null; directory = directory.Parent)
                        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        { return Fail("Remote cache reset is unavailable through a linked directory."); }
                    var managed = Directory.EnumerateDirectories(cache)
                        .Where(path => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), "^[0-9a-f]{64}(?:-[0-9a-f]{32})?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)).ToArray();
                    foreach (var entry in managed)
                    {
                        // Inspect before recursive deletion; cache links may point to user-owned files.
                        if (ContainsLinkedEntry(entry))
                        { return Fail("Remote cache contains a linked entry. Nothing in that cache was deleted; remove the link before retrying."); }
                    }
                    foreach (var entry in managed) Directory.Delete(entry, recursive: true);
                    foreach (var pointer in Directory.EnumerateFiles(cache).Where(path => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), @"^[0-9a-f]{64}\.current$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)))
                        if (!File.GetAttributes(pointer).HasFlag(FileAttributes.ReparsePoint)) File.Delete(pointer);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return Fail("Remote cache reset did not complete. Some cache entries may remain: " + ex.Message); }
            completed.Add("cached online documents and assets");
        }
        if (options.Preferences)
        {
            var properties = typeof(AppSettings).GetProperties().Where(p => p.CanWrite).ToArray();
            var previousValues = properties.ToDictionary(p => p, p => p.GetValue(Settings));
            var previousBindings = new Dictionary<string, string>(Registry.Overrides);
            var defaults = new AppSettings();
            foreach (var property in properties)
                property.SetValue(Settings, property.GetValue(defaults));
            Registry.ResetAll();
            if (!SaveSettings())
            {
                foreach (var (property, value) in previousValues) property.SetValue(Settings, value);
                Registry.LoadOverrides(previousBindings);
                return Fail("Could not persist default preferences. Previous preferences remain active.");
            }
            completed.Add("preferences and keyboard shortcuts");
        }
        if (options.RecentHistory)
        {
            ClearRecentHistory(includePinned: true);
            if (Recent.LastSaveError is not null) return Fail(StatusMessage ?? "Recent history could not be persisted.");
            completed.Add("recent history");
        }
        if (options.SessionState)
        {
            if (!ResetPersistedSession())
            { return Fail("Could not reset session state: " + _sessionStore.LastSaveError?.Message); }
            _viewStates.Clear();
            _tabViewStates.Clear();
            _closedTabs.Clear();
            completed.Add("session restoration");
        }
        StatusMessage = "Reset complete: " + string.Join(", ", completed) + ". Open documents and unsaved work are preserved.";
        return true;
    }

    private static bool ContainsLinkedEntry(string root)
    {
        if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint)) return true;
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
            if (attributes.HasFlag(FileAttributes.Directory) && ContainsLinkedEntry(path)) return true;
        }
        return false;
    }

    internal Func<Task>? BeforeRenamePlanning { get; set; }

    public Task<bool> RenameDocumentAsync(DocumentTab tab, string name) =>
        TrackActivityAsync("Renaming document", tab.DisplayName, activity => RenameDocumentWithProgressAsync(tab, name, activity));

    private async Task<bool> RenameDocumentWithProgressAsync(DocumentTab tab, string name, ActivityScope activity)
    {
        activity.Report("Validating the document and requested name");
        LastRenameRepairPlan = null;
        if (_disposed) { SetStatus("The application is closing; no file was renamed.", StatusSeverity.Warning); return false; }
        if (!tab.CanRename)
        { StatusMessage = "Online files cannot be renamed. Download a local copy first."; return false; }
        if (!Tabs.Contains(tab) || !IsValidDocumentName(name))
        { StatusMessage = "Use a filename without reserved characters, trailing spaces, or a Windows device name."; return false; }
        if (tab.FilePath is not { } oldPath)
        {
            return await SerializeIdentityAsync(() =>
            {
                if (_disposed || !Tabs.Contains(tab) || tab.FilePath is not null)
                { SetStatus("The document changed while naming it. Review it and try again.", StatusSeverity.Warning); return Task.FromResult(false); }
                if (tab.IsRecovered) tab.RecoveryName = name;
                activity.Report("Naming the unsaved draft; no disk file changed");
                tab.Session.SetDraftName(name);
                StatusMessage = "Draft named. Use Save As to choose where it will be stored.";
                return Task.FromResult(true);
            }, parentActivity: activity).ConfigureAwait(true);
        }
        var destination = Path.Combine(Path.GetDirectoryName(oldPath)!, name);
        if (string.Equals(oldPath, destination, StringComparison.Ordinal)) return true;
        var caseOnly = OperatingSystem.IsWindows() && SamePath(oldPath, destination);
        if (Tabs.Any(t => t != tab && t.FilePath is { } p && SamePath(p, destination) && !SamePath(p, oldPath))
            || (!caseOnly && File.Exists(destination)) || Directory.Exists(destination))
        { StatusMessage = "That name already exists. Choose another name."; return false; }
        var renamed = false;
        try
        {
            activity.Report("Reading the disk baseline and planning affected links");
            var revision = tab.Session.Revision;
            var baseline = tab.Session.Baseline;
            var disk = await _files.ReadBaselineAsync(oldPath).ConfigureAwait(true);
            var physical = Tern.Filesystem.FileIdentity.ResolvePhysicalPath(oldPath);
            var workspaces = OpenWorkspaces.Where(workspace => workspace.IsInside(oldPath))
                .Select(workspace => (Workspace: workspace, workspace.Graph.Revision, workspace.Profile)).ToArray();
            var repairs = await PlanRepairsAsync(oldPath, destination).ConfigureAwait(true);
            return await SerializeIdentityAsync(async () =>
            {
                var currentDisk = await _files.ReadBaselineAsync(oldPath).ConfigureAwait(true);
                var currentWorkspaces = OpenWorkspaces.Where(workspace => workspace.IsInside(oldPath)).ToArray();
                if (_disposed || !Tabs.Contains(tab) || tab.FilePath != oldPath || !tab.Session.IsCurrent(revision)
                    || tab.Session.Baseline != baseline || disk is null || currentDisk != disk
                    || !SamePath(physical, Tern.Filesystem.FileIdentity.ResolvePhysicalPath(oldPath))
                    || currentWorkspaces.Length != workspaces.Length
                    || workspaces.Any(state => !currentWorkspaces.Contains(state.Workspace)
                        || state.Workspace.Graph.Revision != state.Revision || state.Workspace.Profile != state.Profile))
                { SetStatus("The file changed while Tern was preparing the rename. Nothing was renamed. Check it and try again.", StatusSeverity.Warning); return false; }
                if (Tabs.Any(other => other != tab && other.FilePath is { } path && SamePath(path, destination) && !SamePath(path, oldPath))
                    || (!caseOnly && File.Exists(destination)) || Directory.Exists(destination))
                { SetStatus("That name now exists. Nothing was renamed; choose another name.", StatusSeverity.Warning); return false; }
                // MoveFileEx supports a case-only rename on Windows without an intermediate name.
                activity.Report("Renaming the disk file");
                File.Move(oldPath, destination, overwrite: false);
                renamed = true;
                activity.Report("File renamed; updating path references, history and index");
                var warnings = await ApplyIdentityChangeAsync(new IdentityChange(oldPath, destination, IdentityChangeKind.Rename)).ConfigureAwait(true);
                OnTabStateChanged(tab.Session, EventArgs.Empty);
                LastRenameRepairPlan = repairs;
                SetStatus("Renamed " + name + (repairs is { TotalEdits: > 0 } ? ". Review the proposed link updates before applying them." : ".")
                    + (warnings.Count > 0 ? " " + string.Join(" ", warnings) : string.Empty),
                    repairs is { TotalEdits: > 0 } || warnings.Count > 0 ? StatusSeverity.Warning : StatusSeverity.Success);
                activity.Complete(warnings.Count > 0 ? string.Join(" ", warnings) : null, warnings.Count > 0,
                    warnings.Count > 0 ? "Renamed with warning" : repairs is { TotalEdits: > 0 } ? "Renamed; link changes await review" : "Renamed");
                return true;
            }, parentActivity: activity).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { activity.Fail((renamed ? "The file was renamed, but follow-up work failed: " : "") + ex.Message); StatusMessage = "Could not rename: " + Tern.App.Shell.UserProblem.Plain(ex); return false; }
    }

    /// <summary>Plans the link edits a rename or move would need, using unsaved text from open documents.</summary>
    private async Task<RepairPlan?> PlanRepairsAsync(string oldPath, string newPath)
    {
        var planningWorkspaces = OpenWorkspaces.Where(w => w.IsInside(oldPath)).OrderBy(w => w.Root.Length)
            .Select(workspace => (workspace.Graph, workspace.Profile)).ToArray();
        if (planningWorkspaces.Length == 0) return null;
        var provider = new OverlayTextProvider(new DiskDocumentTextProvider());
        foreach (var open in Tabs.Where(t => t.FilePath is not null)) provider.Set(open.FilePath!, open.CurrentText);
        var beforePlanning = BeforeRenamePlanning;
        return await Task.Run(async () =>
        {
            if (beforePlanning is not null) await beforePlanning().ConfigureAwait(false);
            var plans = new List<RepairPlan>();
            foreach (var planningWorkspace in planningWorkspaces)
                plans.Add(await new LinkRepairPlanner(planningWorkspace.Graph, provider, planningWorkspace.Profile)
                    .PlanRenameAsync(oldPath, newPath).ConfigureAwait(false));
            // Broad and nested graphs can overlap; retain one coherent edit set per file.
            return new RepairPlan(plans.SelectMany(p => p.Files).GroupBy(f => f.Path, WorkspacePaths.Comparer)
                .Select(g => g.First()).ToList(), plans.SelectMany(p => p.Warnings).Distinct(StringComparer.Ordinal).ToList());
        }).ConfigureAwait(true);
    }

    /// <summary>Applies a reviewed rename plan. Open documents receive undoable edits and require an explicit save.</summary>
    public async Task<RepairResult> ApplyRenameRepairsAsync(RepairPlan plan, string changeDescription = "link update")
    {
        using var activity = Activities.Begin("Applying reviewed link repairs");
        try
        {
            _linkRepairUndo.Clear();
            _lastRepairDescription = changeDescription;
            var results = new List<FileRepairResult>();
            foreach (var repair in plan.Files)
            {
                activity.Report("Validating reviewed edits and updating files", results.Count, plan.Files.Count, "files");
                if (repair.Edits.Count == 0) { results.Add(new(repair, RepairStatus.Skipped, null)); continue; }
                var openCopies = Tabs.Where(t => t.FilePath is { } path && SamePath(path, repair.Path)).ToArray();
                if (openCopies.Length > 0)
                {
                    if (openCopies.Any(open => open.IsReadOnly)) { results.Add(new(repair, RepairStatus.Failed, openCopies.Length == 1 ? "The open document is read-only." : "An open copy of this document is read-only.")); continue; }
                    if (openCopies.Any(open => open.CurrentText != repair.OriginalText)) { results.Add(new(repair, RepairStatus.Conflict, openCopies.Length == 1 ? "The open document changed after review; no edits were applied." : "An open copy changed or differs from the review; no edits were applied.")); continue; }
                    foreach (var open in openCopies) open.ApplyEdits(repair.Edits);
                    results.Add(new(repair, RepairStatus.Applied, "Updated the open document. Save it explicitly to keep these edits."));
                }
                else
                {
                    var (status, message) = await SafeEditApplier.ApplyAsync(_files, repair.Path, repair.OriginalText, repair.Edits,
                        beforeWrite: (path, text) => HistoryStore.CaptureAsync(path, text, "Before " + changeDescription)).ConfigureAwait(true);
                    results.Add(new(repair, status, message));
                    if (status == RepairStatus.Applied)
                        _linkRepairUndo.Add(new LinkRepairUndo(repair.Path, repair.OriginalText, TextEdit.ApplyAll(repair.OriginalText, repair.Edits)));
                }
            }
            var result = new RepairResult(results);
            activity.Report("Reviewed file processing finished", results.Count, plan.Files.Count, "files");
            StatusMessage = result.Succeeded ? $"Reviewed {changeDescription}s applied. Save modified open documents to keep their edits."
                : $"Some reviewed {changeDescription}s were left unchanged. Review the reported conflicts.";
            if (_linkRepairUndo.Count > 0)
                ShowBanner($"Applied {changeDescription}s in {_linkRepairUndo.Count} closed file(s); each earlier version is in its History.", StatusSeverity.Success,
                    new BannerAction("Undo " + changeDescription + "s", () => _ = UndoLinkRepairsAsync()));
            if (!result.Succeeded && result.AppliedCount == 0)
                activity.Fail("No reviewed edits were applied. Review the per-file errors or conflicts; original contents were kept.");
            else activity.Complete($"{result.AppliedCount} file(s) updated. " + (result.Succeeded
                ? "Open-document changes remain unsaved; closed files retain History and the existing Undo action."
                : "Other files failed or conflicted and were left unchanged. Review the reported per-file results."),
                !result.Succeeded, result.Succeeded ? "Reviewed repairs applied; save modified open documents" : "Repairs partly completed; review conflicts");
            return result;
        }
        catch (OperationCanceledException) { activity.Cancel("Link repair stopped; some files may already have changed. Review files and their History."); throw; }
        catch (Exception ex) { activity.Fail("Link repair stopped; some files may already have changed. Review files and their History. " + ex.Message); throw; }
    }

    private readonly List<LinkRepairUndo> _linkRepairUndo = [];
    private string _lastRepairDescription = "link update";

    private sealed record LinkRepairUndo(string Path, string OriginalText, string RepairedText);

    /// <summary>Closed files rewritten by the last link repair that can still be reverted.</summary>
    public int LinkRepairUndoCount => _linkRepairUndo.Count;

    /// <summary>Reverts closed files changed by the last link repair, but only those whose disk text still equals the repaired text.</summary>
    public async Task<(int Restored, int Skipped)> UndoLinkRepairsAsync()
    {
        using var activity = Activities.Begin("Undoing reviewed link repairs");
        try
        {
            var entries = _linkRepairUndo.ToList();
            _linkRepairUndo.Clear();
            int restored = 0, skipped = 0;
            foreach (var entry in entries)
            {
                activity.Report("Checking disk text and restoring unchanged repair results", restored + skipped, entries.Count, "files");
                var (status, _) = await SafeEditApplier.ApplyAsync(_files, entry.Path, entry.RepairedText, [new TextEdit(0, entry.RepairedText.Length, entry.OriginalText)],
                    beforeWrite: (path, text) => HistoryStore.CaptureAsync(path, text, "Before undoing " + _lastRepairDescription)).ConfigureAwait(true);
                if (status == RepairStatus.Applied) restored++; else skipped++;
            }
            SetStatus(skipped == 0 ? $"Reverted {_lastRepairDescription}s in {restored} file(s)."
                : $"Reverted {_lastRepairDescription}s in {restored} file(s); {skipped} changed since the repair and were left as they are.",
                skipped == 0 ? StatusSeverity.Success : StatusSeverity.Warning);
            activity.Report("Restore processing finished", restored + skipped, entries.Count, "files");
            activity.Complete($"{restored} restored; {skipped} skipped.", skipped > 0,
                skipped > 0 ? "Repair undo partly completed; changed files kept" : "Repair undo completed");
            return (restored, skipped);
        }
        catch (OperationCanceledException) { activity.Cancel("Repair undo stopped; some files may already have been restored. Review their History."); throw; }
        catch (Exception ex) { activity.Fail("Repair undo stopped; some files may already have been restored. " + ex.Message); throw; }
    }

    private static bool IsValidDocumentName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name is "." or ".." || name.EndsWith(' ') || name.EndsWith('.')
            || name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c))) return false;
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL")
            && !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³'));
    }
}
