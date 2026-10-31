namespace Tern.Export.Tests;

public sealed class ExtendedDiagramExportTests
{
    [Theory]
    [InlineData("stateDiagram-v2\n[*] --> Ready\nReady --> [*]", "Ready")]
    [InlineData("classDiagram\nclass Ledger {\n+balance() decimal\n}\nLedger *-- Entry", "balance() decimal")]
    [InlineData("mindmap\n root((Topic))\n   Branch\n     Leaf", "Leaf")]
    [InlineData("pie\n\"Success\" : 7\n\"Other\" : 3", "70%")]
    [InlineData("pie title Pets\n\"Dogs\" : 5", "Pets")]
    [InlineData("gantt\ndateFormat YYYY-MM-DD\nDraft :2027-01-01, 2d", "2027-01-01")]
    public void Html_exports_supported_graphics_as_inert_vector_content(string source, string expected)
    {
        var result = HtmlExporter.Export("```mermaid\n" + source + "\n```");
        Assert.Contains("<svg class=\"diagram\"", result.Html);
        Assert.Contains(expected, result.Html);
        Assert.DoesNotContain("could not be drawn", result.Html);
        Assert.DoesNotContain("<script", result.Html);
    }
}
