using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Tern.App.Commands;
using Tern.Desktop.Services;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private Flyout? _aiEntryFlyout;
    internal bool IsAIPopupOpen => _aiEntryFlyout?.IsOpen == true;
    private bool _inspectorForcedCompact;
    private bool _wasNarrow;
    private bool _libraryAutoCollapsed;
    private bool _libraryForcedOpen;
    private bool _shortScreen;
    private SurfaceMotion? _libraryMotion;
    private SurfaceMotion? _inspectorMotion;
    private SurfaceMotion? _bannerMotion;
    private SurfaceMotion? _statusMotion;

    private void ConfigureSurfaceMotion()
    {
        _libraryMotion = new(Sidebar, ChromeMotionLayer, ReturnFromChrome);
        _inspectorMotion = new(RightPanel, ChromeMotionLayer, ReturnFromChrome);
        _bannerMotion = new(NotificationBanner, ChromeMotionLayer, ReturnFromChrome);
        _statusMotion = new(StatusMessage, ChromeMotionLayer, ReturnFromChrome);
        MotionPolicy.Changed += OnMotionPolicyChanged;
    }

    private void ReturnFromChrome()
    {
        if (_shown is null) NewTabButton.Focus();
        else FocusVisibleContent();
    }

    private void OnMotionPolicyChanged(object? sender, EventArgs e)
    {
        _libraryMotion?.Cancel();
        _inspectorMotion?.Cancel();
        _bannerMotion?.Cancel();
        _statusMotion?.Cancel();
    }

    internal bool LibraryAutoCollapsed => _libraryAutoCollapsed;
    internal bool IsShortScreen => _shortScreen;
    private double EffectiveWidth => Bounds.Width > 0 ? Bounds.Width : Width;
    private double EffectiveHeight => Bounds.Height > 0 ? Bounds.Height : Height;

    // Below ~1000 effective px the Library collapses; the Expand Library button and its shortcut still toggle it.
    private void ApplyLibraryVisibility()
    {
        var shown = _libraryAutoCollapsed ? _libraryForcedOpen : _shell.Settings.SidebarVisible;
        _libraryMotion!.SetVisible(shown);
        Body.ColumnDefinitions[0].Width = shown ? new GridLength(_shell.Settings.SidebarWidth) : new GridLength(0);
    }

    private void ConfigureCompactAI()
    {
        MainToolbarGrid.Children.Remove(AIBar);
        AIBar.IsVisible = true;
        _aiEntryFlyout = DialogParts.PanelFlyout(AIBar);
    }

    private void OnAIEntryClick(object? sender, RoutedEventArgs e)
    {
        ReloadAIConnections();
        FitAIPopup();
        _aiEntryFlyout?.ShowAt(AIEntryButton);
    }

    private void FitAIPopup()
    {
        // Constrain measure, not just the final arrangement: long names/paths must wrap
        // inside the card instead of giving the flyout an oversized scroll extent.
        AIBar.Width = Math.Clamp(Bounds.Width - 48, 240, 420);
        AIBar.MaxHeight = Math.Max(160, Math.Min(640, Bounds.Height - 80));
        AIConnectionDetailsText.MaxWidth = Math.Max(160, AIBar.Width - 64);
    }

    private void OnAIEntryCloseClick(object? sender, RoutedEventArgs e) => _aiEntryFlyout?.Hide();

    private void OnAIConnectionsClick(object? sender, RoutedEventArgs e)
    {
        _aiEntryFlyout?.Hide();
        OnMenuCommandClick(sender, e);
    }

    private void UpdateResponsiveLayout()
    {
        if (_windowClosed) return;
        FitAIPopup();
        var scale = Math.Max(1, _shell.Settings.UiScale);
        var collapse = WindowSizing.IsCompactWidth(EffectiveWidth, scale);
        if (collapse != _libraryAutoCollapsed)
        {
            _libraryAutoCollapsed = collapse;
            _libraryForcedOpen = false;
            if (!_focusMode) ApplyLibraryVisibility();
        }
        var short_ = WindowSizing.IsShortHeight(EffectiveHeight);
        _shortScreen = short_;
        ContextGuidance.IsVisible = !short_ && _shell.Settings.ShowContextGuidance;
        var compact = Width < 1180 * Math.Max(1, _shell.Settings.UiScale);
        FindResults.MaxHeight = Math.Clamp((Height / Math.Max(.75, _shell.Settings.UiScale) - 480) * .5, 28, 160);
        UpdateFindLayout(compact);
        Breadcrumb.IsVisible = VersionLabel.IsVisible = TitleGuideButton.IsVisible = TitleSettingsButton.IsVisible = !compact;
        DocumentCaption.IsVisible = false;
        TitleLayout.ColumnDefinitions[0].Width = new GridLength((compact ? 100 : 220) * scale);
        SearchBox.Width = (compact ? 180 : 300) * scale;
        SearchHint.IsVisible = !compact;
        if (_shown is not null) LocationBadge.IsVisible = !compact;
        Grid.SetRow(DocumentActions, compact ? 1 : 0);
        Grid.SetColumn(DocumentActions, compact ? 0 : 2);
        Grid.SetColumnSpan(DocumentActions, compact ? 3 : 1);
        DocumentActions.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        FocusButton.IsVisible = !compact;
        AIEntryButton.MaxWidth = compact ? 130 : 180;
        UpdateAIEntryCaption();
        MainToolbarGrid.RowSpacing = compact ? GuideParts.SpaceValue(4) : 0;
        StatusFields.ItemSpacing = GuideParts.SpaceValue(compact ? 4 : 12);
        Grid.SetRow(StatusFields, 0);
        Grid.SetColumn(StatusFields, compact ? 0 : 1);
        Grid.SetColumnSpan(StatusFields, compact ? 2 : 1);
        UpdateResponsiveStatus();
        var scrollLibrary = Height < 700 * Math.Min(1.4, _shell.Settings.UiScale);
        SidebarScroll.VerticalScrollBarVisibility = scrollLibrary ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        LibraryContent.MaxHeight = scrollLibrary ? Math.Max(120, (Height - 200) * .5) : double.PositiveInfinity;

        var narrow = Width < 1180;
        if (narrow != _wasNarrow) _inspectorForcedCompact = false;
        _wasNarrow = narrow;
        _compactInspector = narrow && !_inspectorForcedCompact;
        if (!_focusMode)
        {
            _inspectorMotion!.SetVisible(_shell.Settings.InspectorVisible && !_compactInspector);
            Body.ColumnDefinitions[4].Width = RightPanel.IsVisible ? new GridLength(_shell.Settings.InspectorWidth) : new GridLength(0);
        }
        UpdatePanelRestoreButtons();
        RefreshAuthoringState();
    }

    private void UpdateResponsiveStatus()
    {
        var compact = Width < 1180 * Math.Max(1, _shell.Settings.UiScale);
        StatusPath.IsVisible = StatusProfile.IsVisible = _shown is not null && !compact && !_shortScreen;
        StatusWords.IsVisible = _shown is not null && (Editor.SelectionLength > 0 || !compact && !_shortScreen);
        // Long recovery descriptions remain in the tooltip/details action. Keep
        // the explicit Encoding and Line endings controls on a compact row.
        StatusSaved.MaxWidth = compact ? 100 : double.PositiveInfinity;
    }

    private void SetInspectorVisibility(bool visible)
    {
        if (_focusMode) ToggleFocusMode();
        _shell.Settings.InspectorVisible = visible;
        _inspectorForcedCompact = visible;
        _compactInspector = false;
        _inspectorMotion!.SetVisible(visible);
        Body.ColumnDefinitions[4].Width = visible ? new GridLength(_shell.Settings.InspectorWidth) : new GridLength(0);
        UpdatePanelRestoreButtons();
        _shell.SaveSettings();
    }

    private void ResetLayout()
    {
        if (_focusMode) ToggleFocusMode();
        var settings = _shell.Settings;
        settings.SidebarVisible = settings.InspectorVisible = true;
        settings.SidebarWidth = 280;
        settings.InspectorWidth = 260;
        settings.SourceShare = .5;
        _inspectorForcedCompact = false;
        _libraryForcedOpen = false;
        ContentGrid.ColumnDefinitions[0].Width = ContentGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
        ApplySettings();
        ApplyViewMode();
        _shell.SaveSettings();
        _shell.StatusMessage = "Layout reset. Fonts, shortcuts, connections and documents were kept.";
    }

    private void OnLayoutResizeCompleted(object? sender, RoutedEventArgs e)
    {
        if (_focusMode) return;
        if (Sidebar.IsVisible) _shell.Settings.SidebarWidth = Sidebar.Bounds.Width;
        if (RightPanel.IsVisible) _shell.Settings.InspectorWidth = RightPanel.Bounds.Width;
        if (_shell.ViewMode == Tern.App.Settings.ViewMode.Split && EditorHost.Bounds.Width + PreviewHost.Bounds.Width > 0)
            _shell.Settings.SourceShare = EditorHost.Bounds.Width / (EditorHost.Bounds.Width + PreviewHost.Bounds.Width);
        _shell.SaveSettings();
    }
}
