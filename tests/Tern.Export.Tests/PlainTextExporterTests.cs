namespace Tern.Export.Tests;

public sealed class PlainTextExporterTests
{
    private static string Export(string markdown, PlainTextExportOptions? options = null) => PlainTextExporter.Export(markdown, options);

    [Fact]
    public void UnderlinesH1AndH2AndKeepsOtherHeadings()
    {
        var text = Export("# Title\n\n## Sub\n\n### Third");
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.Equal("Title", lines[0]);
        Assert.Equal("=====", lines[1]);
        Assert.Contains("Sub", lines);
        Assert.Contains("---", lines);
        Assert.Contains("Third", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RendersListsTasksQuotesAndCode()
    {
        var text = Export("- a\n- b\n\n1. one\n2. two\n\n- [x] done\n- [ ] todo\n\n> quoted\n\n```\ncode line\n```\n");
        Assert.Contains("- a", text, StringComparison.Ordinal);
        Assert.Contains("1. one", text, StringComparison.Ordinal);
        Assert.Contains("2. two", text, StringComparison.Ordinal);
        Assert.Contains("[x] done", text, StringComparison.Ordinal);
        Assert.Contains("[ ] todo", text, StringComparison.Ordinal);
        Assert.Contains("> quoted", text, StringComparison.Ordinal);
        Assert.Contains("    code line", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AlignsTableColumns()
    {
        var text = Export("| Name | Qty |\n|---|--:|\n| Apples | 3 |\n| Fig | 12 |");
        var rows = text.Split('\n').Where(l => l.Contains('|', StringComparison.Ordinal)).Select(l => l.TrimEnd('\r')).ToList();
        Assert.True(rows.Count >= 3);
        Assert.Single(rows.Select(r => r.Length).Distinct());
        Assert.Contains(rows, r => r.Contains("Apples", StringComparison.Ordinal));
    }

    [Fact]
    public void ShowsLinkTargetsImagesMathAndFootnotes()
    {
        var text = Export("See [site](https://example.com) and ![logo](logo.png) with $\\alpha$ here[^1].\n\n[^1]: Note text.");
        Assert.Contains("site (https://example.com/)", text, StringComparison.Ordinal);
        Assert.Contains("[image: logo]", text, StringComparison.Ordinal);
        Assert.Contains("α", text, StringComparison.Ordinal);
        Assert.Contains("Note text.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ClampsHeadingUnderlinesAndRulesToTheWidthWithoutRewrappingText()
    {
        var longTitle = string.Join(' ', Enumerable.Repeat("word", 30));
        var lines = Export($"# {longTitle}\n\n{longTitle}\n\n---", new PlainTextExportOptions { Width = 40 }).Split('\n');
        Assert.Equal(longTitle, lines[0]);
        Assert.Equal(new string('=', 40), lines[1]);
        Assert.Equal(longTitle, lines[3]);
        Assert.Equal(new string('-', 40), lines[5]);
    }

    [Fact]
    public void NeverEmitsHtmlTagsFromRawHtmlOrControlCharacters()
    {
        var text = Export("<script>alert(1)</script>\n\n<b>bold</b> text\u0000\u0007");
        Assert.DoesNotContain("<script", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<b>", text, StringComparison.Ordinal);
        Assert.Contains("bold text", text, StringComparison.Ordinal);
        Assert.DoesNotContain(text, c => c is '\0' or '\a');
    }

    [Fact]
    public void ExportToFileWritesTheText()
    {
        using var dir = new WorkDirectory();
        var path = dir.Combine("out.txt");
        PlainTextExporter.ExportToFile("# Hi", path);
        Assert.Equal(Export("# Hi"), File.ReadAllText(path));
    }
}
