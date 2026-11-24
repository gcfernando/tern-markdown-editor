using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.App.Settings;
using Tern.Desktop.Services;
using Tern.Desktop.Views;
using Tern.Export;
using Tern.Markdown;

namespace Tern.Desktop.Tests;

public sealed class ExportHelpIssueTests
{
    public static IEnumerable<object[]> Layouts => from size in new[] { (900, 560), (1280, 720) }
        from scale in new[] { 1d, 1.5, 2d }
        from theme in new[] { ThemeChoice.Midnight, ThemeChoice.Paper }
        select new object[] { size.Item1, size.Item2, scale, theme };

    [AvaloniaTheory]
    [MemberData(nameof(Layouts))]
    public async Task Export_and_help_forms_reflow_in_both_themes(int width, int height, double scale, ThemeChoice theme)
    {
        using var f = new DesktopFixture();
        f.Window.Width = width; f.Window.Height = height;
        f.Shell.Settings.Theme = theme; f.Shell.Settings.UiScale = scale;
        f.Window.ApplyTheme(); f.Window.ApplySettings();
        var export = new ExportWindow("sample.md", null, "# Sample\n\n**Important**", MarkdownProfile.Extended, null, ExportFormat.Pdf);
        export.Show(f.Window); Pump(export);
        try
        {
            foreach (var format in Enum.GetValues<ExportFormat>())
            {
                export.Format = format; Pump(export); CheckScrollWidth(export);
                Capture(export, $"export-{format}-{theme}-{width}-{scale}");
            }
        }
        finally { export.Close(); }
        using var tutor = new HelpTutorView(f.Window.AIWorkspace, f.Shell.Registry, () => Task.CompletedTask, null, null, _ => { }, null);
        var help = new Window(); DialogParts.Setup(help, "Ask about Tern", Math.Min(width, 900), Math.Min(height, 680), DialogGuidance.Help); help.Content = tutor;
        help.Show(f.Window); Pump(help);
        try
        {
            Assert.DoesNotContain(help.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Use selected text"));
            Assert.False(tutor.Transcript.IsVisible);
            CheckScrollWidth(help);
            Capture(help, $"help-{theme}-{width}-{scale}");
        }
        finally { help.Close(); }
        await Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task Print_preview_contains_formatted_snapshot_and_opens_a_real_PDF()
    {
        using var f = new DesktopFixture();
        const string source = "# Print sample\n\n**Bold text** and a list.\n\n- First\n- Second\n\n| Name | Value |\n|---|---|\n| One | Two |";
        var window = new PrintPreviewWindow("sample.md", source, ExportDefaults.PreviewOptions);
        string? opened = null; window.OpenPdf = path => { opened = path; return null; };
        window.Show(f.Window); await window.Preparation; Pump(window);
        try
        {
            var preview = Assert.Single(window.GetVisualDescendants().OfType<Image>());
            Assert.NotNull(preview.Source);
            Assert.True(window.PageCount > 0);
            foreach (var theme in new[] { ThemeChoice.Midnight, ThemeChoice.Paper })
            {
                f.Shell.Settings.Theme = theme; f.Shell.Settings.UiScale = 1;
                f.Window.ApplyTheme(); f.Window.ApplySettings(); Pump(window);
                Capture(window, $"print-preview-{theme}");
            }
            var path = Path.Combine(f.Docs, "print.pdf");
            Assert.True(await window.OpenPrintablePdfAsync(path));
            Assert.Equal(path, opened);
            Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path)));
            Assert.Contains("Ctrl+P", window.GetVisualDescendants().OfType<TextBlock>().Single(t => AutomationProperties.GetName(t) == "Print preview status").Text);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void One_export_shortcut_opens_all_formats_and_unrelated_help_is_blocked_locally()
    {
        var registry = CommandRegistry.CreateDefault();
        Assert.Equal("Ctrl+Shift+E", Gesture.Describe(registry.GetGesture(CommandIds.ExportAs)));
        Assert.DoesNotContain(registry.Commands, c => c.Id is CommandIds.ExportHtml or CommandIds.ExportPdf);
        var migrated = CommandRegistry.CreateDefault();
        Assert.Empty(migrated.LoadOverrides(new Dictionary<string, string> { [CommandIds.ExportPdf] = "Ctrl+Alt+Shift+E" }));
        Assert.Equal("Ctrl+Alt+Shift+E", migrated.GetGesture(CommandIds.ExportAs));
        foreach (var question in new[] { "Explain photosynthesis", "Write a poem about cats", "Who won the football match?" })
        {
            Assert.False(GuideQuestionPrompt.CanAnswer(registry, question));
            Assert.Throws<InvalidOperationException>(() => GuideQuestionPrompt.Build(registry, question, false, AIConnectionRules.DefaultSendCharacters));
        }
        foreach (var question in new[] { "How do I export a PDF?", "How do I search a note?", "Explain Markdown headings" })
            Assert.True(GuideQuestionPrompt.CanAnswer(registry, question), question);
    }

    [AvaloniaFact]
    public async Task Welcome_card_fits_its_contents_and_explains_the_next_action()
    {
        using var f = new DesktopFixture();
        f.Shell.Settings.Theme = ThemeChoice.Midnight; f.Shell.Settings.UiScale = 1;
        f.Window.ApplyTheme(); f.Window.ApplySettings();
        await f.Window.RunCommandAsync(CommandIds.Welcome); Pump(f.Window);
        var card = Assert.Single(f.Window.OverlayLayer.Children.OfType<Border>());
        Assert.True(card.Bounds.Height < f.Window.Bounds.Height - 150);
        Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "1. Write in Source → 2. Choose Reading → 3. Save with Ctrl+S");
        Capture(f.Window, "welcome-Midnight");
        var close = card.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Close"));
        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(f.Window.OverlayLayer.IsVisible);
    }

    private static void CheckScrollWidth(Window window)
    {
        foreach (var scroll in window.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s.IsEffectivelyVisible && s.Viewport.Width > 1))
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 2, $"{scroll.Name}: {scroll.Extent} exceeds {scroll.Viewport}");
    }
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static void Capture(Window window, string name)
    {
        var folder = Path.Combine(Screenshots.Folder, "export-help-fix"); Directory.CreateDirectory(folder);
        using var bitmap = window.CaptureRenderedFrame(); Assert.NotNull(bitmap);
        using var stream = File.Create(Path.Combine(folder, name + ".png")); bitmap.Save(stream);
    }
}
