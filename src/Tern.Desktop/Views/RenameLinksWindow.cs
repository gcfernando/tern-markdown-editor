using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Tern.Workspaces;

namespace Tern.Desktop.Views;

public sealed class RenameLinksWindow : Window
{
    public RenameLinksWindow(RepairPlan plan, string? operation = null)
    {
        DialogParts.Setup(this, operation is null ? "Review links after rename" : "Review " + operation, 780, 650, DialogGuidance.RenameLinks);
        MinWidth = 500; MinHeight = 350;
        var changes = new StackPanel { [!StackPanel.SpacingProperty] = GuideParts.Space(16).ToBinding() };
        foreach (var warning in plan.Warnings) changes.Children.Add(new TextBlock { Text = warning, TextWrapping = TextWrapping.Wrap });
        foreach (var file in plan.Files)
        {
            var group = new StackPanel { [!StackPanel.SpacingProperty] = GuideParts.Space(8).ToBinding() };
            group.Children.Add(new TextBlock { Text = file.Path, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            foreach (var preview in file.Previews)
                group.Children.Add(new TextBlock { Text = $"Line {preview.Line}\nBefore: {preview.Before}\nAfter: {preview.After}", TextWrapping = TextWrapping.Wrap });
            changes.Children.Add(group);
        }
        var apply = new Button { Content = operation is null ? "Apply reviewed link updates" : "Apply reviewed changes", IsDefault = true };
        var skip = new Button { Content = operation is null ? "Keep links unchanged" : "Keep notes unchanged", IsCancel = true };
        apply.Click += (_, _) => Close(true); skip.Click += (_, _) => Close(false);
        var layout = new Grid { Margin = new Thickness(28), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), [!Grid.RowSpacingProperty] = GuideParts.Space(16).ToBinding() };
        layout.Children.Add(GuideParts.Title(operation is null ? "Keep your links connected" : "Review " + operation));
        layout.Children.Add(new TextBlock { Text = operation is null
            ? $"{plan.Files.Count} affected files, {plan.Files.Sum(f => f.Previews.Count)} link changes. The file has been renamed. Review these exact link changes before applying. Open documents receive undoable edits and need Save; closed files are saved only if their reviewed contents still match. Skipping keeps the rename and leaves links unchanged."
            : $"{plan.Files.Count} affected files, {plan.TotalEdits} edits. Review the exact changes below. Open documents receive undoable edits and need Save. Closed files are safely saved with History only if their reviewed contents still match. Cancel keeps all notes unchanged.", TextWrapping = TextWrapping.Wrap });
        layout.Children.Add(new ScrollViewer { Content = changes });
        layout.Children.Add(GuideParts.Actions(apply, skip));
        for (var i = 0; i < layout.Children.Count; i++) Grid.SetRow(layout.Children[i], i);
        Content = layout;
    }
}
