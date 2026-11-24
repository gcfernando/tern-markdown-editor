using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Tern.Desktop.Themes;
using Tern.Domain.Merging;
using Tern.Recovery;

namespace Tern.Desktop.Views;

/// <summary>Shows today's disk text alongside the recovered checkpoint; restoration always creates a separate copy.</summary>
internal sealed class RecoveryReviewWindow : Window
{
    public const int RestoreChoice = 0;
    public const int SaveCopyChoice = 1;
    public const int DiscardChoice = 2;
    public const int AskLaterChoice = 3;
    public const int ReplaceOriginalChoice = 4;
    public const int ShowHistoryChoice = 5;

    public RecoveryReviewWindow(RecoveryRecord record, string diskText, bool compare = true, bool originalOpen = false, bool hasHistory = false)
    {
        DialogParts.Setup(this, "Review recovered work — " + record.DisplayName, 1000, 680, DialogGuidance.Recovery);
        MinWidth = 700;
        MinHeight = 480;

        var header = GuideParts.Body(
            $"Checkpoint: {record.SavedAtUtc.ToLocalTime():g}\n{record.OriginalPath ?? "Untitled document"}\nRestoring opens a separate unsaved copy. Today's disk file stays unchanged.");
        var diff = new DiffView("Current disk file", "Recovered local edits");
        diff.Show(diskText, record.Text, compare ? null : diskText);

        var restore = Choice("Restore as unsaved copy", RestoreChoice);
        restore.Classes.Add("accent");
        restore.IsDefault = true;
        var saveCopy = Choice("Save recovered copy…", SaveCopyChoice);
        var discard = Choice("Discard checkpoint", DiscardChoice);
        discard.Classes.Add("danger");
        var askLater = Choice("Ask later", AskLaterChoice);
        askLater.IsCancel = true;
        var actions = new List<Button> { discard };
        if (hasHistory) actions.Add(Choice("Show history…", ShowHistoryChoice));
        if (originalOpen) actions.Add(Choice("Replace open original…", ReplaceOriginalChoice));
        actions.Add(saveCopy);

        // The destructive choice sits apart on the left; the safe primary and cancel pair stay on the right.
        Content = GuideParts.Frame(header, diff, GuideParts.Actions(restore, askLater, [.. actions]));
        Opened += (_, _) => restore.Focus();
    }

    private Button Choice(string label, int result)
    {
        var button = new Button { Content = label };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => Close(result);
        return button;
    }
}
