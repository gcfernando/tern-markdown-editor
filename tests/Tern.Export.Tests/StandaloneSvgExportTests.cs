using System.Text;
using Tern.Rendering.Preview;
using Tern.Rendering.Resources;

namespace Tern.Export.Tests;

public sealed class StandaloneSvgExportTests
{
    [Fact]
    public void Actual_pcb_inline_svg_images_are_embedded_as_validated_data_images()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MarkdownSamples", "pcb_circuit_diagram.md"));
        var model = PreviewBuilder.Build(source, new PreviewOptions());
        var result = HtmlExporter.Export(source);
        var imageData = model.Blocks.OfType<ParagraphNode>().SelectMany(p => p.Inlines).OfType<ImageNode>().Select(i => Assert.IsType<InlineImage>(i.Resolution).Data).ToArray();
        Assert.Equal(2, imageData.Length);
        Assert.Equal(2, result.EmbeddedImageCount);
        foreach (var data in imageData)
        {
            Assert.True(SafeSvgPolicy.IsSafe(data));
            Assert.Contains("data:image/svg+xml;base64," + Convert.ToBase64String(data), result.Html, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("<script", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("default-src 'none'", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Local_circuit_svgs_embed_but_active_local_svg_stays_blocked()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MarkdownSamples");
        var result = HtmlExporter.Export("![Circuit](assets/led-series.svg)\n\n![Divider](assets/voltage-divider.svg)",
            new HtmlExportOptions { Preview = new PreviewOptions { DocumentPath = Path.Combine(root, "circuits.md"), WorkspaceRoot = root } });
        Assert.Equal(2, result.EmbeddedImageCount);
        Assert.Empty(result.Warnings);
        foreach (var filename in new[] { "led-series.svg", "voltage-divider.svg" })
        {
            Assert.True(SafeSvgPolicy.TryNormalize(File.ReadAllBytes(Path.Combine(root, "assets", filename)), out var normalized, out _));
            Assert.Contains(Convert.ToBase64String(normalized), result.Html, StringComparison.Ordinal);
        }
        var active = "<svg xmlns='http://www.w3.org/2000/svg'><image href='https://example.com/tracker.svg'/></svg>";
        var blocked = HtmlExporter.Export(active);
        Assert.Equal(0, blocked.EmbeddedImageCount);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(active)), blocked.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("https://example.com", blocked.Html, StringComparison.Ordinal);
    }
}
