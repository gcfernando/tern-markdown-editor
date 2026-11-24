using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.Services;
using Tern.Desktop.Views;
using Tern.Domain.Text;
using Tern.Recovery;

namespace Tern.Desktop.Tests;

/// <summary>U-02 (recovery review), B-11 (damaged recovery files) and the SB-02 destructive-button contract.</summary>
public sealed class RecoveryReviewTests
{
    private static RecoveryRecord Record(string text, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), @"C:\docs\a.md", "a.md", 1, DateTime.UtcNow, null, TextFileFormat.NewFileDefault, text);

    private static Button Named(Window window, string name) =>
        Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b) == name);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void History_explains_large_document_retention_even_without_saved_versions(bool hasVersions)
    {
        var window = new HistoryWindow("large.md",
            hasVersions ? [new DocumentVersion(DateTime.UtcNow, "old", "test")] : [],
            "current", byteBudget: 64 * 1024 * 1024);
        try
        {
            window.Show();
            window.UpdateLayout();
            var notice = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(),
                t => t.Text?.StartsWith("History budget:", StringComparison.Ordinal) == true);
            Assert.Contains("64 MiB", notice.Text);
            Assert.Contains("history is unavailable", notice.Text);
            Assert.Contains("Recovery checkpoints are separate", notice.Text);
        }
        finally { window.Close(); }
    }

    private static async Task<T> Appears<T>(Window owner, Window? previous = null) where T : Window
    {
        for (var i = 0; i < 300; i++)
        {
            Dispatcher.UIThread.RunJobs();
            var found = owner.OwnedWindows.OfType<T>().FirstOrDefault(w => w.IsVisible && !ReferenceEquals(w, previous));
            if (found is not null) { found.UpdateLayout(); return found; }
            await Task.Delay(10);
        }
        throw new TimeoutException(typeof(T).Name + " did not appear.");
    }

    [AvaloniaFact]
    public void Prompt_destructive_button_is_danger_styled_and_never_the_default()
    {
        var prompt = new PromptWindow("Delete", "Sure?", ["Delete", "Keep"], defaultIndex: 0, cancelIndex: 1, destructiveIndex: 0);
        try
        {
            prompt.Show();
            var delete = Named(prompt, "Delete");
            var keep = Named(prompt, "Keep");
            Assert.Contains("danger", delete.Classes);
            Assert.False(delete.IsDefault);
            Assert.DoesNotContain("accent", delete.Classes);
            Assert.DoesNotContain("danger", keep.Classes);
            Assert.True(keep.IsCancel);
        }
        finally { prompt.Close(); }
    }

    [AvaloniaFact]
    public void Prompt_default_is_the_safe_button_when_another_button_is_destructive()
    {
        var prompt = new PromptWindow("Delete", "Sure?", ["Delete", "Keep"], defaultIndex: 1, cancelIndex: 1, destructiveIndex: 0);
        try
        {
            prompt.Show();
            Assert.True(Named(prompt, "Keep").IsDefault);
            Assert.Contains("accent", Named(prompt, "Keep").Classes);
            Assert.DoesNotContain(prompt.GetVisualDescendants().OfType<Button>(), b => b.IsDefault && b.Classes.Contains("danger"));
        }
        finally { prompt.Close(); }
    }

    [AvaloniaFact]
    public void Recovery_review_defaults_to_restore_and_separates_a_danger_styled_discard_on_the_left()
    {
        var window = new RecoveryReviewWindow(Record("recovered\n"), "disk\n");
        try
        {
            window.Show();
            window.UpdateLayout();
            var restore = Named(window, "Restore as unsaved copy");
            var discard = Named(window, "Discard checkpoint");
            var later = Named(window, "Ask later");
            Assert.True(restore.IsDefault);
            Assert.Contains("accent", restore.Classes);
            Assert.False(discard.IsDefault);
            Assert.Contains("danger", discard.Classes);
            Assert.True(later.IsCancel);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.IsDefault && b != restore);
            double X(Control c) => c.TranslatePoint(default, window)!.Value.X;
            Assert.True(X(discard) < X(Named(window, "Save recovered copy…")) && X(discard) < X(restore) && X(restore) < X(later));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Save_fragments_window_compares_a_leftover_backup_with_its_note_and_recycles_it_on_confirmation()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tern-frag-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var note = Path.Combine(dir, "x.md");
        var backup = Path.Combine(dir, $".x.md.{Guid.NewGuid():N}.tern-save-backup");
        await File.WriteAllTextAsync(note, "same\nnote line\n");
        await File.WriteAllTextAsync(backup, "same\nbackup line\n");
        var fragment = new Tern.Workspaces.SaveFragment(backup, Tern.Workspaces.SaveFragmentKind.SaveBackup, note, DateTime.UtcNow.AddDays(-2), 20);
        var recycled = new List<string>();
        var owner = new Window { Width = 800, Height = 600 };
        owner.Show();
        var window = new SaveFragmentsWindow([fragment], f => { recycled.Add(f.Path); return Task.FromResult(true); });
        try
        {
            window.Show(owner);
            window.UpdateLayout();
            var list = Assert.Single(window.GetVisualDescendants().OfType<ListBox>());
            list.SelectedIndex = 0;
            var diff = Assert.Single(window.GetVisualDescendants().OfType<DiffView>());
            for (var i = 0; i < 100 && diff.RightSource.Length == 0; i++) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
            Named(window, "Compare the selected leftover file with its note").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 100 && diff.LeftSource != "same\nnote line\n"; i++) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
            Assert.Equal(1, diff.ChangeCount);
            Assert.Equal("same\nnote line\n", diff.LeftSource);
            Named(window, "Open the selected leftover file as an unsaved copy");
            Named(window, "Move the selected leftover file to the Recycle Bin");
        }
        finally { window.Close(); owner.Close(); Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public void Recovery_review_counts_changes_navigates_them_and_offers_history_and_replace_only_when_possible()
    {
        var plain = new RecoveryReviewWindow(Record("a\nB\nc\nd\nE\n"), "a\nb\nc\nd\ne\n");
        var rich = new RecoveryReviewWindow(Record("a\nB\n"), "a\nb\n", true, originalOpen: true, hasHistory: true);
        try
        {
            plain.Show();
            plain.UpdateLayout();
            var diff = Assert.Single(plain.GetVisualDescendants().OfType<DiffView>());
            Assert.Equal(2, diff.ChangeCount);
            Assert.Contains("2 changes", diff.SummaryText);
            diff.Step(1);
            Assert.StartsWith("Change 1 of 2", diff.SummaryText);
            diff.Step(1);
            Assert.StartsWith("Change 2 of 2", diff.SummaryText);
            Assert.DoesNotContain(plain.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b) is "Show history…" or "Replace open original…");
            rich.Show();
            rich.UpdateLayout();
            Named(rich, "Show history…");
            Named(rich, "Replace open original…");
        }
        finally { plain.Close(); rich.Close(); }
    }

    [AvaloniaFact]
    public void Recovery_review_highlights_only_lines_that_differ()
    {
        var window = new RecoveryReviewWindow(Record("same\nnew line\nend\n"), "same\nold line\nend\n");
        try
        {
            window.Show();
            window.UpdateLayout();
            var panes = window.GetVisualDescendants().OfType<SelectableTextBlock>().ToArray();
            Assert.Equal(2, panes.Length);
            foreach (var pane in panes)
            {
                var runs = pane.Inlines!.OfType<Run>().ToArray();
                Assert.Equal(3, runs.Length);
                Assert.Null(runs[0].Background);
                Assert.NotNull(runs[1].Background);
                Assert.Null(runs[2].Background);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Discarding_needs_two_deliberate_actions_and_keep_returns_to_the_review()
    {
        var owner = new Window { Width = 800, Height = 600 };
        owner.Show();
        var record = Record("recovered text");
        var pending = Startup.ReviewAsync(owner, record, "disk", true);
        try
        {
            var review = await Appears<RecoveryReviewWindow>(owner);
            Click(Named(review, "Discard checkpoint"));
            var confirm = await Appears<PromptWindow>(owner);
            Assert.False(pending.IsCompleted);
            var discard = Named(confirm, "Discard recovered text");
            Assert.Contains("danger", discard.Classes);
            Assert.False(discard.IsDefault);
            Assert.True(Named(confirm, "Keep").IsDefault);
            Click(Named(confirm, "Keep"));

            var again = await Appears<RecoveryReviewWindow>(owner, review);
            Assert.False(pending.IsCompleted);
            Click(Named(again, "Discard checkpoint"));
            var confirmAgain = await Appears<PromptWindow>(owner, confirm);
            Click(Named(confirmAgain, "Discard recovered text"));
            Assert.Equal(RecoveryReviewWindow.DiscardChoice, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { foreach (var w in owner.OwnedWindows.ToArray()) w.Close(); owner.Close(); }
    }

    [AvaloniaFact]
    public async Task Ask_later_and_dismissal_keep_the_checkpoint()
    {
        var owner = new Window { Width = 800, Height = 600 };
        owner.Show();
        var pending = Startup.ReviewAsync(owner, Record("recovered"), "disk", true);
        try
        {
            var review = await Appears<RecoveryReviewWindow>(owner);
            review.Close();
            Assert.Equal(RecoveryReviewWindow.AskLaterChoice, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { owner.Close(); }
    }

    private static async Task<(string Valid, string Bad)> SeedAsync(DesktopFixture fixture)
    {
        var store = fixture.Shell.RecoveryStore;
        var goodId = Guid.NewGuid();
        await store.WriteCheckpointAsync(Record("valid checkpoint", goodId));
        var bad = Path.Combine(store.DirectoryPath, $"{Guid.NewGuid():N}.00000001.tern-recovery");
        await File.WriteAllBytesAsync(bad, "not a checkpoint"u8.ToArray());
        return (goodId.ToString("N"), bad);
    }

    [AvaloniaFact]
    public async Task Keeping_damaged_files_moves_them_aside_so_the_next_startup_is_silent()
    {
        using var fixture = new DesktopFixture();
        var (_, bad) = await SeedAsync(fixture);
        var scan = fixture.Shell.RecoveryStore.Scan();
        var pending = Startup.ReportDamagedAsync(fixture.Window, fixture.Shell, scan.Corrupt);
        var dialog = await Appears<PromptWindow>(fixture.Window);
        Assert.True(Named(dialog, "Keep").IsDefault);
        Assert.Contains("danger", Named(dialog, "Delete damaged files").Classes);
        Click(Named(dialog, "Keep"));
        await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(File.Exists(bad));
        Assert.True(File.Exists(Path.Combine(fixture.Shell.RecoveryStore.DamagedDirectory, Path.GetFileName(bad))));
        var next = fixture.Shell.RecoveryStore.Scan();
        Assert.Empty(next.Corrupt);
        Assert.Single(next.Entries);
    }

    [AvaloniaFact]
    public async Task Deleting_damaged_files_removes_only_them_and_leaves_valid_checkpoints()
    {
        using var fixture = new DesktopFixture();
        var (_, bad) = await SeedAsync(fixture);
        var scan = fixture.Shell.RecoveryStore.Scan();
        var pending = Startup.ReportDamagedAsync(fixture.Window, fixture.Shell, scan.Corrupt);
        var dialog = await Appears<PromptWindow>(fixture.Window);
        Click(Named(dialog, "Delete damaged files"));
        await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(File.Exists(bad));
        Assert.False(Directory.Exists(fixture.Shell.RecoveryStore.DamagedDirectory));
        var next = fixture.Shell.RecoveryStore.Scan();
        Assert.Empty(next.Corrupt);
        Assert.Equal("valid checkpoint", Assert.Single(next.Entries).Record.Text);
    }

    [AvaloniaFact]
    public async Task Show_files_reveals_the_damaged_file_and_asks_again()
    {
        using var fixture = new DesktopFixture();
        var (_, bad) = await SeedAsync(fixture);
        var scan = fixture.Shell.RecoveryStore.Scan();
        var pending = Startup.ReportDamagedAsync(fixture.Window, fixture.Shell, scan.Corrupt);
        var first = await Appears<PromptWindow>(fixture.Window);
        Click(Named(first, "Show files"));
        var second = await Appears<PromptWindow>(fixture.Window, first);
        Assert.Equal([bad], fixture.Launcher.Revealed);
        Assert.True(File.Exists(bad));
        Assert.False(pending.IsCompleted);
        Click(Named(second, "Keep"));
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
