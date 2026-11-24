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

    private async Task ShowHistoryAsync()
    {
        var tab = _shown;
        // A recovered draft has no file of its own; its history is the original file's, when that file is known.
        var path = tab?.FilePath ?? (tab is { IsRecovered: true, IsExternalDraft: false } ? tab.RecoveredFromPath : null);
        if (tab is null || path is null)
        {
            _shell.StatusMessage = "Save this document to start its local version history.";
            return;
        }

        using var activity = _shell.Activities.Begin("Reviewing version history", tab.DisplayName);
        try
        {
            activity.Report("Reading and validating local saved versions");
            var read = await _shell.HistoryStore.ReadWithDamageAsync(path);
            var versions = read.Versions;
            if (_windowClosed || !ReferenceEquals(tab, _shown))
            { activity.Cancel("History review stopped; no document changed."); return; }
            var target = tab.FilePath is not null ? tab : await _shell.FindOpenTabAsync(path);
            if (_windowClosed || !ReferenceEquals(tab, _shown))
            { activity.Cancel("History review stopped; no document changed."); return; }
            activity.WaitForUser("Compare saved versions and choose whether to restore; original unchanged");
            var choice = await new HistoryWindow(Path.GetFileName(path), versions, tab.CurrentText, read.Damaged,
                target is { IsReadOnly: false } ? (ReferenceEquals(target, tab) ? "Replace current document" : "Replace open original") : null,
                _shell.HistoryStore.ByteBudgetPerFile).ShowDialog<HistoryChoice?>(this);
            if (choice is null)
            { activity.Cancel("History review closed; the original file and current draft were kept."); return; }
            if (_windowClosed) { activity.Cancel("Window closed; no history version was applied."); return; }
            activity.Report("Applying the reviewed version to an unsaved buffer");
            if (choice.ReplaceOpenDocument && target is not null)
            {
                if (!_shell.ReplaceContents(target, choice.Text, "the saved version"))
                { activity.Fail("The target is closed or read-only; the history version was not applied."); return; }
            }
            else
            {
                var copy = _shell.NewDocument();
                copy.ApplyEdits([new TextEdit(0, 0, choice.Text)]);
                _shell.SetStatus("This older version opened as an unsaved copy. Your original file has not changed.", Tern.App.Shell.StatusSeverity.Success);
            }
            activity.Complete(outcome: choice.ReplaceOpenDocument && target is not null
                ? "Version applied to buffer; not saved; Undo available" : "Version opened as unsaved copy; original unchanged");
        }
        catch (OperationCanceledException)
        {
            activity.Cancel("History review canceled; the original file was not overwritten.");
            if (!_windowClosed) _shell.SetStatus("History review canceled; the original file was not overwritten.", Tern.App.Shell.StatusSeverity.Info);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity.Fail(ex.Message);
            _shell.SetStatus("Could not read local history: " + _shell.PlainProblem(ex), Tern.App.Shell.StatusSeverity.Warning);
        }
    }
}

/// <summary>What the user chose in the history window: open the version as a copy, or replace the contents of an open document (undoable).</summary>
public sealed record HistoryChoice(string Text, bool ReplaceOpenDocument);

