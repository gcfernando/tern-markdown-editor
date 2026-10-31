using System.Diagnostics;
using Tern.Markdown;
using Tern.Rendering.Preview;
using Tern.Rendering.Resources;

namespace Tern.Rendering.Tests;

internal static class PreviewWalker
{
    public static IEnumerable<PreviewNode> All(IEnumerable<PreviewNode> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
            foreach (var child in Children(root))
            {
                foreach (var nested in All([child]))
                {
                    yield return nested;
                }
            }
        }
    }

    public static IEnumerable<PreviewNode> All(PreviewDocument document) => All(document.Blocks);

    private static IEnumerable<PreviewNode> Children(PreviewNode node) => node switch
    {
        HeadingNode h => h.Inlines,
        ParagraphNode p => p.Inlines,
        ListNode l => l.Items,
        ListItemNode i => i.Blocks,
        BlockQuoteNode q => q.Blocks,
        AlertNode a => a.Blocks,
        TableNode t => (t.Header is null ? [] : new PreviewNode[] { t.Header }).Concat(t.Body),
        TableRowNode r => r.Cells,
        TableCellNode c => c.Inlines,
        FootnoteSectionNode s => s.Items,
        FootnoteNode f => f.Blocks,
        DetailsNode d => d.Summary.Cast<PreviewNode>().Concat(d.Blocks),
        DefinitionListNode dl => dl.Items,
        DefinitionItemNode di => di.Terms.SelectMany(t => t).Cast<PreviewNode>().Concat(di.Definitions),
        EmphasisNode e => e.Children,
        StrongNode s => s.Children,
        StrikethroughNode s => s.Children,
        LinkNode l => l.Children,
        StyledNode s => s.Children,
        _ => [],
    };

    public static string PlainText(IEnumerable<InlineNode> inlines)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextNode t:
                    builder.Append(t.Text);
                    break;
                case CodeSpanNode c:
                    builder.Append(c.Code);
                    break;
                case EmphasisNode e:
                    builder.Append(PlainText(e.Children));
                    break;
                case StrongNode s:
                    builder.Append(PlainText(s.Children));
                    break;
                case StrikethroughNode s:
                    builder.Append(PlainText(s.Children));
                    break;
                case LinkNode l:
                    builder.Append(PlainText(l.Children));
                    break;
                case StyledNode s:
                    builder.Append(PlainText(s.Children));
                    break;
                case ImageNode i:
                    builder.Append(i.Alt);
                    break;
            }
        }

        return builder.ToString();
    }
}

public sealed class PreviewTests : IDisposable
{
    private readonly WorkDirectory _work = new();

    public void Dispose() => _work.Dispose();

    private static PreviewOptions Options(Func<PreviewOptions, PreviewOptions>? configure = null)
    {
        var options = new PreviewOptions();
        return configure is null ? options : configure(options);
    }

    private static PreviewDocument Build(string markdown, Func<PreviewOptions, PreviewOptions>? configure = null) =>
        PreviewBuilder.Build(markdown, Options(configure));

    private static T Single<T>(PreviewDocument document)
        where T : BlockNode => Assert.IsType<T>(Assert.Single(document.Blocks));

    private static void AssertSourceInvariants(PreviewDocument document, string markdown)
    {
        var length = document.SourceLength;
        foreach (var node in PreviewWalker.All(document))
        {
            var s = node.Source;
            Assert.True(s.StartLine >= 1 && s.StartLine <= s.EndLine, $"{node.GetType().Name}: lines {s.StartLine}-{s.EndLine}");
            Assert.True(s.StartOffset >= 0 && s.StartOffset <= s.EndOffset && s.EndOffset <= length, $"{node.GetType().Name}: offsets {s.StartOffset}-{s.EndOffset} of {length}");
            var startLine = 1 + markdown.AsSpan(0, s.StartOffset).Count('\n');
            Assert.True(startLine == s.StartLine, $"{node.GetType().Name}: start offset {s.StartOffset} is on line {startLine}, node says {s.StartLine}");
        }
    }

