using Tern.Markdown;

namespace Tern.UnitTests.Markdown;

public sealed class HtmlAnchorAnalysisTests
{
    [Fact]
    public void Explicit_aliases_and_raw_headings_have_exact_utf16_source_ranges()
    {
        const string text = "😀 intro\r\n\r\n<a id=\"toc\" name=\"legacy\"></a>\r\n\r\n<h1 id=\"top\">A <em>safe</em> title &amp; more</h1>\r\n\r\n## A safe title & more\r\n";
        var analysis = MarkdownAnalyzer.Analyze(text, MarkdownProfile.Gfm);
        Assert.Equal(3, analysis.ExplicitAnchors.Count);
        Assert.Equal(text.IndexOf("<a id", StringComparison.Ordinal), analysis.ExplicitAnchors["toc"].Start);
        Assert.Equal(analysis.ExplicitAnchors["toc"], analysis.ExplicitAnchors["legacy"]);
        Assert.Equal(new[] { "A safe title & more", "A safe title & more" }, analysis.Headings.Select(h => h.Text));
        Assert.Equal(new[] { "top", "a-safe-title--more-1" }, analysis.Headings.Select(h => h.Anchor));
        Assert.Equal(5, analysis.Headings[0].Line);
        Assert.Equal(text.IndexOf("<h1", StringComparison.Ordinal), analysis.Headings[0].Range.Start);
        Assert.Equal("<h1 id=\"top\">A <em>safe</em> title &amp; more</h1>", text[analysis.Headings[0].Range.Start..analysis.Headings[0].Range.End]);
    }

    [Fact]
    public void Code_comments_and_dropped_html_content_cannot_define_targets_or_headings()
    {
        const string text = "`<a id=\"inline-code\">`\n\n```html\n<a id=\"fenced\"></a>\n<h1>Fenced title</h1>\n```\n\n<!-- <a id=\"comment\"></a> -->\n\n<script><a id=\"script\"></a><h1>Hidden</h1></script>\n\n<a id=\"real\"></a>\n\n<h2>Real title</h2>";
        var analysis = MarkdownAnalyzer.Analyze(text, MarkdownProfile.Gfm);
        Assert.Equal("real", Assert.Single(analysis.ExplicitAnchors).Key);
        Assert.Equal("Real title", Assert.Single(analysis.Headings).Text);
    }

    [Fact]
    public void Duplicate_attribute_and_identifier_rules_match_inert_rendering_policy()
    {
        var longId = new string('x', 257);
        var text = $"<a id=\"first\" id=\"second\" name=\"legacy\" name=\"other\"></a>\n\n<a id=\"first\"></a>\n\n<a id=\"bad name\"></a><a id=\"{longId}\"></a><a id=\"&lt;script&gt;\"></a>";
        var analysis = MarkdownAnalyzer.Analyze(text, MarkdownProfile.Gfm);
        Assert.Equal(new[] { "first", "legacy" }, analysis.ExplicitAnchors.Keys);
        Assert.Equal(0, analysis.ExplicitAnchors["first"].Start);
    }
}
