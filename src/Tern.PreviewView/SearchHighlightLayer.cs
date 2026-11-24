using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Tern.PreviewView;

/// <summary>Draws find results without changing selectable text, links, or syntax-coloured runs.</summary>
internal sealed class SearchHighlightLayer(PreviewView owner) : Control
{
    public override void Render(DrawingContext context)
    {
        if (owner.SearchMatches.Count == 0) return;
        var fill = PreviewTheme.Resolve(this, PreviewKeys.FindMatch);
        var otherFill = PreviewTheme.Resolve(this, PreviewKeys.FindMatchOther);
        var outline = new Pen(PreviewTheme.Resolve(this, PreviewKeys.FindMatchOutline), 2);
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
                        context.DrawRectangle(otherFill, null,
                            rect.Translate(new Vector(origin.X + text.Padding.Left, origin.Y + text.Padding.Top)), 2, 2);
                foreach (var range in text.SearchRanges(activeOnly: true))
                    foreach (var rect in text.TextLayout.HitTestTextRange(range.Start, range.Length))
                        context.DrawRectangle(fill, outline,
                            rect.Translate(new Vector(origin.X + text.Padding.Left, origin.Y + text.Padding.Top)), 2, 2);
            }
        }
    }
}
