using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Tern.App.Commands;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private Flyout? _aiEntryFlyout;
    internal bool IsAIPopupOpen => _aiEntryFlyout?.IsOpen == true;
    private bool _inspectorForcedCompact;
    private bool _wasNarrow;

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
        var compact = Width < 1180 * Math.Max(1, _shell.Settings.UiScale);
        FindResults.MaxHeight = Math.Clamp((Height / Math.Max(.75, _shell.Settings.UiScale) - 480) * .5, 28, 160);
        UpdateFindLayout(compact);
        Breadcrumb.IsVisible = VersionLabel.IsVisible = TitleGuideButton.IsVisible = TitleSettingsButton.IsVisible = !compact;
        DocumentCaption.IsVisible = false;
        TitleLayout.ColumnDefinitions[0].Width = new GridLength(compact ? 100 : 220);
        SearchBox.Width = compact ? 180 : 300;
        SearchHint.IsVisible = !compact;
        if (_shown is not null) LocationBadge.IsVisible = !compact;
        Grid.SetRow(DocumentActions, compact ? 1 : 0);
        Grid.SetColumn(DocumentActions, compact ? 0 : 2);
        Grid.SetColumnSpan(DocumentActions, compact ? 3 : 1);
        DocumentActions.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        FocusButton.IsVisible = !compact;
        AIEntryButton.MaxWidth = compact ? 130 : 180;
        UpdateAIEntryCaption();
        MainToolbarGrid.RowSpacing = compact ? 4 : 0;
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
            RightPanel.IsVisible = _shell.Settings.InspectorVisible && !_compactInspector;
            Body.ColumnDefinitions[4].Width = RightPanel.IsVisible ? new GridLength(_shell.Settings.InspectorWidth) : new GridLength(0);
        }
        UpdatePanelRestoreButtons();
    }

    private void UpdateResponsiveStatus()
    {
        var compact = Width < 1180 * Math.Max(1, _shell.Settings.UiScale);
        StatusPath.IsVisible = StatusWords.IsVisible = StatusProfile.IsVisible = _shown is not null && !compact;
    }

    private void SetInspectorVisibility(bool visible)
    {
        if (_focusMode) ToggleFocusMode();
        _shell.Settings.InspectorVisible = visible;
        _inspectorForcedCompact = visible;
        _compactInspector = false;
        RightPanel.IsVisible = visible;
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
