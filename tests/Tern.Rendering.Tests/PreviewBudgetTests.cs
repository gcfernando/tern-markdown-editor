using Tern.Rendering.Preview;

namespace Tern.Rendering.Tests;

public sealed class PreviewBudgetTests
{
    [Theory]
    [InlineData("<details>\n", "</details>\n")]
    [InlineData("<b>", "</b>")]
    public void Reconstructed_html_obeys_the_configured_depth(string opening, string closing)
    {
        var source = string.Concat(Enumerable.Repeat(opening, 100)) + "payload\n" + string.Concat(Enumerable.Repeat(closing, 100));
        var document = PreviewBuilder.Build(source, new PreviewOptions { MaxDepth = 8 });

        Assert.Equal(TruncationReason.DepthLimit, document.Truncation);
        var pending = new Stack<(PreviewNode Node, int Depth)>();
        foreach (var block in document.Blocks) pending.Push((block, 0));
        while (pending.TryPop(out var item))
        {
            Assert.True(item.Depth <= 9, $"Unexpected tree depth {item.Depth}.");
            var children = item.Node switch
            {
                DetailsNode details => details.Blocks.Cast<PreviewNode>().Concat(details.Summary),
                ParagraphNode paragraph => paragraph.Inlines,
                StrongNode strong => strong.Children,
                _ => Enumerable.Empty<PreviewNode>(),
            };
            foreach (var child in children) pending.Push((child, item.Depth + 1));
        }

        Assert.Contains(PreviewWalker.All(document).OfType<TextNode>(), node => node.Text.Contains("payload", StringComparison.Ordinal));
    }

    [Fact]
    public void Details_tags_spend_the_node_budget()
    {
        var source = string.Concat(Enumerable.Repeat("<details>\n", 100));
        var document = PreviewBuilder.Build(source, new PreviewOptions { MaxDepth = 1000, MaxNodes = 16 });
        Assert.Equal(TruncationReason.NodeBudget, document.Truncation);
        Assert.InRange(document.NodeCount, 16, 17);
    }

    [Fact]
    public void Canceled_build_does_not_parse_or_return_a_model()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => PreviewBuilder.Build("# Heading", new PreviewOptions(), null, cancellation.Token));
    }
}
