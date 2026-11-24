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
    internal DocumentFileService FileService => _files;
    private readonly SettingsStore _settingsStore;
    private readonly RecoveryScheduler _recovery;
    private readonly SessionStore _sessionStore;
    private readonly WorkspaceContextStore _contextStore;
    private readonly LinkedImageStore _linkedImages;
    private readonly Stack<string> _closedTabs = new();
    private readonly Dictionary<string, FileViewState> _viewStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<DocumentTab, FileViewState> _tabViewStates = [];
    private readonly Lock _identityGate = new();
    private Task _identityTail = Task.CompletedTask;
    private bool _disposed;

    // Deterministic interleaving for identity/Save As contract tests; never set by production.
    internal Func<Task>? BeforeSaveAsWrite { get; set; }
    internal Func<Task>? BeforeSaveWrite { get; set; }

    private async Task<T> SerializeIdentityAsync<T>(Func<Task<T>> action,
        DocumentOperation operation = DocumentOperation.None, DocumentTab? document = null, bool quiet = false,
        string? activityKind = null, ActivityScope? parentActivity = null)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (_identityGate) { previous = _identityTail; _identityTail = completion.Task; }
        if (quiet)
        {
            // Background disk checks wait their turn behind saves and moves but must not flash the busy state.
            try
            {
                await previous.ConfigureAwait(true);
                return await action().ConfigureAwait(true);
            }
            finally { completion.TrySetResult(); }
        }
        var kind = activityKind ?? (operation == DocumentOperation.Saving ? "Saving document"
            : operation == DocumentOperation.Loading ? "Opening document" : "Updating files");
        using var ownedActivity = parentActivity is null ? Activities.Begin(kind, document?.DisplayName, queued: !previous.IsCompleted) : null;
        var activity = parentActivity ?? ownedActivity!;
        if (!previous.IsCompleted) activity.Queue("Queued: " + kind);
        try
        {
            await previous.ConfigureAwait(true);
            activity.Report(kind);
            IsBusy = true;
            _documentOperation = operation;
            _operationDocument = document;
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(ActiveDocumentState));
            var result = await action().ConfigureAwait(true);
            if (result is SaveFailed { Exception: OutsideAttachmentsException } && parentActivity is not null)
                activity.WaitForUser("Review attachment choices before saving");
            else if (result is SaveFailed failed) activity.Fail(failed.Reason);
            else if (result is SaveConflict && parentActivity is not null)
                activity.WaitForUser("The file changed on disk; review the conflict");
            else if (result is SaveConflict) activity.Fail("The file changed on disk. Review the conflict before saving.");
            else if (result is SaveCancelled) activity.Cancel("Canceled; the original document and attachments were kept.");
            else if (result is false) activity.Cancel();
            return result;
        }
        catch (OperationCanceledException) { activity.Cancel(); throw; }
        catch (Exception ex) { activity.Fail(ex.Message); throw; }
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
            SetStatus(problem, StatusSeverity.Warning);
            ShowBanner(problem, StatusSeverity.Warning);
        }

        Registry = CommandRegistry.CreateDefault();
        Registry.LoadOverrides(Settings.KeyBindings);
        Recent = new RecentItemsStore(paths.StateDirectory);
        _sessionStore = new SessionStore(paths.StateDirectory);
        _contextStore = new WorkspaceContextStore(paths.StateDirectory);
        _linkedImages = new LinkedImageStore(paths.StateDirectory);
        RecoveryStore = new RecoveryStore(paths.RecoveryDirectory, protectCopies: true);
        DiscardedDrafts = new RecoveryStore(Path.Combine(paths.RecoveryDirectory, "discarded"), protectCopies: true);
        HistoryStore = new DocumentHistoryStore(Path.Combine(paths.StateDirectory, "history"), Math.Clamp(Settings.HistoryBudgetMiB, 1, 1024) * 1024L * 1024L,
            protectCopies: true, allowPersistence: path => !IsPrivatePath(path));
        // Retention sweep for history that can no longer be reached; best effort and never blocks startup.
        _ = Task.Run(async () => { try { await HistoryStore.SweepAsync(TimeSpan.FromDays(90)).ConfigureAwait(false); } catch (Exception) { } });
        _recovery = new RecoveryScheduler(RecoveryStore);
        _recovery.CheckpointFailed += (_, ex) => StatusMessage = "Crash-recovery copy could not be written: " + PlainProblem(ex);
        _recovery.RetireFailed += (_, _) => SetStatus("Tern could not remove some old recovery copies. You may see them again after a restart. You can discard them then.", StatusSeverity.Warning);
        _recovery.CheckpointWritten += (_, record) =>
        {
            if (Tabs.FirstOrDefault(t => t.Session.Id == record.SessionId) is { } tab)
            { tab.CheckpointRevision = record.Revision; tab.RecoveryError = null; }
        };
        _recovery.CheckpointWriteFailed += (_, failure) =>
        {
            if (Tabs.FirstOrDefault(t => t.Session.Id == failure.SessionId) is { } tab) tab.RecoveryError = failure.Exception.Message;
            using var activity = Activities.Begin("Recovery checkpoint", failure.SessionId.ToString());
            activity.Fail(failure.Exception.Message);
        };
        _viewMode = Settings.DefaultViewMode;
        InitializeSessionPersistence();
        try { RecentlyDiscarded(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SetStatus("Tern could not remove some old discarded drafts. Your notes are safe. Check that you can write to Tern's data folder: " + PlainProblem(ex), StatusSeverity.Warning); }
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

    /// <summary>
    /// Writes preferences. Never throws: a locked or read-only settings folder leaves the current session's settings
    /// in effect, shows a warning, and returns false so callers that must roll back can do so.
    /// </summary>
    public bool SaveSettings()
    {
        try
        {
            Settings.KeyBindings = new Dictionary<string, string>(Registry.Overrides);
            _settingsStore.Save(Settings);
            ConfigureAutoSave();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        {
            SetStatus("Settings could not be saved: " + PlainProblem(ex) + " Your changes apply until you close Tern.", StatusSeverity.Warning);
            return false;
        }
    }

    public async Task<bool> RetryRecoveryAsync(DocumentTab tab)
    {
        if (_disposed || !Tabs.Contains(tab)) return false;
        using var operation = BeginStatusOperation("Retrying recovery checkpoint", tab.DisplayName);
        var saved = await _recovery.RetryAsync(tab.Session).ConfigureAwait(true);
        if (!saved) operation.Fail(tab.RecoveryError ?? "The recovery checkpoint could not be written. Your open text is kept.");
        return saved;
    }

    // ---- Tabs ---------------------------------------------------------------------------------------------------

    private DocumentTab? _startupDraft;

    /// <summary>The one disposable startup placeholder; deliberately created notes are never placeholders.</summary>
    public DocumentTab NewStartupDocument()
    {
        if (Tabs.Count != 0) throw new InvalidOperationException("A startup document requires an empty session.");
        return _startupDraft = NewDocument();
    }

    public DocumentTab NewDocument(string text = "", bool standalone = false, TextFileFormat? preservedFormat = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var tab = DocumentTab.CreateUntitled(_files, Settings.DefaultProfile, text, preservedFormat ?? Settings.NewFileFormat);
        if (preservedFormat is not null) tab.Session.ChangeFormat(preservedFormat);
        tab.IsStandaloneDraft = standalone;
        tab.DraftWorkspaceRoot = standalone ? null : Workspace?.Root;
        Attach(tab);
        return tab;
    }

    /// <summary>Opens a file, or activates its tab if it is already open. Returns null (after telling the user) when it cannot be read.</summary>
    public async Task<DocumentTab?> OpenFileAsync(string path, TextEncodingKind? encoding = null, bool activate = true, bool recordRecent = true,
        DuplicateOpenBehavior duplicateBehavior = DuplicateOpenBehavior.Activate, CreatedFileReceipt? createdFile = null)
    {
        var placeholder = activate && recordRecent && _startupDraft is { FilePath: null } initial
            && ReferenceEquals(ActiveTab, initial) && Tabs.Contains(initial) ? initial : null;
        var beforeApproval = placeholder?.Session.Revision;
        if (placeholder is not null && !await ConfirmCloseAsync([placeholder]).ConfigureAwait(true)) return null;
        if (placeholder is not null && placeholder.Session.Revision != beforeApproval)
        { StatusMessage = "Your note changed while deciding. It was kept; open the file again when ready."; return null; }
        var approvedRevision = placeholder?.Session.Revision;
        var opened = duplicateBehavior == DuplicateOpenBehavior.Activate
            ? await SerializeIdentityAsync(() => OpenFileCoreAsync(path, encoding, activate, recordRecent, createdFile: createdFile), DocumentOperation.Loading).ConfigureAwait(true)
            : await OpenExplicitFileAsync(path, encoding, activate, recordRecent, duplicateBehavior).ConfigureAwait(true);
        if (opened is not null && placeholder is not null && !ReferenceEquals(opened, placeholder)
            && Tabs.Contains(placeholder) && placeholder.Session.Revision == approvedRevision)
        {
            RemoveTab(placeholder, retireRecovery: true);
            _startupDraft = null;
        }
        return opened;
    }

    private async Task<DocumentTab?> OpenFileCoreAsync(string path, TextEncodingKind? encoding, bool activate, bool recordRecent,
        bool openNewCopy = false, CreatedFileReceipt? createdFile = null)
    {
        if (_disposed) return null;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _dialogs.ShowMessage("Cannot open file", PlainProblem(ex));
            return null;
        }
        var existing = openNewCopy ? null : Tabs.FirstOrDefault(t => t.FilePath is not null && SamePath(t.FilePath, full));
        if (existing is not null)
        {
            if (activate) SelectTab(existing, TabActivationReason.User);
            if (recordRecent) Recent.AddFile(existing.FilePath!);
            return existing;
        }

        try
        {
            if (!openNewCopy)
            {
                existing = await FindOpenTabAsync(full).ConfigureAwait(true);
                if (_disposed) return null;
                if (existing is not null)
                {
                    if (activate) SelectTab(existing, TabActivationReason.User);
                    if (recordRecent) Recent.AddFile(existing.FilePath!);
                    return existing;
                }
            }
            var tab = await DocumentTab.OpenAsync(full, _files, Settings.DefaultProfile, encoding, createdFile: createdFile).ConfigureAwait(true);
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
            _dialogs.ShowMessage("Cannot open file", $"The file could not be opened. Your current documents were kept. Check that the file still exists and you have permission to read it, or choose another Markdown file.\n\n{full}\n\nDetails: {PlainProblem(ex)}");
            return null;
        }
    }

    private void Attach(DocumentTab tab, bool activate = true)
    {
        Tabs.Add(tab);
        _recovery.Track(tab.Session, () => tab.IsExternalDraft, () => tab.SourceUrl, () => tab.RecoveredFromPath, () => !IsPrivateDocument(tab));
        tab.Session.StateChanged += OnTabStateChanged;
        tab.Session.ListenerFailed += OnSessionListenerFailed;
        tab.PropertyChanged += OnDocumentMetadataChanged;
        if (activate) ActiveTab = tab;
    }

    private void OnTabStateChanged(object? sender, EventArgs e)
    {
        if (Tabs.FirstOrDefault(tab => ReferenceEquals(tab.Session, sender)) is { } changed) ScheduleAutoSave(changed);
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

        _movedTargets.Remove(tab);

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
        CancelAutoSave(tab);
        tab.Session.ListenerFailed -= OnSessionListenerFailed;
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
        SelectTab(Tabs[index], TabActivationReason.User);
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
    public Task<bool> SaveTabAsync(DocumentTab tab) =>
        TrackActivityAsync("Saving document", tab.DisplayName, activity => SaveTabCoreAsync(tab, activity));

    private async Task<bool> SaveTabCoreAsync(DocumentTab tab, ActivityScope activity)
    {
        if (_disposed || !Tabs.Contains(tab)) return false;
        if (tab.IsReadOnly)
        {
            activity.Fail(tab.ReadOnlyReason ?? "This document is read-only.");
            _dialogs.ShowMessage("Read-only document", tab.ReadOnlyReason ?? "This document is read-only.");
            return false;
        }

        if (!tab.IsDirty && tab.Session.Baseline is not null && !DocumentCommandPolicy.RequiresCrlfSave(tab.Session))
        {
            activity.Complete(outcome: "No changes to save");
            return true;
        }

        if (tab.IsUntitled)
        {
            return await SaveTabAsCoreAsync(tab, activity).ConfigureAwait(true);
        }

        activity.WaitForUser("Confirm the save location if requested");
        if (!await ConfirmSaveLocationAsync(tab).ConfigureAwait(true)) return false;

        var outcome = await SerializeIdentityAsync<SaveOutcome>(async () =>
        {
            if (_disposed || !Tabs.Contains(tab)) return new SaveFailed("The document is closed.");
            if (BeforeSaveWrite is { } beforeWrite) await beforeWrite().ConfigureAwait(true);
            activity.Report("Preserving the previous disk version");
            await CaptureReplacedVersionAsync(tab, tab.FilePath!, tab.Session.Baseline, externalReplaced: false).ConfigureAwait(true);
            activity.Report("Writing and verifying document");
            return await tab.SaveAsync(SaveOptions).ConfigureAwait(true);
        }, DocumentOperation.Saving, tab, parentActivity: activity).ConfigureAwait(true);
        return await HandleSaveOutcomeAsync(tab, outcome, activity).ConfigureAwait(true);
    }

    public async Task<DocumentTab?> FindOpenTabAsync(string path, DocumentTab? except = null)
    {
        var full = Path.GetFullPath(path);
        while (!_disposed)
        {
            var candidates = Tabs.Where(tab => !ReferenceEquals(tab, except) && tab.FilePath is not null)
                .Select(tab => (Tab: tab, Path: tab.FilePath!)).ToArray();
            var direct = candidates.FirstOrDefault(candidate => SamePath(candidate.Path, full)).Tab;
            if (direct is not null) return direct;
            var sameFile = await FileIdentity.FindSameFileAsync(candidates.Select(candidate => candidate.Path), full).ConfigureAwait(true);
            if (_disposed) return null;
            var current = Tabs.Where(tab => !ReferenceEquals(tab, except) && tab.FilePath is not null)
                .Select(tab => (Tab: tab, Path: tab.FilePath!));
            // Identity changes or newly opened tabs during the worker check require a fresh check.
            if (!candidates.SequenceEqual(current)) continue;
            return candidates.FirstOrDefault(candidate => candidate.Path == sameFile).Tab;
        }
        return null;
    }

    /// <summary>Puts <paramref name="text"/> into an open document as a single undoable edit; nothing is written to disk until the user saves.</summary>
    public bool ReplaceContents(DocumentTab tab, string text, string source)
    {
        if (!Tabs.Contains(tab)) return false;
        if (tab.IsReadOnly)
        {
            SetStatus($"{tab.DisplayName} is read-only, so its contents were not replaced.", StatusSeverity.Warning);
            return false;
        }

        if (!string.Equals(tab.CurrentText, text, StringComparison.Ordinal))
            tab.ApplyEdits([new TextEdit(0, tab.CurrentText.Length, text)]);
        SelectTab(tab, TabActivationReason.Programmatic);
        SetStatus($"Replaced the contents of {tab.DisplayName} with {source}. Undo (Ctrl+Z) brings the previous text back; save to keep the change.", StatusSeverity.Success);
        return true;
    }

    public Task<bool> SaveTabAsAsync(DocumentTab tab) =>
        TrackActivityAsync("Save As", tab.DisplayName, activity => SaveTabAsCoreAsync(tab, activity));

    private async Task<bool> SaveTabAsCoreAsync(DocumentTab tab, ActivityScope activity)
    {
        if (_disposed || !Tabs.Contains(tab)) return false;
        activity.WaitForUser("Choose where to save the document");
        var path = await _dialogs.PickSavePathAsync(SuggestName(tab), SaveFolder(tab)).ConfigureAwait(true);
        if (path is null)
        {
            return false;
        }
        activity.Report("Validating save destination");

        DiskBaseline? expected;
        var sameSource = SamePath(path, tab.FilePath ?? string.Empty);
        var previousPath = tab.FilePath;
        try
        {
            if (tab.IsReadOnly && tab.FilePath is { } protectedPath
                && await FileIdentity.FindSameFileAsync([protectedPath], path).ConfigureAwait(true) is not null)
            {
                activity.Fail("Choose a separate filename; the original bytes are protected.");
                _dialogs.ShowMessage("Choose a separate copy", "The original bytes are protected. Choose a different filename for the UTF-8 copy of the displayed text.");
                return false;
            }
            expected = sameSource ? tab.Session.Baseline : await _files.ReadBaselineAsync(path).ConfigureAwait(true);
            if (await FindOpenTabAsync(path, tab).ConfigureAwait(true) is not null)
            {
                activity.Fail("The destination is already open; choose a different filename.");
                _dialogs.ShowMessage("File is already open", tab.IsRecovered
                    ? "Choose another filename, or open Show history on this recovered tab and use Replace open original to put a saved version into the document that is already open."
                    : "Choose another filename, or edit the document already open at that location.");
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            activity.Fail(ex.Message);
            _dialogs.ShowMessage("Cannot verify save destination", PlainProblem(ex));
            return false;
        }

        if (expected is not null && !sameSource)
        {
            activity.WaitForUser("Confirm replacement of the chosen destination");
            if (!await _dialogs.ConfirmOverwriteAsync(path).ConfigureAwait(true)) return false;
        }
        if (tab.IsReadOnly)
        {
            activity.WaitForUser("Confirm saving a separate UTF-8 copy");
            if (!await _dialogs.ConfirmReadOnlyCopyAsync(tab.DisplayName).ConfigureAwait(true)) return false;
        }

        SaveOutcome outcome;
        try
        {
            outcome = await SaveWithResourceChoiceAsync(tab, (outsideChoice, approvedOutside) => SerializeIdentityAsync<SaveOutcome>(async () =>
            {
                if (_disposed || !Tabs.Contains(tab)) return new SaveFailed("The document was closed before Save As completed.");
                if (await FindOpenTabAsync(path, tab).ConfigureAwait(true) is not null)
                    return new SaveFailed("The destination is already open. Choose another filename.");
                if (_disposed || !Tabs.Contains(tab)) return new SaveFailed("The document was closed before Save As completed.");
                if (BeforeSaveAsWrite is { } beforeWrite) await beforeWrite().ConfigureAwait(true);
                activity.Report("Preserving the previous destination version");
                await CaptureReplacedVersionAsync(tab, path, expected, externalReplaced: !sameSource).ConfigureAwait(true);
                activity.Report("Writing and verifying document");
                return await tab.SaveAsCheckedAsync(path, expected, SaveOptions with { OutsideAttachments = outsideChoice, ApprovedOutsideAttachments = approvedOutside }).ConfigureAwait(true);
            }, DocumentOperation.Saving, tab, parentActivity: activity), activity).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            activity.Fail(ex.Message);
            _dialogs.ShowMessage("Cannot verify save destination", PlainProblem(ex));
            return false;
        }

        if (outcome is SaveConflict && !SamePath(path, tab.FilePath ?? string.Empty))
        {
            activity.Fail("The Save As destination changed. Nothing was overwritten.");
            _dialogs.ShowMessage("Save As destination changed", "The destination changed while Save As was waiting. Nothing was overwritten. Choose the destination again or use another filename.");
            return false;
        }
        var saveWarnings = new List<string>();
        var saved = await HandleSaveOutcomeAsync(tab, outcome, activity,
            finishActivity: previousPath is null || SamePath(previousPath, tab.FilePath ?? string.Empty), completionWarnings: saveWarnings).ConfigureAwait(true);
        if (saved && previousPath is null && tab.FilePath is { } firstPath)
        {
            var warning = StatusSeverity == StatusSeverity.Warning ? " · " + StatusMessage : "";
            var message = "Saved to " + Path.GetDirectoryName(firstPath) + warning;
            var reveal = new BannerAction("Show in folder", () =>
                {
                    try { _launcher.RevealInFileManager(firstPath); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                    { ShowBanner("The file is saved, but its folder could not be opened: " + PlainProblem(ex), StatusSeverity.Warning); }
                });
            if (Banner?.Severity == StatusSeverity.Error)
                Activities.Post(message, warning.Length > 0 ? ActivitySeverity.Warning : ActivitySeverity.Success, [reveal]);
            else ShowBanner(message, warning.Length > 0 ? StatusSeverity.Warning : StatusSeverity.Success, reveal);
        }
        if (saved && previousPath is not null && tab.FilePath is { } savedPath && !SamePath(previousPath, savedPath))
        {
            activity.Report("Updating references for the new document path");
            var warnings = await ApplyIdentityChangeAsync(new IdentityChange(previousPath, savedPath, IdentityChangeKind.SaveAs)).ConfigureAwait(true);
            saveWarnings.AddRange(warnings);
            if (saveWarnings.Count > 0) SetStatus("Saved. " + string.Join(" ", saveWarnings), StatusSeverity.Warning);
            activity.Complete(saveWarnings.Count > 0 ? string.Join("; ", saveWarnings) : null,
                warning: saveWarnings.Count > 0, outcome: saveWarnings.Count > 0 ? "Saved with warning" : "Saved");
        }
        return saved;
    }

    /// <summary>Save As opens in the file's own folder; a recovered draft uses the folder of the file it came from while that folder still exists.</summary>
    internal string? SaveFolder(DocumentTab tab)
    {
        if (tab.FilePath is { } path) return Path.GetDirectoryName(path);
        if (tab is { IsRecovered: true, IsExternalDraft: false, RecoveredFromPath: { } origin }
            && Path.GetDirectoryName(origin) is { Length: > 0 } folder && Directory.Exists(folder)) return folder;
        return Workspace?.Root;
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

    private async Task<bool> HandleSaveOutcomeAsync(DocumentTab tab, SaveOutcome outcome, ActivityScope? activity = null,
        bool finishActivity = true, ICollection<string>? completionWarnings = null)
    {
        switch (outcome)
        {
            case SaveSucceeded:
                return await CompleteSuccessfulSaveAsync(tab, activity, finishActivity, completionWarnings).ConfigureAwait(true);
            case SaveConflict:
                activity?.WaitForUser("Review the changed file before choosing what to keep");
                var resolved = await ResolveConflictAsync(tab, afterSave: true).ConfigureAwait(true);
                if (resolved) activity?.Complete(outcome: "Conflict resolved");
                return resolved;
            case SaveFailed failed:
                activity?.Fail(failed.Reason);
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

    /// <summary>Checks open files against disk, typically when the window regains focus or a watcher fires. Pass a tab to check only that file.</summary>
    public async Task CheckForExternalChangesAsync(DocumentTab? only = null)
    {
        var targets = only is null
            ? Tabs.Where(t => !t.IsUntitled).ToList()
            : Tabs.Contains(only) && !only.IsUntitled ? [only] : [];
        foreach (var tab in targets)
        {
            await CheckTabForExternalChangeAsync(tab).ConfigureAwait(true);
        }
    }

    private async Task CheckTabForExternalChangeAsync(DocumentTab tab)
    {
        DiskCheckResult result;
        var wasChanged = tab.IsChangedOnDisk;
        var wasInaccessible = tab.IsInaccessible;
        try
        {
            // Queued behind any save, move or rename of this document so a half-finished write is never mistaken for an outside change.
            result = await SerializeIdentityAsync(async () =>
            {
                if (_disposed || !Tabs.Contains(tab)) return DiskCheckResult.Unchanged;
                var outcome = await tab.CheckDiskAsync(Settings.AutoReloadCleanDocuments).ConfigureAwait(true);
                tab.SetAccessFailure(outcome == DiskCheckResult.Unreachable);
                return outcome;
            }, quiet: true).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            tab.SetAccessFailure(true);
            // A file mid-write by another tool; the next check will see its final state.
            return;
        }

        if (result == DiskCheckResult.ReloadedSilently)
        {
            SetStatus(tab.DisplayName + " was updated because the file changed on disk.", StatusSeverity.Info);
        }
        else if (result == DiskCheckResult.Unreachable)
        {
            // Quiet by design: the save-state text says "Unavailable — will retry" and the next activation checks again.
            if (!wasInaccessible) Activities.Post(tab.DisplayName + " is unavailable — will retry when you return to it.", ActivitySeverity.Warning);
        }
        else if (result == DiskCheckResult.Conflict && tab.Conflict?.Kind == ConflictKind.DeletedOnDisk && _movedTargets.ContainsKey(tab))
        {
            if (ReferenceEquals(tab, ActiveTab)) ShowMovedBanner(tab);
        }
        else if (result == DiskCheckResult.Conflict && tab.Conflict?.Kind == ConflictKind.DeletedOnDisk && ReferenceEquals(tab, ActiveTab) && !_confirmingClose)
        {
            await ResolveConflictAsync(tab).ConfigureAwait(true);
        }
        else if (result == DiskCheckResult.Conflict && tab.IsChangedOnDisk && !wasChanged)
        {
            // Never a modal here (B-10, S-07): the tab badge and the document bar carry the decision until the user acts or saves.
            SetStatus(tab.DisplayName + " changed on disk. Compare, reload or keep your version from the bar above the document.", StatusSeverity.Warning);
        }
    }

    /// <summary>
    /// Makes a tab current. Only <see cref="TabActivationReason.User"/> activation looks at the disk; closing, restoring and
    /// removing tabs switch programmatically and must never start a prompt (D-02).
    /// </summary>
    public void SelectTab(DocumentTab tab, TabActivationReason reason)
    {
        if (!Tabs.Contains(tab)) return;
        ActiveTab = tab;
        if (reason == TabActivationReason.User && !tab.IsUntitled && !_confirmingClose) _ = CheckActivatedTabAsync(tab);
    }

    private async Task CheckActivatedTabAsync(DocumentTab tab)
    {
        try { await CheckForExternalChangesAsync(tab).ConfigureAwait(true); }
        catch (Exception ex) when (ex is not OperationCanceledException) { ReportUnexpectedError("check the file for changes", ex); }
    }

    /// <summary>The bar's Reload: replaces the buffer with the disk version, confirming first when that discards unsaved edits.</summary>
    public Task<bool> ReloadFromDiskAsync(DocumentTab tab) =>
        TrackActivityAsync("Reloading document", tab.DisplayName, activity => ReloadFromDiskCoreAsync(tab, activity));

    private async Task<bool> ReloadFromDiskCoreAsync(DocumentTab tab, ActivityScope activity)
    {
        if (_disposed || !Tabs.Contains(tab) || tab.FilePath is null) return false;
        if (tab.IsDirty)
        {
            activity.WaitForUser("Confirm replacing unsaved edits with the disk version");
            if (!await _dialogs.ConfirmReloadDiscardingEditsAsync(tab).ConfigureAwait(true)) return false;
        }
        activity.Report("Reading and validating the disk version");
        var reloaded = await SerializeIdentityAsync(() => tab.TakeDiskVersionAsync(), quiet: true).ConfigureAwait(true);
        if (reloaded)
        {
            SetStatus("Reloaded " + tab.DisplayName + " from disk.", StatusSeverity.Success);
            activity.Complete(outcome: "Reloaded from disk");
        }
        else activity.Fail("The disk version could not be reloaded; the open document was kept.");
        return reloaded;
    }

    /// <summary>The bar's Compare: opens the three-way merge. Accepting puts the result in the buffer unsaved (B-08).</summary>
    public Task<bool> CompareWithDiskAsync(DocumentTab tab) => MergeWithDiskAsync(tab, saveAfterAccept: false);

    /// <summary>The bar's Keep mine: replaces the disk version with the buffer, after keeping the replaced version in history.</summary>
    public Task<bool> KeepMineAsync(DocumentTab tab) => KeepMineCoreAsync(tab);

    /// <summary>
    /// Walks the user through a disagreement between the buffer and disk. True when the document ends up saved or reloaded.
    /// <paramref name="afterSave"/> is true only when a Save command hit the conflict; then an accepted merge is written too (B-08).
    /// </summary>
    public async Task<bool> ResolveConflictAsync(DocumentTab tab, bool afterSave = false)
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

        if (tab.Conflict.DiskNotLossless) ShowEncodingMergeRefusal(tab, null);
        switch (await _dialogs.AskConflictAsync(tab).ConfigureAwait(true))
        {
            case ConflictChoice.KeepMine:
                return await KeepMineCoreAsync(tab).ConfigureAwait(true);
            case ConflictChoice.TakeDisk:
                return await tab.TakeDiskVersionAsync().ConfigureAwait(true);
            case ConflictChoice.Merge:
                return await MergeWithDiskAsync(tab, afterSave).ConfigureAwait(true);
            case ConflictChoice.SaveCopy:
                return await SaveConflictCopyAsync(tab).ConfigureAwait(true);
            default:
                return false;
        }
    }

    private Task<bool> KeepMineCoreAsync(DocumentTab tab) =>
        TrackActivityAsync("Replacing disk version", tab.DisplayName, activity => KeepMineWithProgressAsync(tab, activity));

    private async Task<bool> KeepMineWithProgressAsync(DocumentTab tab, ActivityScope activity)
    {
        if (_disposed || !Tabs.Contains(tab) || tab.FilePath is not { } replacedPath) return false;
        var outcome = await SerializeIdentityAsync<SaveOutcome>(async () =>
        {
            if (_disposed || !Tabs.Contains(tab)) return new SaveFailed("The document is closed.");
            activity.Report("Preserving the external version in History");
            await CaptureReplacedVersionAsync(tab, replacedPath, null, externalReplaced: true).ConfigureAwait(true);
            activity.Report("Writing and verifying the chosen open version");
            return await tab.KeepMineAndOverwriteAsync(SaveOptions).ConfigureAwait(true);
        }, DocumentOperation.Saving, tab, parentActivity: activity).ConfigureAwait(true);
        return await HandleSaveOutcomeAsync(tab, outcome, activity).ConfigureAwait(true);
    }

    private Task<bool> MergeWithDiskAsync(DocumentTab tab, bool saveAfterAccept) =>
        TrackActivityAsync("Merging external changes", tab.DisplayName, activity => MergeWithProgressAsync(tab, saveAfterAccept, activity));

    private async Task<bool> MergeWithProgressAsync(DocumentTab tab, bool saveAfterAccept, ActivityScope activity)
    {
        if (_disposed || !Tabs.Contains(tab) || tab.FilePath is null) return false;
        activity.Report("Reading and comparing the external version");
        var build = await tab.TryBuildMergeAsync().ConfigureAwait(true);
        if (build.Merge is not { } merge)
        {
            activity.Fail(build.Refusal ?? MergeBuild.DifferentEncoding);
            ShowEncodingMergeRefusal(tab, build.Refusal);
            return false;
        }
        activity.WaitForUser(saveAfterAccept ? "Review the merge before applying and saving" : "Review the merge; applying will not save");
        var merged = await _dialogs.ResolveMergeAsync(tab, merge, saveAfterAccept).ConfigureAwait(true);
        if (merged is null)
        {
            activity.Cancel("Canceled; the merge did not replace the buffer or the file on disk.");
            return false;
        }

        activity.Report("Validating and applying the approved merge to the buffer");
        if (!await tab.ApplyMergedTextAsync(merged).ConfigureAwait(true))
        {
            activity.Fail("The document or disk changed during review. The proposed merge was not applied.");
            if (tab.Conflict?.DiskNotLossless == true) ShowEncodingMergeRefusal(tab, MergeBuild.DifferentEncoding);
            else StatusMessage = "The document or disk changed during the merge. Review the conflict again; nothing was overwritten.";
            return false;
        }

        if (saveAfterAccept)
        {
            activity.Report("Merge applied; saving through the normal verified save workflow");
            var saved = await SaveTabAsync(tab).ConfigureAwait(true);
            activity.Complete(saved ? null : "The merge is in the open buffer; saving did not finish. Review and save the document.",
                warning: !saved, outcome: saved ? "Merged; save workflow completed" : "Merged into buffer; save unfinished");
            return saved;
        }
        SetStatus("Merged — review and save. " + tab.DisplayName + " is unsaved and the file on disk is unchanged.", StatusSeverity.Info);
        activity.Complete(outcome: "Merged into buffer; not saved");
        return true;
    }

    /// <summary>The merge was refused to protect the external file's characters; nothing on disk or in the buffer changed.</summary>
    private void ShowEncodingMergeRefusal(DocumentTab tab, string? reason)
    {
        ShowBanner("The changed file uses another encoding — Reopen with encoding… · Save my copy as…", StatusSeverity.Warning,
            new BannerAction("Reopen with encoding…", () => SetStatus(tab.IsDirty
                ? "Your edits are unsaved. Use Save my copy as… first, then choose File → Reopen with Encoding…."
                : "Choose File → Reopen with Encoding… to read the file with its real encoding.", StatusSeverity.Info)),
            new BannerAction("Save my copy as…", () => _ = SaveConflictCopyAsync(tab)));
        SetStatus(reason ?? MergeBuild.DifferentEncoding, StatusSeverity.Warning);
    }

    private async Task<bool> SaveConflictCopyAsync(DocumentTab tab)
    {
        var path = await _dialogs.PickSavePathAsync("Copy of " + tab.DisplayName, Path.GetDirectoryName(tab.FilePath)).ConfigureAwait(true);
        if (path is null) return false;
        try
        {
            var expected = await _files.ReadBaselineAsync(path).ConfigureAwait(true);
            if (expected is not null && !await _dialogs.ConfirmOverwriteAsync(path).ConfigureAwait(true)) return false;
            var result = await SaveWithResourceChoiceAsync(tab, (outsideChoice, approvedOutside) => SerializeIdentityAsync<SaveOutcome>(async () =>
            {
                if (_disposed || !Tabs.Contains(tab)) return new SaveFailed("The document was closed before the copy completed.");
                if (await FindOpenTabAsync(path).ConfigureAwait(true) is not null)
                    return new SaveFailed("Choose a separate filename. A copy cannot replace an open source document.");
                if (_disposed || !Tabs.Contains(tab)) return new SaveFailed("The document was closed before the copy completed.");
                return await tab.SaveCopyCheckedAsync(path, expected, outsideAttachments: outsideChoice, approvedOutsideAttachments: approvedOutside).ConfigureAwait(true);
            })).ConfigureAwait(true);
            if (result is SaveFailed failed)
            {
                _dialogs.ShowMessage("Copy failed", failed.Reason);
            }
            else if (result is SaveConflict)
                _dialogs.ShowMessage("Copy destination changed", "The destination changed before the copy could be committed. Choose it again or use another filename.");
            else if (result is SaveSucceeded && tab.LastResourceSaveWarning is { } warning)
                SetStatus("Copy saved. " + warning, StatusSeverity.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { _dialogs.ShowMessage("Copy failed", PlainProblem(ex)); }

        return false;
    }
    // ---- Workspace --------------------------------------------------------------------------------------------------

    public Task<bool> OpenWorkspaceAsync(string folder, IProgress<Search.IndexProgress>? progress = null, bool recordRecent = true, Action? treeReady = null) =>
        SerializeWorkspacesAsync(turn => OpenWorkspaceFromRootAsync(folder, progress, recordRecent, turn, treeReady));

    /// <summary>Browse a physical directory without creating workspace metadata or a workspace history record.</summary>
    public Task<bool> OpenFolderAsync(string folder, bool recordRecent = true, Action? treeReady = null) =>
        SerializeWorkspacesAsync(turn => OpenWorkspaceCoreAsync(folder, null, recordRecent, namedWorkspace: false, turn, treeReady));

    private int _workspaceOpens;

    private async Task<bool> OpenWorkspaceCoreAsync(string folder, IProgress<Search.IndexProgress>? progress, bool recordRecent, bool namedWorkspace = true,
        WorkspaceTurn? turn = null, Action? treeReady = null)
    {
        if (_disposed) return false;
        StatusOperation? operation = null;
        WorkspaceTurn? finishing = null;
        var counted = false;
        try
        {
            var root = WorkspacePaths.Normalize(folder);
            if (!namedWorkspace && IsKnownWorkspace(root))
            {
                StatusMessage = "This folder is a workspace. Use Open Workspace to load it correctly.";
                _dialogs.ShowMessage("Open as workspace", StatusMessage);
                return false;
            }
            Interlocked.Increment(ref _workspaceOpens);
            counted = true;
            IsIndexing = true;
            var definition = namedWorkspace ? await Task.Run(() => WorkspaceDefinition.Load(root)).ConfigureAwait(true) : null;
            var context = namedWorkspace ? await Task.Run(() => _contextStore.LoadOrMigrate(root, definition)).ConfigureAwait(true) : null;
            var existing = OpenWorkspaces.FirstOrDefault(w => WorkspacePaths.PathsEqual(w.Root, root));
            WorkspaceSession? replacedFolder = null;
            if (existing is { IsNamedWorkspace: false } && namedWorkspace)
            {
                // A browsed folder keeps no content index, so becoming a workspace needs a fresh, fully indexed session.
                replacedFolder = existing;
                existing = null;
            }
            if (existing is not null)
            {
                var wasNamedWorkspace = existing.IsNamedWorkspace;
                existing.IsNamedWorkspace = namedWorkspace;
                existing.Name = definition?.Name ?? Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
                SelectWorkspace(existing);
                if (recordRecent && namedWorkspace) Recent.AddWorkspace(existing.Root);
                if (recordRecent && namedWorkspace && !wasNamedWorkspace)
                    await RestoreWorkspaceContextAsync(existing, context).ConfigureAwait(true);
                return true;
            }
            if (OpenWorkspaces.Count - (replacedFolder is null ? 0 : 1) >= SessionStore.MaxWorkspaces)
            { StatusMessage = $"At most {SessionStore.MaxWorkspaces} workspaces can be open at once. Close a workspace first."; return false; }
            var options = new WorkspaceScanOptions { PruneEmptyDirectories = false, IncludeTextFiles = true };
            foreach (var pattern in Settings.WorkspaceExclusions)
            {
                options.ExcludePatterns.Add(pattern);
            }

            using var opening = new CancellationTokenSource();
            operation = BeginStatusOperation("Opening " + (namedWorkspace ? "workspace" : "folder"), root, opening.Cancel);
            operation.Report("Scanning files");
            void PublishTree(WorkspaceSession ready)
            {
                ready.IsNamedWorkspace = namedWorkspace;
                ready.Name = definition?.Name ?? Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
                ready.PreferredViewMode = ViewMode;
                if (_disposed) { ready.Dispose(); return; }
                var dirtyTabs = Tabs.Where(t => t.IsDirty && t.FilePath is not null).ToArray();
                if (replacedFolder is not null && OpenWorkspaces.Contains(replacedFolder))
                {
                    ready.PreferredViewMode = replacedFolder.PreferredViewMode;
                    DetachWorkspace(replacedFolder);
                }
                OpenWorkspaces.Add(ready);
                if (ready.IsPrivate) RememberPrivateRoot(ready.Root);
                ready.ProblemDetailsReported += OnWorkspaceProblem;
                Workspace = ready;
                OnPropertyChanged(nameof(HasWorkspace));
                OnPropertyChanged(nameof(OpenWorkspaces));
                foreach (var tab in dirtyTabs) OnTabStateChanged(tab.Session, EventArgs.Empty);
                // The tree is published: other workspace commands need not wait for indexing.
                turn?.Release();
                treeReady?.Invoke();
            }
            var indexProgress = new Progress<Search.IndexProgress>(p =>
            {
                if (opening.IsCancellationRequested || _disposed) return;
                operation.Report("Indexing workspace", p.Processed, p.Total, "files");
                progress?.Report(p);
            });
            WorkspaceSession session;
            try
            {
                session = await WorkspaceSession.OpenAsync(root, Paths.IndexDirectory, options, Settings.DefaultProfile,
                    indexProgress, opening.Token, indexContent: namedWorkspace, treeReady: PublishTree, privateWorkspace: definition?.Private == true).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (opening.IsCancellationRequested)
            {
                operation.Cancel();
                SetStatus("Opening cancelled before the file tree was ready. No documents were changed.", StatusSeverity.Info);
                return false;
            }
            // Finishing restores tabs and context, so it takes the workspace list back and rechecks that this session is still open.
            if (turn?.IsReleased == true) finishing = await AcquireWorkspaceTurnAsync().ConfigureAwait(true);
            if (_disposed || !OpenWorkspaces.Contains(session)) { session.Dispose(); return false; }
            if (session.IsPrivate) await ClearWorkspaceCopiesAsync(session).ConfigureAwait(true);
            if (!session.IsContentReady)
                operation.Complete(session.ContentStatus, warning: true, outcome: "Workspace open; search updates paused");
            if (recordRecent && namedWorkspace) Recent.AddWorkspace(session.Root);

            if (recordRecent && namedWorkspace) await RestoreWorkspaceContextAsync(session, context).ConfigureAwait(true);

            // The path can contain words like "review" or "error", so the level must not be guessed from this text.
            var openedStatus = $"{(namedWorkspace ? "Workspace" : "Folder")}: {session.Root} ({session.Snapshot.Files.Count} documents)";
            SetStatus(session.Notices.Count > 0 ? openedStatus + ". " + string.Join(" ", session.Notices) : openedStatus, session.Notices.Count > 0 ? StatusSeverity.Warning : StatusSeverity.Info);
            if (!session.IsContentReady)
            {
                SetStatus(session.ContentStatus, StatusSeverity.Warning);
                ShowBanner(session.ContentStatus, StatusSeverity.Warning,
                    new BannerAction("Rescan", session.RequestRescan));
            }
            StartSaveFragmentScan(session);
            operation.Complete(session.Notices.Count > 0 ? string.Join(" ", session.Notices) : null,
                warning: session.Notices.Count > 0, outcome: namedWorkspace ? "Search ready" : "Folder ready");
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Text.Json.JsonException or Search.FtsUnavailableException)
        {
            var problem = UserProblem.Explain(ex, "Could not open " + (namedWorkspace ? "workspace" : "folder") + " " + folder);
            operation?.Fail(problem.Why + " " + problem.Next);
            StatusMessage = problem.Text;
            _dialogs.ShowMessage(namedWorkspace ? "Cannot open workspace" : "Cannot open folder", StatusMessage);
            return false;
        }
        finally
        {
            operation?.Dispose();
            finishing?.Release();
            if (counted && Interlocked.Decrement(ref _workspaceOpens) == 0) IsIndexing = false;
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
        using var activity = Activities.Begin("Restoring recovery checkpoint", record.DisplayName);
        try
        {
            activity.Report("Preparing recovered text as a separate unsaved draft");
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
            activity.Report("Verifying the replacement checkpoint before retiring the old one");
            var checkpointReady = await _recovery.FlushAllAsync().ConfigureAwait(true)
                && RecoveryStore.Scan().Entries.Any(e => e.Record.SessionId == session.Id);
            if (checkpointReady) RecoveryStore.Retire(record.SessionId);
            SetStatus($"Recovered unsaved changes to {record.DisplayName}.", checkpointReady ? StatusSeverity.Success : StatusSeverity.Warning);
            activity.Complete("The original file was not overwritten. " +
                (checkpointReady ? "The new draft's recovery checkpoint was verified." : "The original recovery checkpoint was retained because the new checkpoint could not be verified."),
                !checkpointReady, checkpointReady ? "Recovered as unsaved copy; original unchanged" : "Recovered copy ready; original checkpoint retained");
            return tab;
        }
        catch (OperationCanceledException) { activity.Cancel("Restore stopped; the original file was not overwritten. Any opened recovery copy remains unsaved."); throw; }
        catch (Exception ex) { activity.Fail("Restore stopped; the original file was not overwritten. Any opened recovery copy remains unsaved. " + ex.Message); throw; }
    }

    public void DiscardRecovery(RecoveryRecord record) => RecoveryStore.Retire(record.SessionId);

    /// <summary>Opens the folder holding the first unreadable checkpoint so the user can inspect it.</summary>
    public void ShowDamagedRecovery(IReadOnlyList<CorruptRecovery> damaged)
    {
        if (damaged.Count > 0) _launcher.RevealInFileManager(damaged[0].Path);
    }

    public void DeleteDamagedRecovery(IReadOnlyList<CorruptRecovery> damaged)
    {
        var removed = RecoveryStore.DeleteDamaged(damaged.Select(d => d.Path));
        SetStatus(removed == damaged.Count
            ? $"Deleted {removed} damaged recovery file(s). Readable checkpoints were not touched."
            : $"Deleted {removed} of {damaged.Count} damaged recovery file(s); the rest could not be removed.",
            removed == damaged.Count ? StatusSeverity.Success : StatusSeverity.Warning);
    }

    /// <summary>Keeps unreadable checkpoints but moves them aside so they are reported once, not at every startup.</summary>
    public void QuarantineDamagedRecovery(IReadOnlyList<CorruptRecovery> damaged)
    {
        var moved = RecoveryStore.Quarantine(damaged.Select(d => d.Path));
        SetStatus(moved == damaged.Count
            ? $"Kept {moved} damaged recovery file(s) in {RecoveryStore.DamagedDirectory}."
            : $"Moved {moved} of {damaged.Count} damaged recovery file(s) to {RecoveryStore.DamagedDirectory}; the rest could not be moved.",
            moved == damaged.Count ? StatusSeverity.Info : StatusSeverity.Warning);
    }

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
        lock (_sessionPersistenceGate)
        {
            CancelPendingSessionSave();
            if (window is not null) _sessionWindow = window;
            var saved = _sessionStore.Save(CaptureSessionSnapshot());
            if (!saved) SetStatus("The session could not be saved: " + _sessionStore.LastSaveError?.Message, StatusSeverity.Warning);
            return saved;
        }
    }

    /// <summary>Reopens the previous workspace and tabs. Missing files are skipped; returns the saved window layout, if any.</summary>
    public async Task<WindowLayoutHint?> RestoreSessionAsync(Action? activeDocumentReady = null)
    {
        lock (_sessionPersistenceGate)
        {
            ++_sessionRestoreDepth;
            CancelPendingSessionSave();
        }
        try { return await RestoreSessionCoreAsync(activeDocumentReady).ConfigureAwait(true); }
        finally
        {
            lock (_sessionPersistenceGate) --_sessionRestoreDepth;
            QueueSessionSave();
        }
    }

    private async Task<WindowLayoutHint?> RestoreSessionCoreAsync(Action? activeDocumentReady)
    {
        if (!Settings.RestoreSession)
        {
            return null;
        }

        var snapshot = await Task.Run(_sessionStore.Load).ConfigureAwait(true);
        _sessionWindow = snapshot.Window;
        foreach (var (path, state) in snapshot.FileViews)
        {
            _viewStates[path] = state;
        }

        var restorable = await Task.Run(() => snapshot.Tabs.Select((state, index) => (State: state, Index: index))
            .Where(t => File.Exists(t.State.Path)).ToArray()).ConfigureAwait(true);
        var first = restorable.FirstOrDefault(t => snapshot.ActiveTabIndex == t.Index);
        if (first.State is null) first = restorable.FirstOrDefault(t => snapshot.ActiveTab is { } active && SamePath(t.State.Path, active));
        if (first.State is null) first = restorable.FirstOrDefault();
        var restored = new Dictionary<int, DocumentTab>();
        var changed = new List<DocumentTab>();
        async Task RestoreTabAsync(TabState state, int index, bool activate)
        {
            _viewStates[state.Path] = new FileViewState(state.CaretOffset, state.ScrollOffset);
            var tab = await OpenFileAsync(state.Path, activate: activate, recordRecent: false,
                duplicateBehavior: DuplicateOpenBehavior.NewTab).ConfigureAwait(true);
            if (tab is not null)
            {
                _tabViewStates[tab] = new FileViewState(state.CaretOffset, state.ScrollOffset);
                restored[index] = tab;
                if (state.DiskSha256 is { } hash && tab.Session.Baseline is { } baseline
                    && !string.Equals(hash, baseline.Sha256, StringComparison.OrdinalIgnoreCase)) changed.Add(tab);
            }
        }
        if (first.State is not null) await RestoreTabAsync(first.State, first.Index, activate: true).ConfigureAwait(true);
        if (_disposed) return snapshot.Window;
        activeDocumentReady?.Invoke();
        async Task RestoreOtherDocumentsAsync()
        {
            var processed = 0;
            var batchClock = System.Diagnostics.Stopwatch.StartNew();
            foreach (var tab in restorable)
            {
                if (_disposed) break;
                if (tab.Index != first.Index)
                {
                    await RestoreTabAsync(tab.State, tab.Index, activate: false).ConfigureAwait(true);
                    if ((++processed >= 8 || batchClock.ElapsedMilliseconds >= 50) && UiDispatcher is { } dispatcher)
                    {
                        await dispatcher.YieldAsync().ConfigureAwait(true);
                        processed = 0;
                        batchClock.Restart();
                    }
                }
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
        // The active document was restored first; place it once instead of shifting every preceding tab.
        if (restored.TryGetValue(first.Index, out var firstTab) && Tabs.IndexOf(firstTab) is var firstPosition && firstPosition >= 0)
        {
            var destination = restorable.Count(item => item.Index < first.Index
                && restored.TryGetValue(item.Index, out var tab) && Tabs.Contains(tab));
            if (firstPosition != destination) Tabs.Move(firstPosition, destination);
        }
        // Restore visual tab order without activating/rendering every background document.
        for (var i = 0; i < restorable.Length; i++)
        {
            var index = restored.TryGetValue(restorable[i].Index, out var tab) ? Tabs.IndexOf(tab) : -1;
            if (index >= 0 && index != i) Tabs.Move(index, Math.Min(i, Tabs.Count - 1));
        }

        if (snapshot.LastWorkspace is { } activeRoot && OpenWorkspaces.FirstOrDefault(w => WorkspacePaths.PathsEqual(w.Root, activeRoot)) is { } activeWorkspace)
            SelectWorkspace(activeWorkspace);

        var changedFiles = changed.DistinctBy(tab => tab.FilePath, WorkspacePaths.Comparer).ToArray();
        if (changedFiles.Length > 0)
        {
            var summary = $"{changedFiles.Length} document{(changedFiles.Length == 1 ? "" : "s")} changed on disk since your last session and were reloaded.";
            var details = string.Join(Environment.NewLine, changedFiles.Select(tab => tab.FilePath));
            var review = new BannerAction("Review", () =>
            {
                if (!_disposed && changedFiles.FirstOrDefault(Tabs.Contains) is { } tab) SelectTab(tab, TabActivationReason.User);
            });
            if (Banner?.Severity is StatusSeverity.Error or StatusSeverity.Warning)
                Activities.Post(summary, ActivitySeverity.Neutral, [review], details);
            else ShowBanner(summary, StatusSeverity.Info, details, review);
        }
        return snapshot.Window;
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopLiveSessionPersistence();
        _disposed = true;
        foreach (var cancellation in _idleSaves.Values.ToArray()) cancellation.Cancel();
        _idleSaves.Clear();
        Tabs.CollectionChanged -= OnSessionStructureChanged;
        OpenWorkspaces.CollectionChanged -= OnSessionStructureChanged;
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
