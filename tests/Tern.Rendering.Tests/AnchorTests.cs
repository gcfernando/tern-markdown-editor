using Tern.Rendering.Html;
using Tern.Rendering.Preview;

namespace Tern.Rendering.Tests;

public sealed class AnchorTests
{
    [Fact]
    public void Standalone_id_and_legacy_name_aliases_target_the_following_content_without_blank_blocks()
    {
        const string markdown = "<a id=\"toc\"></a>\n\n## Contents\n\n[a section](#s1)\n\n<a name=\"s1\"></a>\n\n## First section\n\nBody";
        var document = PreviewBuilder.Build(markdown, new PreviewOptions());
        Assert.Equal(4, document.Blocks.Count);
        Assert.DoesNotContain(document.Blocks, b => b is HtmlSafeBlockNode);
        Assert.Equal(3, document.FindAnchorSourceLine("toc"));
        Assert.Equal(9, document.FindAnchorSourceLine("s1"));
        Assert.Equal(markdown.IndexOf("<a name", StringComparison.Ordinal), document.Anchors["s1"].StartOffset);
        Assert.Equal(2, document.DetachedAnchors.Count);
        Assert.Equal(9, document.FindAnchorSourceLine("first-section"));
    }

    [Fact]
    public void Inline_aliases_and_unicode_identifiers_preserve_meaning_and_source_location()
    {
        const string markdown = "Before <a id=\"café-中\" name=\"legacy\"></a> after.\n\n<div id=\"top\" align=\"center\">\n\n# Heading\n\n</div>";
        var document = PreviewBuilder.Build(markdown, new PreviewOptions());
        Assert.Equal(1, document.FindAnchorSourceLine("café-中"));
        Assert.Equal(1, document.FindAnchorSourceLine("legacy"));
        Assert.Equal(5, document.FindAnchorSourceLine("top"));
        var paragraph = Assert.IsType<ParagraphNode>(document.Blocks[0]);
        Assert.Equal("Before  after.", PreviewWalker.PlainText(paragraph.Inlines));
        Assert.Equal(BlockAlignment.Center, Assert.Single(document.Blocks.OfType<HeadingNode>()).Alignment);
    }

    [Fact]
    public void Raw_html_headings_have_outline_entries_and_generated_or_explicit_fragment_targets()
    {
        const string markdown = "<h1 align=\"center\">A <em>safe</em> title</h1>\n\n<h2 id=\"how-to-play\">Play</h2>";
        var document = PreviewBuilder.Build(markdown, new PreviewOptions());
        Assert.Equal(new[] { "A safe title", "Play" }, document.Outline.Select(h => h.Text));
        Assert.Equal(new[] { "a-safe-title", "how-to-play" }, document.Outline.Select(h => h.Anchor));
        Assert.Equal(1, document.FindAnchorSourceLine("a-safe-title"));
        Assert.Equal(3, document.FindAnchorSourceLine("how-to-play"));
        Assert.Contains("id=\"a-safe-title\"", Assert.IsType<HtmlSafeBlockNode>(document.Blocks[0]).Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_aliases_use_the_first_target_and_code_or_script_cannot_define_targets()
    {
        const string markdown = "<a id=\"same\"></a>\n\n## First\n\n<a id=\"same\"></a>\n\n## Second\n\n```html\n<a id=\"code\"></a>\n```\n\n<script><a id=\"script\"></a></script>";
        var document = PreviewBuilder.Build(markdown, new PreviewOptions());
        Assert.Equal(3, document.FindAnchorSourceLine("same"));
        Assert.False(document.Anchors.ContainsKey("code"));
        Assert.False(document.Anchors.ContainsKey("script"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("line\nbreak")]
    [InlineData("\u0001control")]
    [InlineData("&lt;script&gt;")]
    public void Invalid_html_identifiers_are_rejected(string id)
    {
        var html = HtmlSanitizer.Default.Sanitize($"<a id=\"{id}\" name=\"{id}\" onclick=\"alert(1)\"></a>");
        Assert.DoesNotContain(" id=", html, StringComparison.Ordinal);
        Assert.DoesNotContain(" name=", html, StringComparison.Ordinal);
        Assert.DoesNotContain("onclick", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Anchor_length_and_collection_limits_are_enforced()
    {
        var valid = new string('a', 256);
        var tooLong = new string('b', 257);
        var document = PreviewBuilder.Build($"<a id=\"{valid}\"></a><a id=\"{tooLong}\"></a>\n\n# Heading", new PreviewOptions());
        Assert.True(document.Anchors.ContainsKey(valid));
        Assert.False(document.Anchors.ContainsKey(tooLong));
        var many = string.Concat(Enumerable.Range(0, 10010).Select(i => $"<a id=\"a{i}\"></a>\n\n"));
        var bounded = PreviewBuilder.Build(many, new PreviewOptions { MaxBuildTime = TimeSpan.FromSeconds(20) });
        Assert.Equal(10000, bounded.Anchors.Count);
        Assert.Empty(bounded.Blocks);
    }
}
