using Tern.App.Editing;
using Tern.Markdown;

namespace Tern.App.Tests;

public sealed class SpellingTextTests
{
    [Fact]
    public void Masks_protected_markdown_without_changing_prose_offsets()
    {
        const string text = "---\ntitle: mispelledmetadata\n---\nProse mispelled and `mispelledcode`.\n"
            + "[mispelledlabel](https://mispelled.example)\n```cs\nmispelledfence\n```\n\n    mispelledindent\n";
        var analysis = MarkdownAnalyzer.Analyze(text, MarkdownProfile.Extended);
        var masked = SpellingText.Prepare(text, analysis, 0, text.Length);
        Assert.Equal(text.Length, masked.Length);
        Assert.Contains("Prose mispelled", masked);
        Assert.Contains("mispelledlabel", masked);
        foreach (var value in new[] { "mispelledmetadata", "mispelledcode", "mispelled.example", "mispelledfence", "mispelledindent" })
            Assert.DoesNotContain(value, masked);
        for (var index = 0; index < text.Length; index++)
            if (text[index] is '\r' or '\n') Assert.Equal(text[index], masked[index]);
        var start = text.IndexOf("Prose", StringComparison.Ordinal);
        Assert.Equal(masked[start..], SpellingText.Prepare(text, analysis, start, text.Length - start));
    }
}
