using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Tern.App.Documents;
using Tern.App.Shell;
using Tern.Desktop.Views;
using Tern.Recovery;
using Tern.Workspaces;

namespace Tern.Desktop.Services;

/// <summary>What happens after the window first appears: restore the last session, open files from the command line, offer crash recovery.</summary>
public static class Startup
{
    public static async Task RunAsync(MainWindow window, ShellViewModel shell, IReadOnlyList<string> args)
    {
        var cleanupFailures = await Task.Run(() => PrintPreviewFiles.CleanupOld());
        if (cleanupFailures.Count > 0)
            shell.SetStatus("Some old temporary print PDFs could not be removed: " + string.Join("; ", cleanupFailures), StatusSeverity.Warning);
        var layout = await shell.RestoreSessionAsync(() => StartupMeasurements.Mark(shell.ActiveTab is null ? "session-restored-without-active-document" : "active-document-attached"));
        if (!window.IsVisible) return;
        ApplyLayout(window, layout, shell.Settings.UiScale);
        // First launch on a short or narrow screen: Split would leave two unusably small panes.
        if (layout is null && window is MainWindow { } main && (main.IsShortScreen || main.LibraryAutoCollapsed)
            && shell.ViewMode == Tern.App.Settings.ViewMode.Split)
            shell.ViewMode = Tern.App.Settings.ViewMode.Source;

        await OpenArgumentsAsync(window, shell, args);

        await OfferRecoveryAsync(window, shell);
        _ = MarkdownUrlCache.PruneAsync(MarkdownUrlCache.Root(shell.Paths.StateDirectory));
        if (!window.IsVisible) return;
        if (shell.Tabs.Count == 0)
        {
            shell.NewStartupDocument();
            window.ShowFirstUse();
        }
    }

