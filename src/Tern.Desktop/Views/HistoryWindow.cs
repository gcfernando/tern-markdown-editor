using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
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
        var tab = _shown;
        if (tab?.FilePath is not { } path)
        {
            _shell.StatusMessage = "Save this document to start its local version history.";
            return;
        }

        try
        {
            var versions = await _shell.HistoryStore.ReadAsync(path);
            if (_windowClosed || !ReferenceEquals(tab, _shown) || tab.FilePath != path) return;
            var restored = await new HistoryWindow(Path.GetFileName(path), versions, tab.CurrentText).ShowDialog<string?>(this);
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
        var empty = versions.Count == 0;
        DialogParts.Setup(this, "Version history – " + name, empty ? 560 : 1020, empty ? 350 : 620);
        MinWidth = empty ? 360 : 700;
        MinHeight = empty ? 280 : 450;
        var cancel = new Button { Content = "Close", IsCancel = true };
        cancel.Click += (_, _) => Close((string?)null);
        var title = GuideParts.Vertical(GuideParts.Title("Version history"), GuideParts.Caption(name));
        if (empty)
        {
            var guidance = GuideParts.Card(GuideParts.Vertical(
                GuideParts.Body("No saved versions yet"),
                GuideParts.Body("Edit and save this file in Tern, then return here to review the versions Tern records."),
                GuideParts.Caption("Edit the file → Save in Tern → Open History")));
            Content = GuideParts.Frame(title, new ScrollViewer { Content = guidance }, GuideParts.Actions(cancel));
            return;
        }

        var list = new ListBox
        {
            ItemsSource = versions,
            ItemTemplate = new FuncDataTemplate<DocumentVersion>((version, _) =>
            {
                var saved = version!.SavedAtUtc.ToLocalTime();
                var date = GuideParts.Body(saved.ToString("d"));
                date.FontWeight = FontWeight.SemiBold;
                return GuideParts.Vertical(date, GuideParts.Caption(saved.ToString("t")));
            })
        };
        var historical = SourcePane("Saved version", string.Empty);
        var current = SourcePane("Current document", currentText);
        var savedLabel = new TextBlock { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        var restore = new Button { Content = "Open as copy", IsEnabled = false, Classes = { "accent" } };
        AutomationProperties.SetName(list, "Saved document versions");
        AutomationProperties.SetName(restore, "Open selected version as unsaved copy");
        list.SelectionChanged += (_, _) =>
        {
            historical.Text = (list.SelectedItem as DocumentVersion)?.Text;
            restore.IsEnabled = list.SelectedItem is DocumentVersion;
            savedLabel.Text = list.SelectedItem is DocumentVersion selected
                ? $"Saved version · {selected.SavedAtUtc.ToLocalTime():g}" : "Saved version";
        };
        restore.Click += (_, _) =>
        {
            if (list.SelectedItem is DocumentVersion selected) Close(selected.Text);
        };
        title.Children.Add(GuideParts.Body("Choose a saved version to compare it with your current draft. Use Open as copy to open it in a separate unsaved tab."));
        var panes = new Grid { ColumnDefinitions = new ColumnDefinitions("155,*,*"), ColumnSpacing = 12 };
        panes.Children.Add(LabeledSource(new TextBlock { Text = "Saved versions", FontWeight = FontWeight.SemiBold }, list));
        var oldPane = LabeledSource(savedLabel, historical);
        Grid.SetColumn(oldPane, 1);
        panes.Children.Add(oldPane);
        var nowPane = LabeledSource(new TextBlock { Text = "Current draft", FontWeight = FontWeight.SemiBold }, current);
        Grid.SetColumn(nowPane, 2);
        panes.Children.Add(nowPane);
        Content = GuideParts.Frame(title, panes, GuideParts.Vertical(
            GuideParts.Caption("Opening a copy leaves your current draft and saved file untouched."),
            GuideParts.Actions(cancel, restore)));
        list.SelectedIndex = 0;
    }

    private static TextBox SourcePane(string name, string text)
    {
        var box = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"), VerticalContentAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(box, name);
        ScrollViewer.SetVerticalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        return box;
    }

    private static Grid LabeledSource(TextBlock label, Control source)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 8 };
        grid.Children.Add(label);
        Grid.SetRow(source, 1);
        grid.Children.Add(source);
        return grid;
    }
}
