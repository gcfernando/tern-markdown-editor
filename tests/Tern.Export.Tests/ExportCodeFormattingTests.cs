using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Tern.Export.Tests;

public sealed class ExportCodeFormattingTests
{
    private static readonly (string Language, string Code)[] Samples =
    [
        ("csharp", "var\tx = 35; // SELECT stays a comment\n\tConsole.WriteLine(\"<script>never run</script> & café 😀\");"),
        ("json", "{\"text\": \"<script>never run</script>\", \"ready\": true}"),
        ("javascript", "const namespace = \"sealed Class\";\nCONST = 35;"),
        ("kql", "StormEvents | project State | summarize Total = count()"),
        ("unknown-example", "public\t<unsafe>&data  \n\tliteral;")
    ];

    [Theory]
    [InlineData(ExportTheme.Light, "light")]
    [InlineData(ExportTheme.Dark, "dark")]
    [InlineData(ExportTheme.Auto, "auto")]
    public void Html_uses_declared_language_classes_and_keeps_raw_tabs_and_safe_code(ExportTheme theme, string themeName)
    {
        var source = "# Declared-language code samples\n\n" + string.Join("\n\n", Samples.Select(sample =>
            "```" + sample.Language + "\n" + sample.Code + "\n```"));
        using var dir = new WorkDirectory();
        var sourcePath = WriteSource(dir, source);
        var before = File.ReadAllBytes(sourcePath);
        var output = dir.Combine("code-" + themeName + ".html");
        var result = HtmlExporter.ExportToFile(source, output, new HtmlExportOptions
        {
            Theme = theme,
            Preview = ExportDefaults.PreviewOptions with { DocumentPath = sourcePath }
        });
        var html = result.Html;
        var blocks = Regex.Matches(html, "<pre><code(?<attributes>[^>]*)>(?<content>.*?)</code></pre>", RegexOptions.Singleline);
        Assert.Equal(Samples.Length, blocks.Count);
        for (var index = 0; index < Samples.Length; index++)
        {
            Assert.Contains("class=\"language-" + Samples[index].Language + "\"", blocks[index].Groups["attributes"].Value);
            var plain = WebUtility.HtmlDecode(Regex.Replace(blocks[index].Groups["content"].Value, "<[^>]+>", ""));
            Assert.Equal(Samples[index].Code, plain);
        }
        var csharp = blocks[0].Groups["content"].Value;
        Assert.Contains("syntax-keyword\">var</span>", csharp);
        Assert.Contains("syntax-constant\">35</span>", csharp);
        Assert.Contains("syntax-comment\">// SELECT stays a comment</span>", csharp);
        Assert.DoesNotContain("syntax-keyword", blocks[1].Groups["content"].Value);
        Assert.DoesNotContain("syntax-keyword\">namespace</span>", blocks[2].Groups["content"].Value);
        Assert.DoesNotContain("syntax-keyword\">CONST</span>", blocks[2].Groups["content"].Value);
        Assert.Contains("syntax-keyword\">project</span>", blocks[3].Groups["content"].Value);
        Assert.Contains("syntax-keyword\">summarize</span>", blocks[3].Groups["content"].Value);
        Assert.DoesNotContain("<span", blocks[4].Groups["content"].Value);
        Assert.Contains("data-theme=\"" + themeName + "\"", html);
        Assert.Contains("tab-size:4", html);
        Assert.Contains("&lt;script&gt;never run&lt;/script&gt;", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        StrictExportedHtml.AssertSafe(html, "Declared-language code remains literal data");
        Assert.False(result.Truncated);
        Assert.Equal(html, File.ReadAllText(output));
        Assert.Equal(before, File.ReadAllBytes(sourcePath));
        SaveArtifact(output, "code-" + themeName + ".html");
        SaveArtifact(sourcePath, "html-code-source.md");
    }

    [Fact]
    public void Pdf_code_tabs_align_at_column_stops_without_altering_the_markdown()
    {
        const string source = "# Code tab stops\n\n```text\nA\tB\nAA\tB\nAAAA\tB\n\tindent\n```";
        using var dir = new WorkDirectory();
        var sourcePath = WriteSource(dir, source);
        var before = File.ReadAllBytes(sourcePath);
        var output = dir.Combine("code-layout.pdf");
        var pdf = PdfExporter.ExportToFile(source, output, new PdfExportOptions
        {
            Preview = ExportDefaults.PreviewOptions with { DocumentPath = sourcePath }
        });
        var text = PdfText.AllText(pdf.Data);
        Assert.Contains("A   B", text);
        Assert.Contains("AA  B", text);
        Assert.Contains("AAAA    B", text);
        Assert.Contains("    indent", text);
        Assert.DoesNotContain('\t', text);
        Assert.False(pdf.Truncated);
        Assert.Equal(pdf.Data, File.ReadAllBytes(output));
        Assert.Equal(before, File.ReadAllBytes(sourcePath));
        SaveArtifact(output, "code-layout.pdf");
        SaveArtifact(sourcePath, "pdf-code-source.md");
    }

    private static string WriteSource(WorkDirectory directory, string source)
    {
        var path = directory.Combine("source.md");
        // A real file with a BOM makes byte preservation observable during export.
        File.WriteAllBytes(path, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(source)]);
        return path;
    }

    private static void SaveArtifact(string source, string name)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Tern.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var folder = Tern.Tests.Shared.TestOutput.Folder("screenshots", "reported-code-formatting", "exports");
        Directory.CreateDirectory(folder);
        File.Copy(source, Path.Combine(folder, name), overwrite: true);
    }
}
