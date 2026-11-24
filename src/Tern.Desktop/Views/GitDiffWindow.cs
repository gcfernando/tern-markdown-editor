using Avalonia.Controls;

namespace Tern.Desktop.Views;

public sealed class GitDiffWindow : Window
{
    public GitDiffWindow(string name, string committed, string current)
    {
        DialogParts.Setup(this, "Changes since last commit", 1000, 620, DialogGuidance.History, guidance: false);
        var diff = new DiffView("Last commit (HEAD)", "Current Source snapshot");
        diff.Show(committed, current);
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();
        Content = GuideParts.Frame(GuideParts.Vertical(GuideParts.Title("Changes since last commit"), GuideParts.Body(name),
            GuideParts.Caption("Read-only comparison. The index, working files and commits are not changed.")),
            diff, GuideParts.Actions(close));
    }
}
