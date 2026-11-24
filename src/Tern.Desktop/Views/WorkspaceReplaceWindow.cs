using Avalonia.Automation;
using Avalonia.Controls;
using Tern.Search;

namespace Tern.Desktop.Views;

public sealed class WorkspaceReplaceWindow : Window
{
    public WorkspaceReplaceWindow(string workspace)
    {
        DialogParts.Setup(this, "Replace in workspace", 580, 420, DialogGuidance.RenameLinks, guidance: false);
        var find = new TextBox { PlaceholderText = "Text to find" };
        var replacement = new TextBox { PlaceholderText = "Replacement (empty removes matches)" };
        var matchCase = new CheckBox { Content = "Match case" };
        AutomationProperties.SetName(find, "Workspace text to find");
        AutomationProperties.SetName(replacement, "Workspace replacement text");
        var preview = new Button { Content = "Preview replacements", IsDefault = true, IsEnabled = false, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        find.TextChanged += (_, _) => preview.IsEnabled = !string.IsNullOrEmpty(find.Text);
        preview.Click += (_, _) => { if (preview.IsEnabled) Close(new FindOptions(find.Text!, replacement.Text ?? "", MatchCase: matchCase.IsChecked == true)); };
        cancel.Click += (_, _) => Close((FindOptions?)null);
        Content = GuideParts.Frame(GuideParts.Title("Replace in workspace"),
            GuideParts.Vertical(GuideParts.Body(workspace),
                GuideParts.Caption("Preview literal text changes before anything is edited. Unsaved open buffers are included. Empty replacement removes the matched text."),
                GuideParts.Caption("Find"), find, GuideParts.Caption("Replace with"), replacement, matchCase),
            GuideParts.Actions(preview, cancel));
        Opened += (_, _) => find.Focus();
    }
}
