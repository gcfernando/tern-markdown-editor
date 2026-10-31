using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Tern.App.Commands;
using Tern.App.Panels;
using Tern.Markdown;
using Tern.Rendering.Preview;

namespace Tern.Desktop.Tests;

public sealed class EntityHeadingEvidenceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    [AvaloniaFact]
    public async Task Entity_text_is_decoded_in_heading_outline_link_label_and_image_alt()
    {
        const string markdown = "# Café 😀 &amp; Unicode\n\n[Fish &amp; Chips](https://example.invalid/)\n\n![Café &amp; image](missing.png)\n";
        using var fixture = new DesktopFixture();
        var path = await fixture.WriteAsync("entity-heading.md", markdown);
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        for (var attempt = 0; attempt < 80 && tab.Analysis is null; attempt++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.NotNull(tab.Analysis);
        await fixture.Window.RunCommandAsync(CommandIds.ViewSplit);
        await fixture.Window.RebuildPreviewAsync();
        var panel = DocumentPanel.Build(tab, null);
        var preview = fixture.Window.PreviewControl.Document!;
        fixture.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var root = Directory.GetParent(Screenshots.Folder)!.Parent!.FullName;
        var output = Path.Combine(root, "artifacts", "rendering-validation", "regressions", "entity-heading");
        Directory.CreateDirectory(output);
        var phase = Environment.GetEnvironmentVariable("TERN_ENTITY_CAPTURE_PHASE") == "after" ? "after" : "before";
        using (var frame = fixture.Window.CaptureRenderedFrame())
        {
            Assert.NotNull(frame);
            using var stream = File.Create(Path.Combine(output, "tern-" + phase + ".png"));
            frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        await File.WriteAllTextAsync(Path.Combine(output, "tern-" + phase + ".json"), JsonSerializer.Serialize(new
        {
            markdown,
            phase,
            scope = "Headless MainWindow Split with real document analysis and preview models",
            headings = tab.Analysis.Headings,
            outline = panel.Outline,
            links = tab.Analysis.Links,
            previewHeadings = preview.Blocks.OfType<HeadingNode>().Select(h => new { h.PlainText, h.Anchor }),
            previewImageAlts = preview.Blocks.OfType<ParagraphNode>().SelectMany(p => p.Inlines).OfType<ImageNode>().Select(i => i.Alt),
        }, JsonOptions));

        Assert.Equal("Café 😀 & Unicode", Assert.Single(tab.Analysis.Headings).Text);
        Assert.Equal("Café 😀 & Unicode", Assert.Single(panel.Outline).Text);
        Assert.Equal("Fish & Chips", Assert.Single(tab.Analysis.Links, l => l.Kind == LinkKind.Inline).Label);
        Assert.Equal("Café & image", Assert.Single(tab.Analysis.Links, l => l.Kind == LinkKind.Image).Label);
        Assert.Equal("Café 😀 & Unicode", Assert.Single(preview.Blocks.OfType<HeadingNode>()).PlainText);
        Assert.Equal("Café & image", Assert.Single(preview.Blocks.OfType<ParagraphNode>().SelectMany(p => p.Inlines).OfType<ImageNode>()).Alt);
        Assert.Equal(markdown, tab.CurrentText);
        Assert.Equal(markdown, await File.ReadAllTextAsync(path));
        Assert.False(tab.IsDirty);
    }
}