    [Fact]
    public void Headings_have_levels_text_and_github_style_anchors()
    {
        var document = Build("# Hello, World!\n\n## Second *one*\n\n### `code` title\n");

        var headings = document.Blocks.OfType<HeadingNode>().ToList();
        Assert.Equal([1, 2, 3], headings.Select(h => h.Level));
        Assert.Equal("Hello, World!", headings[0].PlainText);
        Assert.Equal("hello-world", headings[0].Anchor);
        Assert.Equal("second-one", headings[1].Anchor);
        Assert.Equal("code-title", headings[2].Anchor);
    }

    [Fact]
    public void Duplicate_headings_get_unique_anchors()
    {
        var document = Build("# Same\n\n# Same\n\n# Same\n");

        var anchors = document.Blocks.OfType<HeadingNode>().Select(h => h.Anchor).ToList();

        Assert.Equal(3, anchors.Distinct().Count());
        Assert.Equal("same", anchors[0]);
        Assert.Equal("same-1", anchors[1]);
    }

    [Fact]
    public void Setext_headings_are_recognised()
    {
        var document = Build("Title\n=====\n\nSub\n---\n");

        Assert.Equal([1, 2], document.Blocks.OfType<HeadingNode>().Select(h => h.Level));
    }

    [Fact]
    public void The_outline_lists_headings_with_their_lines()
    {
        var document = Build("intro\n\n# A\n\ntext\n\n## B\n");

        Assert.Equal([(1, "A", "a", 3), (2, "B", "b", 7)], document.Outline.Select(o => (o.Level, o.Text, o.Anchor, o.Line)));
    }

    [Fact]
    public void Inline_formatting_becomes_nested_nodes()
    {
        var paragraph = Single<ParagraphNode>(Build("a *em* **strong** ~~gone~~ `code` b"));

        Assert.Contains(paragraph.Inlines, i => i is EmphasisNode);
        Assert.Contains(paragraph.Inlines, i => i is StrongNode);
        Assert.Contains(paragraph.Inlines, i => i is StrikethroughNode);
        Assert.Equal("code", Assert.Single(paragraph.Inlines.OfType<CodeSpanNode>()).Code);
        Assert.Equal("a em strong gone code b", PreviewWalker.PlainText(paragraph.Inlines));
    }

    [Fact]
    public void Nested_emphasis_keeps_its_structure()
    {
        var paragraph = Single<ParagraphNode>(Build("***both***"));

        var outer = Assert.Single(paragraph.Inlines);
        var children = outer switch
        {
            EmphasisNode e => e.Children,
            StrongNode s => s.Children,
            _ => throw new Xunit.Sdk.XunitException("expected emphasis or strong"),
        };
        Assert.True(Assert.Single(children) is EmphasisNode or StrongNode);
    }

    [Fact]
    public void Soft_and_hard_line_breaks_are_distinguished()
    {
        var paragraph = Single<ParagraphNode>(Build("one\ntwo  \nthree\\\nfour"));

        var breaks = paragraph.Inlines.OfType<LineBreakNode>().Select(b => b.IsHard).ToList();
        Assert.Equal([false, true, true], breaks);
    }

    [Fact]
    public void Bullet_lists_nest_and_report_tightness()
    {
        var list = Single<ListNode>(Build("- one\n- two\n  - nested a\n  - nested b\n- three\n"));

        Assert.False(list.Ordered);
        Assert.True(list.Tight);
        Assert.Equal(3, list.Items.Count);
        var nested = Assert.Single(list.Items[1].Blocks.OfType<ListNode>());
        Assert.Equal(2, nested.Items.Count);
    }

    [Fact]
    public void Loose_lists_are_detected()
    {
        var list = Single<ListNode>(Build("- one\n\n- two\n"));

        Assert.False(list.Tight);
    }

    [Fact]
    public void Ordered_lists_keep_their_start_number()
    {
        var list = Single<ListNode>(Build("7. seven\n8. eight\n"));

        Assert.True(list.Ordered);
        Assert.Equal(7, list.StartNumber);
        Assert.Equal(2, list.Items.Count);
    }

    [Fact]
    public void Task_lists_report_checked_and_unchecked_items()
    {
        var list = Single<ListNode>(Build("- [ ] todo\n- [x] done\n- [X] also done\n- plain\n"));

        Assert.Equal([TaskState.Unchecked, TaskState.Checked, TaskState.Checked, TaskState.None], list.Items.Select(i => i.Task));
        var text = PreviewWalker.PlainText(Assert.IsType<ParagraphNode>(list.Items[1].Blocks[0]).Inlines);
        Assert.Equal("done", text.Trim());
    }