/// <summary>Compare saved source against the current buffer; restore opens an unsaved copy or, on request, replaces the open document undoably.</summary>
public sealed class HistoryWindow : Window
{
    public HistoryWindow(string name, IReadOnlyList<DocumentVersion> versions, string currentText, IReadOnlyList<DamagedVersion>? damaged = null, string? replaceLabel = null,
        long byteBudget = DocumentHistoryStore.DefaultByteBudget)
    {
        damaged ??= [];
        var empty = versions.Count == 0 && damaged.Count == 0;
        DialogParts.Setup(this, "Version history — " + name, empty ? 560 : 1020, empty ? 350 : 620, DialogGuidance.History);
        MinWidth = empty ? 360 : 700;
        MinHeight = empty ? 280 : 450;
        var cancel = new Button { Content = "Close", IsCancel = true };
        cancel.Click += (_, _) => Close((HistoryChoice?)null);
        var title = GuideParts.Vertical(GuideParts.Title("Version history"), GuideParts.Caption(name));
        title.Children.Add(GuideParts.Caption(
            $"History budget: {byteBudget / (1024 * 1024)} MiB per document. Up to 30 versions from the last hour, then hourly, daily and weekly versions within this budget. Large documents keep fewer versions; history is unavailable when one stored version exceeds the budget. Increase Version history size in Settings. Recovery checkpoints are separate."));
        if (empty)
        {
            var guidance = GuideParts.Card(GuideParts.Vertical(
                GuideParts.Body("No earlier versions yet"),
                GuideParts.Body("Tern keeps the version already on disk the first time you save over it, and a version after every save. Save this file in Tern, then return here."),
                GuideParts.Caption("Edit the file → Save in Tern → Open History")));
            Content = GuideParts.Frame(title, new ScrollViewer { Content = guidance }, GuideParts.Actions(cancel));
            return;
        }

        var items = versions.Cast<object>().Concat(damaged).OrderByDescending(item => item is DocumentVersion v ? v.SavedAtUtc : ((DamagedVersion)item).SavedAtUtc ?? DateTime.MinValue).ToList();
        var list = new ListBox
        {
            ItemsSource = items,
            ItemTemplate = new FuncDataTemplate<object>((item, _) =>
            {
                if (item is DamagedVersion bad)
                {
                    var when = bad.SavedAtUtc?.ToLocalTime();
                    var line = GuideParts.Body((when?.ToString("d") ?? "Unknown date") + " (unreadable)");
                    line.Opacity = 0.6;
                    return GuideParts.Vertical(line, GuideParts.Caption(bad.Reason));
                }
                var version = (DocumentVersion)item!;
                var saved = version.SavedAtUtc.ToLocalTime();
                var date = GuideParts.Body(saved.ToString("d"));
                date.FontWeight = FontWeight.SemiBold;
                return version.Label is { Length: > 0 } label
                    ? GuideParts.Vertical(date, GuideParts.Caption(saved.ToString("t")), GuideParts.Caption(label))
                    : GuideParts.Vertical(date, GuideParts.Caption(saved.ToString("t")));
            })
        };
        var diff = new DiffView("Saved version", "Current draft");
        var restore = new Button { Content = "Open as copy", IsEnabled = false, Classes = { "accent" } };
        var replace = new Button { Content = replaceLabel ?? "Replace current document", IsEnabled = false, IsVisible = replaceLabel is not null };
        AutomationProperties.SetName(list, "Saved document versions");
        AutomationProperties.SetName(restore, "Open selected version as unsaved copy");
        AutomationProperties.SetName(replace, "Replace the open document with the selected version; Undo brings the text back");
        list.SelectionChanged += (_, _) =>
        {
            diff.Show(list.SelectedItem is DocumentVersion v ? v.Text : null, currentText,
                list.SelectedItem is DamagedVersion ? "This saved version is damaged and cannot be shown or restored. Your current file is unaffected." : null);
            restore.IsEnabled = replace.IsEnabled = list.SelectedItem is DocumentVersion;
            diff.LeftLabel = list.SelectedItem is DocumentVersion selected
                ? $"Saved version · {selected.SavedAtUtc.ToLocalTime():g}" : "Saved version";
        };
        restore.Click += (_, _) =>
        {
            if (list.SelectedItem is DocumentVersion selected) Close(new HistoryChoice(selected.Text, false));
        };
        replace.Click += (_, _) =>
        {
            if (list.SelectedItem is DocumentVersion selected) Close(new HistoryChoice(selected.Text, true));
        };
        title.Children.Add(GuideParts.Body("Choose a saved version to compare it with your current draft. Open as copy keeps everything as it is in a separate unsaved tab."
            + (replaceLabel is null ? string.Empty : $" {replaceLabel} puts the version into the open document as one edit you can Undo.")));
        if (damaged.Count > 0)
            title.Children.Add(GuideParts.Caption($"{damaged.Count} damaged version{(damaged.Count == 1 ? " was" : "s were")} set aside and cannot be restored."));
        var panes = new Grid { ColumnDefinitions = new ColumnDefinitions("155,*"), [!Grid.ColumnSpacingProperty] = GuideParts.Space(12).ToBinding() };
        panes.Children.Add(LabeledSource(new TextBlock { Text = "Saved versions", FontWeight = FontWeight.SemiBold }, list));
        Grid.SetColumn(diff, 1);
        panes.Children.Add(diff);
        Content = GuideParts.Frame(title, panes, GuideParts.Vertical(
            GuideParts.Caption(replaceLabel is null ? "Opening a copy leaves your current draft and saved file untouched."
                : "Opening a copy leaves your draft and file untouched. Replacing changes only the open document until you save, and Undo reverses it."),
            GuideParts.Actions(restore, cancel, replace)));
        list.SelectedIndex = 0;
    }

    private static Grid LabeledSource(TextBlock label, Control source)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), [!Grid.RowSpacingProperty] = GuideParts.Space(8).ToBinding() };
        grid.Children.Add(label);
        Grid.SetRow(source, 1);
        grid.Children.Add(source);
        return grid;
    }
}
