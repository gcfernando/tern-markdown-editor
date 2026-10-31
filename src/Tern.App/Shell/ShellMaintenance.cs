using Tern.App.Commands;
using Tern.App.Settings;
using Tern.App.Documents;
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
        ActiveTab?.IsInaccessible == true);

    partial void OnActiveTabChanged(DocumentTab? value)
    {
        if (value?.FilePath is { } path && WorkspaceForPath(path) is { } owner)
        {
            owner.LastActiveDocumentPath = path;
            SelectWorkspace(owner);
        }
        if (value is not null) OnTabStateChanged(value.Session, EventArgs.Empty);
        OnPropertyChanged(nameof(ActiveDocumentState));
    }

    private void OnDocumentMetadataChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
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
            Tabs.Any(t => DocumentCommandPolicy.CanExecute(CommandIds.Save, new(t.Session))), Workspace is not null);

    public bool RemoveRecent(RecentKind kind, string path) => Recent.Remove(kind, path);

    public void ClearRecentHistory()
    {
        Recent.ClearAll();
        _closedTabs.Clear();
        StatusMessage = Recent.LastSaveError is null ? "Recent history cleared. Your documents and workspace are unchanged."
            : "History cleared in memory but could not be persisted: " + Recent.LastSaveError.Message;
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
            try { SaveSettings(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                foreach (var (property, value) in previousValues) property.SetValue(Settings, value);
                Registry.LoadOverrides(previousBindings);
                return Fail("Could not persist default preferences. Previous preferences remain active: " + ex.Message);
            }
            completed.Add("preferences and keyboard shortcuts");
        }
        if (options.RecentHistory)
        {
            ClearRecentHistory();
            if (Recent.LastSaveError is not null) return Fail(StatusMessage ?? "Recent history could not be persisted.");
            completed.Add("recent history");
        }
        if (options.SessionState)
        {
            if (!_sessionStore.Save(SessionSnapshot.Empty))
            { return Fail("Could not reset session state: " + _sessionStore.LastSaveError?.Message); }
            _viewStates.Clear();
            _tabViewStates.Clear();
            _closedTabs.Clear();
            _sessionReset = true;
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

    public Task<bool> RenameDocumentAsync(DocumentTab tab, string name) => SerializeIdentityAsync(async () =>
    {
        LastRenameRepairPlan = null;
        if (!tab.CanRename)
        { StatusMessage = "Online files cannot be renamed. Download a local copy first."; return false; }
        if (!Tabs.Contains(tab) || !IsValidDocumentName(name))
        { StatusMessage = "Use a filename without reserved characters, trailing spaces, or a Windows device name."; return false; }
        if (tab.FilePath is not { } oldPath)
        {
            if (tab.IsRecovered) tab.RecoveryName = name;
            tab.Session.SetDraftName(name);
            StatusMessage = "Draft named. Use Save As to choose where it will be stored.";
            return true;
        }
        var destination = Path.Combine(Path.GetDirectoryName(oldPath)!, name);
        if (string.Equals(oldPath, destination, StringComparison.Ordinal)) return true;
        var caseOnly = OperatingSystem.IsWindows() && SamePath(oldPath, destination);
        if (Tabs.Any(t => t != tab && t.FilePath is { } p && SamePath(p, destination) && !SamePath(p, oldPath))
            || (!caseOnly && File.Exists(destination)) || Directory.Exists(destination))
        { StatusMessage = "That name already exists. Choose another name."; return false; }
        try
        {
            RepairPlan? repairs = null;
            var planningWorkspaces = OpenWorkspaces.Where(w => w.IsInside(oldPath)).OrderBy(w => w.Root.Length).ToList();
            if (planningWorkspaces.Count > 0)
            {
                var provider = new OverlayTextProvider(new DiskDocumentTextProvider());
                foreach (var open in Tabs.Where(t => t.FilePath is not null)) provider.Set(open.FilePath!, open.CurrentText);
                var plans = new List<RepairPlan>();
                foreach (var planningWorkspace in planningWorkspaces)
                    plans.Add(await new LinkRepairPlanner(planningWorkspace.Graph, provider, planningWorkspace.Profile)
                        .PlanRenameAsync(oldPath, destination).ConfigureAwait(true));
                // The broadest graph sees parent backlinks and ambiguous wiki names. Narrower graphs add files
                // excluded by the parent scan; choose one coherent reviewed edit set per document.
                repairs = new RepairPlan(plans.SelectMany(p => p.Files).GroupBy(f => f.Path, WorkspacePaths.Comparer)
                    .Select(g => g.First()).ToList(), plans.SelectMany(p => p.Warnings).Distinct(StringComparer.Ordinal).ToList());
            }
            // MoveFileEx supports a case-only rename on Windows; no overwrite or temporary name is needed.
            File.Move(oldPath, destination, overwrite: false);
            foreach (var workspace in OpenWorkspaces) workspace.ClearUnsavedText(oldPath);
            foreach (var sameSource in Tabs.Where(t => t.FilePath is { } p && SamePath(p, oldPath)).ToList())
                sameSource.Session.UpdateFilePath(destination);
            Recent.Rename(RecentKind.File, oldPath, destination);
            if (_viewStates.Remove(oldPath, out var view)) _viewStates[destination] = view;
            foreach (var workspace in OpenWorkspaces.Where(w => w.IsInside(oldPath))) workspace.Graph.Rename(oldPath, destination);
            OnTabStateChanged(tab.Session, EventArgs.Empty);
            LastRenameRepairPlan = repairs;
            try { await HistoryStore.RenameAsync(oldPath, destination).ConfigureAwait(true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                StatusMessage = "Renamed " + name + "; saved-text history could not migrate and remains available under its previous path: " + ex.Message;
                return true;
            }
            StatusMessage = "Renamed " + name + (repairs is { TotalEdits: > 0 } ? ". Review the proposed link updates before applying them." : ".");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { StatusMessage = "Could not rename: " + ex.Message; return false; }
    });

    /// <summary>Applies a reviewed rename plan. Open documents receive undoable edits and require an explicit save.</summary>
    public async Task<RepairResult> ApplyRenameRepairsAsync(RepairPlan plan)
    {
        var results = new List<FileRepairResult>();
        foreach (var repair in plan.Files)
        {
            if (repair.Edits.Count == 0) { results.Add(new(repair, RepairStatus.Skipped, null)); continue; }
            var open = Tabs.FirstOrDefault(t => t.FilePath is { } path && SamePath(path, repair.Path));
            if (open is not null)
            {
                if (open.IsReadOnly) { results.Add(new(repair, RepairStatus.Failed, "The open document is read-only.")); continue; }
                if (open.CurrentText != repair.OriginalText) { results.Add(new(repair, RepairStatus.Conflict, "The open document changed after review; no edits were applied.")); continue; }
                open.ApplyEdits(repair.Edits);
                results.Add(new(repair, RepairStatus.Applied, "Updated the open document. Save it explicitly to write these link edits."));
            }
            else
            {
                var (status, message) = await SafeEditApplier.ApplyAsync(_files, repair.Path, repair.OriginalText, repair.Edits).ConfigureAwait(true);
                results.Add(new(repair, status, message));
            }
        }
        var result = new RepairResult(results);
        StatusMessage = result.Succeeded ? "Reviewed link updates applied. Save modified open documents to keep their edits."
            : "Some reviewed link updates were left unchanged. Review the reported conflicts.";
        return result;
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
