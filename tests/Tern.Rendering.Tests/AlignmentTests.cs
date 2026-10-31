using Tern.Rendering.Preview;
using Tern.Rendering.Resources;

namespace Tern.Rendering.Tests;

public sealed class AlignmentTests
{
    [Fact]
    public void A_centered_readme_header_preserves_markdown_and_source_positions()
    {
        const string markdown = "<div align=\"center\" markdown=\"1\">\n\n# Python Reference\n\n*Contracts and examples.*\n\n**_By an author_**\n\n![Badge](https://example.test/badge.png)\n\n</div>\n\n## Ordinary section\n";
        var document = PreviewBuilder.Build(markdown, new PreviewOptions());
        var headings = document.Blocks.OfType<HeadingNode>().ToList();
        Assert.Equal(BlockAlignment.Center, headings[0].Alignment);
        Assert.Equal("Python Reference", headings[0].PlainText);
        Assert.Equal(markdown.IndexOf("# Python", StringComparison.Ordinal), headings[0].Source.StartOffset);
        Assert.Equal(BlockAlignment.None, headings[1].Alignment);
        var paragraphs = document.Blocks.OfType<ParagraphNode>().ToList();
        Assert.Equal(3, paragraphs.Count);
        Assert.All(paragraphs, p => Assert.Equal(BlockAlignment.Center, p.Alignment));
        Assert.IsType<EmphasisNode>(paragraphs[0].Inlines[0]);
        var badge = Assert.IsType<ImageNode>(paragraphs[2].Inlines[0]);
        Assert.IsType<Blocked>(badge.Resolution);
        Assert.Equal(TruncationReason.None, document.Truncation);
    }

    [Fact]
    public void Nested_containers_override_and_restore_the_parent_alignment()
    {
        const string markdown = "<div align=\"center\">\n\nOuter\n\n<div align=\"right\">\n\nInner\n\n</div>\n\nOuter again\n\n<div>\n\nInherited\n\n</div>\n\n</div>\n\nOutside\n";
        var paragraphs = PreviewBuilder.Build(markdown, new PreviewOptions()).Blocks.OfType<ParagraphNode>().ToList();
        Assert.Equal(new[] { BlockAlignment.Center, BlockAlignment.Right, BlockAlignment.Center, BlockAlignment.Center, BlockAlignment.None },
            paragraphs.Select(p => p.Alignment));
    }

    [Fact]
    public void Quotes_lists_and_details_inherit_alignment_without_losing_their_semantics()
    {
        const string markdown = "<div align=\"right\">\n\n> Quoted\n\n- Listed\n\n<details>\n<summary>More</summary>\n\n## Nested heading\n\nBody\n\n</details>\n\n</div>\n\nOutside\n";
        var document = PreviewBuilder.Build(markdown, new PreviewOptions());
        var quote = Assert.Single(document.Blocks.OfType<BlockQuoteNode>());
        Assert.Equal(BlockAlignment.Right, quote.Alignment);
        Assert.Equal(BlockAlignment.Right, Assert.Single(quote.Blocks).Alignment);
        var list = Assert.Single(document.Blocks.OfType<ListNode>());
        Assert.Equal(BlockAlignment.Right, list.Alignment);
        Assert.Equal(BlockAlignment.Right, Assert.Single(Assert.Single(list.Items).Blocks).Alignment);
        var details = Assert.Single(document.Blocks.OfType<DetailsNode>());
        Assert.Equal(BlockAlignment.Right, details.Alignment);
        Assert.All(details.Blocks, b => Assert.Equal(BlockAlignment.Right, b.Alignment));
        Assert.Contains(details.Blocks, b => b is HeadingNode { PlainText: "Nested heading" });
        Assert.Equal(BlockAlignment.None, document.Blocks[^1].Alignment);
    }

    [Theory]
    [InlineData("left", BlockAlignment.Left)]
    [InlineData("CENTER", BlockAlignment.Center)]
    [InlineData(" right ", BlockAlignment.Right)]
    [InlineData("justify", BlockAlignment.None)]
    [InlineData("center;color:red", BlockAlignment.None)]
    [InlineData("inherit", BlockAlignment.None)]
    public void Only_explicit_alignment_values_are_accepted(string value, BlockAlignment expected)
    {
        var markdown = $"<div align=\"{value}\" style=\"position:fixed\" onclick=\"alert(1)\">\n\n# Heading\n\n</div>";
        var heading = Assert.Single(PreviewBuilder.Build(markdown, new PreviewOptions()).Blocks.OfType<HeadingNode>());
        Assert.Equal(expected, heading.Alignment);
    }

    [Fact]
    public void Raw_html_headings_keep_safe_alignment_and_strip_active_attributes()
    {
        const string markdown = "<h1 align=\"center\" style=\"color:red\" onclick=\"alert(1)\">Title<script>alert(2)</script></h1>";
        var block = Assert.Single(PreviewBuilder.Build(markdown, new PreviewOptions()).Blocks.OfType<HtmlSafeBlockNode>());
        Assert.Equal(BlockAlignment.Center, block.Alignment);
        Assert.Contains("align=\"center\"", block.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("style", block.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("onclick", block.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("script", block.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("alert", block.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Container_tracking_respects_depth_and_node_budgets()
    {
        var markdown = string.Concat(Enumerable.Repeat("<div align=\"center\">\n\n", 100)) + "# Heading\n\n"
            + string.Concat(Enumerable.Repeat("</div>\n\n", 100));
        var depthLimited = PreviewBuilder.Build(markdown, new PreviewOptions { MaxDepth = 2 });
        Assert.Equal(TruncationReason.DepthLimit, depthLimited.Truncation);
        Assert.Single(depthLimited.Blocks.OfType<HeadingNode>());
        var nodeLimited = PreviewBuilder.Build(markdown, new PreviewOptions { MaxNodes = 10 });
        Assert.Equal(TruncationReason.NodeBudget, nodeLimited.Truncation);
        Assert.True(nodeLimited.NodeCount <= 11);
    }

    [Fact]
    public void Fenced_container_text_cannot_change_following_alignment()
    {
        const string markdown = "```html\n<div align=\"center\">\n```\n\n# Normal\n";
        var document = PreviewBuilder.Build(markdown, new PreviewOptions());
        Assert.Equal(BlockAlignment.None, Assert.Single(document.Blocks.OfType<HeadingNode>()).Alignment);
        Assert.Single(document.Blocks.OfType<CodeBlockNode>());
    }
}
