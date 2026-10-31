using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Tern.Recovery;

namespace Tern.Desktop.Views;

/// <summary>Shows today's disk text alongside the recovered checkpoint; restoration always creates a separate copy.</summary>
internal sealed class RecoveryReviewWindow : Window
{
    public RecoveryReviewWindow(RecoveryRecord record, string diskText)
    {
        DialogParts.Setup(this, "Review recovered work — " + record.DisplayName, 1000, 680);
        MinWidth = 700;
        MinHeight = 480;
        var grid = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 12 };
        grid.Children.Add(new TextBlock
        {
            Text = $"Checkpoint: {record.SavedAtUtc.ToLocalTime():g}\n{record.OriginalPath ?? "Untitled document"}\nRestoring opens a separate unsaved copy. Today's disk file stays unchanged.",
            TextWrapping = TextWrapping.Wrap,
        });
        var panes = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };
        panes.Children.Add(Pane("Current disk file", diskText));
        var recovered = Pane("Recovered local edits", record.Text);
        Grid.SetColumn(recovered, 1);
        panes.Children.Add(recovered);
        Grid.SetRow(panes, 1);
        grid.Children.Add(panes);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var labels = new[] { "Restore as unsaved copy", "Save recovered copy…", "Discard checkpoint", "Ask later" };
        for (var i = 0; i < labels.Length; i++)
        {
            var choice = i;
            var button = new Button { Content = labels[i], Margin = new Thickness(4), IsCancel = i == 3 };
            button.Click += (_, _) => Close(choice);
            buttons.Children.Add(button);
        }
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);
        Content = grid;
    }

    private static Grid Pane(string label, string text)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 8 };
        grid.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold });
        var source = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Cascadia Code, Consolas, monospace") };
        AutomationProperties.SetName(source, label);
        Grid.SetRow(source, 1);
        grid.Children.Add(source);
        return grid;
    }
}
