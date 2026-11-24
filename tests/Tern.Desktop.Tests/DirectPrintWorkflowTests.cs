using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Tern.Desktop.Services;
using Tern.Desktop.Views;
using Tern.Export;
using Tern.App.Shell;

namespace Tern.Desktop.Tests;

public sealed class DirectPrintWorkflowTests
{
    [AvaloniaFact]
    public async Task Print_button_passes_the_selected_formatted_PDF_pages_to_the_native_print_service()
    {
        using var f = new DesktopFixture();
        var markdown = string.Join("\n\n", Enumerable.Range(1, 100).Select(index =>
            $"## Distinct section {index}\n\n**Formatted paragraph {index}** with enough text to occupy several different printable pages."));
        var window = new PrintPreviewWindow("selected.md", markdown, ExportDefaults.PreviewOptions, activities: f.Shell.Activities);
        window.Show(f.Window);
        await window.Preparation;
        try
        {
            Assert.True(window.PageCount >= 4);
            Assert.Equal("Print preview ready; nothing printed",
                Assert.Single(f.Shell.Activities.Items, item => item.Kind == "Preparing print preview").Phase);
            window.GetVisualDescendants().OfType<RadioButton>().Single(button => Equals(button.Content, "Pages")).IsChecked = true;
            window.PageRanges.Text = "2,4";
            await window.Navigation;
            var original = PdfExporter.Export(markdown, new PdfExportOptions
            { Preview = ExportDefaults.PreviewOptions, IncludeFrontMatter = false });
            using var originalPdf = await WindowsPdfPreview.LoadAsync(original.Data);
            var calls = 0;
            window.PrintPdf = async (_, title, bytes, token) =>
            {
                calls++;
                Assert.Equal("selected.md", title);
                Assert.False(token.IsCancellationRequested);
                using var selected = await WindowsPdfPreview.LoadAsync(bytes);
                Assert.Equal(2, selected.PageCount);
                for (var index = 0; index < 2; index++)
                {
                    var actual = await selected.RenderPixelsAsync(index + 1, 400);
                    var expected = await originalPdf.RenderPixelsAsync(index == 0 ? 2 : 4, 400);
                    Assert.Equal(actual.Width * actual.Height * 4, actual.Pixels.Length);
                    Assert.Equal(expected.Pixels, actual.Pixels);
                }
                return true;
            };
            window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Print…"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await window.Printing;
            Assert.Equal(1, calls);
            Assert.Contains("Sent 2 selected pages", Status(window));
            Assert.Null(window.OutputPath);
            var activity = Assert.Single(f.Shell.Activities.Items, item => item.Kind == "Printing selected pages");
            Assert.Equal(ActivityState.Completed, activity.State);
            Assert.Equal("Handed to Windows printer; physical output unconfirmed", activity.Phase);
            Assert.NotNull(activity.Finished);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Print_cancellation_and_driver_failure_are_not_reported_as_success(bool fail)
    {
        using var f = new DesktopFixture();
        var window = new PrintPreviewWindow("cancel.md", "# Keep this text", ExportDefaults.PreviewOptions, activities: f.Shell.Activities);
        window.PrintPdf = (_, _, _, _) => fail
            ? throw new System.ComponentModel.Win32Exception("Synthetic printer failure") : Task.FromResult(false);
        window.Show(f.Window);
        await window.Preparation;
        try
        {
            Assert.False(await window.PrintSelectedAsync());
            Assert.Contains(fail ? "Could not print selected pages" : "Printing canceled", Status(window));
            Assert.DoesNotContain("Sent ", Status(window));
            var activity = Assert.Single(f.Shell.Activities.Items, item => item.Kind == "Printing selected pages");
            Assert.Equal(fail ? ActivityState.Failed : ActivityState.Canceled, activity.State);
            Assert.False(activity.IsActive);
            Assert.NotNull(activity.Finished);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Closing_preview_removes_all_owned_temporary_PDFs_but_not_unrelated_files()
    {
        using var f = new DesktopFixture();
        var directory = Path.Combine(f.Docs, "preview-temp");
        var window = new PrintPreviewWindow("cleanup.md", "# Private print text", ExportDefaults.PreviewOptions)
        { TemporaryDirectory = directory, OpenPdf = _ => null };
        window.Show(f.Window);
        await window.Preparation;
        try
        {
            Assert.True(await window.OpenPrintablePdfAsync());
            var first = window.OutputPath!;
            Assert.True(await window.OpenPrintablePdfAsync());
            var second = window.OutputPath!;
            Assert.NotEqual(first, second);
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(second));
            var unrelated = Path.Combine(directory, "unrelated.pdf");
            await File.WriteAllTextAsync(unrelated, "Keep");
            window.Close();
            Assert.False(File.Exists(first));
            Assert.False(File.Exists(second));
            Assert.Equal("Keep", await File.ReadAllTextAsync(unrelated));
        }
        finally { if (window.IsVisible) window.Close(); }
    }

    [AvaloniaFact]
    public async Task Closing_during_selection_extraction_prevents_a_late_native_print_dialog()
    {
        using var f = new DesktopFixture();
        var markdown = string.Join("\n\n", Enumerable.Range(1, 60).Select(index => "## Section " + index + "\n\nA printable paragraph."));
        var window = new PrintPreviewWindow("late.md", markdown, ExportDefaults.PreviewOptions);
        window.Show(f.Window);
        await window.Preparation;
        try
        {
            window.GetVisualDescendants().OfType<RadioButton>().Single(button => Equals(button.Content, "Current page")).IsChecked = true;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.ExtractPages = async (data, pages) => { await release.Task; return PrintPageSelection.Extract(data, pages); };
            var calls = 0;
            window.PrintPdf = (_, _, _, _) => { calls++; return Task.FromResult(true); };
            var pending = window.PrintSelectedAsync();
            window.Close();
            release.SetResult();
            Assert.False(await pending);
            Assert.Equal(0, calls);
        }
        finally { if (window.IsVisible) window.Close(); }
    }

    [AvaloniaFact]
    public async Task Startup_cleanup_only_removes_expired_generated_PDFs_and_reports_locked_files()
    {
        using var f = new DesktopFixture();
        var now = DateTime.UtcNow;
        var expired = Path.Combine(f.Docs, Guid.NewGuid().ToString("N") + ".pdf");
        var recent = Path.Combine(f.Docs, Guid.NewGuid().ToString("N") + ".pdf");
        var unrelated = Path.Combine(f.Docs, "unrelated.pdf");
        foreach (var path in new[] { expired, recent, unrelated }) await File.WriteAllTextAsync(path, "content");
        File.SetLastWriteTimeUtc(expired, now.AddDays(-2));
        File.SetLastWriteTimeUtc(unrelated, now.AddDays(-2));
        using (new FileStream(expired, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Contains(PrintPreviewFiles.CleanupOld(f.Docs, now), failure => failure.Contains(expired));
        Assert.True(File.Exists(expired));
        Assert.Empty(PrintPreviewFiles.CleanupOld(f.Docs, now));
        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(unrelated));
    }

    private static string Status(PrintPreviewWindow window) => window.GetVisualDescendants().OfType<TextBlock>()
        .Single(text => AutomationProperties.GetName(text) == "Print preview status").Text ?? "";
}
