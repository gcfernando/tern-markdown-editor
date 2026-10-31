using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Tern.App.Commands;
using Tern.App.Documents;
using Tern.App.Settings;
using Tern.Domain.Documents;
using Tern.Domain.Text;
using Tern.Filesystem;
using Tern.Markdown;
using Tern.Recovery;
using Tern.Rendering.Resources;
using Tern.Workspaces;

namespace Tern.App.Shell;

/// <summary>Where the app keeps its state. Tests point every location at a temporary folder.</summary>
public sealed record AppPaths(string SettingsDirectory, string RecoveryDirectory, string StateDirectory, string IndexDirectory)
{
    public static AppPaths Default()
    {
        if (Environment.GetEnvironmentVariable("TERN_DATA_ROOT") is { Length: > 0 } isolatedRoot)
            return Under(Path.GetFullPath(isolatedRoot));
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tern");
        var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tern");
        return new AppPaths(roaming, RecoveryStore.DefaultDirectory, local, Path.Combine(local, "index"));
    }

    public static AppPaths Under(string root) =>
        new(Path.Combine(root, "settings"), Path.Combine(root, "recovery"), Path.Combine(root, "state"), Path.Combine(root, "index"));
}

/// <summary>The application's state and workflows: tabs, workspace, recovery, session restore and link handling.</summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private const int MaxClosedTabs = 20;

    private readonly IShellDialogs _dialogs;
    private readonly IExternalLauncher _launcher;
    private readonly DocumentFileService _files = new();
    private readonly SettingsStore _settingsStore;
    private readonly RecoveryScheduler _recovery;
    private readonly SessionStore _sessionStore;
    private readonly LinkedImageStore _linkedImages;
    private readonly Stack<string> _closedTabs = new();
    private readonly Dictionary<string, FileViewState> _viewStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<DocumentTab, FileViewState> _tabViewStates = [];
    private readonly Lock _identityGate = new();
    private Task _identityTail = Task.CompletedTask;
    private bool _disposed;

    // Deterministic interleaving for identity/Save As contract tests; never set by production.
    internal Func<Task>? BeforeSaveAsWrite { get; set; }

    private async Task<T> SerializeIdentityAsync<T>(Func<Task<T>> action,
        DocumentOperation operation = DocumentOperation.None, DocumentTab? document = null)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (_identityGate) { previous = _identityTail; _identityTail = completion.Task; }
        try
        {
            await previous.ConfigureAwait(true);
            IsBusy = true;
            _documentOperation = operation;
            _operationDocument = document;
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(ActiveDocumentState));
            return await action().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
            _documentOperation = DocumentOperation.None;
            _operationDocument = null;
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(ActiveDocumentState));
            completion.TrySetResult();
        }
    }

    [ObservableProperty]
    private DocumentTab? _activeTab;

    [ObservableProperty]
    private WorkspaceSession? _workspace;

    [ObservableProperty]
    private ViewMode _viewMode;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isIndexing;

    public ShellViewModel(AppPaths paths, IShellDialogs dialogs, IExternalLauncher launcher)
    {
        Paths = paths;
        _dialogs = dialogs;
        _launcher = launcher;
        _settingsStore = new SettingsStore(paths.SettingsDirectory);
        Settings = _settingsStore.Load();
        if (_settingsStore.LastLoadProblem is { } problem)
        {
            StatusMessage = problem;
        }

        Registry = CommandRegistry.CreateDefault();
        Registry.LoadOverrides(Settings.KeyBindings);
        Recent = new RecentItemsStore(paths.StateDirectory);
        _sessionStore = new SessionStore(paths.StateDirectory);
        _linkedImages = new LinkedImageStore(paths.StateDirectory);
        RecoveryStore = new RecoveryStore(paths.RecoveryDirectory);
        HistoryStore = new DocumentHistoryStore(Path.Combine(paths.StateDirectory, "history"));
        _recovery = new RecoveryScheduler(RecoveryStore);
        _recovery.CheckpointFailed += (_, ex) => StatusMessage = "Crash-recovery copy could not be written: " + ex.Message;
        _recovery.CheckpointWritten += (_, record) =>
        {
            if (Tabs.FirstOrDefault(t => t.Session.Id == record.SessionId) is { } tab)
            { tab.CheckpointRevision = record.Revision; tab.RecoveryError = null; }
        };
        _recovery.CheckpointWriteFailed += (_, failure) =>
        {
            if (Tabs.FirstOrDefault(t => t.Session.Id == failure.SessionId) is { } tab) tab.RecoveryError = failure.Exception.Message;
        };
        _viewMode = Settings.DefaultViewMode;
    }

    public AppPaths Paths { get; }

    public AppSettings Settings { get; }

    public CommandRegistry Registry { get; }

    public RecentItemsStore Recent { get; }

    public RecoveryStore RecoveryStore { get; }

    public DocumentHistoryStore HistoryStore { get; }

    public ObservableCollection<DocumentTab> Tabs { get; } = [];

    /// <summary>Raised when a link or search result wants the editor to move to a line or heading in a tab.</summary>
    public event EventHandler<NavigationRequest>? NavigationRequested;

    public bool HasWorkspace => Workspace?.IsNamedWorkspace == true;

    public void SaveSettings()
    {
        Settings.KeyBindings = new Dictionary<string, string>(Registry.Overrides);
        _settingsStore.Save(Settings);
    }

    // ---- Tabs ---------------------------------------------------------------------------------------------------

    private DocumentTab? _startupDraft;

    /// <summary>The one disposable startup placeholder; deliberately created notes are never placeholders.</summary>
    public DocumentTab NewStartupDocument()
    {
        if (Tabs.Count != 0) throw new InvalidOperationException("A startup document requires an empty session.");
        return _startupDraft = NewDocument();
    }

    public DocumentTab NewDocument(string text = "", bool standalone = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var tab = DocumentTab.CreateUntitled(_files, Settings.DefaultProfile, text);
        tab.IsStandaloneDraft = standalone;
        Attach(tab);
        return tab;
    }

    /// <summary>Opens a file, or activates its tab if it is already open. Returns null (after telling the user) when it cannot be read.</summary>
    public async Task<DocumentTab?> OpenFileAsync(string path, TextEncodingKind? encoding = null, bool activate = true, bool recordRecent = true,
        DuplicateOpenBehavior duplicateBehavior = DuplicateOpenBehavior.Activate)
    {
        var placeholder = activate && recordRecent && _startupDraft is { FilePath: null } initial
            && ReferenceEquals(ActiveTab, initial) && Tabs.Contains(initial) ? initial : null;
        var beforeApproval = placeholder?.Session.Revision;
        if (placeholder is not null && !await ConfirmCloseAsync([placeholder]).ConfigureAwait(true)) return null;
        if (placeholder is not null && placeholder.Session.Revision != beforeApproval)
        { StatusMessage = "Your note changed while deciding. It was kept; open the file again when ready."; return null; }
        var approvedRevision = placeholder?.Session.Revision;
        var opened = duplicateBehavior == DuplicateOpenBehavior.Activate
            ? await SerializeIdentityAsync(() => OpenFileCoreAsync(path, encoding, activate, recordRecent), DocumentOperation.Loading).ConfigureAwait(true)
            : await OpenExplicitFileAsync(path, encoding, activate, recordRecent, duplicateBehavior).ConfigureAwait(true);
        if (opened is not null && placeholder is not null && !ReferenceEquals(opened, placeholder)
            && Tabs.Contains(placeholder) && placeholder.Session.Revision == approvedRevision)
        {
            RemoveTab(placeholder, retireRecovery: true);
            _startupDraft = null;
        }
        return opened;
    }

    private async Task<DocumentTab?> OpenFileCoreAsync(string path, TextEncodingKind? encoding, bool activate, bool recordRecent)
    {
        if (_disposed) return null;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _dialogs.ShowMessage("Cannot open file", ex.Message);
            return null;
        }
        var existing = Tabs.FirstOrDefault(t => t.FilePath is not null && SamePath(t.FilePath, full));
        if (existing is not null)
        {
            if (activate) ActiveTab = existing;
            if (recordRecent) Recent.AddFile(existing.FilePath!);
            return existing;
        }

        try
        {
            existing = Tabs.FirstOrDefault(t => t.FilePath is { } open && FileIdentity.AreSameFile(open, full));
            if (existing is not null)
            {
                if (activate) ActiveTab = existing;
                if (recordRecent) Recent.AddFile(existing.FilePath!);
                return existing;
            }
            var tab = await DocumentTab.OpenAsync(full, _files, Settings.DefaultProfile, encoding).ConfigureAwait(true);
            tab.LinkedImageFiles = _linkedImages.Get(full);
            if (_disposed) { tab.Dispose(); return null; }
            Attach(tab, activate);
            if (recordRecent) Recent.AddFile(full);
            if (tab.ReadOnlyReason is { } reason)
            {
                StatusMessage = reason;
            }

            return tab;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            _dialogs.ShowMessage("Cannot open file", $"The file could not be opened. Your current documents were kept. Check that the file still exists and you have permission to read it, or choose another Markdown file.\n\n{full}\n\nDetails: {ex.Message}");
            return null;
        }
    }

    private void Attach(DocumentTab tab, bool activate = true)
    {
        Tabs.Add(tab);
        _recovery.Track(tab.Session, () => tab.IsExternalDraft, () => tab.SourceUrl, () => tab.RecoveredFromPath);
        tab.Session.StateChanged += OnTabStateChanged;
        tab.PropertyChanged += OnDocumentMetadataChanged;
        if (activate) ActiveTab = tab;
    }

    private void OnTabStateChanged(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, ActiveTab?.Session)) OnPropertyChanged(nameof(ActiveDocumentState));
        if (sender is not DocumentSession session || session.FilePath is not { } path)
        {
            return;
        }

        foreach (var workspace in OpenWorkspaces.Where(w => w.IsInside(path)))
        {
            var dirty = Tabs.Where(t => t.FilePath is { } open && SamePath(open, path) && t.IsDirty)
                .OrderByDescending(t => ReferenceEquals(t, ActiveTab)).FirstOrDefault();
            if (dirty is not null) workspace.SetUnsavedText(path, dirty.CurrentText);
            else workspace.ClearUnsavedText(path);
        }
    }

    /// <summary>Closes a tab after asking about unsaved work. Returns false if the user cancelled.</summary>
    public async Task<bool> CloseTabAsync(DocumentTab tab)
    {
        if (!Tabs.Contains(tab)) return true;
        if (!await ConfirmCloseAsync([tab]).ConfigureAwait(true)) return false;
        RemoveTab(tab, retireRecovery: true);
        return true;
    }

    private void RemoveTab(DocumentTab tab, bool retireRecovery)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        if (tab.FilePath is { } path)
        {
            _closedTabs.Push(path);
            while (_closedTabs.Count > MaxClosedTabs)
            {
                // Stack<T> has no tail trim; rebuilding at a small fixed size is cheap.
                var keep = _closedTabs.Take(MaxClosedTabs).Reverse().ToArray();
                _closedTabs.Clear();
                foreach (var p in keep)
                {
                    _closedTabs.Push(p);
                }
            }

            foreach (var workspace in OpenWorkspaces)
            {
                var remaining = Tabs.FirstOrDefault(t => !ReferenceEquals(t, tab) && t.FilePath is { } open && SamePath(open, path) && t.IsDirty);
                if (remaining is not null) workspace.SetUnsavedText(path, remaining.CurrentText);
                else workspace.ClearUnsavedText(path);
            }
        }

        tab.Session.StateChanged -= OnTabStateChanged;
        tab.PropertyChanged -= OnDocumentMetadataChanged;
        _recovery.Untrack(tab.Session, retireRecovery);
        _tabViewStates.Remove(tab);
        Tabs.RemoveAt(index);
        if (ReferenceEquals(ActiveTab, tab))
        {
            ActiveTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        }

        tab.Dispose();
    }

    public async Task<DocumentTab?> ReopenClosedTabAsync()
    {
        while (_closedTabs.Count > 0)
        {
            var path = _closedTabs.Pop();
            if (File.Exists(path))
            {
                return await OpenFileAsync(path).ConfigureAwait(true);
            }
        }

        StatusMessage = "No recently closed file to reopen.";
        return null;
    }

    /// <summary>Asks about every unsaved tab; true when the app may exit.</summary>
    public Task<bool> CloseAllAsync() => ConfirmCloseAsync(Tabs.ToList());

    public void NextTab() => StepTab(1);

    public void PreviousTab() => StepTab(-1);

    private void StepTab(int delta)
    {
        if (Tabs.Count == 0 || ActiveTab is null)
        {
            return;
        }

        var index = (Tabs.IndexOf(ActiveTab) + delta + Tabs.Count) % Tabs.Count;
        ActiveTab = Tabs[index];
    }

    public void MoveTab(int from, int to)
    {
        if (from < 0 || from >= Tabs.Count || to < 0 || to >= Tabs.Count || from == to)
        {
            return;
        }

        Tabs.Move(from, to);
    }

    // ---- Saving ---------------------------------------------------------------------------------------------------

    private TabSaveOptions SaveOptions => new(Settings.EnsureFinalNewlineOnSave, Settings.TrimTrailingWhitespaceOnSave);

    /// <summary>Saves a tab, asking for a path (untitled) and resolving conflicts through the dialogs. False if not saved.</summary>
    public async Task<bool> SaveTabAsync(DocumentTab tab)
    {
        if (_disposed || !Tabs.Contains(tab)) return false;
        if (tab.IsReadOnly)
        {
            _dialogs.ShowMessage("Read-only document", tab.ReadOnlyReason ?? "This document is read-only.");
            return false;
        }

        if (!tab.IsDirty && tab.Session.Baseline is not null && !DocumentCommandPolicy.RequiresCrlfSave(tab.Session)) return true;

        if (tab.IsUntitled)
        {
            return await SaveTabAsAsync(tab).ConfigureAwait(true);
        }

        var outcome = await SerializeIdentityAsync<SaveOutcome>(async () =>
        {
            if (_disposed || !Tabs.Contains(tab)) return new SaveFailed("The document is closed.");
            return await tab.SaveAsync(SaveOptions).ConfigureAwait(true);
        }, DocumentOperation.Saving, tab).ConfigureAwait(true);
        return await HandleSaveOutcomeAsync(tab, outcome).ConfigureAwait(true);
    }

    public async Task<bool> SaveTabAsAsync(DocumentTab tab)
    {
        if (_disposed || !Tabs.Contains(tab)) return false;
        var path = await _dialogs.PickSavePathAsync(SuggestName(tab), tab.FilePath is null ? Workspace?.Root : Path.GetDirectoryName(tab.FilePath)).ConfigureAwait(true);
        if (path is null)
        {
            return false;
        }

        DiskBaseline? expected;
        var sameSource = SamePath(path, tab.FilePath ?? string.Empty);
        try
        {
            if (tab.IsReadOnly && tab.FilePath is { } protectedPath && FileIdentity.AreSameFile(protectedPath, path))
            {
                _dialogs.ShowMessage("Choose a separate copy", "The original bytes are protected. Choose a different filename for the UTF-8 copy of the displayed text.");
                return false;
            }
            expected = sameSource ? tab.Session.Baseline : await _files.ReadBaselineAsync(path).ConfigureAwait(true);
            if (Tabs.Any(other => !ReferenceEquals(other, tab) && other.FilePath is { } open && FileIdentity.AreSameFile(open, path)))
            {
                _dialogs.ShowMessage("File is already open", "Choose another filename, or edit the document already open at that location.");
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _dialogs.ShowMessage("Cannot verify save destination", ex.Message);
            return false;
        }

        if (expected is not null && !sameSource && !await _dialogs.ConfirmOverwriteAsync(path).ConfigureAwait(true)) return false;
        if (tab.IsReadOnly && !await _dialogs.ConfirmReadOnlyCopyAsync(tab.DisplayName).ConfigureAwait(true)) return false;

        SaveOutcome outcome;
        try
        {
            outcome = await SerializeIdentityAsync<SaveOutcome>(async () =>
            {
                if (_disposed || !Tabs.Contains(tab)) return new SaveFailed("The document was closed before Save As completed.");
                if (Tabs.Any(other => !ReferenceEquals(other, tab) && other.FilePath is { } open && FileIdentity.AreSameFile(open, path)))
                    return new SaveFailed("The destination is already open. Choose another filename.");
                if (BeforeSaveAsWrite is { } beforeWrite) await beforeWrite().ConfigureAwait(true);
                return await tab.SaveAsCheckedAsync(path, expected, SaveOptions).ConfigureAwait(true);
            }, DocumentOperation.Saving, tab).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _dialogs.ShowMessage("Cannot verify save destination", ex.Message);
            return false;
        }

        if (outcome is SaveConflict && !SamePath(path, tab.FilePath ?? string.Empty))
        {
            _dialogs.ShowMessage("Save As destination changed", "The destination changed while Save As was waiting. Nothing was overwritten. Choose the destination again or use another filename.");
            return false;
        }
        return await HandleSaveOutcomeAsync(tab, outcome).ConfigureAwait(true);
    }

    private static string SuggestName(DocumentTab tab)
    {
        if (tab.FilePath is { } path)
        {
            return Path.GetFileName(path);
        }

        if (tab.Session.HasExplicitDraftName)
            return Path.HasExtension(tab.Session.DisplayName) ? tab.Session.DisplayName : tab.Session.DisplayName + ".md";

        var analysis = tab.Analysis;
        var title = analysis is { Headings.Count: > 0 } ? analysis.Headings[0].Text : null;
        if (string.IsNullOrWhiteSpace(title))
        {
            return tab.DisplayName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? tab.DisplayName : tab.DisplayName + ".md";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. title.Where(c => !invalid.Contains(c))]).Trim();
        return cleaned.Length == 0 ? "Untitled.md" : cleaned + ".md";
    }

    private async Task<bool> HandleSaveOutcomeAsync(DocumentTab tab, SaveOutcome outcome)
    {
        switch (outcome)
        {
            case SaveSucceeded:
                Recent.AddFile(tab.FilePath!);
                StatusMessage = "Saved " + tab.DisplayName;
                try
                {
                    await HistoryStore.CaptureAsync(tab.FilePath!, tab.MergeBase).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    StatusMessage = "File saved; local history unavailable: " + ex.Message;
                }
                if (tab.FilePath is { } saved)
                {
                    foreach (var workspace in OpenWorkspaces.Where(w => w.IsInside(saved)).ToList())
                    {
                        await workspace.NotifySavedAsync(saved, tab.MergeBase).ConfigureAwait(true);
                        if (OpenWorkspaces.Contains(workspace))
                        {
                            var dirty = Tabs.Where(t => t.FilePath is { } open && SamePath(open, saved) && t.IsDirty)
                                .OrderByDescending(t => ReferenceEquals(t, ActiveTab)).FirstOrDefault();
                            if (dirty is not null) workspace.SetUnsavedText(saved, dirty.CurrentText);
                        }
                    }
                }

                return true;
            case SaveConflict:
                return await ResolveConflictAsync(tab).ConfigureAwait(true);
            case SaveFailed failed:
                _dialogs.ShowMessage("Save failed", $"{tab.DisplayName} was not saved.\n\n{failed.Reason}\n\nYour text is still open. Save a copy to another location to protect it; a recovery checkpoint may still be pending or unavailable.");
                return false;
            default:
                return false;
        }
    }

    public async Task<int> SaveAllAsync()
    {
        var saved = 0;
        foreach (var tab in Tabs.Where(t => DocumentCommandPolicy.CanExecute(CommandIds.Save, new(t.Session))).ToList())
        {
            if (await SaveTabAsync(tab).ConfigureAwait(true))
            {
                saved++;
            }
        }

        return saved;
    }

    // ---- Disk changes and conflicts -------------------------------------------------------------------------------

    /// <summary>Checks every open file against disk, typically when the window regains focus or the watcher fires.</summary>
    public async Task CheckForExternalChangesAsync()
    {
        foreach (var tab in Tabs.Where(t => !t.IsUntitled).ToList())
        {
            DiskCheckResult result;
            try
            {
                result = await tab.CheckDiskAsync(Settings.AutoReloadCleanDocuments).ConfigureAwait(true);
                tab.SetAccessFailure(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                tab.SetAccessFailure(true);
                // A file mid-write by another tool; the next check will see its final state.
                continue;
            }

            if (result == DiskCheckResult.ReloadedSilently)
            {
                StatusMessage = tab.DisplayName + " was updated from disk.";
            }
            else if (result == DiskCheckResult.Conflict && ReferenceEquals(tab, ActiveTab))
            {
                await ResolveConflictAsync(tab).ConfigureAwait(true);
            }
        }
    }

    /// <summary>Walks the user through a disagreement between the buffer and disk. True when the document ends up saved or reloaded.</summary>
    public async Task<bool> ResolveConflictAsync(DocumentTab tab)
    {
        if (tab.Conflict is null)
        {
            return true;
        }

        if (tab.Conflict.Kind == ConflictKind.DeletedOnDisk)
        {
            if (await _dialogs.AskFileMissingAsync(tab).ConfigureAwait(true) == MissingFileChoice.Close)
            {
                RemoveTab(tab, retireRecovery: false);
                return true;
            }

            tab.AcceptMissingFile();
            return false;
        }

        switch (await _dialogs.AskConflictAsync(tab).ConfigureAwait(true))
        {
            case ConflictChoice.KeepMine:
                return await HandleSaveOutcomeAsync(tab, await tab.KeepMineAndOverwriteAsync(SaveOptions).ConfigureAwait(true)).ConfigureAwait(true);
            case ConflictChoice.TakeDisk:
                return await tab.TakeDiskVersionAsync().ConfigureAwait(true);
            case ConflictChoice.Merge:
                var merge = await tab.BuildMergeAsync().ConfigureAwait(true);
                var merged = await _dialogs.ResolveMergeAsync(tab, merge).ConfigureAwait(true);
                if (merged is null)
                {
                    return false;
                }

                if (!await tab.ApplyMergedTextAsync(merged).ConfigureAwait(true))
                {
                    StatusMessage = "The document or disk changed during the merge. Review the conflict again; nothing was overwritten.";
                    return false;
                }
                return await SaveTabAsync(tab).ConfigureAwait(true);
            case ConflictChoice.SaveCopy:
                var path = await _dialogs.PickSavePathAsync("Copy of " + tab.DisplayName, Path.GetDirectoryName(tab.FilePath)).ConfigureAwait(true);
                if (path is not null)
                {
                    try
                    {
                        var expected = await _files.ReadBaselineAsync(path).ConfigureAwait(true);
                        if (expected is not null && !await _dialogs.ConfirmOverwriteAsync(path).ConfigureAwait(true)) return false;
                        var result = await SerializeIdentityAsync<SaveOutcome>(async () =>
                        {
                            if (_disposed || !Tabs.Contains(tab)) return new SaveFailed("The document was closed before the copy completed.");
                            if (Tabs.Any(open => open.FilePath is { } source && FileIdentity.AreSameFile(source, path)))
                                return new SaveFailed("Choose a separate filename. A copy cannot replace an open source document.");
                            return await tab.SaveCopyCheckedAsync(path, expected).ConfigureAwait(true);
                        }).ConfigureAwait(true);
                        if (result is SaveFailed failed)
                        {
                            _dialogs.ShowMessage("Copy failed", failed.Reason);
                        }
                        else if (result is SaveConflict)
                            _dialogs.ShowMessage("Copy destination changed", "The destination changed before the copy could be committed. Choose it again or use another filename.");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                    { _dialogs.ShowMessage("Copy failed", ex.Message); }
                }

                return false;
            default:
                return false;
        }
    }

    // ---- Workspace --------------------------------------------------------------------------------------------------

    public Task<bool> OpenWorkspaceAsync(string folder, IProgress<Search.IndexProgress>? progress = null, bool recordRecent = true) =>
        SerializeWorkspacesAsync(() => OpenWorkspaceFromRootAsync(folder, progress, recordRecent));

    /// <summary>Browse a physical directory without creating workspace metadata or a workspace history record.</summary>
    public Task<bool> OpenFolderAsync(string folder, bool recordRecent = true) =>
        SerializeWorkspacesAsync(() => OpenWorkspaceCoreAsync(folder, null, recordRecent, namedWorkspace: false));

    private async Task<bool> OpenWorkspaceCoreAsync(string folder, IProgress<Search.IndexProgress>? progress, bool recordRecent, bool namedWorkspace = true)
    {
        if (_disposed) return false;
        try
        {
            var root = WorkspacePaths.Normalize(folder);
            if (!namedWorkspace && IsKnownWorkspace(root))
            {
                StatusMessage = "This folder is a workspace. Use Open Workspace to load it correctly.";
                _dialogs.ShowMessage("Open as workspace", StatusMessage);
                return false;
            }
            IsIndexing = true;
            var definition = namedWorkspace ? await Task.Run(() => WorkspaceDefinition.Load(root)).ConfigureAwait(true) : null;
            var existing = OpenWorkspaces.FirstOrDefault(w => WorkspacePaths.PathsEqual(w.Root, root));
            if (existing is not null)
            {
                var wasNamedWorkspace = existing.IsNamedWorkspace;
                existing.IsNamedWorkspace = namedWorkspace;
                existing.Name = definition?.Name ?? Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
                SelectWorkspace(existing);
                if (recordRecent && namedWorkspace) Recent.AddWorkspace(existing.Root);
                if (recordRecent && namedWorkspace && !wasNamedWorkspace)
                    await RestoreWorkspaceContextAsync(existing, definition?.WorkingContext).ConfigureAwait(true);
                return true;
            }
            if (OpenWorkspaces.Count >= SessionStore.MaxWorkspaces)
            { StatusMessage = $"At most {SessionStore.MaxWorkspaces} workspaces can be open at once. Close a workspace first."; return false; }
            IsIndexing = true;
            var options = new WorkspaceScanOptions { PruneEmptyDirectories = false, IncludeTextFiles = true };
            foreach (var pattern in Settings.WorkspaceExclusions)
            {
                options.ExcludePatterns.Add(pattern);
            }

            var session = await WorkspaceSession.OpenAsync(root, Paths.IndexDirectory, options, Settings.DefaultProfile, progress).ConfigureAwait(true);
            session.IsNamedWorkspace = namedWorkspace;
            session.Name = definition?.Name ?? Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
            session.PreferredViewMode = ViewMode;
            if (_disposed) { session.Dispose(); return false; }
            // Capture before publishing Workspace: observers can resume background tab restoration.
            var dirtyTabs = Tabs.Where(t => t.IsDirty && t.FilePath is not null).ToArray();
            OpenWorkspaces.Add(session);
            Workspace = session;
            if (recordRecent && namedWorkspace) Recent.AddWorkspace(session.Root);
            OnPropertyChanged(nameof(HasWorkspace));
            OnPropertyChanged(nameof(OpenWorkspaces));
            foreach (var tab in dirtyTabs)
            {
                OnTabStateChanged(tab.Session, EventArgs.Empty);
            }

            if (recordRecent && namedWorkspace) await RestoreWorkspaceContextAsync(session, definition?.WorkingContext).ConfigureAwait(true);

            StatusMessage = $"{(namedWorkspace ? "Workspace" : "Folder")}: {session.Root} ({session.Snapshot.Files.Count} documents)";
            if (session.Notices.Count > 0) StatusMessage += ". " + string.Join(" ", session.Notices);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Text.Json.JsonException or Search.FtsUnavailableException)
        {
            StatusMessage = "Could not open " + (namedWorkspace ? "workspace" : "folder") + ": " + ex.Message;
            _dialogs.ShowMessage(namedWorkspace ? "Cannot open workspace" : "Cannot open folder", StatusMessage);
            return false;
        }
        finally
        {
            IsIndexing = false;
        }
    }

    public void CloseWorkspace()
    {
        if (Workspace is { } workspace) DetachWorkspace(workspace);
    }

    // ---- Links ----------------------------------------------------------------------------------------------------

    /// <summary>Acts on a link the user activated in the preview or outline. Nothing leaves the machine without confirmation.</summary>
    public async Task<LinkActivation> ActivateLinkAsync(ResourceDecision decision)
    {
        switch (decision)
        {
            case Anchor anchor:
                NavigationRequested?.Invoke(this, new NavigationRequest(ActiveTab, null, anchor.Fragment, 0));
                return new LinkActivation(LinkOutcome.ScrolledToAnchor, null, anchor.Fragment);
            case LocalFile local:
                if (!local.Exists)
                {
                    var message = "The linked file does not exist: " + local.CanonicalPath;
                    StatusMessage = message;
                    return new LinkActivation(LinkOutcome.FileMissing, local.CanonicalPath, local.Fragment, message);
                }

                if (IsMarkdownPath(local.CanonicalPath))
                {
                    var tab = await OpenFileAsync(local.CanonicalPath).ConfigureAwait(true);
                    if (tab is null)
                    {
                        return new LinkActivation(LinkOutcome.Blocked, local.CanonicalPath, null, "The file could not be opened.");
                    }

                    NavigationRequested?.Invoke(this, new NavigationRequest(tab, null, local.Fragment, 0));
                    return new LinkActivation(LinkOutcome.OpenedDocument, local.CanonicalPath, local.Fragment);
                }

                // Anything that is not a document is handed to the OS only after the user agrees; it could be an executable.
                if (await _dialogs.ConfirmOpenExternalAsync(new Uri(local.CanonicalPath)).ConfigureAwait(true))
                {
                    _launcher.RevealInFileManager(local.CanonicalPath);
                    return new LinkActivation(LinkOutcome.OpenedWithSystem, local.CanonicalPath);
                }

                return new LinkActivation(LinkOutcome.DeclinedExternal, local.CanonicalPath);
            case ExternalLink external:
                if (!Settings.AllowExternalLinkOpening)
                {
                    return new LinkActivation(LinkOutcome.DeclinedExternal, null, null, "Opening external links is turned off in settings.");
                }

                if (await _dialogs.ConfirmOpenExternalAsync(external.Uri).ConfigureAwait(true))
                {
                    _launcher.OpenUri(external.Uri);
                    return new LinkActivation(LinkOutcome.OpenedExternal, external.Uri.AbsoluteUri);
                }

                return new LinkActivation(LinkOutcome.DeclinedExternal, external.Uri.AbsoluteUri);
            case Blocked blocked:
                var text = ResourceDecisionDescriber.Describe(blocked);
                StatusMessage = text;
                return new LinkActivation(LinkOutcome.Blocked, null, null, text);
            default:
                return new LinkActivation(LinkOutcome.Blocked, null, null, "This kind of link cannot be opened.");
        }
    }

    /// <summary>Opens a file and moves to a line, used by search results, backlinks and broken-link entries.</summary>
    public async Task<bool> GoToAsync(string path, int line, int column = 0)
    {
        var tab = await OpenFileAsync(path).ConfigureAwait(true);
        if (tab is null)
        {
            return false;
        }

        NavigationRequested?.Invoke(this, new NavigationRequest(tab, line, null, column));
        return true;
    }

    private static bool IsMarkdownPath(string path) =>
        WorkspaceScanOptions.DefaultMarkdownExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static bool SamePath(string a, string b) =>
        a.Length > 0 && b.Length > 0 && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // ---- Crash recovery -------------------------------------------------------------------------------------------

    /// <summary>Checkpoints left by a previous run that did not exit cleanly.</summary>
    public RecoveryScan FindRecoverable()
    {
        var scan = RecoveryStore.Scan();
        return new RecoveryScan([.. scan.Entries.Where(e => !e.OwnerIsAlive)], scan.Corrupt);
    }

    /// <summary>Reopens a checkpoint as an unsaved tab, so nothing is written to the original file until the user saves.</summary>
    public async Task<DocumentTab> RestoreRecoveryAsync(RecoveryRecord record)
    {
        // Recovery is a separate unsaved copy, never permission to overwrite today's disk version.
        var session = new DocumentSession(null, string.Empty, record.Format, null);
        session.SetDraftName(record.DisplayName);
        session.ApplyEdits([new TextEdit(0, 0, record.Text)]);
        var tab = new DocumentTab(session, _files, Settings.DefaultProfile)
        {
            IsRecovered = true,
            IsExternalDraft = record.IsExternalDraft,
            SourceUrl = record.SourceUrl,
            RecoveredFromPath = record.OriginalPath,
            ResourceDocumentPath = record.IsExternalDraft ? null : record.OriginalPath,
            LinkedImageFiles = record.OriginalPath is { } origin ? _linkedImages.Get(origin) : [],
            RecoveryName = record.DisplayName,
            DisplayName = "Recovered — " + record.DisplayName,
        };
        Attach(tab);
        if (await _recovery.FlushAllAsync().ConfigureAwait(true)
            && RecoveryStore.Scan().Entries.Any(e => e.Record.SessionId == session.Id)) RecoveryStore.Retire(record.SessionId);
        StatusMessage = $"Recovered unsaved changes to {record.DisplayName}.";
        return tab;
    }

    public void DiscardRecovery(RecoveryRecord record) => RecoveryStore.Retire(record.SessionId);

    // ---- Session --------------------------------------------------------------------------------------------------

    public void RememberViewState(DocumentTab tab, int caretOffset, double scrollOffset)
    {
        _tabViewStates[tab] = new FileViewState(caretOffset, scrollOffset);
        if (tab.FilePath is { } path)
        {
            _viewStates[path] = new FileViewState(caretOffset, scrollOffset);
        }
    }

    public FileViewState? GetViewState(string path) => _viewStates.TryGetValue(path, out var state) ? state : null;

    public FileViewState? GetViewState(DocumentTab tab) => _tabViewStates.TryGetValue(tab, out var state) ? state
        : tab.FilePath is { } path ? GetViewState(path) : null;

    public bool SaveSession(WindowLayoutHint? window = null)
    {
        if (_sessionReset) return _sessionStore.Save(SessionSnapshot.Empty);
        var documents = Tabs.Where(t => t.FilePath is not null).ToList();
        var tabs = documents
            .Select(t => new TabState(t.FilePath!, GetViewState(t)?.CaretOffset ?? 0, GetViewState(t)?.ScrollOffset ?? 0))
            .ToList();
        var saved = _sessionStore.Save(new SessionSnapshot(Workspace?.Root, tabs, ActiveTab?.FilePath, window, new Dictionary<string, FileViewState>(_viewStates))
        { ActiveTabIndex = ActiveTab is { } active ? documents.IndexOf(active) : null,
            OpenWorkspaceRoots = OpenWorkspaces.Where(w => w.IsNamedWorkspace).Select(w => w.Root).ToList(),
            OpenFolderRoots = OpenWorkspaces.Where(w => !w.IsNamedWorkspace).Select(w => w.Root).ToList() });
        if (!saved) StatusMessage = "The session could not be saved: " + _sessionStore.LastSaveError?.Message;
        return saved;
    }

    /// <summary>Reopens the previous workspace and tabs. Missing files are skipped; returns the saved window layout, if any.</summary>
    public async Task<WindowLayoutHint?> RestoreSessionAsync(Action? activeDocumentReady = null)
    {
        if (!Settings.RestoreSession)
        {
            return null;
        }

        var snapshot = _sessionStore.Load();
        foreach (var (path, state) in snapshot.FileViews)
        {
            _viewStates[path] = state;
        }

        var restorable = snapshot.Tabs.Select((state, index) => (State: state, Index: index)).Where(t => File.Exists(t.State.Path)).ToArray();
        var first = restorable.FirstOrDefault(t => snapshot.ActiveTabIndex == t.Index);
        if (first.State is null) first = restorable.FirstOrDefault(t => snapshot.ActiveTab is { } active && SamePath(t.State.Path, active));
        if (first.State is null) first = restorable.FirstOrDefault();
        var restored = new Dictionary<int, DocumentTab>();
        async Task RestoreTabAsync(TabState state, int index, bool activate)
        {
            _viewStates[state.Path] = new FileViewState(state.CaretOffset, state.ScrollOffset);
            var tab = await OpenFileAsync(state.Path, activate: activate, recordRecent: false,
                duplicateBehavior: DuplicateOpenBehavior.NewTab).ConfigureAwait(true);
            if (tab is not null)
            {
                _tabViewStates[tab] = new FileViewState(state.CaretOffset, state.ScrollOffset);
                restored[index] = tab;
            }
        }
        if (first.State is not null) await RestoreTabAsync(first.State, first.Index, activate: true).ConfigureAwait(true);
        if (_disposed) return snapshot.Window;
        activeDocumentReady?.Invoke();
        async Task RestoreOtherDocumentsAsync()
        {
            foreach (var tab in restorable)
            {
                if (_disposed) break;
                if (tab.Index != first.Index)
                    await RestoreTabAsync(tab.State, tab.Index, activate: false).ConfigureAwait(true);
            }
        }

        async Task RestoreWorkspaceRootsAsync()
        {
            var roots = snapshot.OpenWorkspaceRoots.Concat(snapshot.LastWorkspace is { } previous && !snapshot.OpenFolderRoots.Contains(previous, WorkspacePaths.Comparer) ? [previous] : [])
                .Select(WorkspacePaths.Normalize).Distinct(WorkspacePaths.Comparer).Take(SessionStore.MaxWorkspaces);
            foreach (var root in roots)
            {
                if (_disposed) break;
                if (Directory.Exists(root)) await OpenWorkspaceAsync(root, recordRecent: false).ConfigureAwait(true);
            }
            foreach (var root in snapshot.OpenFolderRoots.Take(SessionStore.MaxWorkspaces))
            {
                if (_disposed) break;
                if (Directory.Exists(root)) await OpenFolderAsync(root, recordRecent: false).ConfigureAwait(true);
            }
        }

        // Both paths retain their own serialization gates and UI continuations.
        // Index preparation can overlap background file I/O after the active tab is usable.
        await Task.WhenAll(RestoreWorkspaceRootsAsync(), RestoreOtherDocumentsAsync()).ConfigureAwait(true);
        if (_disposed) return snapshot.Window;
        // Restore visual tab order without activating/rendering every background document.
        for (var i = 0; i < restorable.Length; i++)
        {
            var index = restored.TryGetValue(restorable[i].Index, out var tab) ? Tabs.IndexOf(tab) : -1;
            if (index >= 0 && index != i) Tabs.Move(index, Math.Min(i, Tabs.Count - 1));
        }

        if (snapshot.LastWorkspace is { } activeRoot && OpenWorkspaces.FirstOrDefault(w => WorkspacePaths.PathsEqual(w.Root, activeRoot)) is { } activeWorkspace)
            SelectWorkspace(activeWorkspace);

        return snapshot.Window;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var workspace in OpenWorkspaces.ToList()) DetachWorkspace(workspace);
        _recovery.Dispose();
        HistoryStore.Dispose();
        foreach (var tab in Tabs)
        {
            tab.Dispose();
        }
    }
}

/// <summary>A request for the editor to reveal a position: either a 1-based line, or a heading anchor.</summary>
public sealed record NavigationRequest(DocumentTab? Tab, int? Line, string? Anchor, int Column);
