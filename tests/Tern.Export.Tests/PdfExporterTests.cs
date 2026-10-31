using PdfSharp.Pdf;
using PdfSharp.Pdf.Annotations;
using Tern.Rendering.Preview;
using Xunit.Abstractions;

namespace Tern.Export.Tests;

public sealed class PdfExporterTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset FixedDate = new(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static PdfExportOptions Options(Func<PdfExportOptions, PdfExportOptions>? tweak = null)
    {
        var options = new PdfExportOptions { CreationDate = FixedDate };
        return tweak is null ? options : tweak(options);
    }

    private const string Sample = """
        # Title Heading

        Intro paragraph with **bold**, *italic*, `code` and a [link](https://example.com/page).

        ## Lists

        - first item
        - second item
          - nested item
        1. one
        2. two

        - [x] done task
        - [ ] open task

        ## Table

        | Name | Qty |
        |:-----|----:|
        | Apples | 3 |
        | Pears | 12 |

        > A quoted line

        > [!WARNING]
        > Careful here

        ```csharp
        var answer = 42;
        ```

        ### Deep heading

        Footnote reference[^1].

        [^1]: The footnote text.
        """;

    [Fact]
    public void ProducesAValidPdfWithPagesAndTextThatReadsBack()
    {
        var result = PdfExporter.Export(Sample, Options());

        Assert.True(result.PageCount > 0);
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(result.Data, 0, 5), StringComparison.Ordinal);
        using var document = PdfText.Open(result.Data);
        Assert.Equal(result.PageCount, document.PageCount);

        var text = PdfText.AllText(result.Data);
        output.WriteLine(text);
        foreach (var expected in new[] { "Title Heading", "Intro paragraph", "first item", "nested item", "done task", "open task", "Apples", "Pears", "12", "A quoted line", "Careful here", "var answer = 42;", "The footnote text." })
        {
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BuildsNestedBookmarksFromHeadings()
    {
        var result = PdfExporter.Export(Sample, Options());
        using var document = PdfText.Open(result.Data);

        var top = document.Outlines.Cast<PdfOutline>().ToList();
        Assert.Single(top);
        Assert.Equal("Title Heading", top[0].Title);
        var children = top[0].Outlines.Cast<PdfOutline>().Select(o => o.Title).ToList();
        Assert.Contains("Lists", children);
        Assert.Contains("Table", children);
        var table = top[0].Outlines.Cast<PdfOutline>().First(o => o.Title == "Table");
        Assert.Equal("Deep heading", Assert.Single(table.Outlines.Cast<PdfOutline>()).Title);
    }

    [Theory]
    [InlineData(PdfPageSize.A4, 595.3, 841.9)]
    [InlineData(PdfPageSize.Letter, 612, 792)]
    public void UsesTheRequestedPageSize(PdfPageSize size, double width, double height)
    {
        var result = PdfExporter.Export("hello", Options(o => o with { PageSize = size }));
        using var document = PdfText.Open(result.Data);
        Assert.InRange(document.Pages[0].Width.Point, width - 1, width + 1);
        Assert.InRange(document.Pages[0].Height.Point, height - 1, height + 1);
    }

    [Fact]
    public void NumbersPagesWhenEnabledAndNotWhenDisabled()
    {
        var long_ = string.Join("\n\n", Enumerable.Range(1, 200).Select(i => $"Paragraph number {i} with a little filler text to take space."));
        var on = PdfExporter.Export(long_, Options());
        Assert.True(on.PageCount > 1);
        var lastPage = PdfText.PageText(PdfText.Open(on.Data).Pages[on.PageCount - 1]);
        Assert.Contains($"{on.PageCount} / {on.PageCount}", lastPage, StringComparison.Ordinal);

        var off = PdfExporter.Export(long_, Options(o => o with { PageNumbers = false }));
        Assert.DoesNotContain($"{off.PageCount} / {off.PageCount}", PdfText.AllText(off.Data), StringComparison.Ordinal);
    }

    [Fact]
    public void WritesTitleAndAuthorMetadata()
    {
        var result = PdfExporter.Export("# Heading Title\n\ntext", Options(o => o with { Author = "Ada" }));
        using var document = PdfText.Open(result.Data);
        Assert.Equal("Heading Title", document.Info.Title);
        Assert.Equal("Ada", document.Info.Author);
        Assert.Equal("Tern", document.Info.Creator);
    }

    [Fact]
    public void ExternalLinksBecomeUriAnnotations()
    {
        var result = PdfExporter.Export("see [site](https://example.com/page) now", Options());
        using var document = PdfText.Open(result.Data);
        var uris = new List<string>();
        foreach (var page in document.Pages.Cast<PdfPage>())
        {
            foreach (var annotation in page.Annotations.Cast<PdfAnnotation>())
            {
                var action = annotation.Elements.GetDictionary("/A");
                if (action?.Elements.GetString("/URI") is { Length: > 0 } uri)
                {
                    uris.Add(uri);
                }
            }
        }

        Assert.Equal(["https://example.com/page"], uris);
    }

    [Fact]
    public void EmbedsLocalImagesAndPlaceholdersRemoteOnes()
    {
        using var dir = new WorkDirectory();
        var docPath = dir.Write("doc.md", "x");
        dir.Write("pic.png", SampleImages.Png);
        var options = Options(o => o with
        {
            Preview = ExportDefaults.PreviewOptions with { DocumentPath = docPath },
        });

        var result = PdfExporter.Export("![local](pic.png)\n\n![remote](https://example.com/x.png)\n", options);

        using var document = PdfText.Open(result.Data);
        var images = 0;
        foreach (var page in document.Pages.Cast<PdfPage>())
        {
            var xobjects = page.Resources.Elements.GetDictionary("/XObject");
            images += xobjects?.Elements.Count ?? 0;
        }

        Assert.Equal(1, images);
        Assert.Contains("remote", PdfText.AllText(result.Data), StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedExportsWithAFixedDateHaveTheSamePagesAndText()
    {
        var first = PdfExporter.Export(Sample, Options());
        var second = PdfExporter.Export(Sample, Options());
        Assert.Equal(first.PageCount, second.PageCount);
        Assert.Equal(PdfText.AllText(first.Data), PdfText.AllText(second.Data));
        Assert.Equal(first.Data.Length, second.Data.Length);
    }

    [Fact]
    public void StopsAtMaxPagesAndReportsTruncation()
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 400).Select(i => $"Paragraph {i} filler filler filler filler."));
        var result = PdfExporter.Export(text, Options(o => o with { MaxPages = 2 }));
        Assert.Equal(2, result.PageCount);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void LongWordsAndWideTablesDoNotThrow()
    {
        var markdown = new string('W', 5000) + "\n\n| a | b | c | d | e | f | g | h |\n|---|---|---|---|---|---|---|---|\n"
            + "| " + string.Join(" | ", Enumerable.Repeat(new string('x', 80), 8)) + " |\n";
        var result = PdfExporter.Export(markdown, Options());
        Assert.True(result.PageCount > 0);
    }

    [Fact]
    public void RendersMermaidAndShowsUnsupportedDiagramSource()
    {
        var markdown = "```mermaid\nflowchart LR\n  A[Start] --> B{Ok?}\n  B --> C[Done]\n```\n\n```mermaid\ngantt\n  title Plan\n```\n";
        var result = PdfExporter.Export(markdown, Options());
        var text = PdfText.AllText(result.Data);
        Assert.Contains("Start", text, StringComparison.Ordinal);
        Assert.Contains("Done", text, StringComparison.Ordinal);
        Assert.Contains("gantt", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RendersMathAsUnicode()
    {
        var result = PdfExporter.Export("Euler: $\\alpha + \\beta$\n\n$$\n\\sum_{i=1}^{n} i\n$$\n", Options());
        var text = PdfText.AllText(result.Data);
        Assert.Contains("α", text, StringComparison.Ordinal);
        Assert.Contains("β", text, StringComparison.Ordinal);
        Assert.Contains("∑", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>\n\n[x](javascript:alert(1))\n\n![i](file:///c:/windows/win.ini)")]
    [InlineData("<img src=x onerror=alert(1)>\n\n<iframe src=//evil></iframe>")]
    [InlineData("[a](\\\\server\\share\\f.md) ![b](../../../etc/passwd) ![c](data:image/svg+xml;base64,PHN2Zz4=)")]
    [InlineData("\0\u0001 weird \u202E control \uD83D\uDE00 emoji \uFFFF")]
    public void HostileMarkdownStillProducesAPdfWithoutActiveContent(string markdown)
    {
        var result = PdfExporter.Export(markdown, Options());
        Assert.True(result.PageCount > 0);
        var raw = System.Text.Encoding.Latin1.GetString(result.Data);
        Assert.DoesNotContain("/JavaScript", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("/Launch", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("/EmbeddedFile", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript:", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("file://", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsInvalidOptions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfExporter.Export("x", Options(o => o with { BaseFontSize = 2 })));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfExporter.Export("x", Options(o => o with { MaxPages = 0 })));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfExporter.Export("x", Options(o => o with { Margins = PdfMargins.FromMillimetres(150) })));
        Assert.Throws<ArgumentNullException>(() => PdfExporter.Export((string)null!));
    }

    [Fact]
    public void WritesFilesAtomically()
    {
        using var dir = new WorkDirectory();
        var path = dir.Combine("out", "x.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var result = PdfExporter.ExportToFile("# T\n\nbody", path, Options());
        Assert.Equal(result.Data, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void LargeDocumentExportsWithinBudget()
    {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < 1000; i++)
        {
            builder.Append("## Section ").Append(i).Append("\n\nParagraph with **bold** text and `code` number ").Append(i).Append(".\n\n- item a\n- item b\n\n");
        }

        var lines = builder.ToString().Count(c => c == '\n');
        Assert.True(lines >= 5000);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = PdfExporter.Export(builder.ToString(), Options(o => o with { Preview = ExportDefaults.PreviewOptions }));
        stopwatch.Stop();
        output.WriteLine($"{lines} lines -> {result.PageCount} pages, {result.Data.Length / 1024} KiB in {stopwatch.ElapsedMilliseconds} ms");
        Assert.True(result.PageCount > 20);
        Assert.False(result.Truncated);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), $"took {stopwatch.Elapsed}");
        using var document = PdfText.Open(result.Data);
        Assert.Equal(1000, document.Outlines.Count);
    }
}
