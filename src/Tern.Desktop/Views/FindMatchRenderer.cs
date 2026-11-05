using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Tern.Search;

namespace Tern.Desktop.Views;

internal sealed class FindMatchRenderer : IBackgroundRenderer
{
    public FindMatch? Match { get; set; }
    private static readonly IBrush Fill = new SolidColorBrush(Color.Parse("#55FFB300"));
    private static readonly Pen Outline = new(new SolidColorBrush(Color.Parse("#E48B00")), 2);

    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Match is not { Length: > 0 } match || !textView.VisualLinesValid
            || textView.Document is not { } document || match.Start + match.Length > document.TextLength)
            return;

        var geometry = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 2 };
        geometry.AddSegment(textView, new SimpleSegment(match.Start, match.Length));
        if (geometry.CreateGeometry() is { } outline)
            drawingContext.DrawGeometry(Fill, Outline, outline);
    }
}
