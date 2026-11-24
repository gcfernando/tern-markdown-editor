using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Tern.Desktop.Views;

/// <summary>Chooses the destination folder for moving a document or folder inside its workspace.</summary>
public sealed class MoveToWindow : Window
{
    private sealed record Choice(string Path, string Label)
    {
        public override string ToString() => Label;
    }

    public MoveToWindow(string itemName, string workspaceRoot, IReadOnlyList<string> destinations)
    {
        DialogParts.Setup(this, "Move " + itemName, 520, 460, DialogGuidance.Move);
        var list = new ListBox
        {
            ItemsSource = destinations.Select(d => new Choice(d, Label(workspaceRoot, d))).ToList(),
            MinHeight = 200,
            MaxHeight = 260,
        };
        Avalonia.Automation.AutomationProperties.SetName(list, "Destination folder");
        var move = new Button { Content = "Move", IsDefault = true, IsEnabled = false };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        list.SelectionChanged += (_, _) => move.IsEnabled = list.SelectedItem is Choice;
        list.DoubleTapped += (_, e) => { if (IsOnItem(e.Source) && list.SelectedItem is Choice c) Close(c.Path); };
        move.Click += (_, _) => { if (list.SelectedItem is Choice c) Close(c.Path); };
        cancel.Click += (_, _) => Close((string?)null);
        var empty = new TextBlock
        {
            Text = "There is no other folder in this workspace to move it to.",
            IsVisible = destinations.Count == 0,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        var fields = GuideParts.Vertical(
            new TextBlock { Text = "Choose a folder in the workspace. An existing name in the destination is never overwritten.", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "muted" } },
            list, empty);
        var frame = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("*,Auto"), [!Grid.RowSpacingProperty] = GuideParts.Space(16).ToBinding() };
        var actions = GuideParts.Actions(move, cancel);
        frame.Children.Add(new ScrollViewer { Content = fields, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        frame.Children.Add(actions);
        Grid.SetRow(actions, 1);
        Content = frame;
        Opened += (_, _) => list.Focus();
    }

    /// <summary>A double-click on empty list space or the scrollbar must not move the file to the last selected folder.</summary>
    internal static bool IsOnItem(object? source) => source is Visual visual && visual.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null;

    private static string Label(string root, string folder)
    {
        var relative = Path.GetRelativePath(root, folder);
        return relative == "." ? "Workspace root" : relative;
    }
}
