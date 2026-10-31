using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Tern.Workspaces;

namespace Tern.Desktop.Views;

public sealed class RenameLinksWindow : Window
{
    public RenameLinksWindow(RepairPlan plan)
    {
        DialogParts.Setup(this, "Review links after rename", 780, 650);
        MinWidth = 500; MinHeight = 350;
        var changes = new StackPanel { Spacing = 18 };
        foreach (var warning in plan.Warnings) changes.Children.Add(new TextBlock { Text = warning, TextWrapping = TextWrapping.Wrap });
        foreach (var file in plan.Files)
        {
            var group = new StackPanel { Spacing = 8 };
            group.Children.Add(new TextBlock { Text = file.Path, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            foreach (var preview in file.Previews)
                group.Children.Add(new TextBlock { Text = $"Line {preview.Line}\nBefore: {preview.Before}\nAfter: {preview.After}", TextWrapping = TextWrapping.Wrap });
            changes.Children.Add(group);
        }
        var apply = new Button { Content = "Apply reviewed link updates", IsDefault = true };
        var skip = new Button { Content = "Keep links unchanged", IsCancel = true };
        apply.Click += (_, _) => Close(true); skip.Click += (_, _) => Close(false);
        var layout = new Grid { Margin = new Thickness(28), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), RowSpacing = 18 };
        layout.Children.Add(new TextBlock { Text = "Keep your links connected", FontSize = GuideParts.Scaled(22), FontWeight = FontWeight.Bold });
        layout.Children.Add(new TextBlock { Text = "The file has been renamed. Review these exact link changes before applying. Open documents receive undoable edits and need Save; closed files are saved only if their reviewed contents still match. Skipping keeps the rename and leaves links unchanged.", TextWrapping = TextWrapping.Wrap });
        layout.Children.Add(new ScrollViewer { Content = changes });
        layout.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { skip, apply } });
        for (var i = 0; i < layout.Children.Count; i++) Grid.SetRow(layout.Children[i], i);
        Content = layout;
    }
}
