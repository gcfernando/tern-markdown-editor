using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Tern.Desktop.Views;

public enum AddDestinationChoice { Selected, Outside, Cancel }

/// <summary>A compact destination decision; it neither creates items nor opens a system picker.</summary>
public sealed class AddDestinationWindow : Window
{
    public AddDestinationWindow(string selectedName, string selectedPath, bool isWorkspace, string actionLabel = "Add")
    {
        DialogParts.Setup(this, "Choose where to add", 500, 480);
        var selected = DestinationButton(
            (isWorkspace ? "Workspace · " : "Folder · ") + selectedName,
            selectedPath, "Use selected location");
        ToolTip.SetTip(selected, selectedPath);
        selected.IsDefault = true;
        selected.Click += (_, _) => Close(AddDestinationChoice.Selected);
        var outside = DestinationButton("Outside all workspaces", "Choose a separate location", "Outside all workspaces");
        outside.Click += (_, _) => Close(AddDestinationChoice.Outside);
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        AutomationProperties.SetName(cancel, "Cancel destination choice");
        cancel.Click += (_, _) => Close(AddDestinationChoice.Cancel);
        var choices = new StackPanel
        {
            Spacing = 10,
            Children = { selected, outside },
        };
        var scroll = new ScrollViewer
        {
            Content = choices,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var actions = GuideParts.Actions(cancel);
        var heading = new TextBlock { Text = actionLabel, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        var content = new Grid
        {
            Margin = new Thickness(20), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 14,
            Children = { heading, scroll, actions },
        };
        Grid.SetRow(scroll, 1);
        Grid.SetRow(actions, 2);
        Content = content;
        // The scrollable choices may shrink on a small screen; Cancel remains
        // outside that viewport and every destination can be brought into view.
        content.SizeChanged += (_, _) => MinHeight = Math.Min(MaxHeight,
            heading.DesiredSize.Height + actions.DesiredSize.Height + 68
            + Math.Min(240, Math.Max(96, Math.Max(selected.DesiredSize.Height, outside.DesiredSize.Height))));
        Opened += (_, _) => selected.Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Close(AddDestinationChoice.Cancel);
            e.Handled = true;
        };
    }

    private static Button DestinationButton(string title, string detail, string accessibleName)
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(14, 10), MinHeight = 62,
            Content = new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = detail, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap },
                },
            },
        };
        AutomationProperties.SetName(button, accessibleName);
        return button;
    }

    public static async Task<AddDestinationChoice> AskAsync(Window owner, string selectedName, string selectedPath,
        bool isWorkspace, string actionLabel = "Add") =>
        await new AddDestinationWindow(selectedName, selectedPath, isWorkspace, actionLabel)
            .ShowDialog<AddDestinationChoice?>(owner) ?? AddDestinationChoice.Cancel;
}
