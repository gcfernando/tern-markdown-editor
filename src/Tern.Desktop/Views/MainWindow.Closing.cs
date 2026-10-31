using Avalonia.Controls;
using Avalonia.Interactivity;
using Tern.Desktop.ViewModels;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private async void OnTreeCloseClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Control)?.DataContext is not TreeRow row) return;
        var tab = _shell.Tabs.FirstOrDefault(t => string.Equals(t.FilePath, row.FullPath, StringComparison.OrdinalIgnoreCase));
        if (tab is not null && await _shell.CloseTabAsync(tab)) FileTree.SelectedItem = null;
    }

    private async void OnFolderCloseClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Control)?.DataContext is TreeRow { IsDirectory: true } row)
            await CloseTreeFolderAsync(row);
    }

    internal async Task<bool> CloseTreeFolderAsync(TreeRow row)
    {
        if (!await _shell.CloseFolderDocumentsAsync(row.FullPath)) return false;
        row.IsExpanded = false;
        FileTree.SelectedItem = null;
        return true;
    }

    private static void OnFolderCollapseClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Control)?.DataContext is TreeRow row) row.IsExpanded = false;
    }
}
