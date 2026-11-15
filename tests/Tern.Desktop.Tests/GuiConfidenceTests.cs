using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Tern.App.Documents;
using Tern.App.Shell;
using Tern.Desktop.Views;
using Tern.Domain.Merging;
using Tern.Domain.Text;
using Tern.Markdown;

namespace Tern.Desktop.Tests;

public sealed class GuiConfidenceTests
{
    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetName(c) == name);

    [AvaloniaFact]
    public void Protection_prioritizes_failures_and_never_calls_a_checkpoint_a_disk_save()
    {
        using var fixture = new DesktopFixture();
        var tab = fixture.Shell.NewDocument("Draft");
        tab.CheckpointRevision = tab.Session.Revision;
        var protectedState = DocumentProtection.For(tab, DocumentState.NewModified);
        Assert.Contains("Unsaved", protectedState.Label);
        Assert.Equal(ActivitySeverity.Neutral, protectedState.Severity);
        tab.RecoveryError = "Checkpoint unavailable";
        Assert.Equal(ActivitySeverity.Error, DocumentProtection.For(tab, DocumentState.NewModified).Severity);
        Assert.True(fixture.Window.FindControl<Border>("DocumentProblem")!.IsVisible);
        tab.RecoveryError = null;
        Assert.False(fixture.Window.FindControl<Border>("DocumentProblem")!.IsVisible);
    }

    [AvaloniaFact]
    public async Task Export_locks_one_captured_request_and_preserves_all_warnings_and_output_actions()
    {
        using var fixture = new DesktopFixture();
        var path = Path.Combine(fixture.Docs, "output.html");
        var window = new ExportWindow("draft.md", null, "# Test\n\n![One](missing-a.png)\n\n![Two](missing-b.png)", MarkdownProfile.Gfm, null, ExportFormat.Html);
        var gate = new TaskCompletionSource();
        var calls = 0;
        window.BeforeExport = () => { calls++; return gate.Task; };
        window.Show();
        var work = window.ExportAsync(path);
        Assert.True(window.IsExporting);
        Assert.False(await window.ExportAsync(path));
        window.Format = ExportFormat.Pdf;
        Assert.Equal(ExportFormat.Html, window.Format);
        Assert.Equal(1, calls);
        gate.SetResult();
        Assert.True(await work);
        Assert.True(window.Warnings.Count >= 2);
        var notes = window.GetVisualDescendants().OfType<Expander>().Single(e => e.Header?.ToString()?.StartsWith("Export notes", StringComparison.Ordinal) == true);
        Assert.False(notes.IsExpanded);
        notes.IsExpanded = true;
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Contains("One", Named<TextBox>(window, "All export warnings and omissions").Text);
        Assert.Contains("Two", Named<TextBox>(window, "All export warnings and omissions").Text);
        Assert.All(window.Warnings, warning => Assert.Contains(warning, Named<TextBox>(window, "All export warnings and omissions").Text));
        window.LaunchOutput = (_, _) => "No registered viewer";
        window.OpenResult(false);
        Assert.Contains("Output was created", window.StatusText);
        Assert.True(File.Exists(path));
        Assert.Equal(1, calls);
        window.Close();
    }

    [AvaloniaFact]
    public void Manual_merge_edits_do_not_review_other_conflicts_or_enable_implicit_acceptance()
    {
        var a = new ConflictRegion("old-a\n", "mine-a\n", "disk-a\n");
        var b = new ConflictRegion("old-b\n", "mine-b\n", "disk-b\n");
        var window = new MergeWindow("draft.md", new MergeResult([a, b]));
        window.Show();
        Named<TextBox>(window, "Merged result").Text = "Manual text\n";
        Assert.False(Named<Button>(window, "Accept merged text").IsEnabled);
        Named<Button>(window, "Mark this conflict resolved manually").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(Named<Button>(window, "Accept merged text").IsEnabled);
        Named<Button>(window, "Next conflict").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Named<Button>(window, "Mark this conflict resolved manually").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(Named<Button>(window, "Accept merged text").IsEnabled);
        Assert.Equal("Manual text\n", Named<TextBox>(window, "Merged result").Text);
        window.Close();
    }
}
