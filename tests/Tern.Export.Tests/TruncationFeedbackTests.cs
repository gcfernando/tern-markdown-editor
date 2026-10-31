using Tern.Rendering.Preview;

namespace Tern.Export.Tests;

public sealed class TruncationFeedbackTests
{
    [Fact]
    public void Both_exporters_warn_when_the_source_budget_truncates_the_document()
    {
        var preview = new PreviewOptions { MaxSourceLength = 30 };
        var text = "# Heading\n\n" + new string('x', 100);
        var html = HtmlExporter.Export(text, new HtmlExportOptions { Preview = preview });
        Assert.True(html.Truncated);
        Assert.Contains(html.Warnings, warning => warning.Contains("incomplete", StringComparison.Ordinal));
        var pdf = PdfExporter.Export(text, new PdfExportOptions { Preview = preview });
        Assert.True(pdf.Truncated);
        Assert.Contains(pdf.Warnings, warning => warning.Contains("incomplete", StringComparison.Ordinal));
    }
}
