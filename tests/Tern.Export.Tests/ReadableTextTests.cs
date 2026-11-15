namespace Tern.Export.Tests;

public sealed class ReadableTextTests
{
    [Fact]
    public void Wide_tables_become_labelled_entries_and_keep_all_cells_and_links()
    {
        var detail = string.Join(' ', Enumerable.Repeat("Important information about the selected resource.", 4));
        var text = PlainTextExporter.Export($"| Resource | Details | Open |\n|---|---|---|\n| First resource | {detail} | [Read](https://example.com/a/long/path) |\n| Second resource | A second description. | [Read again](https://example.com/a/long/path) |");
        Assert.Contains("Resource: First resource", text);
        Assert.Contains("Resource: Second resource", text);
        Assert.Contains("Details: Important", text);
        Assert.Contains("Open: Read [L1]", text);
        Assert.Contains("Read again [L1]", text);
        Assert.Single(text.Split('\n').Where(l => l == "[L1] https://example.com/a/long/path"));
        Assert.DoesNotContain(text.Split('\n'), l => l.Contains('|'));
        Assert.All(text.Split('\n').Where(l => !l.StartsWith("[L")), l => Assert.True(l.Length <= 80, l));
    }

    [Fact]
    public void Wrapping_preserves_nested_lists_code_and_unicode_without_leading_blank_lines()
    {
        var text = PlainTextExporter.Export("   \n\n# Title\n\n- " + string.Join(' ', Enumerable.Repeat("Nested readable paragraph.", 12)) + "\n  - child\n\n```python\n    print('日本語 😀')\n```", new PlainTextExportOptions { Width = 40 });
        Assert.StartsWith("Title\n=====", text);
        Assert.Contains("  - child", text);
        Assert.Contains("        print('日本語 😀')", text);
        Assert.DoesNotContain('\uFFFD', text);
        Assert.All(text.Split('\n').Where(l => !l.StartsWith("    ")), l => Assert.True(l.Length <= 40, l));
        Assert.StartsWith("    first", PlainTextExporter.Export("```\nfirst\n```"));
    }

    [Fact]
    public void Legacy_inline_layout_is_explicit_and_short_tables_remain_aligned()
    {
        Assert.Contains("site (https://example.com/)", PlainTextExporter.Export("[site](https://example.com)", new PlainTextExportOptions { ReadableLayout = false }));
        var text = PlainTextExporter.Export("| A | B |\n|---|---|\n| short | value |");
        Assert.Equal(3, text.Split('\n').Count(l => l.StartsWith('|')));
    }
}
