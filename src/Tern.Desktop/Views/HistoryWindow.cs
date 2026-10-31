using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Tern.Domain.Text;
using Tern.Recovery;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private async void OnHistoryClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await ShowHistoryAsync();

    private async Task ShowHistoryAsync()
    {
        if (_shown?.FilePath is not { } path)
        {
            _shell.StatusMessage = "Save this document to start its local version history.";
            return;
        }

        try
        {
            var versions = await _shell.HistoryStore.ReadAsync(path);
            var restored = await new HistoryWindow(Path.GetFileName(path), versions, _shown.CurrentText).ShowDialog<string?>(this);
            if (restored is not null)
            {
                var copy = _shell.NewDocument();
                copy.ApplyEdits([new TextEdit(0, 0, restored)]);
                _shell.StatusMessage = "Historical version opened as an unsaved copy. The original file is unchanged.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _shell.StatusMessage = "Could not read local history: " + ex.Message;
        }
    }
}

/// <summary>Compare saved source against the current buffer; restore always opens an unsaved copy.</summary>
public sealed class HistoryWindow : Window
{
    public HistoryWindow(string name, IReadOnlyList<DocumentVersion> versions, string currentText)
    {
        DialogParts.Setup(this, "Version history – " + name, 1080, 700);
        MinWidth = 720;
        MinHeight = 480;
        var list = new ListBox { ItemsSource = versions, MinWidth = 180 };
        var historical = SourcePane("Saved version", string.Empty);
        var current = SourcePane("Current document", currentText);
        var restore = new Button { Content = "Open as unsaved copy", IsEnabled = false };
        AutomationProperties.SetName(list, "Saved document versions");
        AutomationProperties.SetName(restore, "Open selected version as unsaved copy");
        list.SelectionChanged += (_, _) =>
        {
            historical.Text = (list.SelectedItem as DocumentVersion)?.Text;
            restore.IsEnabled = list.SelectedItem is DocumentVersion;
        };
        restore.Click += (_, _) => Close((list.SelectedItem as DocumentVersion)?.Text);
        var cancel = new Button { Content = "Close", IsCancel = true };
        cancel.Click += (_, _) => Close((string?)null);
        var grid = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 16 };
        grid.Children.Add(new TextBlock { Text = versions.Count == 0 ? "No saved versions yet. History starts with your next save." : "Local saved versions · 30 versions / 32 MiB per document · restore opens a separate copy", TextWrapping = TextWrapping.Wrap });
        var panes = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*,*"), ColumnSpacing = 12 };
        panes.Children.Add(list);
        var oldPane = LabeledSource("Saved version", historical);
        Grid.SetColumn(oldPane, 1);
        panes.Children.Add(oldPane);
        var nowPane = LabeledSource("Current document", current);
        Grid.SetColumn(nowPane, 2);
        panes.Children.Add(nowPane);
        Grid.SetRow(panes, 1);
        grid.Children.Add(panes);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(restore);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);
        Content = grid;
        if (versions.Count > 0) list.SelectedIndex = 0;
    }

    private static TextBox SourcePane(string name, string text)
    {
        var box = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"), VerticalContentAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(box, name);
        ScrollViewer.SetHorizontalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        return box;
    }

    private static Grid LabeledSource(string label, Control source)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 8 };
        grid.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold });
        Grid.SetRow(source, 1);
        grid.Children.Add(source);
        return grid;
    }
}