    [Fact]
    public void Tables_expose_alignment_header_and_body()
    {
        var table = Single<TableNode>(Build("| L | C | R | N |\n|:--|:-:|--:|---|\n| a | b | c | d |\n| e | f | g | h |\n"));

        Assert.Equal([TableAlignment.Left, TableAlignment.Center, TableAlignment.Right, TableAlignment.None], table.Alignments);
        Assert.NotNull(table.Header);
        Assert.True(table.Header.IsHeader);
        Assert.Equal(["L", "C", "R", "N"], table.Header.Cells.Select(c => PreviewWalker.PlainText(c.Inlines).Trim()));
        Assert.Equal(2, table.Body.Count);
        Assert.Equal(["a", "b", "c", "d"], table.Body[0].Cells.Select(c => PreviewWalker.PlainText(c.Inlines).Trim()));
        Assert.Equal([TableAlignment.Left, TableAlignment.Center, TableAlignment.Right, TableAlignment.None], table.Body[1].Cells.Select(c => c.Alignment));
    }

    [Fact]
    public void Table_cells_can_contain_inline_markup()
    {
        var table = Single<TableNode>(Build("| a |\n|---|\n| **b** [x](#y) |\n"));

        var cell = table.Body[0].Cells[0];
        Assert.Contains(cell.Inlines, i => i is StrongNode);
        Assert.Contains(cell.Inlines, i => i is LinkNode);
    }

    [Fact]
    public void Fenced_code_keeps_language_and_exact_text()
    {
        var code = Single<CodeBlockNode>(Build("```csharp\nvar x = 1;\n  indented\n```\n"));

        Assert.True(code.IsFenced);
        Assert.Equal("csharp", code.Language);
        Assert.Equal("var x = 1;\n  indented", code.Code.TrimEnd('\n'));
    }

    [Fact]
    public void Indented_code_is_not_fenced()
    {
        var code = Single<CodeBlockNode>(Build("    indented code\n"));

        Assert.False(code.IsFenced);
        Assert.Null(code.Language);
        Assert.Equal("indented code", code.Code.TrimEnd('\n'));
    }