    public static async Task OpenArgumentsAsync(MainWindow window, ShellViewModel shell, IReadOnlyList<string> args)
    {
        DocumentTab? lastOpened = null;
        var processed = 0;
        foreach (var arg in args.Where(a => !string.IsNullOrWhiteSpace(a) && !a.StartsWith('-')))
        {
            if (!window.IsVisible) return;
            try
            {
                var full = Path.GetFullPath(arg);
                var kind = await Task.Run(() => (Directory: Directory.Exists(full), File: File.Exists(full)));
                if (kind.Directory) await shell.OpenDirectoryAsync(full);
                else if (kind.File)
                {
                    var opened = await shell.OpenFileAsync(full, activate: lastOpened is null);
                    if (opened is not null) lastOpened = opened;
                }
                else shell.StatusMessage = "The requested path is unavailable. Use Open files to choose another note.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { shell.StatusMessage = "Could not open the requested path: " + ex.Message; }
            if (++processed % 32 == 0)
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        if (!window.IsVisible) return;
        if (lastOpened is not null && shell.Tabs.Contains(lastOpened))
            shell.SelectTab(lastOpened, TabActivationReason.User);
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private static void ApplyLayout(Window window, WindowLayoutHint? layout, double uiScale)
    {
        if (layout is null)
        {
            return;
        }

        if (layout.Width is { } width && layout.Height is { } height && width >= window.MinWidth && height >= window.MinHeight)
        {
            (window.Width, window.Height) = WindowSizing.ClampSaved(window, uiScale, width, height);
        }

        if (layout.Left is { } left && layout.Top is { } top && IsOnAnyScreen(window, (int)left, (int)top))
        {
            window.Position = new PixelPoint((int)left, (int)top);
        }

        // The first-launch fit may have maximized the window; a saved layout is authoritative.
        window.WindowState = layout.Maximized ? WindowState.Maximized : WindowState.Normal;
    }

    // A saved position can belong to a monitor that is no longer attached.
    private static bool IsOnAnyScreen(Window window, int x, int y) =>
        window.Screens.All.Any(s => s.Bounds.Contains(new PixelPoint(x + 20, y + 20)));

    public static async Task OfferRecoveryAsync(Window owner, ShellViewModel shell)
    {
        RecoveryScan scan;
        using (var activity = shell.Activities.Begin("Scanning recovery checkpoints"))
        {
            try
            {
                activity.Report("Validating local checkpoint headers and integrity");
                scan = await Task.Run(shell.FindRecoverable);
                activity.Complete(scan.Corrupt.Count > 0 ? $"{scan.Corrupt.Count} unreadable checkpoints require review; readable copies were kept." : null,
                    scan.Corrupt.Count > 0, scan.Corrupt.Count > 0 ? "Recovery scan completed with unreadable checkpoints"
                        : scan.Entries.Count == 0 ? "Recovery scan complete; no abandoned checkpoints" : "Recovery checkpoints ready for review");
            }
            catch (Exception ex) { activity.Fail(ex.Message); throw; }
        }
        var entries = scan.Entries;
        if (scan.Corrupt.Count > 0) await ReportDamagedAsync(owner, shell, scan.Corrupt);
        if (entries.Count == 0)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (!owner.IsVisible) return;
            using var activity = shell.Activities.Begin("Reviewing recovery checkpoint", entry.Record.DisplayName);
            try
            {
                activity.Report("Reading the original backing file for comparison");
                string disk;
                var diskAvailable = false;
                try
                {
                    if (entry.Record.OriginalPath is { } path && File.Exists(path))
                    {
                        disk = (await new Tern.Filesystem.DocumentFileService().LoadAsync(path)).Text;
                        diskAvailable = true;
                    }
                    else disk = "The original backing file is unavailable.";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { disk = "The backing file could not be read: " + ex.Message; }
                activity.WaitForUser("Compare recovered text and choose restore, save, discard or ask later");
                var answer = await ReviewAsync(owner, entry.Record, disk, diskAvailable, shell);
                if (!owner.IsVisible) { activity.Cancel("Recovery review closed; checkpoint retained."); return; }
                if (answer == RecoveryReviewWindow.ReplaceOriginalChoice)
                {
                    activity.Report("Applying the approved recovered text as an undoable buffer edit");
                    var open = entry.Record.OriginalPath is { } originalPath ? await shell.FindOpenTabAsync(originalPath) : null;
                    if (!owner.IsVisible) { activity.Cancel("Recovery review closed; checkpoint retained."); return; }
                    if (open is not null && shell.ReplaceContents(open, entry.Record.Text, "the recovered text"))
                    {
                        shell.DiscardRecovery(entry.Record);
                        activity.Complete(outcome: "Recovered text applied to open buffer; not saved; Undo available");
                    }
                    else activity.Fail("The original is closed or read-only; recovered text was not applied and the checkpoint was kept.");
                }
                else if (answer == RecoveryReviewWindow.RestoreChoice)
                {
                    activity.Report("Restoring a separate unsaved copy");
                    await shell.RestoreRecoveryAsync(entry.Record);
                    activity.Complete(outcome: "Recovery copy opened; original file unchanged; see checkpoint verification result");
                }
                else if (answer == RecoveryReviewWindow.SaveCopyChoice)
                {
                    activity.Report("Restoring a separate unsaved copy before Save As");
                    var restored = await shell.RestoreRecoveryAsync(entry.Record);
                    activity.WaitForUser("Save the recovered copy with Save As; see its independent save outcome");
                    await shell.SaveTabAsAsync(restored);
                    activity.Complete(outcome: "Recovery copy opened; Save As decision processed; see save outcome");
                }
                else if (answer == RecoveryReviewWindow.DiscardChoice)
                {
                    shell.DiscardRecovery(entry.Record);
                    activity.Complete(outcome: "Checkpoint discard confirmed; original document unchanged");
                }
                else activity.Cancel("Recovery deferred; the checkpoint and original file were kept.");
            }
            catch (OperationCanceledException) { activity.Cancel("Recovery workflow stopped; review any open recovered copy and save outcome."); throw; }
            catch (Exception ex) { activity.Fail("Recovery workflow stopped; review any open recovered copy and save outcome. " + ex.Message); throw; }
        }
    }

    /// <summary>Discarding needs a second, deliberate confirmation; declining returns to the review so nothing is lost by accident.</summary>
    internal static async Task<int> ReviewAsync(Window owner, RecoveryRecord record, string diskText, bool compare, ShellViewModel? shell = null)
    {
        var original = record.OriginalPath is { } path && !record.IsExternalDraft ? path : null;
        while (true)
        {
            if (!owner.IsVisible) return RecoveryReviewWindow.AskLaterChoice;
            // Replacing is offered only while the original is open and editable, so the undoable edit lands in the document the user sees.
            var originalOpen = original is not null && shell is not null
                && await shell.FindOpenTabAsync(original) is { IsReadOnly: false };
            if (!owner.IsVisible) return RecoveryReviewWindow.AskLaterChoice;
            var answer = await new RecoveryReviewWindow(record, diskText, compare, originalOpen, hasHistory: shell is not null && original is not null)
                .ShowDialog<int?>(owner) ?? RecoveryReviewWindow.AskLaterChoice;
            if (answer == RecoveryReviewWindow.ShowHistoryChoice)
            {
                if (!owner.IsVisible) return RecoveryReviewWindow.AskLaterChoice;
                await ShowRecordHistoryAsync(owner, shell!, record, original!);
                continue;
            }
            if (answer != RecoveryReviewWindow.DiscardChoice) return answer;
            if (!owner.IsVisible) return RecoveryReviewWindow.AskLaterChoice;
            var confirm = await PromptWindow.AskAsync(owner, "Discard recovered text?",
                "The recovered text of " + record.DisplayName + " will be permanently deleted. This cannot be undone.",
                ["Discard recovered text", "Keep"], 1, 1, destructiveIndex: 0);
            if (confirm == 0) return RecoveryReviewWindow.DiscardChoice;
        }
    }

    /// <summary>Lets the user compare the recovered text with the saved versions of the original file before deciding; a chosen version opens as an unsaved copy.</summary>
    private static async Task ShowRecordHistoryAsync(Window owner, ShellViewModel shell, RecoveryRecord record, string original)
    {
        using var activity = shell.Activities.Begin("Reviewing recovery version history", record.DisplayName);
        try
        {
            activity.Report("Reading and validating local saved versions");
            var read = await shell.HistoryStore.ReadWithDamageAsync(original);
            if (!owner.IsVisible) { activity.Cancel(); return; }
            activity.WaitForUser("Compare saved history with recovered text; original unchanged");
            var choice = await new HistoryWindow(Path.GetFileName(original), read.Versions, record.Text, read.Damaged).ShowDialog<HistoryChoice?>(owner);
            if (choice is null || !owner.IsVisible) { activity.Cancel("History review closed; original and checkpoint kept."); return; }
            activity.Report("Opening the chosen saved version as an unsaved copy");
            var copy = shell.NewDocument();
            copy.ApplyEdits([new Tern.Domain.Text.TextEdit(0, 0, choice.Text)]);
            shell.SetStatus("This older version opened as an unsaved copy. Your original file has not changed.", StatusSeverity.Success);
            activity.Complete(outcome: "History version opened as unsaved copy; original unchanged");
        }
        catch (OperationCanceledException)
        {
            activity.Cancel("History review canceled; original file not overwritten.");
            if (owner.IsVisible) shell.SetStatus("History review canceled; original file not overwritten.", StatusSeverity.Info);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity.Fail(ex.Message);
            shell.SetStatus("Could not read local history: " + ex.Message, StatusSeverity.Warning);
        }
    }

    /// <summary>Unreadable checkpoints are offered for inspection or deletion once; whatever the answer, they never prompt again.</summary>
    internal static async Task ReportDamagedAsync(Window owner, ShellViewModel shell, IReadOnlyList<CorruptRecovery> damaged)
    {
        while (true)
        {
            var choice = await PromptWindow.AskAsync(owner, "Some recovery files could not be read",
                $"{damaged.Count} recovery file(s) failed integrity checks, in {shell.RecoveryStore.DirectoryPath}. No document was changed and readable checkpoints are unaffected.\n\n"
                + "Show files opens the folder. Delete damaged files removes only the unreadable files. Keep moves them to a \"damaged\" subfolder so this message does not return.",
                ["Show files", "Delete damaged files", "Keep"], 2, 2, destructiveIndex: 1);
            if (choice == 0)
            {
                shell.ShowDamagedRecovery(damaged);
                continue;
            }
            if (choice == 1) shell.DeleteDamagedRecovery(damaged);
            else shell.QuarantineDamagedRecovery(damaged);
            return;
        }
    }
}
