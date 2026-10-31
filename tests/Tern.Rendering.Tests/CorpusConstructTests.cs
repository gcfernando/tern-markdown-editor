using System.Collections;
using System.Reflection;
using Tern.Markdown;
using Tern.Rendering.Preview;
using Tern.Rendering.Resources;

namespace Tern.Rendering.Tests;

public sealed class CorpusConstructTests
{
    [Theory]
    [InlineData(MarkdownProfile.CommonMark, false)]
    [InlineData(MarkdownProfile.Gfm, true)]
    [InlineData(MarkdownProfile.Extended, true)]
    public void Profile_extensions_are_explicit_and_code_is_retained(MarkdownProfile profile, bool gfm)
    {
        const string source = "# Café 😀 &amp; Unicode\n\n**Strong** and *emphasis* and ~~strike~~.\n\n| left | right |\n|:---|---:|\n| a \\| b | `x` |\n\n- [x] Checked\n- [ ] Open\n\n```python\n    print('unchanged')\n```\n";
        var document = PreviewBuilder.Build(source, new PreviewOptions { Profile = profile });
        var nodes = Walk(document.Blocks).ToArray();
        Assert.False(document.Truncated);
        Assert.Equal("Café 😀 & Unicode", Assert.Single(nodes.OfType<HeadingNode>()).PlainText);
        Assert.Equal("    print('unchanged')", Assert.Single(nodes.OfType<CodeBlockNode>()).Code);
        Assert.Equal(gfm, nodes.OfType<TableNode>().Any());
        Assert.Equal(gfm, nodes.OfType<StrikethroughNode>().Any());
        if (gfm)
        {
            var table = Assert.Single(nodes.OfType<TableNode>());
            Assert.Equal(TableAlignment.Left, table.Alignments[0]); Assert.Equal(TableAlignment.Right, table.Alignments[1]);
            Assert.Equal("a | b", string.Concat(Walk(table.Body[0].Cells[0]).OfType<TextNode>().Select(n => n.Text)));
            Assert.Contains(nodes.OfType<ListItemNode>(), n => n.Task == TaskState.Checked);
        }
    }

    [Fact]
    public void Extended_constructs_preserve_nested_content_and_distinct_models()
    {
        const string source = "---\ntitle: Corpus\n---\n\n# Same\n\n# Same\n\n> [!NOTE]\n> Keep this warning\n\nTerm\n: Definition body\n\nFootnote[^n].\n\n[^n]: Retained footnote\n\n$$\nx^2 + y^2\n$$\n\n```mermaid\nflowchart LR\n A[Start] --> B[End]\n```\n\n<details><summary>More</summary>\n\n- Nested item\n\n</details>\n";
        var document = PreviewBuilder.Build(source, new PreviewOptions()); var nodes = Walk(document.Blocks).ToArray();
        Assert.False(document.Truncated);
        Assert.Single(nodes.OfType<FrontMatterNode>()); Assert.Single(nodes.OfType<AlertNode>());
        Assert.Single(nodes.OfType<DefinitionListNode>()); Assert.Single(nodes.OfType<FootnoteSectionNode>());
        Assert.Single(nodes.OfType<MathBlockNode>()); Assert.Single(nodes.OfType<DiagramNode>()); Assert.Single(nodes.OfType<DetailsNode>());
        Assert.Equal(new[] { "same", "same-1" }, nodes.OfType<HeadingNode>().Select(n => n.Anchor));
        Assert.Contains(nodes.OfType<TextNode>(), n => n.Text.Contains("Nested item", StringComparison.Ordinal));
    }

    [Fact]
    public void Unsafe_destinations_remain_classified_without_fetching_or_executing_html()
    {
        const string source = "[blocked](javascript:alert) [public](https://example.com/) ![remote](https://example.com/image.png)\n\n<script>alert('do not execute')</script>\n\nVisible <kbd>Ctrl</kbd> text.";
        var document = PreviewBuilder.Build(source, new PreviewOptions()); var nodes = Walk(document.Blocks).ToArray();
        Assert.Contains(nodes.OfType<LinkNode>(), n => n.Resolution is Blocked);
        Assert.Contains(nodes.OfType<LinkNode>(), n => n.Resolution is ExternalLink);
        Assert.DoesNotContain(nodes.OfType<ImageNode>(), n => n.Resolution is LocalFile);
        Assert.Contains(nodes.OfType<StyledNode>(), n => n.Style == InlineStyle.Kbd);
        Assert.DoesNotContain(nodes.OfType<HtmlSafeBlockNode>(), n => n.Html.Contains("<script", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Incomplete_fence_long_line_and_unicode_keep_literal_payload()
    {
        var code = string.Concat(Enumerable.Repeat("café 😀 <literal> ", 1200));
        var document = PreviewBuilder.Build("```text\n" + code, new PreviewOptions());
        Assert.False(document.Truncated); Assert.Equal(code, Assert.IsType<CodeBlockNode>(Assert.Single(document.Blocks)).Code);
    }

    private static IEnumerable<PreviewNode> Walk(object? value)
    {
        if (value is null or string) yield break;
        if (value is PreviewNode node)
        {
            yield return node;
            foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
                foreach (var child in Walk(property.GetValue(node))) yield return child;
        }
        else if (value is IEnumerable items) foreach (var item in items) foreach (var child in Walk(item)) yield return child;
    }
}
