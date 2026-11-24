using Tern.Rendering.Preview;

namespace Tern.Desktop.Services;

internal static class RemoteImageCount
{
    public static int Count(PreviewDocument document) => document.Blocks.Sum(Block);
    private static bool Remote(string destination) => Uri.TryCreate(destination, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
    private static int Inline(InlineNode node) => node switch
    {
        ImageNode image => Remote(image.Destination) ? 1 : 0,
        LinkNode link => link.Children.Sum(Inline),
        EmphasisNode span => span.Children.Sum(Inline),
        StrongNode span => span.Children.Sum(Inline),
        StrikethroughNode span => span.Children.Sum(Inline),
        StyledNode span => span.Children.Sum(Inline),
        _ => 0,
    };
    private static int Block(BlockNode node) => node switch
    {
        ParagraphNode paragraph => paragraph.Inlines.Sum(Inline),
        HeadingNode heading => heading.Inlines.Sum(Inline),
        BlockQuoteNode quote => quote.Blocks.Sum(Block),
        AlertNode alert => alert.Blocks.Sum(Block),
        ListNode list => list.Items.Sum(item => item.Blocks.Sum(Block)),
        DetailsNode details => details.Summary.Sum(Inline) + details.Blocks.Sum(Block),
        TableNode table => (table.Header?.Cells.Sum(cell => cell.Inlines.Sum(Inline)) ?? 0)
            + table.Body.Sum(row => row.Cells.Sum(cell => cell.Inlines.Sum(Inline))),
        FootnoteSectionNode footnotes => footnotes.Items.Sum(item => item.Blocks.Sum(Block)),
        DefinitionListNode list => list.Items.Sum(item => item.Terms.Sum(term => term.Sum(Inline)) + item.Definitions.Sum(Block)),
        HtmlSafeBlockNode html => html.Images.Keys.Count(Remote),
        _ => 0,
    };
}