    [Fact]
    public void Code_text_is_never_interpreted_as_markup_or_html()
    {
        var code = Single<CodeBlockNode>(Build("```\n<script>alert(1)</script>\n# not a heading\n```\n"));

        Assert.Contains("<script>", code.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void Thematic_breaks_and_block_quotes_are_blocks()
    {
        var document = Build("> quoted\n> more\n\n---\n");

        var quote = Assert.IsType<BlockQuoteNode>(document.Blocks[0]);
        Assert.IsType<ParagraphNode>(Assert.Single(quote.Blocks));
        Assert.IsType<ThematicBreakNode>(document.Blocks[1]);
    }

    [Theory]
    [InlineData("NOTE", AlertKind.Note)]
    [InlineData("TIP", AlertKind.Tip)]
    [InlineData("IMPORTANT", AlertKind.Important)]
    [InlineData("WARNING", AlertKind.Warning)]
    [InlineData("CAUTION", AlertKind.Caution)]
    public void Github_alerts_become_callouts(string marker, AlertKind kind)
    {
        var alert = Single<AlertNode>(Build($"> [!{marker}]\n> Body text\n"));

        Assert.Equal(kind, alert.Kind);
        var paragraphs = alert.Blocks.OfType<ParagraphNode>().Select(p => PreviewWalker.PlainText(p.Inlines)).ToList();
        Assert.Contains(paragraphs, p => p.Contains("Body text", StringComparison.Ordinal));
        Assert.DoesNotContain(paragraphs, p => p.Contains("[!", StringComparison.Ordinal));
    }

    [Fact]
    public void A_plain_quote_is_not_an_alert()
    {
        Assert.IsType<BlockQuoteNode>(Single<BlockQuoteNode>(Build("> [!UNKNOWN]\n> text\n")));
    }

    [Fact]
    public void Footnotes_produce_references_and_a_section()
    {
        var document = Build("Text[^a] and more[^b].\n\n[^a]: First note.\n[^b]: Second note.\n");

        var paragraph = Assert.IsType<ParagraphNode>(document.Blocks[0]);
        var refs = paragraph.Inlines.OfType<FootnoteRefNode>().ToList();
        Assert.Equal(["a", "b"], refs.Select(r => r.Label));
        Assert.Equal([1, 2], refs.Select(r => r.Number));
        var section = document.Blocks.OfType<FootnoteSectionNode>().Single();
        Assert.Equal(2, section.Items.Count);
        Assert.Equal(["a", "b"], section.Items.Select(f => f.Label));
        Assert.Contains("First note.", PreviewWalker.PlainText(Assert.IsType<ParagraphNode>(section.Items[0].Blocks[0]).Inlines), StringComparison.Ordinal);
    }

    [Fact]
    public void Math_blocks_and_spans_carry_the_latex_source()
    {
        var document = Build("Inline $a^2 + b^2$ here.\n\n$$\n\\frac{a}{b}\n$$\n");

        var paragraph = Assert.IsType<ParagraphNode>(document.Blocks[0]);
        Assert.Equal("a^2 + b^2", Assert.Single(paragraph.Inlines.OfType<MathSpanNode>()).Latex.Trim());
        var block = document.Blocks.OfType<MathBlockNode>().Single();
        Assert.Equal("\\frac{a}{b}", block.Latex.Trim());
    }

    [Fact]
    public void Mermaid_fences_become_diagram_blocks_and_other_fences_do_not()
    {
        var document = Build("```mermaid\ngraph TD\n  A --> B\n```\n\n```text\nplain\n```\n");

        var diagram = document.Blocks.OfType<DiagramNode>().Single();
        Assert.Equal("mermaid", diagram.DiagramType);
        Assert.Contains("A --> B", diagram.DiagramSource, StringComparison.Ordinal);
        Assert.Single(document.Blocks.OfType<CodeBlockNode>());
    }

    [Fact]
    public void Front_matter_becomes_a_properties_block()
    {
        var document = Build("---\ntitle: My Doc\ntags: [a, b]\nauthor: \"Jane\"\n---\n\n# Body\n");

        var front = Assert.IsType<FrontMatterNode>(document.Blocks[0]);
        Assert.Equal("My Doc", front.Properties.Single(p => p.Key == "title").Value);
        Assert.Contains("a", front.Properties.Single(p => p.Key == "tags").Value, StringComparison.Ordinal);
        Assert.Equal("Jane", front.Properties.Single(p => p.Key == "author").Value);
        Assert.IsType<HeadingNode>(document.Blocks[1]);
        Assert.Equal(1, front.Source.StartLine);
        Assert.Equal(5, front.Source.EndLine);
    }

    [Fact]
    public void A_leading_thematic_break_is_not_front_matter()
    {
        var document = Build("text\n\n---\n\nmore\n");

        Assert.DoesNotContain(document.Blocks, b => b is FrontMatterNode);
        Assert.Contains(document.Blocks, b => b is ThematicBreakNode);
    }

    [Fact]
    public void Raw_html_blocks_are_sanitised()
    {
        var document = Build("<div onclick=\"alert(1)\"><script>alert(2)</script><b>bold</b></div>\n");

        var html = Single<HtmlSafeBlockNode>(document);
        Assert.DoesNotContain("script", html.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", html.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<b>bold</b>", html.Html, StringComparison.Ordinal);
        StrictHtmlChecker.AssertSafe(html.Html, "html block");
    }

    [Fact]
    public void Inline_html_maps_to_styled_nodes_and_drops_everything_else()
    {
        var paragraph = Single<ParagraphNode>(Build("Press <kbd>Ctrl</kbd>, H<sub>2</sub>O, x<sup>2</sup>, <mark>hi</mark> <script>alert(1)</script> done"));

        var styles = paragraph.Inlines.OfType<StyledNode>().Select(s => s.Style).ToList();
        Assert.Equal([InlineStyle.Kbd, InlineStyle.Subscript, InlineStyle.Superscript, InlineStyle.Mark], styles);
        var text = PreviewWalker.PlainText(paragraph.Inlines);
        Assert.DoesNotContain("<script", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inline_br_becomes_a_hard_break()
    {
        var paragraph = Single<ParagraphNode>(Build("a<br>b"));

        Assert.Contains(paragraph.Inlines, i => i is LineBreakNode { IsHard: true });
    }

    [Fact]
    public void Details_blocks_are_preserved_with_their_summary()
    {
        var document = Build("<details>\n<summary>More</summary>\n\nHidden **text**\n\n</details>\n");

        var all = PreviewWalker.All(document).ToList();
        Assert.DoesNotContain(all, n => n is HtmlSafeBlockNode h && h.Html.Contains("script", StringComparison.OrdinalIgnoreCase));
        Assert.True(all.Any(n => n is DetailsNode) || all.Any(n => n is HtmlSafeBlockNode h && h.Html.Contains("<details", StringComparison.Ordinal)));
    }

    [Fact]
    public void Links_are_classified_by_the_resource_policy()
    {
        var document = Build("[web](https://example.com) [mail](mailto:a@b.c) [anchor](#top) [js](javascript:alert(1)) [file](file:///C:/x) [unc](\\\\server\\share)\n");

        var links = Assert.IsType<ParagraphNode>(document.Blocks[0]).Inlines.OfType<LinkNode>().ToList();
        Assert.IsType<ExternalLink>(links[0].Resolution);
        Assert.IsType<ExternalLink>(links[1].Resolution);
        Assert.IsType<Anchor>(links[2].Resolution);
        Assert.Equal(BlockReason.ScriptScheme, Assert.IsType<Blocked>(links[3].Resolution).Reason);
        Assert.Equal(BlockReason.FileUrl, Assert.IsType<Blocked>(links[4].Resolution).Reason);
        Assert.IsType<Blocked>(links[5].Resolution);
        Assert.Equal("javascript:alert(1)", links[3].Destination);
    }

    [Fact]
    public void Link_titles_and_text_are_kept()
    {
        var link = Assert.Single(Assert.IsType<ParagraphNode>(Build("[text *em*](https://example.com \"The Title\")").Blocks[0]).Inlines.OfType<LinkNode>());

        Assert.Equal("The Title", link.Title);
        Assert.Equal("text em", PreviewWalker.PlainText(link.Children));
    }

    [Fact]
    public void Autolinks_are_links()
    {
        var paragraph = Single<ParagraphNode>(Build("<https://example.com/a> and https://example.org/b"));

        var links = paragraph.Inlines.OfType<LinkNode>().ToList();
        Assert.Equal(2, links.Count);
        Assert.All(links, l => Assert.IsType<ExternalLink>(l.Resolution));
    }

    [Fact]
    public void Local_images_resolve_relative_to_the_document_and_remote_ones_are_blocked()
    {
        _work.Write("notes/pic.png", SampleImages.Png);
        var path = _work.Combine("notes", "doc.md");

        var document = Build("![local](pic.png) ![remote](https://example.com/x.png) ![gone](missing.png) ![escape](../../../etc/x.png)\n", o => o with { DocumentPath = path });

        var images = Assert.IsType<ParagraphNode>(document.Blocks[0]).Inlines.OfType<ImageNode>().ToList();
        var local = Assert.IsType<LocalFile>(images[0].Resolution);
        Assert.True(File.Exists(local.CanonicalPath));
        Assert.Equal("local", images[0].Alt);
        var remote = Assert.IsType<Blocked>(images[1].Resolution);
        Assert.Equal(BlockReason.RemoteResourceBlocked, remote.Reason);
        Assert.Equal("https://example.com/x.png", remote.Original);
        Assert.Equal(BlockReason.NotFound, Assert.IsType<Blocked>(images[2].Resolution).Reason);
        Assert.IsType<Blocked>(images[3].Resolution);
    }

    [Fact]
    public void Without_a_document_path_relative_images_are_refused()
    {
        var image = Assert.Single(Assert.IsType<ParagraphNode>(Build("![x](a.png)").Blocks[0]).Inlines.OfType<ImageNode>());

        Assert.Equal(BlockReason.NoBaseDirectory, Assert.IsType<Blocked>(image.Resolution).Reason);
    }

    [Fact]
    public void Allowed_roots_and_workspace_root_are_honoured()
    {
        _work.Write("ws/root.png", SampleImages.Png);
        _work.Write("ws/docs/doc.md", "x");
        var path = _work.Combine("ws", "docs", "doc.md");

        var document = Build("![a](/root.png)", o => o with { DocumentPath = path, WorkspaceRoot = _work.Combine("ws") });

        var image = Assert.Single(Assert.IsType<ParagraphNode>(document.Blocks[0]).Inlines.OfType<ImageNode>());
        Assert.IsType<LocalFile>(image.Resolution);
    }

    [Fact]
    public void Html_images_and_links_go_through_the_same_policy()
    {
        _work.Write("p/a.png", SampleImages.Png);
        var path = _work.Combine("p", "doc.md");

        var document = Build("<p><img src=\"a.png\" alt=\"ok\"><img src=\"https://evil.example/t.png\"><img src=\"../../../x.png\"><a href=\"javascript:alert(1)\">j</a></p>\n", o => o with { DocumentPath = path });

        var html = Assert.IsType<HtmlSafeBlockNode>(document.Blocks[0]).Html;
        Assert.Contains("src=\"a.png\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.example", html, StringComparison.Ordinal);
        Assert.DoesNotContain("x.png", html, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_ranges_for_lf_documents_are_exact()
    {
        const string markdown = "# Title\n\nFirst paragraph\nspans two lines.\n\n- a\n- b\n\n```\ncode\n```\n";

        var document = Build(markdown);

        var heading = document.Blocks[0];
        var paragraph = document.Blocks[1];
        var list = document.Blocks[2];
        var code = document.Blocks[3];
        Assert.Equal((1, 1), (heading.Source.StartLine, heading.Source.EndLine));
        Assert.Equal((3, 4), (paragraph.Source.StartLine, paragraph.Source.EndLine));
        Assert.Equal((6, 7), (list.Source.StartLine, list.Source.EndLine));
        Assert.Equal((9, 11), (code.Source.StartLine, code.Source.EndLine));
        Assert.Equal("# Title", markdown[heading.Source.StartOffset..heading.Source.EndOffset].TrimEnd());
        Assert.Equal("First paragraph\nspans two lines.", markdown[paragraph.Source.StartOffset..paragraph.Source.EndOffset].TrimEnd());
        AssertSourceInvariants(document, markdown);
    }

    [Fact]
    public void Source_ranges_for_crlf_documents_match_lf_documents_by_line()
    {
        const string lf = "# Title\n\nFirst paragraph\nspans two lines.\n\n- a\n- b\n\n| x | y |\n|---|---|\n| 1 | 2 |\n";
        var crlf = lf.Replace("\n", "\r\n", StringComparison.Ordinal);

        var fromLf = Build(lf);
        var fromCrlf = Build(crlf);

        Assert.Equal(fromLf.Blocks.Select(b => (b.GetType(), b.Source.StartLine, b.Source.EndLine)), fromCrlf.Blocks.Select(b => (b.GetType(), b.Source.StartLine, b.Source.EndLine)));
        Assert.Equal(fromLf.LineCount, fromCrlf.LineCount);
        var paragraph = fromCrlf.Blocks[1];
        Assert.Equal("First paragraph\r\nspans two lines.", crlf[paragraph.Source.StartOffset..paragraph.Source.EndOffset].TrimEnd());
        AssertSourceInvariants(fromCrlf, crlf);
    }

    [Fact]
    public void Source_offsets_are_utf16_so_emoji_do_not_shift_later_nodes()
    {
        const string markdown = "😀 **bold 🎉** text\n\n## 日本語 😀\n\nlast *line*\n";

        var document = Build(markdown);

        var paragraph = Assert.IsType<ParagraphNode>(document.Blocks[0]);
        var strong = Assert.Single(paragraph.Inlines.OfType<StrongNode>());
        Assert.Equal("**bold 🎉**", markdown[strong.Source.StartOffset..strong.Source.EndOffset]);
        var heading = document.Blocks[1];
        Assert.Equal("## 日本語 😀", markdown[heading.Source.StartOffset..heading.Source.EndOffset].TrimEnd());
        Assert.Equal(3, heading.Source.StartLine);
        var last = Assert.IsType<ParagraphNode>(document.Blocks[2]);
        Assert.Equal(5, last.Source.StartLine);
        Assert.Equal("*line*", markdown[last.Inlines.OfType<EmphasisNode>().Single().Source.StartOffset..last.Inlines.OfType<EmphasisNode>().Single().Source.EndOffset]);
        AssertSourceInvariants(document, markdown);
    }

    [Fact]
    public void Every_node_of_a_rich_document_has_consistent_source_positions()
    {
        const string markdown = """
            ---
            title: T
            ---

            # Heading *one*

            Paragraph with [link](https://example.com), ![img](a.png), `code`, $x^2$ and footnote[^1].

            1. first
               - nested [ ] text
            2. second

            - [x] task

            > [!NOTE]
            > Alert **body**

            | a | b |
            |:-:|--:|
            | 1 | 2 |

            ```mermaid
            graph TD
              A --> B
            ```

            $$
            E = mc^2
            $$

            <details>
            <summary>S</summary>

            inner

            </details>

            [^1]: The note.
            """;

        var document = Build(markdown);

        AssertSourceInvariants(document, markdown);
        Assert.False(document.Truncated);
    }

    [Fact]
    public void Blocks_are_ordered_and_do_not_overlap_at_the_top_level()
    {
        var document = Build("# a\n\ntext\n\n- x\n\n```\nc\n```\n\n> q\n");

        for (var i = 1; i < document.Blocks.Count; i++)
        {
            Assert.True(document.Blocks[i - 1].Source.EndOffset <= document.Blocks[i].Source.StartOffset);
        }
    }

    [Fact]
    public void FindBlockAtLine_maps_lines_to_blocks()
    {
        var document = Build("# Title\n\nPara one\nline two\n\n```\ncode\n```\n");

        Assert.IsType<HeadingNode>(document.FindBlockAtLine(1));
        Assert.IsType<ParagraphNode>(document.FindBlockAtLine(3));
        Assert.IsType<ParagraphNode>(document.FindBlockAtLine(4));
        Assert.IsType<CodeBlockNode>(document.FindBlockAtLine(7));
        Assert.Equal(0, document.FindBlockIndexAtLine(1));
        Assert.Equal(1, document.FindBlockIndexAtLine(4));
    }

    [Fact]
    public void FindBlockAtLine_snaps_blank_lines_to_the_preceding_block_and_clamps_outliers()
    {
        var document = Build("# Title\n\n\n\nPara\n");

        Assert.IsType<HeadingNode>(document.FindBlockAtLine(3));
        Assert.Null(document.FindBlockAtLine(-5));
        Assert.IsType<ParagraphNode>(document.FindBlockAtLine(9999));
    }

    [Fact]
    public void FindBlockAtLine_on_an_empty_document_returns_nothing()
    {
        var document = Build(string.Empty);

        Assert.Empty(document.Blocks);
        Assert.Null(document.FindBlockAtLine(1));
        Assert.Equal(-1, document.FindBlockIndexAtLine(1));
    }

    [Fact]
    public void LineForBlockIndex_is_the_start_line_of_the_block()
    {
        var document = Build("# A\n\ntext\n\n- x\n");

        Assert.Equal([1, 3, 5], Enumerable.Range(0, document.Blocks.Count).Select(document.LineForBlockIndex));
    }

    [Fact]
    public void FindDeepestBlockAtLine_descends_into_containers()
    {
        var document = Build("- a\n- b\n  ```\n  code\n  ```\n- c\n");

        var deepest = document.FindDeepestBlockAtLine(4);

        Assert.IsType<CodeBlockNode>(deepest);
    }

    [Fact]
    public void A_huge_document_is_truncated_by_the_node_budget_within_a_time_limit()
    {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < 60000; i++)
        {
            builder.Append("Paragraph ").Append(i).Append(" with *emphasis* and [link](https://example.com/").Append(i).Append(")\n\n");
        }

        var watch = Stopwatch.StartNew();
        var document = Build(builder.ToString(), o => o with { MaxNodes = 5000 });
        watch.Stop();

        Assert.True(document.Truncated);
        Assert.Equal(TruncationReason.NodeBudget, document.Truncation);
        Assert.InRange(document.NodeCount, 1, 5100);
        Assert.NotEmpty(document.Blocks);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Source_over_the_size_limit_is_cut_and_marked()
    {
        var markdown = string.Concat(Enumerable.Repeat("line of text that repeats\n\n", 2000));

        var document = Build(markdown, o => o with { MaxSourceLength = 10000 });

        Assert.Equal(TruncationReason.SourceTooLarge, document.Truncation);
        Assert.True(document.SourceLength <= 10000);
        Assert.NotEmpty(document.Blocks);
    }

    [Fact]
    public void An_exhausted_time_budget_degrades_instead_of_hanging()
    {
        var markdown = string.Concat(Enumerable.Repeat("some *text* with [a](b) link\n\n", 50000));

        var watch = Stopwatch.StartNew();
        var document = Build(markdown, o => o with { MaxBuildTime = TimeSpan.FromMilliseconds(1), MaxNodes = int.MaxValue });
        watch.Stop();

        Assert.True(document.Truncated);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Deeply_nested_structures_hit_the_depth_limit_without_overflowing_the_stack()
    {
        var quotes = string.Concat(Enumerable.Repeat("> ", 2000)) + "deep\n";
        var lists = string.Concat(Enumerable.Range(0, 500).Select(i => new string(' ', i * 2) + "- item\n"));
        var emphasis = string.Concat(Enumerable.Repeat("*a ", 3000)) + "x" + string.Concat(Enumerable.Repeat("*", 3000));

        foreach (var markdown in new[] { quotes, lists, emphasis })
        {
            var document = Build(markdown);
            Assert.True(document.NodeCount > 0);
        }
    }

    [Fact]
    public void Empty_and_whitespace_documents_build_empty_previews()
    {
        Assert.Empty(Build(string.Empty).Blocks);
        Assert.Empty(Build("   \n\n  \r\n").Blocks);
    }

    [Fact]
    public void Build_never_throws_on_a_seeded_fuzz_corpus()
    {
        var random = new Random(1234567);
        string[] fragments =
        [
            "# ", "## heading\n", "- ", "1. ", "* ", "> ", "> [!NOTE]\n", "```", "```mermaid\n", "~~~\n", "$$\n", "$x$", "[", "]", "(", ")", "![", "](",
            "<", ">", "<b>", "</b>", "<script>", "<details>", "</details>", "<br>", "[^1]", "[^1]: note\n", "| a | b |\n", "|---|:-:|\n", "---\n", "***", "**", "*", "_",
            "~~", "`", "\\", "&amp;", "&#x1F600;", "😀", "日本", "\r\n", "\n", "\n\n", "  ", "\t", "    ", "http://x.y", "<https://a.b>", "[x]: http://y", "- [ ] t\n",
            "- [x] t\n", "javascript:alert(1)", "\u0000", "\u2028", "[a](b)", "[a][b]", "{#id}", "term\n: def\n", "==mark==", "H~2~O", "x^2^", "<!--", "-->",
        ];

        for (var i = 0; i < 800; i++)
        {
            var count = random.Next(1, 60);
            var markdown = string.Concat(Enumerable.Range(0, count).Select(_ => fragments[random.Next(fragments.Length)]));

            var document = Build(markdown);

            AssertSourceInvariants(document, document.SourceLength == markdown.Length ? markdown : markdown[..document.SourceLength]);
            foreach (var node in PreviewWalker.All(document).OfType<HtmlSafeBlockNode>())
            {
                StrictHtmlChecker.AssertSafe(node.Html, markdown);
            }
        }
    }

    [Fact]
    public void Build_with_every_profile_succeeds_and_commonmark_ignores_extensions()
    {
        const string markdown = "| a |\n|---|\n| b |\n\n~~x~~\n";

        var common = Build(markdown, o => o with { Profile = MarkdownProfile.CommonMark });
        var extended = Build(markdown, o => o with { Profile = MarkdownProfile.Extended });

        Assert.DoesNotContain(common.Blocks, b => b is TableNode);
        Assert.Contains(extended.Blocks, b => b is TableNode);
        Assert.NotNull(Build(markdown, o => o with { Profile = MarkdownProfile.Gfm }));
    }

    [Fact]
    public void Wide_rich_content_builds_the_same_every_time()
    {
        const string markdown = "# T\n\n- a\n- b\n\n| x |\n|---|\n| y |\n\n[l](https://e.com)\n";

        Assert.Equal(Build(markdown).Blocks, Build(markdown).Blocks, new StructuralComparer());
    }

    private sealed class StructuralComparer : IEqualityComparer<BlockNode>
    {
        public bool Equals(BlockNode? x, BlockNode? y) => x?.GetType() == y?.GetType() && x?.Source == y?.Source;

        public int GetHashCode(BlockNode obj) => obj.Source.GetHashCode();
    }
}
