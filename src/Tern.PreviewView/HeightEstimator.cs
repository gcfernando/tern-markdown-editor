using Tern.Rendering.Preview;

namespace Tern.PreviewView;

/// <summary>Cheap height guesses for blocks that have not been laid out yet, so the scrollbar is roughly right.</summary>
internal static class HeightEstimator
{
    private static readonly double[] HeadingScales = [2, 1.5, 1.25, 1.1, 1, 0.9];

    public static double Estimate(BlockNode node, double width, double font)
    {
        width = Math.Max(120, width);
        var gap = font * 0.9;
        return node switch
        {
            HeadingNode heading => (font * HeadingScales[Math.Clamp(heading.Level, 1, 6) - 1] * 1.4) + (font * 1.65) + (heading.Level <= 2 ? (font * 0.3) + 1 : 0),
            ThematicBreakNode => 1 + (font * 1.2),
            _ => Content(node, width, font) + gap,
        };
    }

    private static double Content(BlockNode node, double width, double font)
    {
        switch (node)
        {
            case ParagraphNode paragraph:
                return Lines(Length(paragraph.Inlines), width, font) * font * 1.4;
            case ListNode list:
                {
                    double total = 0;
                    foreach (var item in list.Items)
                    {
                        total += Blocks(item.Blocks, width - (font * 1.7), font) + 4;
                    }

                    return total;
                }

            case BlockQuoteNode quote:
                return Blocks(quote.Blocks, width - 20, font) + 4;
            case AlertNode alert:
                return Blocks(alert.Blocks, width - 24, font) + font * 1.6 + 20;
            case CodeBlockNode code:
                return (Math.Min(CountLines(code.Code), 1500) * font * 1.25) + 48;
            case TableNode table:
                return ((table.Body.Count + (table.Header is null ? 0 : 1)) * ((font * 1.4) + 14)) + 2;
            case FootnoteSectionNode section:
                return section.Items.Sum(i => Blocks(i.Blocks, width - 30, font) + 6) + 40;
            case MathBlockNode:
                return font * 3.2;
            case DiagramNode diagram:
                return Math.Min(CountLines(diagram.DiagramSource) * 34, 600) + 60;
            case HtmlSafeBlockNode html:
                return Math.Max(font * 1.4, Lines(html.Html.Length / 2, width, font) * font * 1.4);
            case DetailsNode details:
                return (font * 1.4) + (details.IsOpen ? Blocks(details.Blocks, width - 18, font) : 0);
            case FrontMatterNode front:
                return Math.Max(1, front.Properties.Count) * font * 1.6 + 26;
            case DefinitionListNode definitions:
                return definitions.Items.Sum(i => (i.Terms.Count * font * 1.4) + Blocks(i.Definitions, width - 24, font) + 10);
            default:
                return font * 2;
        }
    }

    private static double Blocks(IReadOnlyList<BlockNode> blocks, double width, double font)
    {
        double total = 0;
        foreach (var block in blocks)
        {
            total += Estimate(block, width, font);
        }

        return total;
    }

    private static double Lines(int characters, double width, double font) =>
        Math.Max(1, Math.Ceiling(characters * font * 0.52 / Math.Max(60, width)));

    private static int CountLines(string text)
    {
        var count = 1;
        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private static int Length(IReadOnlyList<InlineNode> inlines)
    {
        var total = 0;
        foreach (var inline in inlines)
        {
            total += inline switch
            {
                TextNode text => text.Text.Length,
                CodeSpanNode code => code.Code.Length,
                EmphasisNode e => Length(e.Children),
                StrongNode s => Length(s.Children),
                StrikethroughNode s => Length(s.Children),
                LinkNode l => Length(l.Children),
                StyledNode s => Length(s.Children),
                MathSpanNode m => m.Latex.Length,
                ImageNode => 40,
                _ => 1,
            };
        }

        return total;
    }
}
