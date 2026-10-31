using Xunit.Abstractions;

namespace Tern.Export.Tests;

public sealed class HtmlExporterTests(ITestOutputHelper output)
{
    private static HtmlExportResult Export(string markdown, HtmlExportOptions? options = null)
    {
        var result = HtmlExporter.Export(markdown, options);
        StrictExportedHtml.AssertSafe(result.Html, markdown);
        return result;
    }

    [Fact]
    public void ProducesASelfContainedDocumentWithCspAndBothThemes()
    {
        var html = Export("# Hello\n\nworld").Html;

        Assert.StartsWith("<!DOCTYPE html>", html, StringComparison.Ordinal);
        Assert.Contains("<meta http-equiv=\"Content-Security-Policy\"", html, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", html, StringComparison.Ordinal);
        Assert.Contains("prefers-color-scheme:dark", html, StringComparison.Ordinal);
        Assert.Contains("data-theme", html, StringComparison.Ordinal);
        Assert.Contains("@media print", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CspStyleHashMatchesTheEmbeddedStylesheet()
    {
        var html = Export("x").Html;
        var start = html.IndexOf("<style>", StringComparison.Ordinal) + "<style>".Length;
        var css = html[start..html.IndexOf("</style>", StringComparison.Ordinal)];
        var hash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(css)));
        Assert.Contains($"'sha256-{hash}'", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ExportTheme.Light, "light")]
    [InlineData(ExportTheme.Dark, "dark")]
    [InlineData(ExportTheme.Auto, "auto")]
    public void AppliesTheRequestedTheme(ExportTheme theme, string expected)
    {
        var html = Export("x", new HtmlExportOptions { Theme = theme }).Html;
        Assert.Contains($"data-theme=\"{expected}\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void PicksTitleFromOptionThenFrontMatterThenHeading()
    {
        Assert.Equal("Given", Export("# H", new HtmlExportOptions { Title = "Given" }).Title);
        Assert.Equal("From FM", Export("---\ntitle: From FM\n---\n# H\n").Title);
        Assert.Equal("Only Heading", Export("text\n\n# Only Heading\n").Title);
        Assert.Equal("Document", Export("just text").Title);
    }

    [Fact]
    public void EscapesTheTitleAndRejectsBadLanguageTags()
    {
        var result = Export("x", new HtmlExportOptions { Title = "</title><script>alert(1)</script>", Language = "en\" onload=\"x" });
        Assert.DoesNotContain("<script", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<html lang=\"en\"", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void HeadingsGetStableAnchorIds()
    {
        var html = Export("# Hello World\n\n## Hello World\n\n### Ünïcode & more").Html;
        Assert.Contains("<h1 id=\"hello-world\"", html, StringComparison.Ordinal);
        Assert.Contains("<h2 id=\"hello-world-1\"", html, StringComparison.Ordinal);
        Assert.Contains("<h3 id=\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RendersCommonBlocks()
    {
        var html = Export("""
            Text with **bold**, *em*, ~~gone~~, `code` and [a link](https://example.com/x).

            - one
            - two
              1. nested
            - [x] done
            - [ ] open

            > quote

            > [!NOTE]
            > a note

            | A | B |
            |:--|--:|
            | 1 | 2 |

            ```csharp
            var x = 1 < 2;
            ```

            ---
            """).Html;

        foreach (var fragment in new[] { "<strong>bold</strong>", "<em>em</em>", "<del>gone</del>", "<code>code</code>", "<ul", "<ol", "<blockquote", "<table", "<thead", "language-csharp", "var x = 1 &lt; 2;", "<hr" })
        {
            Assert.Contains(fragment, html, StringComparison.Ordinal);
        }

        Assert.Contains("href=\"https://example.com/x\"", html, StringComparison.Ordinal);
        Assert.Contains("rel=\"noopener noreferrer\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"task done\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"not done\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<input", html, StringComparison.Ordinal);
        Assert.Contains("a note", html, StringComparison.Ordinal);
        Assert.Contains("class=\"a-left\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"a-right\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void InlinesLocalImagesAsDataUris()
    {
        using var dir = new WorkDirectory();
        var docPath = dir.Write("doc.md", "x");
        dir.Write("img/pic.png", SampleImages.Png);
        var options = new HtmlExportOptions { Preview = ExportDefaults.PreviewOptions with { DocumentPath = docPath } };

        var result = Export("![a picture](img/pic.png)", options);

        Assert.Equal(1, result.EmbeddedImageCount);
        Assert.Contains(SampleImages.PngDataUri, result.Html, StringComparison.Ordinal);
        Assert.Contains("alt=\"a picture\"", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacesRemoteSvgAndOversizedImagesWithPlaceholders()
    {
        using var dir = new WorkDirectory();
        var docPath = dir.Write("doc.md", "x");
        dir.Write("big.png", [.. SampleImages.Png, .. new byte[4096]]);
        dir.Write("v.svg", "<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>");
        var options = new HtmlExportOptions
        {
            Preview = ExportDefaults.PreviewOptions with { DocumentPath = docPath },
            MaxInlineImageBytes = 1024,
        };

        var result = Export("![r](https://example.com/r.png) ![s](v.svg) ![b](big.png)", options);

        Assert.Equal(0, result.EmbeddedImageCount);
        Assert.DoesNotContain("example.com", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", result.Html, StringComparison.Ordinal);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Count(result.Html, "class=\"image-placeholder\""));
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void RendersMathAsUnicodeText()
    {
        var html = Export("Inline $\\alpha \\leq \\beta$ and\n\n$$\n\\sum_{i=1}^{n} x_i\n$$\n").Html;
        Assert.Contains("α", html, StringComparison.Ordinal);
        Assert.Contains("≤", html, StringComparison.Ordinal);
        Assert.Contains("∑", html, StringComparison.Ordinal);
        Assert.DoesNotContain("\\alpha", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RendersFlowchartAndSequenceDiagramsAsInlineSvg()
    {
        var flow = Export("```mermaid\nflowchart TD\n  A[Start] --> B{Ok?}\n  B -->|yes| C[Done]\n```\n").Html;
        Assert.Contains("<svg", flow, StringComparison.Ordinal);
        Assert.Contains("viewBox=", flow, StringComparison.Ordinal);
        foreach (var label in new[] { "Start", "Ok?", "Done", "yes" })
        {
            Assert.Contains(label, flow, StringComparison.Ordinal);
        }

        var sequence = Export("```mermaid\nsequenceDiagram\n  Alice->>Bob: Hello\n  Bob-->>Alice: Hi\n```\n").Html;
        Assert.Contains("<svg", sequence, StringComparison.Ordinal);
        Assert.Contains("Alice", sequence, StringComparison.Ordinal);
        Assert.Contains("Hello", sequence, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedAndBrokenDiagramsShowSourceWithANotice()
    {
        var result = Export("```mermaid\ngantt\n  title Plan\n```\n\n```mermaid\nflowchart TD\n  A --> \n```\n");
        Assert.Contains("gantt", result.Html, StringComparison.Ordinal);
        Assert.Contains("class=\"notice\"", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<svg", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void RendersFrontMatterFootnotesAndSafeHtml()
    {
        var html = Export("---\ntitle: T\nauthor: Ada\n---\n\nRef[^a].\n\nPress <kbd>Ctrl</kbd>, H<sub>2</sub>O<br>next\n\n[^a]: The note.\n").Html;
        Assert.Contains("front-matter", html, StringComparison.Ordinal);
        Assert.Contains("Ada", html, StringComparison.Ordinal);
        Assert.Contains("id=\"fn-1\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"#fn-1\"", html, StringComparison.Ordinal);
        Assert.Contains("<kbd>Ctrl</kbd>", html, StringComparison.Ordinal);
        Assert.Contains("<sub>2</sub>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void CanOmitFrontMatter()
    {
        var html = Export("---\nsecret: value\n---\n\nbody", new HtmlExportOptions { IncludeFrontMatter = false }).Html;
        Assert.DoesNotContain("secret", html, StringComparison.Ordinal);
    }

    public static TheoryData<string> Malicious() =>
    [
        "<script>alert(1)</script>",
        "<img src=x onerror=alert(1)>",
        "<IMG SRC=\"jav&#x09;ascript:alert(1)\">",
        "<a href=\"javascript:alert(1)\">x</a>",
        "[x](javascript:alert(1))",
        "[x](  JaVaScRiPt:alert(1))",
        "[x](vbscript:msgbox(1))",
        "[x](data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==)",
        "[x](file:///c:/windows/win.ini)",
        "[x](\\\\evil\\share\\x)",
        "![x](https://evil.example/track.png)",
        "![x](//evil.example/track.png)",
        "![x](data:image/svg+xml;base64,PHN2ZyBvbmxvYWQ9YWxlcnQoMSk+)",
        "![x](../../../../windows/win.ini)",
        "![x](C:\\Windows\\win.ini)",
        "<svg onload=alert(1)>",
        "<iframe srcdoc=\"<script>alert(1)</script>\"></iframe>",
        "<style>@import 'http://evil/x.css'; body{background:url(http://evil/x)}</style>",
        "<meta http-equiv=\"refresh\" content=\"0;url=http://evil\">",
        "<base href=\"http://evil/\"><a href=x>y</a>",
        "<form action=\"http://evil\"><input name=x></form>",
        "<object data=\"x.swf\"></object><embed src=\"x.swf\">",
        "<div style=\"background:url(javascript:alert(1))\">x</div>",
        "<details open ontoggle=alert(1)><summary>s</summary></details>",
        "<!-- --><script>alert(1)</script><!-- -->",
        "<![CDATA[<script>alert(1)</script>]]>",
        "<math><mtext><table><mglyph><style><!--</style><img title=\"--&gt;&lt;img src=1 onerror=alert(1)&gt;\">",
        "<a href=\"&#106;avascript:alert(1)\">x</a>",
        "<a href=\"java\tscript:alert(1)\">x</a>",
        "<<script>alert(1);//<</script>",
        "<img src=\"x\" alt=\"\" onerror=\"alert(1)\"",
        "`<script>`\n\n```html\n<script>alert(1)</script>\n```",
        "# <script>alert(1)</script>\n\n| <img src=x onerror=1> | b |\n|---|---|\n| <svg onload=1> | d |",
        "```mermaid\nflowchart TD\n  A[\"<script>alert(1)</script>\"] --> B[<img src=x onerror=1>]\n```",
        "```mermaid\nsequenceDiagram\n  A->>B: <script>alert(1)</script>\n```",
        "$$\n\\text{<script>alert(1)</script>}\n$$",
        "---\ntitle: <script>alert(1)</script>\nx: \"</style><script>1</script>\"\n---\n",
        "[ref]: javascript:alert(1)\n\n[ref]",
        "[^1]: <script>alert(1)</script>\n\nx[^1]",
        "\0<script\0>alert(1)</script\0>",
    ];

    [Theory]
    [MemberData(nameof(Malicious))]
    public void MaliciousMarkdownNeverYieldsActiveOrExternalContent(string markdown)
    {
        var html = Export(markdown).Html;
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img src=\"http", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url(http", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LargeDocumentExportsQuickly()
    {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < 1000; i++)
        {
            builder.Append("## Section ").Append(i).Append("\n\nParagraph with **bold**, `code` and [link](https://example.com/").Append(i).Append(").\n\n- item a\n- item b\n\n");
        }

        var lines = builder.ToString().Count(c => c == '\n');
        Assert.True(lines >= 5000);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = Export(builder.ToString());
        stopwatch.Stop();
        output.WriteLine($"{lines} lines -> {result.Html.Length / 1024} KiB in {stopwatch.ElapsedMilliseconds} ms");
        Assert.False(result.Truncated);
        Assert.Contains("id=\"section-999\"", result.Html, StringComparison.Ordinal);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public void HugeInputIsTruncatedWithABanner()
    {
        var options = new HtmlExportOptions { Preview = ExportDefaults.PreviewOptions with { MaxNodes = 500 } };
        var markdown = string.Join("\n\n", Enumerable.Range(0, 2000).Select(i => $"Paragraph {i}"));
        var result = Export(markdown, options);
        Assert.True(result.Truncated);
        Assert.Contains("class=\"truncated\"", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void SameInputGivesIdenticalOutput()
    {
        const string Markdown = "# A\n\n```mermaid\nflowchart LR\n A-->B\n```\n\n| a | b |\n|---|---|\n| 1 | 2 |";
        Assert.Equal(Export(Markdown).Html, Export(Markdown).Html);
    }

    [Fact]
    public void ExportToFileWritesUtf8WithoutLeavingTempFiles()
    {
        using var dir = new WorkDirectory();
        var path = dir.Combine("out.html");
        var result = HtmlExporter.ExportToFile("# Ünï ✓", path);
        Assert.Equal(result.Html, File.ReadAllText(path, System.Text.Encoding.UTF8));
        Assert.Equal([path], Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void Export_refuses_to_overwrite_the_markdown_source()
    {
        using var dir = new WorkDirectory();
        const string source = "# Keep original\r\n";
        var path = dir.Write("document.md", source);
        var options = new HtmlExportOptions { Preview = ExportDefaults.PreviewOptions with { DocumentPath = path } };

        Assert.Throws<ExportException>(() => HtmlExporter.ExportToFile(source, path, options));
        Assert.Equal(source, File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(dir.Path));
    }
}
