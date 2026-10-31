using Avalonia.Controls;
using Avalonia.Interactivity;
using Tern.App.Documents;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    internal void ToggleReferencePane()
    {
        var show = !ReferencePane.IsVisible;
        ReferencePane.IsVisible = ReferenceSplitter.IsVisible = show;
        DocumentPanes.ColumnDefinitions[1].Width = new GridLength(show ? 4 : 0);
        DocumentPanes.ColumnDefinitions[2].Width = show ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        if (show && ReferenceTabs.SelectedItem is null)
            ReferenceTabs.SelectedItem = _shell.ActiveTab;
        if (show) ReferenceEditor.Focus(); else Editor.Focus();
    }

    private void OnCloseReferenceClick(object? sender, RoutedEventArgs e) => ToggleReferencePane();

    private void OnReferenceTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        ReferenceEditor.Document = ReferenceTabs.SelectedItem is DocumentTab tab
            ? ContextFor(tab).Buffer.Document
            : new AvaloniaEdit.Document.TextDocument();
    }
}
