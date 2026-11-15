using System.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Tern.Rendering.Preview;

namespace Tern.Export.Tests;

public sealed class DocumentFidelityTests
{
    [Fact]
    public void Code_symbols_remain_searchable_and_wrapping_never_splits_a_scalar()
    {
        var source = "```python\n# 😀 code symbol\nprint('café')\n" + new string('x', 200) + "😀\n```";
        var result = PdfExporter.Export(source);
        var text = PdfText.AllText(result.Data);
        Assert.Contains("café", text);
        if (!result.Warnings.Any(w => w.Contains("U+1F600", StringComparison.Ordinal))) Assert.Contains("😀", text);
        Assert.DoesNotContain("\uFFFD", text);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Installed_international_and_symbol_fonts_preserve_non_Latin_examples()
    {
        var result = PdfExporter.Export("# International\n\n日本語 ①②③\n\n```sql\n-- 日本語 ①②③\nSELECT 1;\n```");
        var text = PdfText.AllText(result.Data);
        foreach (var character in "日本語①②③")
            if (!result.Warnings.Any(w => w.Contains($"U+{(int)character:X}", StringComparison.Ordinal))) Assert.Contains(character.ToString(), text);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Stylesheet_CSP_hash_matches_browser_normalized_line_endings()
    {
        var html = HtmlExporter.Export("# Styled document").Html;
        var style = Regex.Match(html, "<style>(.*?)</style>", RegexOptions.Singleline).Groups[1].Value;
        Assert.NotEmpty(style);
        var browserText = style.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var browserHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(browserText)));
        Assert.Contains("style-src 'sha256-" + browserHash + "'", html);
        Assert.DoesNotContain("unsafe-inline", html);
    }
    [Fact]
    public void Unicode_glyphs_are_not_replaced_and_supplementary_maps_remain_searchable()
    {
        var result = PdfExporter.Export("# 😀 Delivery forecast\n\nRésumé — α + β → result.\n\n**Important** and `code`.",
            new PdfExportOptions { IncludeFrontMatter = false });
        var text = PdfText.AllText(result.Data);
        Assert.Contains("Delivery forecast", text);
        Assert.Contains("Résumé", text);
        if (result.Warnings.Any(w => w.Contains("U+1F600", StringComparison.Ordinal)))
            Assert.Contains("Install a font", string.Join("\n", result.Warnings));
        else
            Assert.Contains("😀", text);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Html_retains_document_structure_and_print_styles_wrap_code_and_repeat_table_headers()
    {
        const string source = "# 🌈 Workbook\n\n## Lesson\n\n**Bold** and *italic*.\n\n- one\n- two\n\n| Name | Value |\n|---|---|\n| A | B |\n\n```python\nif True:\n    print('kept')\n```\n\nLast paragraph.";
        var html = HtmlExporter.Export(source).Html;
        Assert.Contains("🌈 Workbook", html);
        Assert.Contains("<strong>Bold</strong>", html);
        Assert.Contains("<em>italic</em>", html);
        var code = Regex.Match(html, "<pre><code[^>]*>(.*?)</code></pre>", RegexOptions.Singleline).Groups[1].Value;
        Assert.Contains("    print('kept')", System.Net.WebUtility.HtmlDecode(Regex.Replace(code, "<[^>]+>", "")));
        Assert.Contains("syntax-keyword", code);
        Assert.Contains("<table>", html);
        Assert.Contains("<ul>", html);
        Assert.Contains("thead{display:table-header-group}", html);
        Assert.Contains("white-space:pre-wrap", html);
        Assert.Contains("Last paragraph.", html);
        StrictExportedHtml.AssertSafe(html, "Export formatting must not enable active content");
    }
}
