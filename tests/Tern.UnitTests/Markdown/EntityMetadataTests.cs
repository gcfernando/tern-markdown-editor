using Tern.Markdown;

namespace Tern.UnitTests.Markdown;

public sealed class EntityMetadataTests
{
    [Theory]
    [InlineData(MarkdownProfile.CommonMark)]
    [InlineData(MarkdownProfile.Gfm)]
    [InlineData(MarkdownProfile.Extended)]
    public void Metadata_decodes_entities_once_and_preserves_inline_code(MarkdownProfile profile)
    {
        const string source = "# Fish &amp; &#38; &#x1F600; &amp;amp; `&amp;`\n\n[Fish &#38; chips](target.md)\n\n![Smile &#x1F600;](image.png)\n";
        var analysis = MarkdownAnalyzer.Analyze(source, profile);

        Assert.Equal("Fish & & 😀 &amp; &amp;", Assert.Single(analysis.Headings).Text);
        Assert.Equal("Fish & chips", Assert.Single(analysis.Links, l => l.Kind == LinkKind.Inline).Label);
        Assert.Equal("Smile 😀", Assert.Single(analysis.Links, l => l.Kind == LinkKind.Image).Label);
        Assert.Equal(source.IndexOf('#'), Assert.Single(analysis.Headings).Range.Start);
    }
}
