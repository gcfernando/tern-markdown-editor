using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private int _documentContextGeneration;

    private async Task<Tern.App.Documents.DocumentTab?> FindContextTabAsync(object? context)
    {
        try
        {
            return context switch
            {
                Tern.App.Documents.DocumentTab tab => _shell.Tabs.Contains(tab) ? tab : null,
                Tern.Desktop.ViewModels.SideRow { Path: { } path } row => await _shell.FindOpenSourceAsync(path, row.IsOnline),
                Tern.Desktop.ViewModels.TreeRow { IsDirectory: false } row => await _shell.FindOpenTabAsync(row.FullPath),
                _ => null
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            if (!_windowClosed) _shell.SetStatus("Tern could not check the open document. Save your work, then try again: " + _shell.PlainProblem(ex), Tern.App.Shell.StatusSeverity.Error);
            return null;
        }
    }

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
        foreach (var command in new[] { "file.new", "file.open", "file.openUrl", "tab.closeAll" })
        {
            var item = new MenuItem { Tag = command };
            RefreshMenuCommand(item);
            item.Click += OnMenuCommandClick;
            menu.Items.Add(item);
        }
        return menu;
    }

    private async void OnDocumentContainerContext(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Control source) return;
        Control? container = source as ListBoxItem ?? source.FindAncestorOfType<ListBoxItem>();
        container ??= source as TreeViewItem ?? source.FindAncestorOfType<TreeViewItem>();
        if (container is null) return;
        // Expanded TreeViewItems also contain their children's templates. Only the
        // clicked item's visible header may supply its context menu, including when
        // the click lands in that item's surrounding padding.
        var content = container.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(control => control.ContextMenu is not null
                && control.IsEffectivelyVisible
                && ReferenceEquals(control.GetVisualAncestors()
                    .FirstOrDefault(ancestor => ancestor is ListBoxItem or TreeViewItem), container));
        if (content?.ContextMenu is not { } menu) return;
        EnsureLibraryEntryMenu(menu);
        RefreshMenuCommands(menu);
        var context = container.DataContext;
        var generation = ++_documentContextGeneration;
        foreach (var action in menu.Items.OfType<MenuItem>())
        {
            if (Equals(action.Tag, "tab.closeAll")) action.IsEnabled = _shell.Tabs.Count > 0;
            else if (action.Tag is "tab.close" or "tab.closeRight" or "tab.closeOthers") action.IsEnabled = false;
        }
        // The row's own menu also handles its surrounding item padding. Opening a menu
        // must not activate a Library entry or start a network request.
        menu.Open(content);
        e.Handled = true;
        var tab = await FindContextTabAsync(context);
        if (_windowClosed || generation != _documentContextGeneration || !menu.IsOpen
            || !ReferenceEquals(context, container.DataContext)) return;
        foreach (var action in menu.Items.OfType<MenuItem>())
            if (action.Tag is "tab.close" or "tab.closeRight" or "tab.closeOthers")
                action.IsEnabled = tab is not null && _shell.Tabs.Contains(tab);
    }

    private void EnsureLibraryEntryMenu(ContextMenu menu)
    {
        if (!Equals(menu.Tag, "library-entry") || menu.Items.Count > 0) return;
        if (FileTree.Resources["LibraryEntryMenuTemplate"] is not IDataTemplate template
            || template.Build(menu.DataContext) is not ContextMenu source)
            throw new InvalidOperationException("The Library entry menu template is missing.");
        var items = source.Items.Cast<object>().ToArray();
        source.Items.Clear();
        foreach (var item in items) menu.Items.Add(item);
    }

    private void OnTreeRowCloseClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: Tern.Desktop.ViewModels.TreeRow row }) return;
        if (row.IsWorkspace) OnTreeWorkspaceCloseClick(sender, e);
        else if (row.IsDirectory) OnFolderCloseClick(sender, e);
        else OnTreeCloseClick(sender, e);
    }
}
