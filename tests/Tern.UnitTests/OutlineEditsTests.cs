using Tern.Domain.Text;
using Tern.Markdown;

namespace Tern.UnitTests;

public sealed class OutlineEditsTests
{
    [Theory]
    [InlineData("\n", false)]
    [InlineData("\r\n", false)]
    [InlineData("\r", false)]
    [InlineData("\n", true)]
    [InlineData("\r\n", true)]
    public void Moving_a_subtree_preserves_line_terminators_and_last_line_state(string newline, bool terminated)
    {
        var first = "## First" + newline + "### Child" + newline + "one";
        var second = "## Second" + newline + "two";
        var text = first + newline + second + (terminated ? newline : "");
        var analysis = MarkdownAnalyzer.Analyze(text, MarkdownProfile.Extended);
        var edit = OutlineEdits.Move(text, analysis, 1, true, newline);
        Assert.Null(edit.Refusal);
        var actual = TextEdit.ApplyAll(text, edit.Edits);
        Assert.Equal(second + newline + first + (terminated ? newline : ""), actual);
        Assert.Equal(text, TextEdit.ApplyAll(actual, OutlineEdits.Move(actual,
            MarkdownAnalyzer.Analyze(actual, MarkdownProfile.Extended), 1, true, newline).Edits));
    }

    [Theory]
    [InlineData("Title *inline*\n===\nbody", 1, "## Title *inline*\nbody")]
    [InlineData("<h2>Title</h2>\nbody", 1, "<h3>Title</h3>\nbody")]
    [InlineData("## Parent\n### Child\nbody", -1, "# Parent\n## Child\nbody")]
    public void Shifting_changes_heading_syntax_without_losing_inline_markdown_or_subheadings(string text, int delta, string expected)
    {
        var change = OutlineEdits.Shift(text, MarkdownAnalyzer.Analyze(text, MarkdownProfile.Extended), 1, delta);
        Assert.Null(change.Refusal);
        Assert.Equal(expected, TextEdit.ApplyAll(text, change.Edits));
    }

    [Theory]
    [InlineData("# Top\nbody", -1)]
    [InlineData("## Parent\n###### Deep\nbody", 1)]
    public void Out_of_range_subtree_shifts_are_refused_atomically(string text, int delta)
    {
        var change = OutlineEdits.Shift(text, MarkdownAnalyzer.Analyze(text, MarkdownProfile.Extended), 1, delta);
        Assert.NotNull(change.Refusal);
        Assert.Empty(change.Edits);
    }
}
