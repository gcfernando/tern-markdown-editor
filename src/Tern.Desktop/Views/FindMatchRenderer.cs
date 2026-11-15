using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Tern.Search;

namespace Tern.Desktop.Views;

internal sealed class FindMatchRenderer : IBackgroundRenderer
{
    public FindMatch? Match { get; set; }
    public IReadOnlyList<FindMatch> Matches { get; set; } = [];
    private static readonly IBrush Fill = new SolidColorBrush(Color.Parse("#55FFB300"));
    private static readonly IBrush OtherFill = new SolidColorBrush(Color.Parse("#40FFD54F"));
    private static readonly Pen Outline = new(new SolidColorBrush(Color.Parse("#E48B00")), 2);

    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid || textView.VisualLines.Count == 0
            || textView.Document is not { } document)
            return;

        var first = textView.VisualLines[0].FirstDocumentLine.Offset;
        var last = textView.VisualLines[^1].LastDocumentLine.EndOffset;
        foreach (var match in Matches)
        {
            if (match.Start > last) break;
            if (match.Start + match.Length < first || match.Start + match.Length > document.TextLength) continue;
            var active = match == Match;
            var geometry = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 2 };
            geometry.AddSegment(textView, new SimpleSegment(match.Start, match.Length));
            if (geometry.CreateGeometry() is { } outline)
                drawingContext.DrawGeometry(active ? Fill : OtherFill, active ? Outline : null, outline);
        }
    }
}
