using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
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
    private ILookup<string, DocumentTab>? _tabNameLookup;

    private async void OnTabMiddlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(TabStrip).Properties.IsMiddleButtonPressed || e.Source is not Visual source) return;
        if (source.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext is not DocumentTab tab) return;
        e.Handled = true;
        await _shell.CloseTabAsync(tab);
    }

    private void OnTabBarDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source ||
            source.GetSelfAndVisualAncestors().TakeWhile(visual => !ReferenceEquals(visual, sender))
                .Any(visual => visual is ListBoxItem or Button or Avalonia.Controls.Primitives.ScrollBar))
            return;
        _shell.NewDocument();
        e.Handled = true;
    }

    private async void OnDocumentContextClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        if (Equals(item.Tag, "tab.reopen"))
        {
            await _shell.ReopenClosedTabAsync();
            e.Handled = true;
            return;
        }
        var context = item.DataContext;
        var tab = await FindContextTabAsync(context);
        if (_windowClosed || !ReferenceEquals(context, item.DataContext)) return;
        if (tab is null && Equals(item.Tag, "tab.closeAll")) tab = _shell.ActiveTab;
        if (tab is null) return;
        if (Equals(item.Tag, "tab.copyPath"))
        {
            if (tab.FilePath is { } path && Clipboard is { } clipboard) await clipboard.SetTextAsync(path);
            e.Handled = true;
            return;
        }
        if (Equals(item.Tag, "tab.reveal"))
        {
            if (tab.FilePath is { } path)
            {
                try { _launcher.RevealInFileManager(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                { _shell.SetStatus("Cannot reveal the document: " + _shell.PlainProblem(ex), StatusSeverity.Error); }
            }
            e.Handled = true;
            return;
        }
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
        UpdateTabTitles();
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

    private static string TabTitle(DocumentTab tab, ILookup<string, DocumentTab> names)
    {
        var peers = names[tab.DisplayName].ToArray();
        if (peers.Length <= 1) return tab.DisplayName;
        var location = tab.FilePath is { } path ? Path.GetDirectoryName(path) : tab.SourceUrl ?? "Unsaved draft";
        if (tab.FilePath is not null)
        {
            var parts = (location ?? string.Empty).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (var depth = 1; depth <= parts.Length; depth++)
            {
                var suffix = string.Join(Path.DirectorySeparatorChar, parts.TakeLast(depth));
                if (!peers.Any(other => !ReferenceEquals(other, tab) && other.FilePath != tab.FilePath
                    && (Path.GetDirectoryName(other.FilePath) ?? "").EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                { location = suffix; break; }
            }
        }
        var sameSource = peers.Where(t => t.FilePath == tab.FilePath && t.SourceUrl == tab.SourceUrl).ToArray();
        var copy = sameSource.Length > 1 ? $" · copy {Array.IndexOf(sameSource, tab) + 1}" : string.Empty;
        return tab.DisplayName + " · " + location + copy;
    }

    private void UpdateTabTitles()
    {
        var names = _tabNameLookup ??= _shell.Tabs.ToLookup(tab => tab.DisplayName, StringComparer.OrdinalIgnoreCase);
        var renameHint = "Double-click to rename; " + CommandHint(Tern.App.Commands.CommandIds.Rename) + "\n";
        foreach (var item in TabStrip.GetVisualDescendants().OfType<ListBoxItem>())
        {
            if (item.DataContext is not DocumentTab tab) continue;
            if (item.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "TabTitleText") is not { } title) continue;
            title.SetCurrentValue(TextBlock.TextProperty, TabTitle(tab, names));
            ToolTip.SetTip(title, renameHint + (tab.FilePath ?? tab.SourceUrl ?? "Unsaved draft"));
        }
    }

    private void OnOpenDocumentsClick(object? sender, RoutedEventArgs e) => ShowPicker("Open documents", string.Empty,
        query =>
        {
            var names = _shell.Tabs.ToLookup(tab => tab.DisplayName, StringComparer.OrdinalIgnoreCase);
            return _shell.Tabs.Where(t => t.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (t.FilePath?.Contains(query, StringComparison.OrdinalIgnoreCase) == true))
            .Select(tab => new PickerItem(TabTitle(tab, names), tab.FilePath ?? tab.SourceUrl ?? "Unsaved draft",
                (ReferenceEquals(tab, _shown) ? "Active · " : "") + (tab.IsReadOnly ? "Read-only" : tab.IsDirty ? "Unsaved" : "Saved"), tab,
                CloseDocument: async () => { if (_shell.Tabs.Contains(tab)) await _shell.CloseTabAsync(tab); })).ToArray();
        },
        item => { if (item.Payload is DocumentTab tab && _shell.Tabs.Contains(tab)) _shell.SelectTab(tab, Tern.App.Shell.TabActivationReason.User); HideOverlay(); }, "No open documents");

    private void OnTabScrollClick(object? sender, RoutedEventArgs e)
    {
        if (TabScroller is not { } scroll || sender is not Button button) return;
        var direction = Equals(button.Tag, "left") ? -1 : 1;
        var maximum = Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width);
        scroll.Offset = new Vector(Math.Clamp(scroll.Offset.X + direction * Math.Max(100, scroll.Viewport.Width * .75), 0, maximum), scroll.Offset.Y);
        UpdateTabNavigation();
    }
}
