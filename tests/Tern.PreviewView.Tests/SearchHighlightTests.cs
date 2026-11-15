using Avalonia;
using Avalonia.Headless.XUnit;

namespace Tern.PreviewView.Tests;

public sealed class SearchHighlightTests
{
    [AvaloniaFact]
    public void All_occurrences_and_active_occurrence_have_distinct_source_projections()
    {
        const string source = "hot **hot** `hot` [hot](target.md)";
        var (window, view) = Harness.Show(Harness.Build(source));
        try
        {
            view.HighlightSearchMatches([(0, 3), (6, 3), (13, 3), (19, 3)], 2, source);
            Harness.Pump(window);
            var block = Assert.Single(Harness.All<RichTextBlock>(view));
            Assert.Equal(new[] { (0, 3), (4, 3), (8, 3), (12, 3) }, block.SearchRanges());
            Assert.Equal((8, 3), Assert.Single(block.SearchRanges(activeOnly: true)));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Repeated_words_after_decoded_entities_project_to_the_correct_occurrence()
    {
        const string source = "hot &amp; hot &amp; hot";
        var (window, view) = Harness.Show(Harness.Build(source));
        try
        {
            view.HighlightSearchMatches([(0, 3), (10, 3), (20, 3)], 2, source);
            Harness.Pump(window);
            var block = Assert.Single(Harness.All<RichTextBlock>(view));
            Assert.Equal("hot & hot & hot", block.DisplayText);
            Assert.Equal(new[] { (0, 3), (6, 3), (12, 3) }, block.SearchRanges());
            Assert.Equal((12, 3), Assert.Single(block.SearchRanges(activeOnly: true)));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Navigation_reveals_exact_glyphs_deep_in_one_wrapped_paragraph()
    {
        var source = string.Join(' ', Enumerable.Repeat("Long wrapped paragraph content.", 160)) + " needle";
        var (window, view) = Harness.Show(Harness.Build(source), width: 500, height: 320);
        try
        {
            view.ReduceMotion = true;
            view.HighlightSearchMatch(source.LastIndexOf("needle", StringComparison.Ordinal), 6, source);
            Assert.True(view.RevealSearchMatch(1));
            Harness.Pump(window);
            var block = Assert.Single(Harness.All<RichTextBlock>(view));
            var range = Assert.Single(block.SearchRanges(activeOnly: true));
            var rect = block.TextLayout.HitTestTextRange(range.Start, range.Length).First();
            var origin = block.TranslatePoint(default, view.Scroller)!.Value;
            Assert.InRange(rect.Top + origin.Y + block.Padding.Top, -1, view.Scroller.Viewport.Height);
            Assert.InRange(rect.Bottom + origin.Y + block.Padding.Top, 0, view.Scroller.Viewport.Height + 1);
            Assert.True(view.Scroller.Offset.Y > 500);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("```text\n", "\n```", 2)]
    [InlineData("<details>\n<summary>Open</summary>\n\n", "\n\n</details>", 4)]
    public void Navigation_reveals_a_match_inside_nested_scroll_or_collapsed_details(string prefix, string suffix, int firstLine)
    {
        var content = string.Join("\n", Enumerable.Range(1, 120).Select(i => $"Line {i} with content"));
        var source = prefix + content + " needle" + suffix;
        var (window, view) = Harness.Show(Harness.Build(source), width: 600, height: 350);
        try
        {
            view.HighlightSearchMatch(source.LastIndexOf("needle", StringComparison.Ordinal), 6, source);
            Assert.True(view.RevealSearchMatch(firstLine + 119));
            Harness.Pump(window);
            var block = Assert.Single(Harness.All<RichTextBlock>(view), b => b.SearchRanges(activeOnly: true).Count > 0);
            var range = Assert.Single(block.SearchRanges(activeOnly: true));
            var rect = block.TextLayout.HitTestTextRange(range.Start, range.Length).First();
            var origin = block.TranslatePoint(default, view.Scroller)!.Value;
            Assert.InRange(rect.Top + origin.Y + block.Padding.Top, -1, view.Scroller.Viewport.Height);
            Assert.InRange(rect.Bottom + origin.Y + block.Padding.Top, 0, view.Scroller.Viewport.Height + 1);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("hot hot hot", 4, "hot hot hot", 4)]
    [InlineData("A **hot** word", 4, "A hot word", 2)]
    [InlineData("A `hot` word", 3, "A hot word", 2)]
    [InlineData("[hot](other.md)", 1, "hot", 0)]
    [InlineData("| Name |\n| --- |\n| hot |", 19, "hot", 0)]
    [InlineData("```text\nhot hot\n```", 12, "hot hot", 4)]
    [InlineData("```text\r\nhot hot\r\n```", 13, "hot hot", 4)]
    public void Selected_source_match_highlights_only_its_rendered_characters(string source, int offset, string rendered, int expected)
    {
        var (window, view) = Harness.Show(Harness.Build(source));
        try
        {
            Assert.Equal("hot", source.Substring(offset, 3));
            view.ReduceMotion = true;
            view.HighlightSearchMatch(offset, 3, source);
            Harness.Pump(window);
            var block = Assert.Single(Harness.All<RichTextBlock>(view), b => b.DisplayText == rendered);
            Assert.Equal((expected, 3), Assert.Single(block.SearchRanges()));
            var rectangles = block.TextLayout.HitTestTextRange(expected, 3);
            Assert.NotEmpty(rectangles);
            view.HighlightSearchMatch(0, 0, string.Empty);
            Assert.All(Harness.All<RichTextBlock>(view), b => Assert.Empty(b.SearchRanges()));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Highlight_survives_virtualization_and_reused_blocks_after_edits_above()
    {
        var source = string.Join("\n\n", Enumerable.Range(0, 200).Select(i => $"Paragraph {i} with hot content."));
        var (window, view) = Harness.Show(Harness.Build(source));
        try
        {
            var start = source.LastIndexOf("hot", StringComparison.Ordinal);
            view.HighlightSearchMatch(start, 3, source);
            view.ScrollToSourceLine(399);
            Harness.Pump(window);
            Assert.Single(Harness.All<RichTextBlock>(view).SelectMany(b => b.SearchRanges()));
            source = "New paragraph.\n\n" + source;
            view.Document = Harness.Build(source);
            view.HighlightSearchMatch(start + 16, 3, source);
            view.ScrollToSourceLine(401);
            Harness.Pump(window);
            Assert.Single(Harness.All<RichTextBlock>(view).SelectMany(b => b.SearchRanges()));
        }
        finally { window.Close(); }
    }
}
