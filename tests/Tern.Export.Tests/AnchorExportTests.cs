namespace Tern.Export.Tests;

public sealed class AnchorExportTests
{
    [Fact]
    public void Html_export_preserves_detached_and_raw_heading_aliases_as_inert_targets()
    {
        const string markdown = "<a id=\"toc\"></a>\n\n## Contents\n\n[Go](#europe)\n\n<a name=\"europe\"></a>\n\n<h2 id=\"how-to-play\">Play</h2>\n\nText <span id=\"inline\">here</span>.";
        var result = HtmlExporter.Export(markdown);
        StrictExportedHtml.AssertSafe(result.Html, markdown);
        foreach (var id in new[] { "toc", "europe", "how-to-play", "inline" })
            Assert.Contains($"id=\"{id}\"", result.Html, StringComparison.Ordinal);
        Assert.Contains("href=\"#europe\"", result.Html, StringComparison.Ordinal);
        Assert.True(result.Html.IndexOf("id=\"europe\"", StringComparison.Ordinal)
            < result.Html.IndexOf("id=\"how-to-play\"", StringComparison.Ordinal));
        Assert.DoesNotContain("onclick", result.Html, StringComparison.Ordinal);
    }
}
