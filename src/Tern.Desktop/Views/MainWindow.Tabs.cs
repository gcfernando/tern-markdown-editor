using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Documents;
using Tern.App.Shell;
using Tern.Desktop.ViewModels;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private bool _tabNavigationWired;

    private async void OnDocumentContextClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        var tab = item.DataContext switch
        {
            DocumentTab document => document,
            SideRow { Path: { } path } row => _shell.FindOpenSource(path, row.IsOnline),
            TreeRow { IsDirectory: false } row => _shell.FindOpenSource(row.FullPath),
            _ => null
        };
        if (tab is null && Equals(item.Tag, "tab.closeAll")) tab = _shell.ActiveTab;
        if (tab is null) return;
        if (Equals(item.Tag, "tab.rename"))
        {
            if (tab.CanRename) await RenameDocumentFromContextAsync(tab);
            e.Handled = true;
            return;
        }
        var scope = (item.Tag as string) switch
        {
            "tab.closeAll" => TabCloseScope.All,
            "tab.closeRight" => TabCloseScope.Right,
            "tab.closeOthers" => TabCloseScope.Others,
            "tab.close" => TabCloseScope.This,
            _ => (TabCloseScope?)null
        };
        if (scope is not null) await _shell.CloseTabsRelativeAsync(tab, scope.Value);
        e.Handled = true;
    }

    private void OnTabStripLoaded(object? sender, RoutedEventArgs e)
    {
        if (_tabNavigationWired) return;
        _tabNavigationWired = true;
        TabStrip.SelectionChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (TabStrip.SelectedItem is { } selected) TabStrip.ScrollIntoView(selected);
            if (TabStrip.SelectedIndex == 0 && TabScroller is { } scroll)
                scroll.Offset = new Vector(0, scroll.Offset.Y);
            UpdateTabNavigation();
        }, DispatcherPriority.Loaded);
        TabStrip.LayoutUpdated += (_, _) => UpdateTabNavigation();
        UpdateTabNavigation();
    }

    private ScrollViewer? TabScroller => TabStrip.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    private void UpdateTabNavigation()
    {
        if (TabScroller is not { } scroll) return;
        var left = this.FindControl<Button>("TabsPrevious");
        var right = this.FindControl<Button>("TabsNext");
        // Include currently occupied arrow space so visibility cannot oscillate at the boundary.
        var available = scroll.Viewport.Width + (left?.IsVisible == true ? left.Bounds.Width + 8 : 0)
            + (right?.IsVisible == true ? right.Bounds.Width + 8 : 0);
        var overflow = scroll.Extent.Width > available + .5;
        if (left is not null) { left.IsVisible = overflow; left.IsEnabled = scroll.Offset.X > 0.5; }
        if (right is not null) { right.IsVisible = overflow; right.IsEnabled = scroll.Offset.X + scroll.Viewport.Width < scroll.Extent.Width - 0.5; }
    }

    private void OnTabScrollClick(object? sender, RoutedEventArgs e)
    {
        if (TabScroller is not { } scroll || sender is not Button button) return;
        var direction = Equals(button.Tag, "left") ? -1 : 1;
        var maximum = Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width);
        scroll.Offset = new Vector(Math.Clamp(scroll.Offset.X + direction * Math.Max(100, scroll.Viewport.Width * .75), 0, maximum), scroll.Offset.Y);
        UpdateTabNavigation();
    }
}
