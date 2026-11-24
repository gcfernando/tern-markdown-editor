using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Tern.App.Commands;
using Tern.App.Documents;
using Tern.App.Settings;
using Tern.App.Shell;
using Tern.Desktop.Editing;
using Tern.Desktop.Services;
using Tern.Desktop.Themes;
using Tern.Rendering.Preview;
using Tern.Rendering.Resources;
using Tern.Filesystem;
using Tern.Workspaces;

namespace Tern.Desktop.Views;

/// <summary>
/// The single main window. It only presents <see cref="ShellViewModel"/>: tabs, one editor and one preview that swap
/// documents, and panels derived from the active tab. Each tab keeps its own AvaloniaEdit document so undo history survives tab switches.
/// </summary>
public sealed partial class MainWindow : Window, IDisposable
{
    private static readonly TimeSpan PreviewDelay = TimeSpan.FromMilliseconds(150);

    private readonly ShellViewModel _shell;
    private readonly Dictionary<DocumentTab, TabContext> _contexts = [];
    private readonly Dictionary<DocumentTab, IDisposable> _cacheLeases = [];
    private readonly MarkdownColorizer _colorizer = new();
    private readonly DispatcherTimer _previewTimer = new() { Interval = PreviewDelay };
    private readonly IExternalLauncher _launcher;
    private readonly DesktopDialogs? _dialogs;
    private readonly RemoteImageLoader? _remoteImages;
    private DocumentTab? _shown;
    private DocumentTab? _renderedPreviewTab;
    private long _renderedPreviewRevision = -1;
    private bool _syncingTabs;
    private bool _syncingScroll;
    private bool _switchingDocument;
    private long _previewGeneration;
    private bool _windowClosed;
    private bool _exitApproved;
    private bool _checkingWindowClose;
    private bool _compactInspector;
    private readonly HashSet<object> _externalChecksRunning = new();
    private readonly HashSet<object> _externalChecksRerun = new();
    private Task _previewWork = Task.CompletedTask;
    private CancellationTokenSource? _previewCancellation;
    private bool _focusMode;
    private bool _focusSidebar;
    private bool _focusInspector;
    private GridLength _focusSidebarWidth;
    private GridLength _focusInspectorWidth;

    internal bool IsFocusMode => _focusMode;

    private void ToggleFocusMode()
    {
        _focusMode = !_focusMode;
        if (_focusMode)
        {
            _focusSidebar = Sidebar.IsVisible;
            _focusInspector = RightPanel.IsVisible;
            _focusSidebarWidth = Body.ColumnDefinitions[0].Width;
            _focusInspectorWidth = Body.ColumnDefinitions[4].Width;
            _libraryMotion!.SetVisible(false);
            _inspectorMotion!.SetVisible(false);
            Body.ColumnDefinitions[0].Width = Body.ColumnDefinitions[4].Width = new GridLength(0);
        }
        else
        {
            _libraryMotion!.SetVisible(_focusSidebar);
            _inspectorMotion!.SetVisible(_focusInspector);
            Body.ColumnDefinitions[0].Width = _focusSidebarWidth;
            Body.ColumnDefinitions[4].Width = _focusInspectorWidth;
        }

        FocusButton.IsChecked = _focusMode;
        UpdatePanelRestoreButtons();
        if (_shell.ViewMode == ViewMode.Reading) Preview.Focus(); else Editor.TextArea.Focus();
    }

    private void OnFocusClick(object? sender, RoutedEventArgs e) => ToggleFocusMode();

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Buttons keep their own input; every other part of the caption can move the window.
        if (e.Source is Visual visual && visual.GetSelfAndVisualAncestors().Any(v => v is Button)) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            BeginMoveDrag(e);
        e.Handled = true;
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnCloseWindowClick(object? sender, RoutedEventArgs e) => Close();

    private void UpdateCaptionButtons()
    {
        var maximized = WindowState == WindowState.Maximized;
        if (MaximizeButton.Content is VectorIcon icon) icon.Kind = maximized ? "Restore" : "Maximize";
        MaximizeButton.SetValue(ToolTip.TipProperty, maximized ? "Restore down" : "Maximize");
        AutomationProperties.SetName(MaximizeButton, maximized ? "Restore down" : "Maximize");
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        WindowSizing.ApplyInitial(this, _shell.Settings.UiScale);
        UpdateCaptionButtons();
        UpdateResponsiveLayout();
    }

