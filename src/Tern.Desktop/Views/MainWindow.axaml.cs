using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
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
    private readonly MarkdownColorizer _colorizer = new();
    private readonly DispatcherTimer _previewTimer = new() { Interval = PreviewDelay };
    private readonly SystemLauncher _launcher = new();
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
    private bool _checkingExternalChanges;
    private GridLength _wideInspectorWidth = new(240);
    private Task _previewWork = Task.CompletedTask;
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
            Sidebar.IsVisible = RightPanel.IsVisible = false;
            Body.ColumnDefinitions[0].Width = Body.ColumnDefinitions[4].Width = new GridLength(0);
        }
        else
        {
            Sidebar.IsVisible = _focusSidebar;
            RightPanel.IsVisible = _focusInspector;
            Body.ColumnDefinitions[0].Width = _focusSidebarWidth;
            Body.ColumnDefinitions[4].Width = _focusInspectorWidth;
        }

        FocusButton.IsChecked = _focusMode;
        UpdatePanelRestoreButtons();
        if (_shell.ViewMode == ViewMode.Reading) Preview.Focus(); else Editor.Focus();
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

    public MainWindow(ShellViewModel shell, DesktopDialogs? dialogs = null, RemoteImageLoader? remoteImages = null, AIWorkspaceService? aiWorkspace = null)
    {
        _shell = shell;
        _dialogs = dialogs;
        _remoteImages = remoteImages;
        _aiWorkspace = aiWorkspace ?? new AIWorkspaceService(shell.Paths.SettingsDirectory);
        if (dialogs is not null)
        {
            dialogs.Owner = this;
        }

        InitializeComponent();
        ConfigureContextMenus();
        Closed += (_, _) => { foreach (var request in _onlineRequests.Values.ToArray()) request.Cancel(); };
        SideList.AddHandler(PointerPressedEvent, TrackLibraryPointer, RoutingStrategies.Tunnel, handledEventsToo: true);
        SideList.AddHandler(KeyDownEvent, (_, _) => _libraryRightPress = false, RoutingStrategies.Tunnel);
        SideList.AddHandler(KeyDownEvent, OnLibraryDeleteKeyDown, RoutingStrategies.Tunnel);
        FileTree.AddHandler(PointerPressedEvent, TrackLibraryPointer, RoutingStrategies.Tunnel, handledEventsToo: true);
        FileTree.AddHandler(KeyDownEvent, (_, _) => _libraryRightPress = false, RoutingStrategies.Tunnel);
        FileTree.AddHandler(KeyDownEvent, OnLibraryDeleteKeyDown, RoutingStrategies.Tunnel);
        if (_remoteImages is not null) Preview.RemoteImageLoader = _remoteImages.LoadAsync;
        ApplyTheme();
        ConfigureEditor();
        WirePreview();
        WireScrollInteraction();
        WireShell();
        _shell.Recent.Changed += OnRecentChanged;
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
            Breadcrumb.IsVisible = Width >= 1120;
            DocumentCaption.IsVisible = Width >= 1180;
            SearchBox.Width = Width < 1120 ? 200 : 300;
            if (!_focusMode && Width < 1180 && RightPanel.IsVisible)
            {
                _wideInspectorWidth = Body.ColumnDefinitions[4].Width;
                _compactInspector = true;
                RightPanel.IsVisible = false;
                Body.ColumnDefinitions[4].Width = new GridLength(0);
            }
            else if (!_focusMode && Width >= 1180 && _compactInspector)
            {
                _compactInspector = false;
                RightPanel.IsVisible = true;
                Body.ColumnDefinitions[4].Width = _wideInspectorWidth;
            }
            UpdatePanelRestoreButtons();
        };
        Closing += OnWindowClosing;
        Activated += async (_, _) => await CheckExternalChangesAsync();
        Closed += (_, _) => Dispose();
    }

    public void Dispose()
    {
        if (_windowClosed) return;
        _windowClosed = true;
        StopObservingAIConnections();
        foreach (var prompt in OverlayLayer.Children.OfType<AIPromptPanel>()) prompt.Dispose();
        _previewGeneration++;
        _previewTimer.Stop();
        Preview.Document = null;
        _remoteImages?.Dispose();
        foreach (var (tab, context) in _contexts)
        {
            context.Monitor?.Dispose();
            tab.PropertyChanged -= OnTabPropertyChanged;
        }
        _shell.Tabs.CollectionChanged -= OnTabsChanged;
        _shell.OpenWorkspaces.CollectionChanged -= OnOpenWorkspacesChanged;
        _shell.Recent.Changed -= OnRecentChanged;
        _shell.PropertyChanged -= OnShellPropertyChanged;
        _shell.NavigationRequested -= OnNavigationRequested;
        _colorizer.Attach(null);
        foreach (var workspace in _observedWorkspaces) workspace.FilesChanged -= OnWorkspaceFilesChanged;
        GC.SuppressFinalize(this);
    }

    public ShellViewModel Shell => _shell;

    internal TextEditor EditorControl => Editor;

    internal PreviewView.PreviewView PreviewControl => Preview;

    private sealed class TabContext(AvaloniaDocumentBuffer buffer)
    {
        public AvaloniaDocumentBuffer Buffer { get; } = buffer;

        public int Caret { get; set; }

        public double ScrollY { get; set; }
        public int ReadingLine { get; set; } = 1;
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
                _previewGeneration++;
                _previewTimer.Stop();
                _previewTimer.Start();
                InvalidateFind();
            }
        };
        tab.AnalysisChanged += (_, _) =>
        {
            if (ReferenceEquals(tab, _shown))
            {
                RefreshPanels();
                UpdateStatus();
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
            context.Monitor.Changed += (_, _) => Dispatcher.UIThread.Post(async () => await CheckExternalChangesAsync());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _shell.StatusMessage = "File monitoring unavailable; changes are checked on focus and save: " + ex.Message;
        }
    }

    internal async Task CheckExternalChangesAsync()
    {
        if (_windowClosed || _checkingExternalChanges || OwnedWindows.Any()) return;
        _checkingExternalChanges = true;
        try { await _shell.CheckForExternalChangesAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _shell.StatusMessage = "Could not check disk changes: " + ex.Message; }
        finally { _checkingExternalChanges = false; }
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
                StatusMessage.Text = _shell.StatusMessage;
                break;
            case nameof(ShellViewModel.Workspace):
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

        if (e.PropertyName is nameof(DocumentTab.IsDirty) or nameof(DocumentTab.DisplayName) or nameof(DocumentTab.Conflict))
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
            previous.ScrollY = Editor.VerticalOffset;
            if (_shell.ViewMode == ViewMode.Reading) previous.ReadingLine = Math.Max(1, Preview.TopSourceLine);
            _shell.RememberViewState(_shown, previous.Caret, previous.ScrollY);
        }

        _switchingDocument = true;
        ++_previewGeneration;
        _renderedPreviewTab = null;
        Preview.Document = null;
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
            Editor.CaretOffset = Math.Clamp(caret, 0, context.Buffer.Document.TextLength);
            var scroll = state?.ScrollOffset ?? context.ScrollY;
            Dispatcher.UIThread.Post(() => { if (!_windowClosed && ReferenceEquals(_shown, tab)) Editor.ScrollToVerticalOffset(scroll); }, DispatcherPriority.Loaded);
            RebuildPreview();
        }

        _switchingDocument = false;
        UpdateChrome();
        RefreshPanels();
        UpdateStatus();
        RefreshTreeDirtyState();
    }

    private void ConfigureEditor()
    {
        // Native key bindings run before routed key handlers. Let the registry
        // own history shortcuts, including when users remove or move them.
        RemoveNativeHistoryBindings(Editor.KeyBindings);
        RemoveNativeHistoryBindings(Editor.TextArea.KeyBindings);
        RemoveNativeHistoryBindings(Editor.TextArea.DefaultInputHandler);
        Editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateStatus();
        Editor.TextArea.TextView.ScrollOffsetChanged += OnEditorScrolled;
        Editor.TextArea.SelectionChanged += (_, _) => UpdateStatus();
        Editor.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
        Editor.TextArea.TextView.CurrentLineBackground = null;
        ApplyEditorColors();
        UpdatePanelRestoreButtons();
    }

    private static void RemoveNativeHistoryBindings(ICollection<KeyBinding> bindings)
    {
        foreach (var binding in bindings.Where(b => ReferenceEquals(b.Command, ApplicationCommands.Undo)
                     || ReferenceEquals(b.Command, ApplicationCommands.Redo)
                     || (b.Gesture is { Key: Key.Z or Key.Y } gesture
                         && gesture.KeyModifiers is KeyModifiers.Control or (KeyModifiers.Control | KeyModifiers.Shift))).ToArray())
            bindings.Remove(binding);
    }

    private void RemoveNativeHistoryBindings(AvaloniaEdit.Editing.TextAreaInputHandler handler)
    {
        RemoveNativeHistoryBindings(handler.KeyBindings);
        // AvaloniaEdit also dispatches a routed command's default gesture
        // directly from this collection, independently of KeyBindings.
        foreach (var binding in handler.CommandBindings.Where(b => ReferenceEquals(b.Command, ApplicationCommands.Undo)
                     || ReferenceEquals(b.Command, ApplicationCommands.Redo)).ToArray())
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

    internal void ApplySettings()
    {
        var s = _shell.Settings;
        Editor.FontFamily = new Avalonia.Media.FontFamily(s.EditorFontFamily);
        Editor.FontSize = s.EditorFontSize;
        Editor.WordWrap = s.WordWrap;
        Editor.ShowLineNumbers = s.ShowLineNumbers;
        Editor.Options.HighlightCurrentLine = s.HighlightCurrentLine;
        Editor.Options.IndentationSize = s.TabSize;
        Editor.Options.ConvertTabsToSpaces = s.InsertSpacesForTab;
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        ReferenceEditor.FontFamily = Editor.FontFamily;
        ReferenceEditor.FontSize = s.EditorFontSize;
        ReferenceEditor.WordWrap = s.WordWrap;
        Preview.FontSize = s.PreviewFontSize;
        // Document typography follows the Windows/browser reading stack rather
        // than inheriting the Inter font used by the application chrome.
        Preview.FontFamily = new Avalonia.Media.FontFamily("Segoe UI, Noto Sans, Helvetica, Arial, sans-serif");
        Preview.ReadingWidth = s.ReadingWidth;
        Preview.UseFullWidth = s.PreviewFullWidth;
        if (Application.Current is { } application)
        {
            application.Resources["Tern.UiFontSize"] = 14 * s.UiScale;
            application.Resources["Tern.SmallFontSize"] = 12 * s.UiScale;
            application.Resources["Tern.TitleFontSize"] = 22 * s.UiScale;
            application.Resources["Tern.SectionFontSize"] = 18 * s.UiScale;
            application.Resources["Tern.MenuItemHeight"] = 24 * s.UiScale + 12;
        }
        Preview.ReduceMotion = s.ReduceMotion;
        Body.ColumnDefinitions[0].Width = s.SidebarVisible ? new GridLength(s.SidebarWidth) : new GridLength(0);
        Sidebar.IsVisible = s.SidebarVisible;
        SearchHint.Text = Gesture.Describe(_shell.Registry.GetGesture(CommandIds.QuickOpen));
        RefreshCommandHints();
        ApplyEditorColors();
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
    }

    private void _colorizerRefresh() => Editor.TextArea.TextView.Redraw();

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exitApproved) return;
        e.Cancel = true;
        if (_checkingWindowClose) return;
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _shell.StatusMessage = "Could not finish closing: " + ex.Message;
            _dialogs?.ShowMessage("Tern is still open", _shell.StatusMessage);
        }
        finally
        {
            _checkingWindowClose = false;
        }
    }

    private void RebuildPreview() => _ = RebuildPreviewAsync();

    internal Task RebuildPreviewAsync()
    {
        _previewWork = BuildPreviewAsync(_previewWork, ++_previewGeneration);
        return _previewWork;
    }

    private async Task BuildPreviewAsync(Task previous, long generation)
    {
        if (_shown is null)
        {
            Preview.Document = null;
            return;
        }

        if (_shown is { FilePath: null, ResourceDocumentPath: null, SourceUrl: { } remoteSource } online)
        {
            var cached = await MarkdownUrlCache.ReadAsync(MarkdownUrlCache.Root(_shell.Paths.StateDirectory), remoteSource);
            if (cached is not null) { online.ResourceDocumentPath = cached.CachedPath; online.ResourceFiles = cached.Assets; }
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
            var document = await Task.Run(() => PreviewBuilder.Build(snapshot.Text, options));
            if (_windowClosed || generation != _previewGeneration || !ReferenceEquals(tab, _shown)
                || !tab.Session.IsCurrent(snapshot.Revision)) return;
            // A navigation request may arrive while this build is awaiting parsing.
            // Read the latest position only after the tab/revision checks, and before
            // replacing the view can emit position notifications for its old content.
            var context = ContextFor(tab);
            var restoreLine = _shell.ViewMode == ViewMode.Reading ? context.ReadingLine : topLine;
            Preview.Document = document;
            _renderedPreviewTab = tab;
            _renderedPreviewRevision = snapshot.Revision;
            context.ReadingLine = Math.Max(1, restoreLine);
            ScrollPreviewTo(Math.Max(1, restoreLine));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            if (generation == _previewGeneration && !_windowClosed)
                _shell.StatusMessage = "Preview could not be updated: " + ex.Message;
        }
    }

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
        Preview.AnchorRequested += (_, e) => e.Handled = Preview.ScrollToAnchor(e.Fragment);
        Preview.SourceLineActivated += (_, e) => GoToLine(e.Line, 0);
        Preview.TopSourceLineChanged += (_, e) =>
        {
            if (_shell.ViewMode == ViewMode.Reading && !Preview.SuppressNotifications && _shown is { } readingTab)
                ContextFor(readingTab).ReadingLine = Math.Max(1, e.Line);
            if (_scrollDragOwner == ScrollDragOwner.Source || _switchingDocument || !ReferenceEquals(_renderedPreviewTab, _shown) || _shown?.Session.IsCurrent(_renderedPreviewRevision) != true || !_shell.Settings.SyncScroll || _shell.ViewMode != ViewMode.Split || Preview.SuppressNotifications || _syncingScroll)
            {
                return;
            }

            ScrollEditorTo(e.Line);
        };
    }

    private void OnEditorScrolled(object? sender, EventArgs e)
    {
        if (_scrollDragOwner == ScrollDragOwner.Preview || _switchingDocument || !ReferenceEquals(_renderedPreviewTab, _shown) || _shown?.Session.IsCurrent(_renderedPreviewRevision) != true || !_shell.Settings.SyncScroll || _shell.ViewMode != ViewMode.Split || _syncingScroll || Preview.Document is null)
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
        Editor.ScrollToVerticalOffset(Editor.TextArea.TextView.GetVisualTopByDocumentLine(target));
        Dispatcher.UIThread.Post(() => _syncingScroll = false, DispatcherPriority.Background);
    }

    internal void GoToLine(int line, int column)
    {
        var document = Editor.Document;
        var clamped = Math.Clamp(line, 1, Math.Max(1, document.LineCount));
        var docLine = document.GetLineByNumber(clamped);
        Editor.CaretOffset = Math.Min(docLine.Offset + Math.Max(0, column - 1), docLine.EndOffset);
        Editor.ScrollTo(clamped, Math.Max(1, column));
        Editor.TextArea.Caret.BringCaretToView();
        if (_shell.ViewMode == ViewMode.Reading)
            NavigateReadingPreviewTo(clamped);
        else
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
        var heading = _shown?.Analysis?.Headings.FirstOrDefault(h => string.Equals(h.Anchor, anchor, StringComparison.OrdinalIgnoreCase));
        if (heading is not null)
        {
            GoToLine(heading.Line, 1);
        }

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
        EditorHost.IsVisible = mode != ViewMode.Reading;
        PreviewHost.IsVisible = mode != ViewMode.Source;
        ContentSplitter.IsVisible = _shown is not null && mode == ViewMode.Split;
        var columns = ContentGrid.ColumnDefinitions;
        columns[0].Width = mode == ViewMode.Reading ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        columns[1].Width = mode == ViewMode.Split ? new GridLength(1) : new GridLength(0);
        columns[2].Width = mode == ViewMode.Source ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        if (mode != ViewMode.Source)
        {
            RebuildPreview();
        }
        Dispatcher.UIThread.Post(FocusVisibleContent, DispatcherPriority.Loaded);
        UpdatePanelRestoreButtons();
    }

    private void FocusVisibleContent()
    {
        if (_windowClosed || _shown is null) return;
        if (_shell.ViewMode == ViewMode.Reading) Preview.Focus(); else Editor.Focus();
    }

    private void UpdateChrome()
    {
        var tab = _shown;
        var hasTab = tab is not null;
        var online = tab is { IsExternalDraft: true, FilePath: null };
        LocationBadge.IsVisible = hasTab;
        LocationBadgeText.Text = online ? "Online files" : "Local files";
        ToolTip.SetTip(LocationBadge, online
            ? "This tab was opened from a URL. Save As creates a local file; changes are not uploaded."
            : "This tab is a local file or a new local document. Linked web images may still load from their source.");
        EmptyText.IsVisible = !hasTab;
        ContentSplitter.IsVisible = hasTab && _shell.ViewMode == ViewMode.Split;
        InspectorContent.IsVisible = hasTab;
        InspectorEmpty.IsVisible = !hasTab;
        EditorHost.IsVisible = hasTab && _shell.ViewMode != ViewMode.Reading;
        PreviewHost.IsVisible = hasTab && _shell.ViewMode != ViewMode.Source;
        Title = tab is null ? "Tern" : $"{(tab.IsDirty ? "● " : string.Empty)}{tab.DisplayName} – Tern";
        Breadcrumb.Text = BreadcrumbFor(tab);
        DocumentCaption.Text = tab is null ? "Your writing desk" : tab.DisplayName + (tab.IsDirty ? "  ·  Unsaved changes" : string.Empty);
        RefreshCommandAvailability();
        UpdatePanelRestoreButtons();
    }

    private void UpdatePanelRestoreButtons()
    {
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
            _shell.ActiveTab = tab;
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
