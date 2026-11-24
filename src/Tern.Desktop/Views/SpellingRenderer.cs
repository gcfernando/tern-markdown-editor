using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Tern.App.Editing;

namespace Tern.Desktop.Views;

internal sealed class SpellingRenderer : IBackgroundRenderer
{
    public IReadOnlyList<SpellingIssue> Issues { get; set; } = [];
    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid || textView.VisualLines.Count == 0 || textView.Document is not { } document) return;
        var first = textView.VisualLines[0].FirstDocumentLine.Offset;
        var last = textView.VisualLines[^1].LastDocumentLine.EndOffset;
        var brush = Tern.PreviewView.PreviewTheme.Resolve(textView, "Tern.Danger");
        var pen = new Pen(brush, 1);
        foreach (var issue in Issues)
        {
            if (issue.Start > last) break;
            if (issue.Start + issue.Length < first || issue.Start + issue.Length > document.TextLength) continue;
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, new SimpleSegment(issue.Start, issue.Length)))
            {
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(new Point(rect.Left, rect.Bottom - 1), false);
                    for (var x = rect.Left + 2; x <= rect.Right; x += 2)
                        context.LineTo(new Point(x, rect.Bottom - (((int)((x - rect.Left) / 2) & 1) == 0 ? 1 : 3)));
                    context.EndFigure(false);
                }
                drawingContext.DrawGeometry(null, pen, geometry);
            }
        }
    }
}
