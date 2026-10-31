using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private void ConfigureContextMenus()
    {
        this.FindControl<Grid>("LibraryContent")!.ContextMenu = CreateBackgroundMenu();
        SideList.ContextMenu = CreateBackgroundMenu();
        FileTree.ContextMenu = CreateBackgroundMenu();
        TabStrip.ContextMenu = CreateBackgroundMenu();
        SideList.AddHandler(ContextRequestedEvent, OnDocumentContainerContext, RoutingStrategies.Tunnel);
        FileTree.AddHandler(ContextRequestedEvent, OnDocumentContainerContext, RoutingStrategies.Tunnel);
        TabStrip.AddHandler(ContextRequestedEvent, OnDocumentContainerContext, RoutingStrategies.Tunnel);
    }

    private ContextMenu CreateBackgroundMenu()
    {
        var menu = new ContextMenu();
        foreach (var (label, command) in new[]
        {
            ("Create Document", "file.new"), ("Open Files…", "file.open"),
            ("Open from URL…", "file.openUrl"), ("Close all documents", "tab.closeAll")
        })
        {
            var item = new MenuItem { Header = label, Tag = command };
            item.Click += OnMenuCommandClick;
            menu.Items.Add(item);
        }
        return menu;
    }

    private void OnDocumentContainerContext(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Control source) return;
        Control? container = source as ListBoxItem ?? source.FindAncestorOfType<ListBoxItem>();
        container ??= source as TreeViewItem ?? source.FindAncestorOfType<TreeViewItem>();
        if (container is null) return;
        // Templates contain both a section card and an ordinary row, one hidden.
        // Expanded TreeViewItems also contain their children's templates. Only the
        // clicked item's visible header may supply its context menu, including when
        // the click lands in that item's surrounding padding.
        var content = container.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(control => control.ContextMenu is not null
                && control.IsEffectivelyVisible
                && ReferenceEquals(control.GetVisualAncestors()
                    .FirstOrDefault(ancestor => ancestor is ListBoxItem or TreeViewItem), container));
        if (content?.ContextMenu is not { } menu) return;
        var hasOpenTab = container.DataContext switch
        {
            Tern.App.Documents.DocumentTab => true,
            Tern.Desktop.ViewModels.SideRow { Path: { } path } row => _shell.FindOpenSource(path, row.IsOnline) is not null,
            Tern.Desktop.ViewModels.TreeRow { IsDirectory: false } row => _shell.FindOpenSource(row.FullPath) is not null,
            _ => false
        };
        foreach (var action in menu.Items.OfType<MenuItem>())
        {
            if (Equals(action.Tag, "tab.closeAll")) action.IsEnabled = _shell.Tabs.Count > 0;
            else if (action.Tag is "tab.close" or "tab.closeRight" or "tab.closeOthers") action.IsEnabled = hasOpenTab;
        }
        // The row's own menu also handles its surrounding item padding. Opening a menu
        // must not activate a Library entry or start a network request.
        menu.Open(content);
        e.Handled = true;
    }
}