    public MainWindow(ShellViewModel shell, DesktopDialogs? dialogs = null, RemoteImageLoader? remoteImages = null, AIWorkspaceService? aiWorkspace = null,
        IExternalLauncher? launcher = null)
    {
        _shell = shell;
        _dialogs = dialogs;
        _launcher = launcher ?? new SystemLauncher();
        _remoteImages = remoteImages;
        _imageAccess = new RemoteImageAccess(shell.Settings);
        if (_remoteImages is not null) _remoteImages.AccessAllowed = _imageAccess.Allows;
        _aiWorkspace = aiWorkspace ?? new AIWorkspaceService(shell.Paths.SettingsDirectory);
        _aiWorkspace.NetworkAllowed = () => !_shell.Settings.OfflineMode;
        if (dialogs is not null)
        {
            dialogs.Owner = this;
        }

        InitializeComponent();
        ConfigureSurfaceMotion();
        ConfigureGitAwareness();
        VersionLabel.Text = "v" + (typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? throw new InvalidOperationException("The application version metadata is missing."));
        FindBar.AddHandler(KeyDownEvent, OnFindBarKeyDown, RoutingStrategies.Tunnel);
        TabStrip.AddHandler(PointerPressedEvent, OnTabMiddlePressed, RoutingStrategies.Tunnel);
        ContextGuidance.SetDismissAction(() =>
        {
            _shell.Settings.ShowContextGuidance = false;
            _shell.SaveSettings();
            UpdateResponsiveLayout();
        });
        ConfigureCompactAI();
        ConfigureWelcome();
        ConfigureAuthoringToolbar();
        _shell.Activities.Changed += OnActivityChanged;
        WireStatusHold();
        _shell.BannerChanged += (_, _) => RefreshNotificationBanner();
        _shell.ErrorReportRequested += OnErrorReportRequested;
        _shell.SaveFragmentsReviewRequested += (_, _) => Dispatcher.UIThread.Post(async () => { if (!_windowClosed) await ShowSaveFragmentsAsync(); });
        ConfigureContextMenus();
        Closed += (_, _) => { foreach (var request in _onlineRequests.Values.ToArray()) request.Cancel(); };
        SideList.AddHandler(PointerPressedEvent, TrackLibraryPointer, RoutingStrategies.Tunnel, handledEventsToo: true);
        SideList.AddHandler(KeyDownEvent, (_, _) => _libraryRightPress = false, RoutingStrategies.Tunnel);
        SideList.AddHandler(KeyDownEvent, OnLibraryDeleteKeyDown, RoutingStrategies.Tunnel);
        FileTree.AddHandler(PointerPressedEvent, TrackLibraryPointer, RoutingStrategies.Tunnel, handledEventsToo: true);
        FileTree.AddHandler(KeyDownEvent, (_, _) => _libraryRightPress = false, RoutingStrategies.Tunnel);
        FileTree.AddHandler(KeyDownEvent, OnLibraryDeleteKeyDown, RoutingStrategies.Tunnel);
        // A document-scoped loader is attached when its preview is built.
        ApplyTheme();
        ConfigureEditor();
        WirePreview();
        WireScrollInteraction();
        WireShell();
        _shell.Recent.Changed += OnRecentChanged;
        _shell.Recent.Renamed += OnRecentRenamed;
        ApplySettings();
        ReloadAIConnections();
        ObserveAIConnections();
        ApplyViewMode();
        ShowTab(_shell.ActiveTab);
        RefreshWorkspace();
        UpdateChrome();

        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            RebuildPreview();
        };
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, e) =>
        {
            if (e.Key is Key.LeftAlt or Key.RightAlt)
                if (OverlayLayer.Children.OfType<AIPromptPanel>().FirstOrDefault() is { } prompt)
                { prompt.FocusQuestion(); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        // Avalonia drag events bubble; a tunnel-only handler never receives native drops.
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        SizeChanged += (_, _) =>
        {
            OnMotionPolicyChanged(this, EventArgs.Empty);
            UpdateResponsiveLayout();
        };
        Opened += OnWindowOpened;
        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty) UpdateCaptionButtons(); };
        Closing += OnWindowClosing;
        Activated += async (_, _) =>
        {
            MotionPolicy.Apply(_shell.Settings.ReduceMotion);
            Preview.ReduceMotion = MotionPolicy.IsReduced;
            _knowledgeAssistant?.RefreshSettings();
            await CheckExternalChangesAsync();
        };
        Closed += (_, _) => Dispose();
        ActualThemeVariantChanged += OnActualThemeChanged;
    }

    public void Dispose()
    {
        _endWalkthrough?.Invoke();
        if (_windowClosed) return;
        _windowClosed = true;
        _commandControls?.Dispose();
        _commandControls = null;
        foreach (var directory in _observedTreeDirectories) directory.PropertyChanged -= OnTreeDirectoryChanged;
        _observedTreeDirectories.Clear();
        MotionPolicy.Changed -= OnMotionPolicyChanged;
        _libraryMotion?.Dispose();
        _inspectorMotion?.Dispose();
        _bannerMotion?.Dispose();
        _statusMotion?.Dispose();
        _spellingCancellation?.Cancel();
        foreach (var cancellation in _headingRepairs.Values) cancellation.Cancel();
        _headingRepairs.Clear();
        _headingBaselines.Clear();
        _mentionCancellation?.Cancel();
        _taskRefreshCancellation?.Cancel();
        CloseKnowledgeAssistant();
        ActualThemeVariantChanged -= OnActualThemeChanged;
        _shell.Activities.Changed -= OnActivityChanged;
        StopObservingAIConnections();
        foreach (var prompt in OverlayLayer.Children.OfType<AIPromptPanel>()) prompt.Dispose();
        _previewGeneration++;
        _previewTimer.Stop();
        _previewCancellation?.Cancel();
        Preview.Document = null;
        _remoteImages?.Dispose();
        _findCancellation?.Cancel();
        foreach (var lease in _cacheLeases.Values) lease.Dispose();
        _cacheLeases.Clear();
        foreach (var (tab, context) in _contexts)
        {
            context.Monitor?.Dispose();
            tab.PropertyChanged -= OnTabPropertyChanged;
        }
        _shell.Tabs.CollectionChanged -= OnTabsChanged;
        _shell.OpenWorkspaces.CollectionChanged -= OnOpenWorkspacesChanged;
        _shell.Recent.Changed -= OnRecentChanged;
        _shell.Recent.Renamed -= OnRecentRenamed;
        _shell.PropertyChanged -= OnShellPropertyChanged;
        _shell.NavigationRequested -= OnNavigationRequested;
        _colorizer.Attach(null);
        DetachHeadingFolds();
        foreach (var workspace in _observedWorkspaces) { workspace.FilesChanged -= OnWorkspaceFilesChanged; workspace.GraphChanged -= OnWorkspaceGraphChanged; workspace.ProblemReported -= OnWorkspaceProblemReported; workspace.ContentStatusChanged -= OnWorkspaceContentStatusChanged; }
        GC.SuppressFinalize(this);
    }

    public ShellViewModel Shell => _shell;

    internal TextEditor EditorControl => Editor;

    internal PreviewView.PreviewView PreviewControl => Preview;

    private sealed class TabContext(AvaloniaDocumentBuffer buffer)
    {
        public AvaloniaDocumentBuffer Buffer { get; } = buffer;

        public int Caret { get; set; }
        public int SelectionStart { get; set; }
        public int SelectionLength { get; set; }

        public double ScrollY { get; set; }
        public int ReadingLine { get; set; } = 1;
        public string? PendingAnchor { get; set; }
        public FileChangeMonitor? Monitor { get; set; }
        public string? MonitoredPath { get; set; }
    }

    private TabContext ContextFor(DocumentTab tab)
    {
        if (_contexts.TryGetValue(tab, out var existing))
        {
            return existing;
        }

        // The editor document becomes the session's only copy of the text.
        var buffer = new AvaloniaDocumentBuffer(tab.Session.Buffer.GetText());
        tab.Session.AttachBuffer(buffer);
        buffer.Changed += (_, _) =>
        {
            if (ReferenceEquals(tab, _shown))
            {
                _previewCancellation?.Cancel();
                _previewGeneration++;
                _previewTimer.Stop();
                _previewTimer.Start();
                InvalidateFind();
                ScheduleSpelling();
            }
        };
        tab.AnalysisChanged += (_, _) =>
        {
            ScheduleHeadingRepair(tab);
            ScheduleTaskRefresh();
            if (ReferenceEquals(tab, _shown))
            {
                RefreshPanels();
                if (_shell.ViewMode == ViewMode.Source) RefreshSourceRemoteImageConsent();
                UpdateStatus();
                ScheduleSpelling();
            }
        };
        tab.PropertyChanged += OnTabPropertyChanged;

        var context = new TabContext(buffer);
        _contexts[tab] = context;
        MonitorTab(tab, context);
        return context;
    }

    private void MonitorTab(DocumentTab tab, TabContext context)
    {
        if (context.MonitoredPath == tab.FilePath) return;
        context.Monitor?.Dispose();
        context.Monitor = null;
        context.MonitoredPath = tab.FilePath;
        if (tab.FilePath is not { } path) return;
        try
        {
            context.Monitor = new FileChangeMonitor(path);
            context.Monitor.Changed += (_, _) => Dispatcher.UIThread.Post(async () => await CheckExternalChangesAsync(tab));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _shell.StatusMessage = "File monitoring unavailable; changes are checked on focus and save: " + _shell.PlainProblem(ex);
        }
    }

    internal async Task CheckExternalChangesAsync(DocumentTab? only = null)
    {
        if (_windowClosed || OwnedWindows.Any()) return;
        var key = only ?? (object)this;
        if (!_externalChecksRunning.Add(key)) { _externalChecksRerun.Add(key); return; }
        try
        {
            do
            {
                _externalChecksRerun.Remove(key);
                if (_windowClosed || OwnedWindows.Any()) return;
                try { await _shell.CheckForExternalChangesAsync(only); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { _shell.StatusMessage = "Could not check disk changes: " + _shell.PlainProblem(ex); }
            }
            while (_externalChecksRerun.Contains(key));
        }
        finally { _externalChecksRunning.Remove(key); }
    }

    private void WireShell()
    {
        _shell.Tabs.CollectionChanged += OnTabsChanged;
        _shell.OpenWorkspaces.CollectionChanged += OnOpenWorkspacesChanged;
        _shell.PropertyChanged += OnShellPropertyChanged;
        _shell.NavigationRequested += OnNavigationRequested;
        TabStrip.ItemsSource = _shell.Tabs;
        ReferenceTabs.ItemsSource = _shell.Tabs;
        foreach (var tab in _shell.Tabs)
        {
            ContextFor(tab);
        }
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _tabNameLookup = null;
        if (e.Action == NotifyCollectionChangedAction.Move) { UpdateChrome(); return; }
        if (e.NewItems is not null)
        {
            foreach (DocumentTab tab in e.NewItems)
            {
                ContextFor(tab);
            }
        }

        if (e.OldItems is not null)
        {
            foreach (DocumentTab tab in e.OldItems)
            {
                tab.PropertyChanged -= OnTabPropertyChanged;
                if (_contexts.TryGetValue(tab, out var context)) context.Monitor?.Dispose();
                _contexts.Remove(tab);
                _headingBaselines.Remove(tab);
                if (_headingRepairs.Remove(tab, out var headingRepair)) headingRepair.Cancel();
                if (_cacheLeases.Remove(tab, out var lease)) lease.Dispose();
            }
        }

        UpdateChrome();
        RefreshTreeDirtyState();
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshCommandAvailability();
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.ActiveTab):
                ShowTab(_shell.ActiveTab);
                break;
            case nameof(ShellViewModel.ViewMode):
                ApplyViewMode();
                break;
            case nameof(ShellViewModel.StatusMessage):
                if (_shell.StatusMessage == "Ready") _statusMotion?.CaptureExit();
                StatusMessage.Text = _shell.StatusMessage;
                ToolTip.SetTip(StatusMessageButton, _shell.StatusMessage.Length > 40 ? _shell.StatusMessage + " (open activity)" : "Open activity");
                ApplyStatusSeverity();
                if (_shell.StatusSeverity is StatusSeverity.Info or StatusSeverity.Success)
                    MotionPolicy.Enter(StatusMessage, MotionPurpose.Notification);
                else MotionPolicy.Stop(StatusMessage);
                break;
            case nameof(ShellViewModel.ActiveDocumentState):
                UpdateStatus();
                break;
            case nameof(ShellViewModel.Workspace):
                _picker?.InvalidateScopeIfChanged();
                RefreshWorkspace();
                break;
            case nameof(ShellViewModel.WorkspaceRevealPath):
                RevealOpenedWorkspacePath();
                break;
        }
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not DocumentTab tab)
        {
            return;
        }
        if (_contexts.TryGetValue(tab, out var context)) MonitorTab(tab, context);
        if (e.PropertyName is nameof(DocumentTab.DisplayName) or nameof(DocumentTab.FilePath)) _tabNameLookup = null;

        if (e.PropertyName is nameof(DocumentTab.IsDirty) or nameof(DocumentTab.DisplayName) or nameof(DocumentTab.Conflict) or nameof(DocumentTab.IsChangedOnDisk))
        {
            RefreshTreeDirtyState();
        }

        if (ReferenceEquals(tab, _shown))
        {
            Editor.IsReadOnly = tab.IsReadOnly;
            UpdateStatus();
            UpdateChrome();
        }
    }

    private void OnNavigationRequested(object? sender, NavigationRequest request)
    {
        if (request.Tab is not null && !ReferenceEquals(request.Tab, _shell.ActiveTab))
        {
            _shell.ActiveTab = request.Tab;
        }

        if (request.Line is { } line)
        {
            GoToLine(line, request.Column);
        }
        else if (request.Anchor is { } anchor)
        {
            GoToAnchor(anchor);
        }
    }

    private void ShowTab(DocumentTab? tab)
    {
        if (_shown is not null && _contexts.TryGetValue(_shown, out var previous))
        {
            previous.Caret = Editor.CaretOffset;
            previous.SelectionStart = Editor.SelectionStart;
            previous.SelectionLength = Editor.SelectionLength;
            previous.ScrollY = Editor.VerticalOffset;
            if (_shell.ViewMode == ViewMode.Reading) previous.ReadingLine = Math.Max(1, Preview.TopSourceLine);
            _shell.RememberViewState(_shown, previous.Caret, previous.ScrollY);
        }

        _switchingDocument = true;
        ResetScrollIntent();
        Tern.PreviewView.SmoothScrolling.CancelWithin(Editor);
        Tern.PreviewView.SmoothScrolling.CancelWithin(Preview);
        ++_previewGeneration;
        _renderedPreviewTab = null;
        Preview.Document = null;
        // Selection geometry belongs to the old document, including while close awaits a safety copy.
        Editor.Select(0, 0);
        DetachHeadingFolds();
        _shown = tab;
        _documentWorkspaceRoot = tab is null ? null : WorkspaceRootFor(tab);
        InvalidateFind();
        _syncingTabs = true;
        TabStrip.SelectedItem = tab;
        _syncingTabs = false;

        if (tab is null)
        {
            Editor.Document = new AvaloniaEdit.Document.TextDocument();
            _colorizer.Attach(null);
            Preview.Document = null;
        }
        else
        {
            var context = ContextFor(tab);
            Editor.Document = context.Buffer.Document;
            _colorizer.Attach(context.Buffer.Document);
            Editor.IsReadOnly = tab.IsReadOnly;
            var state = _shell.GetViewState(tab);
            var caret = state?.CaretOffset ?? context.Caret;
            var selectionStart = Math.Clamp(context.SelectionStart, 0, context.Buffer.Document.TextLength);
            Editor.Select(selectionStart, Math.Clamp(context.SelectionLength, 0, context.Buffer.Document.TextLength - selectionStart));
            Editor.CaretOffset = Math.Clamp(caret, 0, context.Buffer.Document.TextLength);
            var scroll = state?.ScrollOffset ?? context.ScrollY;
            Dispatcher.UIThread.Post(() => { if (!_windowClosed && ReferenceEquals(_shown, tab)) SetEditorVerticalOffset(scroll); }, DispatcherPriority.Loaded);
            RebuildPreview();
        }

        _switchingDocument = false;
        ScheduleSpelling();
        UpdateChrome();
        RefreshPanels();
        UpdateStatus();
        RefreshTreeDirtyState();
    }

    private static bool IsRegistryEditorCommand(System.Windows.Input.ICommand? command) =>
        ReferenceEquals(command, ApplicationCommands.Undo) || ReferenceEquals(command, ApplicationCommands.Redo)
        || ReferenceEquals(command, ApplicationCommands.Cut) || ReferenceEquals(command, ApplicationCommands.Copy)
        || ReferenceEquals(command, ApplicationCommands.Paste) || ReferenceEquals(command, ApplicationCommands.SelectAll);

    private void ConfigureEditor()
    {
        // Native key bindings run before routed key handlers. Let the registry
        // own history and clipboard shortcuts, including removed or moved bindings.
        RemoveNativeHistoryBindings(Editor.KeyBindings);
        RemoveNativeHistoryBindings(Editor.TextArea.KeyBindings);
        RemoveNativeHistoryBindings(Editor.TextArea.DefaultInputHandler);
        Editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        _colorizer.RepaintStateChanges = (document, offset, length) => Dispatcher.UIThread.Post(() =>
        {
            if (_windowClosed || !ReferenceEquals(Editor.Document, document)) return;
            var start = Math.Clamp(offset, 0, document.TextLength);
            Editor.TextArea.TextView.Redraw(start, Math.Clamp(length, 0, document.TextLength - start));
        });
        _colorizer.RefreshStateRequested = document => Dispatcher.UIThread.Post(() =>
        {
            if (!_windowClosed && ReferenceEquals(Editor.Document, document)) _colorizer.RefreshState(document);
        });
        Editor.TextArea.TextView.ElementGenerators.Add(_hybridSource);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_findHighlight);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_spelling);
        Editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => ScheduleSpelling();
        Editor.TextArea.Caret.PositionChanged += (_, _) => { UpdateStatus(); RefreshHybridCaret(); };
        Editor.TextArea.TextView.ScrollOffsetChanged += OnEditorScrolled;
        Editor.TextArea.SelectionChanged += (_, _) => UpdateStatus();
        Editor.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
        Editor.TextArea.TextEntered += (_, e) => { if (e.Text == "[") TryShowWikiCompletion(); };
        Editor.TextArea.TextView.CurrentLineBackground = null;
        ApplyEditorColors();
        UpdatePanelRestoreButtons();
    }

    private static void RemoveNativeHistoryBindings(ICollection<KeyBinding> bindings)
    {
        foreach (var binding in bindings.Where(b => IsRegistryEditorCommand(b.Command)
                     || (b.Gesture is { Key: Key.Z or Key.Y } gesture
                         && gesture.KeyModifiers is KeyModifiers.Control or (KeyModifiers.Control | KeyModifiers.Shift))).ToArray())
            bindings.Remove(binding);
    }

    private void RemoveNativeHistoryBindings(AvaloniaEdit.Editing.TextAreaInputHandler handler)
    {
        RemoveNativeHistoryBindings(handler.KeyBindings);
        // AvaloniaEdit also dispatches a routed command's default gesture
        // directly from this collection, independently of KeyBindings.
        foreach (var binding in handler.CommandBindings.Where(b => IsRegistryEditorCommand(b.Command)).ToArray())
        {
            handler.CommandBindings.Remove(binding);
            // Preserve explicit native command execution (for example a context
            // menu) without retaining the handler's fixed keyboard gestures.
            if (!Editor.TextArea.CommandBindings.Contains(binding))
                Editor.TextArea.CommandBindings.Add(binding);
        }
        foreach (var nested in handler.NestedInputHandlers.OfType<AvaloniaEdit.Editing.TextAreaInputHandler>())
            RemoveNativeHistoryBindings(nested);
    }

    private void ApplyEditorColors()
    {
        if (Application.Current is { } app && app.TryGetResource(ThemeManager.LineHighlight, app.ActualThemeVariant, out var brush) && brush is Avalonia.Media.IBrush line)
        {
            Editor.TextArea.TextView.CurrentLineBackground = line;
        }

        Editor.TextArea.TextView.Redraw();
    }

    private void OnActualThemeChanged(object? sender, EventArgs e)
    {
        if (_windowClosed) return;
        ApplyEditorColors();
        ReferenceEditor.TextArea.TextView.Redraw();
        Preview.InvalidateVisual();
        RefreshMenuState();
    }

    internal void ApplySettings()
    {
        var s = _shell.Settings;
        MotionPolicy.Apply(s.ReduceMotion);
        if (!s.KnowledgeAssistantEnabled) CloseKnowledgeAssistant(); else _knowledgeAssistant?.RefreshSettings();
        RefreshNetworkState();
        Editor.FontFamily = new Avalonia.Media.FontFamily(s.EditorFontFamily);
        Editor.FontSize = s.EditorFontSize * s.DocumentTextScale;
        Editor.WordWrap = s.WordWrap;
        RefreshHybridSource(force: true);
        _spellingUnavailable = false;
        ScheduleSpelling();
        Editor.ShowLineNumbers = s.ShowLineNumbers;
        Editor.Options.HighlightCurrentLine = s.HighlightCurrentLine;
        Editor.Options.IndentationSize = s.TabSize;
        Editor.Options.ConvertTabsToSpaces = s.InsertSpacesForTab;
        foreach (var source in new[] { Editor, ReferenceEditor })
        {
            source.Options.ShowSpaces = s.ShowSpaces;
            source.Options.ShowTabs = s.ShowTabs;
            source.Options.ShowEndOfLine = s.ShowEndOfLine;
            source.Options.ShowColumnRulers = s.ShowColumnRuler;
            source.Options.ColumnRulerPositions = [s.ColumnRulerPosition];
            source.Options.LineHeightFactor = s.EditorLineHeight;
            source.Options.AllowScrollBelowDocument = s.AllowScrollBelowDocument;
            source.Options.CutCopyWholeLine = s.CutCopyWholeLine;
        }
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        ReferenceEditor.FontFamily = Editor.FontFamily;
        ReferenceEditor.FontSize = s.EditorFontSize * s.DocumentTextScale;
        ReferenceEditor.WordWrap = s.WordWrap;
        Preview.FontSize = s.PreviewFontSize * s.DocumentTextScale;
        // Document typography follows the Windows/browser reading stack rather
        // than inheriting the Inter font used by the application chrome.
        Preview.FontFamily = new Avalonia.Media.FontFamily(s.PreviewFontFamily);
        Preview.ReadingWidth = s.ReadingWidth;
        Preview.UseFullWidth = s.PreviewFullWidth;
        if (Application.Current is { } application)
        {
            ThemeMetrics.Apply(application, s.UiScale);
        }
        Preview.ReduceMotion = MotionPolicy.IsReduced;
        SyncScrollingCheck.IsChecked = s.SyncScroll;
        FullReadingWidthCheck.IsChecked = s.PreviewFullWidth;
        ApplyFindPreferences();
        if (TaskGroupByFile.IsChecked != s.TaskGroupByFile)
        {
            TaskGroupByFile.IsChecked = s.TaskGroupByFile;
            RefreshTaskRows();
        }
        ApplyLibraryVisibility();
        SearchHint.Text = Gesture.Describe(_shell.Registry.GetGesture(CommandIds.QuickOpen));
        RefreshCommandHints();
        ApplyEditorColors();
        UpdateResponsiveLayout();
        UpdatePanelRestoreButtons();
    }

    internal void ApplyTheme()
    {
        if (Application.Current is { } app)
        {
            ThemeManager.Apply(app, _shell.Settings.Theme);
            ApplyEditorColors();
            _colorizerRefresh();
        }
        RefreshMenuState();
    }

    private void _colorizerRefresh() => Editor.TextArea.TextView.Redraw();

    private async void OnErrorReportRequested(object? sender, Tern.App.Shell.ErrorReportEventArgs e)
    {
        try
        {
            if (e.Copy)
            {
                if (Clipboard is { } clipboard) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, e.Report);
                _shell.SetStatus("Error report copied. Review it before sharing.", Tern.App.Shell.StatusSeverity.Success);
            }
            else _dialogs?.ShowMessage("Error details", e.Report);
        }
        catch (Exception) { /* Clipboard may be unavailable; the banner stays visible. */ }
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exitApproved) return;
        e.Cancel = true;
        if (_checkingWindowClose)
        {
            _shell.SetStatus("Finish the open save prompt first.", StatusSeverity.Info);
            return;
        }
        _checkingWindowClose = true;
        try
        {
            if (_focusMode) ToggleFocusMode();
            if (_shown is not null) _shell.RememberViewState(_shown, Editor.CaretOffset, Editor.VerticalOffset);
            var layout = new WindowLayoutHint(Position.X, Position.Y, Width, Height, WindowState == WindowState.Maximized, Sidebar.Bounds.Width, _shell.Settings.SidebarVisible);
            if (await _shell.PrepareExitAsync(layout))
            {
                _exitApproved = true;
                Dispatcher.UIThread.Post(Close);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _shell.ReportUnexpectedError("close Tern", ex);
            if (ex is IOException or UnauthorizedAccessException) _dialogs?.ShowMessage("Tern is still open", "Could not finish closing: " + _shell.PlainProblem(ex));
        }
        finally
        {
            _checkingWindowClose = false;
        }
    }

    private void RebuildPreview() => _ = RebuildPreviewAsync();

    internal Func<Task>? BeforePreviewBuild { get; set; }
    private string? _previewFailureMessage;

    internal Task RebuildPreviewAsync()
    {
        _previewCancellation?.Cancel();
        var generation = ++_previewGeneration;
        PreviewProblem.IsVisible = false;
        if (_shell.ViewMode == ViewMode.Source) { RefreshSourceRemoteImageConsent(); return Task.CompletedTask; }
        var cancellation = _previewCancellation = new CancellationTokenSource();
        _previewWork = RunPreviewAsync(_previewWork, generation, cancellation);
        return _previewWork;
    }

    private async Task RunPreviewAsync(Task previous, long generation, CancellationTokenSource cancellation)
    {
        try { await BuildPreviewAsync(previous, generation, cancellation.Token); }
        catch (OperationCanceledException) { }
        // Each build waits for the one before it, so a failure that escaped here would stop every later preview.
        catch (Exception ex)
        { if (!_windowClosed && generation == _previewGeneration) ShowPreviewFailure(ex); }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation)) _previewCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task BuildPreviewAsync(Task previous, long generation, CancellationToken token)
    {
        if (_shown is null)
        {
            Preview.Document = null;
            Preview.RemoteImageLoader = null;
            RemoteImagesBanner.IsVisible = false;
            return;
        }

        if (_shown is { FilePath: null, ResourceDocumentPath: null, SourceUrl: { } remoteSource } online)
        {
            var cached = await MarkdownUrlCache.ReadAsync(MarkdownUrlCache.Root(_shell.Paths.StateDirectory), remoteSource, includeAssets: false);
            if (cached is not null && !_windowClosed && generation == _previewGeneration && ReferenceEquals(online, _shown))
            {
                online.ResourceDocumentPath = cached.CachedPath;
                online.ResourceFiles = new Dictionary<string, byte[]>();
                if (cached.CachedPath is { } cachedPath)
                {
                    if (_cacheLeases.Remove(online, out var previousLease)) previousLease.Dispose();
                    _cacheLeases[online] = MarkdownUrlCache.AcquireLease(cachedPath);
                }
            }
        }
        if (_shown is null || _windowClosed || generation != _previewGeneration) return;
        var options = new PreviewOptions
        {
            Profile = _shown.Profile,
            DocumentPath = _shown.FilePath ?? _shown.ResourceDocumentPath,
            WorkspaceRoot = WorkspaceRootFor(_shown),
            Resolver = new ResourceResolverOptions { AdditionalImageFiles = _shown.LinkedImageFiles },
        };

        var tab = _shown;
        var snapshot = tab.Session.GetSnapshot();
        var topLine = _shell.ViewMode == ViewMode.Split ? EditorTopLine() : ContextFor(tab).ReadingLine;
        await previous;
        try
        {
            if (_windowClosed || generation != _previewGeneration) return;
            // Supersede queued generations without throwing expected cancellation into
            // the parser. Bounded work already running finishes off the UI thread;
            // generation/revision checks discard obsolete results before presentation.
            if (BeforePreviewBuild is { } before) await before();
            if (_windowClosed || generation != _previewGeneration) return;
            var document = await Task.Run(() => PreviewBuilder.Build(snapshot.Text, options));
            if (_windowClosed || generation != _previewGeneration || !ReferenceEquals(tab, _shown)
                || !tab.Session.IsCurrent(snapshot.Revision)) return;
            // A navigation request may arrive while this build is awaiting parsing.
            // Read the latest position only after the tab/revision checks, and before
            // replacing the view can emit position notifications for its old content.
            var context = ContextFor(tab);
            var restoreLine = _shell.ViewMode == ViewMode.Reading ? context.ReadingLine : topLine;
            BindRemoteImages(tab, document);
            Preview.Document = document;
            PreviewProblem.IsVisible = false;
            if (_previewFailureMessage is { } failure)
            {
                if (_shell.Banner?.Text == failure) _shell.DismissBanner();
                _previewFailureMessage = null;
                _shell.SetStatus("Preview updated. Your Markdown is unchanged.", StatusSeverity.Success);
            }
            _renderedPreviewTab = tab;
            _renderedPreviewRevision = snapshot.Revision;
            Preview.CanToggleTasks = !tab.IsReadOnly;
            RefreshPreviewFindHighlight();
            UpdateFirstNoteChecklist();
            context.ReadingLine = Math.Max(1, restoreLine);
            if (context.PendingAnchor is { } anchor)
                GoToAnchor(anchor);
            else
                ScrollPreviewTo(Math.Max(1, restoreLine));
            QueueFindReveal();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            if (generation == _previewGeneration && !_windowClosed)
                ShowPreviewFailure(ex);
        }
    }

    private void ShowPreviewFailure(Exception error)
    {
        Preview.Document = null;
        _renderedPreviewTab = null;
        PreviewProblemText.Text = "Preview could not be updated: " + _shell.PlainProblem(error) + "\nYour Markdown is unchanged.";
        _previewFailureMessage = PreviewProblemText.Text;
        PreviewProblem.IsVisible = true;
        _shell.ShowBanner(PreviewProblemText.Text, StatusSeverity.Error,
            new BannerAction("Rebuild preview", async () => { if (!_windowClosed) await RebuildPreviewAsync(); }));
    }

    private async void OnRetryPreviewClick(object? sender, RoutedEventArgs e) => await RebuildPreviewAsync();

    private int EditorTopLine()
    {
        // AvaloniaEdit detaches TextView.Document temporarily while replacing a tab.
        if (_switchingDocument || Editor.TextArea.TextView.Document is null) return 1;
        var line = Editor.TextArea.TextView.GetDocumentLineByVisualTop(Editor.TextArea.TextView.ScrollOffset.Y);
        return line?.LineNumber ?? 1;
    }

    private void ScrollPreviewTo(int line)
    {
        Preview.SuppressNotifications = true;
        Preview.ScrollToSourceLine(line);
        Dispatcher.UIThread.Post(() => Preview.SuppressNotifications = false, DispatcherPriority.Background);
    }

    private void WirePreview()
    {
        Preview.LinkActivated += async (_, e) =>
        {
            var result = await _shell.ActivateLinkAsync(e.Resolution);
            if (result.Outcome == LinkOutcome.ScrolledToAnchor && result.Fragment is { } fragment)
            {
                Preview.ScrollToAnchor(fragment);
            }
            else if (result.Message is { } message)
            {
                _shell.StatusMessage = message;
            }
        };
        Preview.AnchorRequested += (_, e) =>
        {
            if (!Preview.ScrollToAnchor(e.Fragment))
                _shell.StatusMessage = "The linked heading or anchor was not found: #" + e.Fragment;
            e.Handled = true;
        };
        Preview.SourceLineActivated += (_, e) =>
        {
            if (ReferenceEquals(_renderedPreviewTab, _shown) && _shown?.Session.IsCurrent(_renderedPreviewRevision) == true) GoToLine(e.Line, 0);
            else _shell.StatusMessage = "The preview changed. Wait for Reading to update, then try again.";
        };
        Preview.ImageActionRequested += async (_, e) => await RepairPreviewImageAsync(e);
        Preview.TaskActivated += async (_, e) =>
        {
            if (_taskActivationBusy) return;
            var tab = _renderedPreviewTab;
            if (tab is null || !ReferenceEquals(tab, _shown) || !ReferenceEquals(e.Document, Preview.Document)
                || !tab.Session.IsCurrent(_renderedPreviewRevision) || !CanRunCommand(CommandIds.ToggleTask))
            { _shell.StatusMessage = "The task preview changed. Wait for Reading to update, then try again."; return; }
            var snapshot = tab.Session.GetSnapshot();
            var profile = tab.Profile;
            _taskActivationBusy = true;
            try
            {
                var analysis = tab.Analysis ?? await Task.Run(() => Tern.Markdown.MarkdownAnalyzer.Analyze(snapshot.Text, profile));
                if (_windowClosed || !ReferenceEquals(tab, _shown) || !tab.Session.IsCurrent(snapshot.Revision) || tab.IsReadOnly || !ReferenceEquals(e.Document, Preview.Document)) return;
                var task = analysis.Tasks.SingleOrDefault(t => t.Line == e.Line);
                if (task is null || task.Checked != e.WasChecked || task.Marker.Start < 0 || task.Marker.End > tab.CurrentText.Length) return;
                var marker = tab.CurrentText[task.Marker.Start..task.Marker.End];
                var replacement = marker switch { "[ ]" => "[x]", "[x]" or "[X]" => "[ ]", " " => "x", "x" or "X" => " ", _ => null };
                if (replacement is null) return;
                tab.ApplyEdits([new Tern.Domain.Text.TextEdit(task.Marker.Start, task.Marker.Length, replacement)]);
                _shell.StatusMessage = "Task updated in the note. Undo to revert; Save to update the file.";
            }
            finally { _taskActivationBusy = false; }
        };
        Preview.TopSourceLineChanged += (_, e) =>
        {
            UpdateCurrentSection(e.Line);
            if (_shell.ViewMode == ViewMode.Reading && !Preview.SuppressNotifications && _shown is { } readingTab)
                ContextFor(readingTab).ReadingLine = Math.Max(1, e.Line);
            if (_scrollDragOwner == ScrollDragOwner.Source || WheelOwns(ScrollDragOwner.Source) || _switchingDocument || !ReferenceEquals(_renderedPreviewTab, _shown) || _shown?.Session.IsCurrent(_renderedPreviewRevision) != true || !_shell.Settings.SyncScroll || _shell.ViewMode != ViewMode.Split || Preview.SuppressNotifications || _syncingScroll)
            {
                return;
            }

            ScrollEditorTo(e.Line);
        };
    }

    private void OnEditorScrolled(object? sender, EventArgs e)
    {
        if (_scrollDragOwner == ScrollDragOwner.Preview || WheelOwns(ScrollDragOwner.Preview) || _switchingDocument || !ReferenceEquals(_renderedPreviewTab, _shown) || _shown?.Session.IsCurrent(_renderedPreviewRevision) != true || !_shell.Settings.SyncScroll || _shell.ViewMode != ViewMode.Split || _syncingScroll || Preview.Document is null)
        {
            return;
        }

        QueueSourceToPreviewScroll();
    }

    private void ScrollEditorTo(int line)
    {
        var document = Editor.Document;
        if (document.LineCount == 0)
        {
            return;
        }

        _syncingScroll = true;
        var target = Math.Clamp(line, 1, document.LineCount);
        SetEditorVerticalOffset(Editor.TextArea.TextView.GetVisualTopByDocumentLine(target));
        Dispatcher.UIThread.Post(() => _syncingScroll = false, DispatcherPriority.Background);
    }

    private void SetEditorVerticalOffset(double offset)
    {
        Tern.PreviewView.SmoothScrolling.CancelWithin(Editor);
        Editor.ApplyTemplate();
        // AvaloniaEdit 12's ScrollToVerticalOffset method is an unimplemented compatibility stub.
        if (Editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } viewer)
            viewer.SetCurrentValue(ScrollViewer.OffsetProperty, new Vector(viewer.Offset.X, offset));
    }

    internal void GoToLine(int line, int column, bool focusEditor = true)
    {
        ResetScrollIntent();
        Tern.PreviewView.SmoothScrolling.CancelWithin(Editor);
        Tern.PreviewView.SmoothScrolling.CancelWithin(Preview);
        if (_shown is { } tab) ContextFor(tab).PendingAnchor = null;
        var document = Editor.Document;
        var clamped = Math.Clamp(line, 1, Math.Max(1, document.LineCount));
        var docLine = document.GetLineByNumber(clamped);
        Editor.CaretOffset = Math.Min(docLine.Offset + Math.Max(0, column - 1), docLine.EndOffset);
        Editor.ScrollTo(clamped, Math.Max(1, column));
        Editor.TextArea.Caret.BringCaretToView();
        if (_shell.ViewMode == ViewMode.Reading)
            NavigateReadingPreviewTo(clamped);
        else if (focusEditor)
        {
            Editor.Focus();
        }
    }

    private void NavigateReadingPreviewTo(int line)
    {
        if (_shown is not { } tab) return;
        var target = Math.Clamp(line, 1, Math.Max(1, Editor.Document.LineCount));
        ContextFor(tab).ReadingLine = target;
        if (ReferenceEquals(_renderedPreviewTab, tab) && tab.Session.IsCurrent(_renderedPreviewRevision))
            ScrollPreviewTo(target);
    }

    private void GoToAnchor(string anchor)
    {
        if (_shown is not { } tab) return;
        var context = ContextFor(tab);
        // Opening a linked document schedules its preview asynchronously. Keep the
        // fragment with that tab until its current model can resolve the target.
        context.PendingAnchor = anchor;
        if (!ReferenceEquals(_renderedPreviewTab, tab) || !tab.Session.IsCurrent(_renderedPreviewRevision)) return;
        context.PendingAnchor = null;
        if (!Preview.ScrollToAnchor(anchor))
        {
            _shell.StatusMessage = "The linked heading or anchor was not found: #" + anchor.TrimStart('#');
            return;
        }

        var wanted = anchor.TrimStart('#');
        try { wanted = Uri.UnescapeDataString(wanted); }
        catch (UriFormatException) { }
        var line = Preview.Document!.FindAnchorSourceLine(wanted)
            ?? Preview.Document.Outline.First(o => string.Equals(o.Anchor, wanted, StringComparison.OrdinalIgnoreCase)).Line;
        context.ReadingLine = Math.Max(1, line);
        GoToLine(line, 1);
        ScrollEditorTo(line);
        Preview.ScrollToAnchor(anchor);
    }

    private void ApplyViewMode()
    {
        var mode = _shell.ViewMode;
        if (_shown is { } tab && _contexts.TryGetValue(tab, out var context))
        {
            if (mode == ViewMode.Reading && EditorHost.IsVisible) context.ReadingLine = EditorTopLine();
            else if (mode != ViewMode.Reading && !EditorHost.IsVisible && PreviewHost.IsVisible)
                ScrollEditorTo(Math.Max(1, Preview.TopSourceLine));
        }
        SourceButton.IsChecked = mode == ViewMode.Source;
        SplitButton.IsChecked = mode == ViewMode.Split;
        ReadingButton.IsChecked = mode == ViewMode.Reading;
        SyncScrollingCheck.IsEnabled = mode == ViewMode.Split;
        EditorHost.IsVisible = mode != ViewMode.Reading;
        PreviewHost.IsVisible = mode != ViewMode.Source;
        ContentSplitter.IsVisible = _shown is not null && mode == ViewMode.Split;
        var columns = ContentGrid.ColumnDefinitions;
        columns[0].Width = mode == ViewMode.Reading ? new GridLength(0) : new GridLength(mode == ViewMode.Split ? _shell.Settings.SourceShare : 1, GridUnitType.Star);
        columns[1].Width = mode == ViewMode.Split ? new GridLength(1) : new GridLength(0);
        columns[2].Width = mode == ViewMode.Source ? new GridLength(0) : new GridLength(mode == ViewMode.Split ? 1 - _shell.Settings.SourceShare : 1, GridUnitType.Star);
        if (mode != ViewMode.Source)
        {
            RebuildPreview();
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (FindBar.IsVisible) FindBox.Focus();
            else FocusVisibleContent();
        }, DispatcherPriority.Loaded);
        UpdatePanelRestoreButtons();
    }

    private void FocusVisibleContent()
    {
        if (_windowClosed || _shown is null) return;
        if (EmptyText.IsVisible) { EmptyText.GetVisualDescendants().OfType<Button>().FirstOrDefault()?.Focus(); return; }
        if (_shell.ViewMode == ViewMode.Reading) Preview.Focus(); else Editor.TextArea.Focus();
    }

    private void UpdateChrome()
    {
        var hasSavedFile = _shown?.FilePath is not null;
        this.FindControl<Button>("HistoryOpenButton")!.IsEnabled = hasSavedFile;
        this.FindControl<TextBlock>("HistoryStartHint")!.IsVisible = !hasSavedFile;
        var tab = _shown;
        var hasTab = tab is not null;
        var online = tab is { IsExternalDraft: true, FilePath: null };
        LocationBadge.IsVisible = hasTab && Width >= 1180 * Math.Max(1, _shell.Settings.UiScale);
        LocationBadgeText.Text = online ? tab!.IsCachedSource ? "From URL · cached copy" : "From URL" : tab?.FilePath is not null ? tab.SourceUrl is null ? "Local file" : "Saved local copy" : "Unsaved draft";
        ToolTip.SetTip(LocationBadge, online
            ? "This tab was opened from a URL. Save As creates a local file; changes are not uploaded."
            : "This tab is a local file or a new local document. Linked web images may still load from their source.");
        EmptyText.IsVisible = !hasTab || _welcomeIsOpen && tab is { IsUntitled: true } && tab.CurrentText.Length == 0;
        SetWelcomeDetailsVisibility();
        UpdateFirstNoteChecklist();
        ContentSplitter.IsVisible = hasTab && _shell.ViewMode == ViewMode.Split;
        InspectorContent.IsVisible = hasTab;
        InspectorEmpty.IsVisible = !hasTab;
        EditorHost.IsVisible = hasTab && _shell.ViewMode != ViewMode.Reading;
        PreviewHost.IsVisible = hasTab && _shell.ViewMode != ViewMode.Source;
        Title = tab is null ? "Tern" : $"{(tab.IsDirty ? "● " : string.Empty)}{tab.DisplayName} — Tern";
        Breadcrumb.Text = BreadcrumbFor(tab);
        DocumentCaption.Text = tab is null ? "Your writing desk" : tab.DisplayName + (tab.IsDirty ? "  ·  Unsaved changes" : string.Empty);
        RefreshCommandAvailability();
        UpdatePanelRestoreButtons();
    }

    private void UpdatePanelRestoreButtons()
    {
        RefreshMenuState();
        RestoreLibraryButton.IsVisible = !Sidebar.IsVisible;
        RestoreInspectorButton.IsVisible = !RightPanel.IsVisible;
        RestoreReadingButton.IsVisible = _shown is not null && _shell.ViewMode == ViewMode.Source;
    }

    private string BreadcrumbFor(DocumentTab? tab)
    {
        if (tab is null)
        {
            return string.Empty;
        }

        if (tab.FilePath is not { } path)
        {
            return "/  " + tab.DisplayName;
        }

        var root = _shell.Workspace?.Root;
        var relative = root is not null && _shell.Workspace!.IsInside(path) ? Path.GetRelativePath(root, path) : path;
        return "/  " + string.Join("  /  ", relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).TakeLast(3));
    }

    private void OnTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_syncingTabs && TabStrip.SelectedItem is DocumentTab tab && !ReferenceEquals(tab, _shell.ActiveTab))
        {
            _shell.SelectTab(tab, TabActivationReason.User);
        }
    }

    private async void OnTabCloseClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DocumentTab tab)
        {
            await _shell.CloseTabAsync(tab);
            e.Handled = true;
        }
    }

    private void OnNewTabClick(object? sender, RoutedEventArgs e) => _shell.NewDocument();

    private void OnModeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string tag } && Enum.TryParse<ViewMode>(tag, out var mode))
        {
            var id = mode switch { ViewMode.Source => CommandIds.ViewSource, ViewMode.Reading => CommandIds.ViewReading, _ => CommandIds.ViewSplit };
            if (!CanRunCommand(id)) return;
            var changed = _shell.ViewMode != mode;
            _shell.ViewMode = mode;
            if (changed) _shell.StatusMessage = mode + " view";
        }

        ApplyViewMode();
    }

    private void OnSearchBoxClick(object? sender, RoutedEventArgs e) => ShowQuickOpen(string.Empty);
}
