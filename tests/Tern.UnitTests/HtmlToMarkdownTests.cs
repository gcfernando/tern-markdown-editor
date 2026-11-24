using Tern.Markdown;
using Tern.Rendering.Html;

namespace Tern.UnitTests;

public sealed class HtmlToMarkdownTests
{
    [Fact]
    public void Converts_structural_and_inline_html_to_real_markdown()
    {
        var result = HtmlToMarkdown.Convert("<h2>Plans &amp; notes</h2><p>A <strong>bold</strong> <em>idea</em> and <a href='https://example.com/a?q=1&amp;x=2'>source</a>.</p>"
            + "<ul><li>First</li><li>Second<ul><li>Nested</li></ul></li></ul><pre><code>var x = `value`;\n  next();</code></pre>"
            + "<table><tr><th>Name</th><th>Value</th></tr><tr><td>Alpha</td><td>3</td></tr></table>");
        var analysis = MarkdownAnalyzer.Analyze(result, MarkdownProfile.Gfm);
        Assert.Equal("Plans & notes", Assert.Single(analysis.Headings).Text);
        Assert.Contains("**bold**", result);
        Assert.Contains("*idea*", result);
        Assert.Contains("- First", result);
        Assert.Contains("  - Nested", result);
        Assert.Equal("https://example.com/a?q=1&x=2", Assert.Single(analysis.Links).Destination);
        Assert.Single(analysis.CodeBlocks);
        Assert.Single(analysis.Tables);
    }

    [Fact]
    public void Drops_active_content_and_does_not_authorize_remote_images()
    {
        var result = HtmlToMarkdown.Convert("<script>secret()</script><svg><text>hidden</text></svg><p>"
            + "<a href='javascript:alert(1)'>safe label</a><img src='https://tracking.example/pixel' alt='picture'/>"
            + "&lt;img src=x onerror=run()&gt;</p>");
        Assert.DoesNotContain("secret", result);
        Assert.DoesNotContain("hidden", result);
        Assert.DoesNotContain("javascript", result);
        Assert.DoesNotContain("tracking", result);
        Assert.Contains("safe label", result);
        Assert.Contains("picture", result);
        Assert.Contains("\\<img", result);
        Assert.Empty(MarkdownAnalyzer.Analyze(result, MarkdownProfile.Gfm).Links);
    }

    [Fact]
    public void Code_delimiters_and_table_pipes_are_preserved()
    {
        var result = HtmlToMarkdown.Convert("<p><code>a`b</code></p><pre>```literal```\n  keep spaces</pre>"
            + "<table><tr><td>a|b</td><td>x<br>y</td></tr><tr><td>c</td><td>d</td></tr></table>", "\r\n");
        Assert.Contains("``a`b``", result);
        Assert.Contains("````\r\n```literal```", result);
        Assert.Contains("a\\|b", result);
        Assert.Contains("x<br>y", result);
        Assert.DoesNotContain("\n", result.Replace("\r\n", ""));
    }

    [Fact]
    public void Conversion_never_silently_truncates_or_flattens_merged_cells()
    {
        Assert.Throws<InvalidDataException>(() => HtmlToMarkdown.Convert(new string('a', HtmlToMarkdown.MaxInputLength + 1)));
        Assert.Throws<InvalidDataException>(() => HtmlToMarkdown.Convert("<table><tr><td colspan='2'>merged</td></tr></table>"));
        Assert.Throws<InvalidDataException>(() => HtmlToMarkdown.Convert("<table><tr><td><table><tr><td>nested</td></tr></table></td></tr></table>"));
        Assert.Throws<InvalidDataException>(() => HtmlToMarkdown.Convert(string.Concat(Enumerable.Repeat("<br>", 210000))));
    }
}
