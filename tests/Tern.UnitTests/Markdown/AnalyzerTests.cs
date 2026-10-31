using Tern.Domain.Text;
using Tern.Markdown;

namespace Tern.UnitTests.Markdown;

public class AnalyzerTests
{
    private static MarkdownAnalysis Analyze(string text, MarkdownProfile profile = MarkdownProfile.Extended) =>
        MarkdownAnalyzer.Analyze(text, profile);

    [Fact]
    public void Headings_have_levels_text_lines_and_github_style_anchors()
    {
        var a = Analyze("# Title\n\ntext\n\n## Café & Bar!\n\nSetext\n======\n\n## Title\n\n## Title\n");

        Assert.Equal(["Title", "Café & Bar!", "Setext", "Title", "Title"], a.Headings.Select(h => h.Text));
        Assert.Equal([1, 2, 1, 2, 2], a.Headings.Select(h => h.Level));
        Assert.Equal(["title", "café--bar", "setext", "title-1", "title-2"], a.Headings.Select(h => h.Anchor));
        Assert.Equal([1, 5, 7, 10, 12], a.Headings.Select(h => h.Line));
    }

    [Fact]
    public void Heading_text_flattens_inline_formatting_and_code()
    {
        var a = Analyze("## Use `dotnet` **now** [here](x.md)\n");

        Assert.Equal("Use dotnet now here", a.Headings.Single().Text);
        Assert.Equal("use-dotnet-now-here", a.Headings.Single().Anchor);
    }

    [Fact]
    public void Generated_anchor_suffixes_do_not_collide_with_real_headings()
    {
        var anchors = new HeadingAnchors();
        Assert.Equal(["a", "a-1", "a-1-1", "a-2"], new[] { anchors.Next("a"), anchors.Next("a"), anchors.Next("a-1"), anchors.Next("a") });
    }

    [Fact]
    public void Headings_inside_code_blocks_and_quotes_are_classified_correctly()
    {
        var a = Analyze("```\n# not a heading\n```\n\n    # indented code\n\n> # quoted heading\n");

        Assert.Equal(["quoted heading"], a.Headings.Select(h => h.Text.ToLowerInvariant()));
        Assert.Equal(2, a.CodeBlocks.Count);
        Assert.True(a.CodeBlocks[0].IsFenced);
    }

    [Fact]
    public void Code_fence_language_is_reported()
    {
        var a = Analyze("```csharp\nvar x = 1;\n```\n\n~~~\nplain\n~~~\n");

        Assert.Equal("csharp", a.CodeBlocks[0].Language);
        Assert.Null(a.CodeBlocks[1].Language);
    }

    [Fact]
    public void Front_matter_is_detected_and_excluded_from_outline_and_word_count()
    {
        var text = "---\ntitle: Hello\ntags: [a, b]\n---\n# Real heading\n\none two three\n";
        var a = Analyze(text);

        Assert.NotNull(a.FrontMatter);
        Assert.Equal("title: Hello\ntags: [a, b]", a.FrontMatter!.Yaml);
        Assert.Equal(0, a.FrontMatter.Range.Start);
        Assert.StartsWith("---\ntitle", text[a.FrontMatter.Range.Start..a.FrontMatter.Range.End], StringComparison.Ordinal);
        Assert.Equal(["Real heading"], a.Headings.Select(h => h.Text));
        Assert.Equal(5, a.WordCount);
    }

    [Fact]
    public void Dashes_not_at_the_start_are_not_front_matter()
    {
        Assert.Null(Analyze("text\n\n---\nkey: v\n---\n").FrontMatter);
    }

    [Fact]
    public void Links_report_exact_destination_ranges()
    {
        var text = "See [one](docs/a.md#top) and ![img](pics/p.png \"t\") and <https://x.org>.\n";
        var a = Analyze(text);

        var inline = a.Links.First(l => l.Kind == LinkKind.Inline);
        Assert.Equal("docs/a.md#top", inline.Destination);
        Assert.Equal("docs/a.md#top", text[inline.Range.Start..inline.Range.End]);
        var image = a.Links.First(l => l.Kind == LinkKind.Image);
        Assert.Equal("pics/p.png", text[image.Range.Start..image.Range.End]);
        Assert.Contains(a.Links, l => l.Kind == LinkKind.Autolink && l.Destination == "https://x.org");
    }

    [Fact]
    public void Angle_bracket_destinations_exclude_the_brackets()
    {
        var text = "[a](<my file.md>)\n";
        var link = Analyze(text).Links.Single();

        Assert.Equal("my file.md", link.Destination);
        Assert.Equal("my file.md", text[link.Range.Start..link.Range.End]);
    }

    [Fact]
    public void Reference_definitions_are_links_and_use_sites_are_not_double_counted()
    {
        var text = "Go [there][r] or [r].\n\n[r]: other.md \"title\"\n";
        var a = Analyze(text);

        var def = Assert.Single(a.Links);
        Assert.Equal(LinkKind.ReferenceDefinition, def.Kind);
        Assert.Equal("other.md", text[def.Range.Start..def.Range.End]);
    }

