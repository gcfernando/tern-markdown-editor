using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Tern.PreviewView;

/// <summary>Draws find results without changing selectable text, links, or syntax-coloured runs.</summary>
internal sealed class SearchHighlightLayer(PreviewView owner) : Control
{
    private static readonly IBrush Fill = new SolidColorBrush(Color.Parse("#55FFB300"));
    private static readonly IBrush OtherFill = new SolidColorBrush(Color.Parse("#40FFD54F"));
    private static readonly Pen Outline = new(new SolidColorBrush(Color.Parse("#E48B00")), 2);

    public override void Render(DrawingContext context)
    {
        if (owner.SearchMatches.Count == 0) return;
        foreach (var text in owner.GetVisualDescendants().OfType<RichTextBlock>())
        {
            if (!text.IsEffectivelyVisible || text.TranslatePoint(default, this) is not { } origin) continue;
            var clip = new Rect(Bounds.Size);
            foreach (var scroller in text.GetVisualAncestors().OfType<ScrollViewer>())
                if (scroller.TranslatePoint(default, this) is { } position)
                    clip = clip.Intersect(new Rect(position, scroller.Viewport));
            using (context.PushClip(clip))
            {
                foreach (var range in text.SearchRanges())
                    foreach (var rect in text.TextLayout.HitTestTextRange(range.Start, range.Length))
                        context.DrawRectangle(OtherFill, null,
                            rect.Translate(new Vector(origin.X + text.Padding.Left, origin.Y + text.Padding.Top)), 2, 2);
                foreach (var range in text.SearchRanges(activeOnly: true))
                    foreach (var rect in text.TextLayout.HitTestTextRange(range.Start, range.Length))
                        context.DrawRectangle(Fill, Outline,
                            rect.Translate(new Vector(origin.X + text.Padding.Left, origin.Y + text.Padding.Top)), 2, 2);
            }
        }
    }
}
