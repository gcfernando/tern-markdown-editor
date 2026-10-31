using System.Text.RegularExpressions;

namespace Tern.Export.Tests;

public sealed class PcbDiagramExportTests
{
    [Fact]
    public void Supplied_pcb_diagrams_export_as_curved_svg_and_pdf_without_fallbacks()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MarkdownSamples", "complex_pcb_circuit_network_design.md");
        var markdown = File.ReadAllText(path);
        var html = HtmlExporter.Export(markdown);
        Assert.Equal(10, Regex.Count(html.Html, "<svg class=\"diagram\""));
        Assert.Contains(" Q ", html.Html);
        foreach (Match graphic in Regex.Matches(html.Html, "<svg class=\"diagram\".*?</svg>", RegexOptions.Singleline).Take(9))
        {
            Assert.Contains("<path class=\"d-edge", graphic.Value);
            Assert.DoesNotContain("<polyline class=\"d-edge", graphic.Value);
        }
        Assert.Empty(html.Warnings);
        var pdf = PdfExporter.Export(markdown);
        Assert.True(pdf.PageCount > 0);
        Assert.Empty(pdf.Warnings);
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/diagram-upgrade"));
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "pcb-design.html"), html.Html);
        File.WriteAllBytes(Path.Combine(output, "pcb-design.pdf"), pdf.Data);
    }
}