    [Fact]
    public void Wiki_links_are_found_with_targets_and_aliases_but_not_in_code()
    {
        var text = "[[Note One]] and [[Other#Part|shown]] and `[[code]]`\n\n```\n[[fenced]]\n```\n";
        var wiki = Analyze(text).Links.Where(l => l.Kind == LinkKind.WikiLink).ToList();

        Assert.Equal(["Note One", "Other#Part"], wiki.Select(l => l.Destination));
        Assert.Equal("shown", wiki[1].Label);
        Assert.Equal("Other#Part", text[wiki[1].Range.Start..wiki[1].Range.End]);
    }

    [Fact]
    public void Tasks_are_listed_with_state_and_text()
    {
        var a = Analyze("- [ ] first\n- [x] second\n  - [X] nested\n- plain\n");

        Assert.Equal([false, true, true], a.Tasks.Select(t => t.Checked));
        Assert.Equal(["first", "second", "nested"], a.Tasks.Select(t => t.Text));
        Assert.Equal((2, 3), TaskEdits.Progress(a));
    }

    [Fact]
    public void Tasks_are_not_recognised_in_the_commonmark_profile()
    {
        Assert.Empty(Analyze("- [ ] a\n", MarkdownProfile.CommonMark).Tasks);
    }

    [Fact]
    public void Tables_and_footnotes_are_discovered()
    {
        var a = Analyze("| a | b |\n|---|---|\n| 1 | 2 |\n\nText[^1]\n\n[^1]: Note\n");

        var table = Assert.Single(a.Tables);
        Assert.Equal(2, table.Columns);
        Assert.Equal(1, table.Line);
        Assert.Equal("1", Assert.Single(a.Footnotes).Label);
    }

    [Fact]
    public void Line_index_handles_all_line_endings()
    {
        var index = new LineIndex("a\r\nb\rc\nd");

        Assert.Equal(4, index.LineCount);
        Assert.Equal([1, 1, 1, 2, 2, 3, 3, 4], Enumerable.Range(0, 8).Select(index.LineOf));
        Assert.Equal(7, index.LineStart(4));
    }

    [Fact]
    public void Offsets_stay_correct_with_surrogate_pairs_and_crlf()
    {
        var text = "😀 title\r\n\r\n# Head 😀\r\n[l](x.md)\r\n";
        var a = Analyze(text);

        Assert.Equal(3, a.Headings.Single().Line);
        var link = a.Links.Single();
        Assert.Equal("x.md", text[link.Range.Start..link.Range.End]);
    }

    [Fact]
    public void An_empty_document_analyses_cleanly()
    {
        var a = Analyze(string.Empty);

        Assert.Empty(a.Headings);
        Assert.Equal(0, a.WordCount);
    }

    [Fact]
    public void Link_targets_are_classified_and_decoded()
    {
        Assert.Equal(LinkTargetKind.External, LinkTarget.Parse("https://a.b/c").Kind);
        Assert.Equal(LinkTargetKind.External, LinkTarget.Parse("mailto:x@y.z").Kind);
        Assert.Equal(LinkTargetKind.External, LinkTarget.Parse("javascript:alert(1)").Kind);
        Assert.Equal(LinkTargetKind.Absolute, LinkTarget.Parse(@"C:\docs\a.md").Kind);
        Assert.Equal(LinkTargetKind.Absolute, LinkTarget.Parse("C:/docs/a.md").Kind);
        Assert.Equal(LinkTargetKind.RootRelative, LinkTarget.Parse("/a.md").Kind);

        var rel = LinkTarget.Parse("my%20dir/a%20b.md?x=1#Some%20Head");
        Assert.Equal(LinkTargetKind.Relative, rel.Kind);
        Assert.Equal("my dir/a b.md", rel.Path);
        Assert.Equal("Some Head", rel.Fragment);

        var anchor = LinkTarget.Parse("#intro");
        Assert.Equal(LinkTargetKind.Anchor, anchor.Kind);
        Assert.Equal("intro", anchor.Fragment);

        var wiki = LinkTarget.Parse("Note A#Sec", isWikiLink: true);
        Assert.Equal(("Note A", "Sec"), (wiki.Path, wiki.Fragment));
    }

    [Theory]
    [InlineData("a b.md", null, "a%20b.md")]
    [InlineData(@"dir\sub (1)\x.md", "My Head", "dir/sub%20%281%29/x.md#My%20Head")]
    public void Paths_are_written_back_as_safe_destinations(string path, string? fragment, string expected)
    {
        Assert.Equal(expected, LinkTarget.FormatPath(path, fragment));
        Assert.Equal(path.Replace('\\', '/'), LinkTarget.Parse(expected).Path);
    }

    [Fact]
    public void Link_rewriting_changes_only_destinations()
    {
        var text = "[a](old.md) text ![i](old.md \"keep\") `[x](old.md)` [b](other.md)\n";
        var analysis = Analyze(text);

        var edits = LinkRewriter.Rewrite(text, analysis, l => l.Destination == "old.md" ? "new/name.md" : null);
        var result = TextEdit.ApplyAll(text, edits);

        Assert.Equal("[a](new/name.md) text ![i](new/name.md \"keep\") `[x](old.md)` [b](other.md)\n", result);
    }
}
