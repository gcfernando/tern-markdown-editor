using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Tern.Search;
using Tern.PreviewView;

namespace Tern.Desktop.Views;

internal sealed class FindMatchRenderer : IBackgroundRenderer
{
    public FindMatch? Match { get; set; }
    public IReadOnlyList<FindMatch> Matches { get; set; } = [];

    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid || textView.VisualLines.Count == 0
            || textView.Document is not { } document)
            return;

        var first = textView.VisualLines[0].FirstDocumentLine.Offset;
        var last = textView.VisualLines[^1].LastDocumentLine.EndOffset;
        var fill = PreviewTheme.Resolve(textView, PreviewKeys.FindMatch);
        var otherFill = PreviewTheme.Resolve(textView, PreviewKeys.FindMatchOther);
        var pen = new Pen(PreviewTheme.Resolve(textView, PreviewKeys.FindMatchOutline), 2);
        foreach (var match in Matches)
        {
            if (match.Start > last) break;
            if (match.Start + match.Length < first || match.Start + match.Length > document.TextLength) continue;
            var active = match == Match;
            var geometry = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 2 };
            geometry.AddSegment(textView, new SimpleSegment(match.Start, match.Length));
            if (geometry.CreateGeometry() is { } outline)
                drawingContext.DrawGeometry(active ? fill : otherFill, active ? pen : null, outline);
        }
    }
}
