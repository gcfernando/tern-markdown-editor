using Tern.Markdown;
using Tern.Rendering.Preview;

namespace Tern.Rendering.Tests;

public sealed class EmptyPreviewLifecycleTests
{
    [Theory]
    [InlineData(MarkdownProfile.CommonMark)]
    [InlineData(MarkdownProfile.Gfm)]
    [InlineData(MarkdownProfile.Extended)]
    public void Empty_document_has_complete_navigation_metadata_in_every_profile(MarkdownProfile profile)
    {
        var preview = PreviewBuilder.Build(string.Empty, new PreviewOptions { Profile = profile });
        Assert.Empty(preview.Blocks);
        Assert.Empty(preview.Outline);
        Assert.Empty(preview.Anchors);
        Assert.Empty(preview.DetachedAnchors);
        Assert.Equal(0, preview.NodeCount);
        Assert.Equal(0, preview.SourceLength);
        Assert.Equal(1, preview.LineCount);
        Assert.Equal(TruncationReason.None, preview.Truncation);
        Assert.Null(preview.FindAnchorSourceLine("previous-document-heading"));
    }

    [Theory]
    [InlineData(MarkdownProfile.CommonMark)]
    [InlineData(MarkdownProfile.Gfm)]
    [InlineData(MarkdownProfile.Extended)]
    public void Cancellation_is_observed_even_when_no_parser_work_is_required(MarkdownProfile profile)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PreviewBuilder.Build(string.Empty,
            new PreviewOptions { Profile = profile }, null, cancellation.Token));
    }
}
