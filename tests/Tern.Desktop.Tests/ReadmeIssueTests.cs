using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Settings;
using Tern.Desktop.Services;
using Tern.Desktop.Views;
using Tern.Markdown;
using Tern.Rendering.Preview;

namespace Tern.Desktop.Tests;

public sealed class ReadmeIssueTests
{
    [AvaloniaFact]
    public async Task Export_actions_are_valid_only_for_the_current_successful_result()
    {
        using var f = new DesktopFixture();
        var window = new ExportWindow("note.md", null, "# Note", MarkdownProfile.Extended, null, ExportFormat.Html);
        window.Show(f.Window); Pump(window);
        var actions = window.GetVisualDescendants().OfType<Button>().Where(b => b.Content is "Open file" or "Show in folder").ToArray();
        void Hidden() => Assert.All(actions, b => Assert.False(b.IsVisible));
        try
        {
            Hidden(); Assert.Equal(2, actions.Length);
            var output = Path.Combine(f.Docs, "note.html");
            Assert.True(await window.ExportAsync(output)); Assert.All(actions, b => Assert.True(b.IsVisible));
            window.Format = ExportFormat.Pdf; Pump(window); Hidden();
            Assert.Equal(Path.Combine(f.Docs, "note.pdf"), Find<TextBox>(window, "Output file").Text);
            Assert.Empty(window.Warnings); Assert.False(window.Truncated);
            Assert.False(await window.ExportAsync(null)); Hidden();
            var pdf = Path.Combine(f.Docs, "note.pdf"); Assert.True(await window.ExportAsync(pdf));
            window.GetVisualDescendants().OfType<NumericUpDown>().Single().Value = 30; Hidden();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.BeforeExport = () => gate.Task;
            var running = window.ExportAsync(pdf); Hidden(); Assert.True(window.IsExporting);
            gate.SetResult(); Assert.True(await running); Assert.All(actions, b => Assert.True(b.IsVisible));
            window.BeforeExport = null;
            var blocked = Path.Combine(f.Docs, "blocked");
            await File.WriteAllTextAsync(blocked, "A file cannot be used as an output directory.");
            Assert.False(await window.ExportAsync(Path.Combine(blocked, "failed.pdf"))); Hidden();
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(ThemeChoice.Midnight, 1d)]
    [InlineData(ThemeChoice.Paper, 1d)]
    [InlineData(ThemeChoice.Midnight, 1.5d)]
    [InlineData(ThemeChoice.Paper, 2d)]
    public async Task Tutor_toolbar_stays_above_the_larger_composer_and_renders_markdown(ThemeChoice theme, double scale)
    {
        using var f = new DesktopFixture();
        f.Shell.Settings.Theme = theme; f.Shell.Settings.UiScale = scale; f.Window.ApplyTheme(); f.Window.ApplySettings();
        using var tutor = new HelpTutorView(f.Window.AIWorkspace, f.Shell.Registry, () => Task.CompletedTask, null, null, _ => { }, null);
        var window = new Window { Content = tutor, Width = 1000, Height = 900 }; window.Show(f.Window); Pump(window);
        try
        {
            var copy = tutor.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Copy conversation"));
            Assert.False(copy.IsEnabled); Assert.True(tutor.Question.MinHeight >= 150);
            Assert.True(tutor.Connections.TranslatePoint(default, window)!.Value.Y < tutor.Question.TranslatePoint(default, window)!.Value.Y);
            Assert.True(copy.TranslatePoint(default, window)!.Value.Y < tutor.Question.TranslatePoint(default, window)!.Value.Y);
            Assert.True(await tutor.Transcript.ShowAsync("## Steps\n\n**Important**\n\n1. First\n2. Second\n\n```sql\nSELECT 1;\n```", TestContext.Current.CancellationToken, () => true)); Pump(window);
            Assert.True(copy.IsEnabled);
            Assert.Contains(tutor.Transcript.Preview.Document!.Blocks, b => b is HeadingNode);
            Assert.Contains(tutor.Transcript.Preview.Document.Blocks, b => b is CodeBlockNode);
            Assert.True(tutor.Transcript.Bounds.Height > 100);
            Capture(window, $"formatted-help-{theme}-{scale}");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Safe_svg_badge_is_embedded_in_the_desktop_PDF()
    {
        using var f = new DesktopFixture();
        var svg = "<svg xmlns='http://www.w3.org/2000/svg' width='120' height='24'><rect width='120' height='24' fill='#156e66'/><text fill='white'><tspan x='5' y='17'>Documentation</tspan></text></svg>";
        await File.WriteAllTextAsync(Path.Combine(f.Docs, "badge.svg"), svg, TestContext.Current.CancellationToken);
        var raster = Tern.PreviewView.SvgRasterizer.RenderPng(System.Text.Encoding.UTF8.GetBytes(svg));
        Assert.NotNull(raster);
        using (var image = PdfSharp.Drawing.XImage.FromStream(new MemoryStream(raster)))
        {
            Assert.InRange(image.PointWidth, 89.9, 90.1);
            Assert.InRange(image.PointHeight, 17.9, 18.1);
        }
        var source = await f.WriteAsync("badge.md", "# Badges\n\n![Documentation](badge.svg)");
        var window = new ExportWindow("badge.md", source, File.ReadAllText(source), MarkdownProfile.Extended, f.Docs, ExportFormat.Pdf);
        window.Show(f.Window); Pump(window);
        try
        {
            var output = Path.Combine(f.Docs, "badge.pdf");
            Assert.True(await window.ExportAsync(output)); Assert.Empty(window.Warnings);
            using var pdf = PdfSharp.Pdf.IO.PdfReader.Open(output, PdfSharp.Pdf.IO.PdfDocumentOpenMode.ReadOnly);
            var objects = pdf.Pages[0].Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/XObject")!;
            Assert.Contains(objects.Elements.Keys, key => objects.Elements.GetDictionary(key)?.Elements.GetName("/Subtype") == "/Image");
            Capture(window, "svg-pdf-complete");
            Assert.Null(Tern.PreviewView.SvgRasterizer.RenderPng(System.Text.Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>")));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Guidance_is_present_in_the_main_window_and_shared_dialog_shell()
    {
        using var f = new DesktopFixture();
        Assert.NotEmpty(f.Window.GetVisualDescendants().OfType<GuidanceStrip>());
        var window = new Window(); DialogParts.Setup(window, "Insert image", 500, 400, DialogGuidance.Image); window.Content = new TextBox(); window.Show(f.Window); Pump(window);
        try { Assert.Contains("image", Assert.Single(window.GetVisualDescendants().OfType<GuidanceStrip>()).Message); }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Offline_answer_is_visible_and_export_format_reset_removes_completion_ui()
    {
        using var f = new DesktopFixture();
        using var tutor = new HelpTutorView(f.Window.AIWorkspace, f.Shell.Registry, () => Task.CompletedTask, null, null, _ => { }, null);
        var help = new Window { Content = tutor, Width = 1000, Height = 900 }; help.Show(f.Window);
        try
        {
            tutor.Question.Text = "How do I export a PDF?";
            tutor.SendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 200 && !tutor.Transcript.IsVisible; i++) { await Task.Delay(10); Pump(help); }
            Assert.True(tutor.Transcript.IsVisible);
            Assert.Contains("offline answer", tutor.Transcript.Text);
            Assert.Contains(help.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.StartsWith("Answered from the installed guides.") == true);
            Capture(help, "offline-help-answer");
        }
        finally { help.Close(); }

        var export = new ExportWindow("note.md", null, "# Note\n\n![Missing image](missing.png)", MarkdownProfile.Extended, null, ExportFormat.Html);
        export.Show(f.Window); Pump(export);
        try
        {
            Assert.True(await export.ExportAsync(Path.Combine(f.Docs, "warnings.html"))); Pump(export);
            Assert.NotEmpty(export.Warnings);
            var notes = export.GetVisualDescendants().OfType<Expander>().Single();
            Assert.False(notes.IsExpanded); Capture(export, "export-notes-collapsed");
            notes.IsExpanded = true; Pump(export); Capture(export, "export-notes-expanded");
            export.Format = ExportFormat.Pdf; Pump(export);
            Assert.Empty(export.Warnings); Assert.False(notes.IsVisible);
            Assert.Equal(Path.Combine(f.Docs, "warnings.pdf"), Find<TextBox>(export, "Output file").Text);
            Assert.All(export.GetVisualDescendants().OfType<Button>().Where(b => b.Content is "Open file" or "Show in folder"), b => Assert.False(b.IsVisible));
            Capture(export, "export-format-reset");
        }
        finally { export.Close(); }
    }

    private static T Find<T>(Control root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetName(c) == name);
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static void Capture(Window window, string name)
    {
        var folder = Path.Combine(Screenshots.Folder, "readme-ui-fix"); Directory.CreateDirectory(folder);
        using var bitmap = window.CaptureRenderedFrame(); Assert.NotNull(bitmap);
        using var stream = File.Create(Path.Combine(folder, name + ".png")); bitmap.Save(stream);
    }
}
