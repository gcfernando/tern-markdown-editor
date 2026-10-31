using PdfSharp.Drawing;
using Tern.Rendering.Mermaid;

namespace Tern.Export;

/// <summary>Draws Mermaid layouts onto a PDF page in diagram coordinates. The caller sets up translation and scale first.</summary>
internal sealed class PdfDiagramPainter(PdfTypography typography)
{
    public const double FontSize = 14;
    private const double TextLineHeight = 18;

    private static readonly XColor NodeFill = XColor.FromArgb(232, 240, 254);
    private static readonly XColor NodeStroke = XColor.FromArgb(74, 111, 165);
    private static readonly XColor EdgeColor = XColor.FromArgb(75, 85, 99);
    private static readonly XColor FrameColor = XColor.FromArgb(156, 163, 175);
    private static readonly XColor NoteFill = XColor.FromArgb(255, 248, 197);
    private static readonly XColor TextColor = XColor.FromArgb(31, 35, 40);
    private static readonly XColor White = XColor.FromArgb(255, 255, 255);

    private readonly XFont _font = typography.Font(default, FontSize);
    private readonly XFont _smallFont = typography.Font(new TextStyle(true, false, false, false, false, false, 0, 0), 12);

    /// <summary>Measures a label for the layout engine using the real PDF font.</summary>
    public (double W, double H) Measure(string text)
    {
        var lines = text.Split('\n');
        return (lines.Max(l => typography.Width(PdfText.Safe(l), _font)), lines.Length * TextLineHeight);
    }

    public void Draw(XGraphics gfx, FlowchartLayout layout)
    {
        var edgePen = new XPen(EdgeColor, 1.5);
        foreach (var sub in layout.Subgraphs)
        {
            gfx.DrawRoundedRectangle(new XPen(FrameColor, 1) { DashStyle = XDashStyle.Dash }, Rect(sub.Bounds), new XSize(6, 6));
            Text(gfx, sub.Subgraph.Title, sub.Bounds.X + 8, sub.Bounds.Y + 10, _smallFont, XStringFormats.CenterLeft);
        }

        foreach (var edge in layout.Edges)
        {
            if (edge.Edge.Style == FlowLineStyle.Invisible || edge.Points.Count < 2)
            {
                continue;
            }

            var pen = new XPen(EdgeColor, edge.Edge.Style == FlowLineStyle.Thick ? 3 : 1.5);
            if (edge.Edge.Style == FlowLineStyle.Dotted)
            {
                pen.DashStyle = XDashStyle.Dash;
            }
            if (edge.LabelLeader.Count > 1)
                gfx.DrawLines(new XPen(EdgeColor, 0.8) { DashStyle = XDashStyle.Dot }, XPoints(edge.LabelLeader));

            var path = new XGraphicsPath();
            foreach (var segment in DiagramEdgePath.Build(edge.Points))
            {
                if (segment.Control is { } control)
                {
                    // PDF uses cubic Beziers: this conversion represents the quadratic exactly.
                    path.AddBezier(segment.Start.X, segment.Start.Y,
                        segment.Start.X + (control.X - segment.Start.X) * 2 / 3,
                        segment.Start.Y + (control.Y - segment.Start.Y) * 2 / 3,
                        segment.End.X + (control.X - segment.End.X) * 2 / 3,
                        segment.End.Y + (control.Y - segment.End.Y) * 2 / 3,
                        segment.End.X, segment.End.Y);
                }
                else path.AddLine(segment.Start.X, segment.Start.Y, segment.End.X, segment.End.Y);
            }
            gfx.DrawPath(pen, path);
            Head(gfx, edge.StartArrow, edge.Edge.StartHead, false);
            Head(gfx, edge.EndArrow, edge.Edge.EndHead, false);
        }

        var nodePen = new XPen(NodeStroke, 1.5);
        var fill = new XSolidBrush(NodeFill);
        foreach (var node in layout.Nodes)
        {
            DrawNode(gfx, node, nodePen, fill, edgePen);
        }

        foreach (var edge in layout.Edges)
        {
            if (edge.LabelBounds is { } label && edge.Edge.Label is { Length: > 0 } text)
            {
                gfx.DrawRectangle(new XSolidBrush(White), Rect(label));
                CenteredText(gfx, text, label);
            }
        }
    }

    public void Draw(XGraphics gfx, SequenceLayout layout)
    {
        var edgePen = new XPen(EdgeColor, 1.5);
        var framePen = new XPen(FrameColor, 1.2);
        var lifePen = new XPen(FrameColor, 1) { DashStyle = XDashStyle.Dash };
        foreach (var frame in layout.Frames)
        {
            gfx.DrawRectangle(framePen, Rect(frame.Bounds));
            var title = frame.Label.Length == 0 ? frame.Kind : $"{frame.Kind} [{frame.Label}]";
            Text(gfx, title, frame.Bounds.X + 6, frame.Bounds.Y + 10, _smallFont, XStringFormats.CenterLeft);
            foreach (var divider in frame.Dividers)
            {
                gfx.DrawLine(lifePen, frame.Bounds.X, divider.Y, frame.Bounds.Right, divider.Y);
                if (divider.Label.Length > 0)
                {
                    Text(gfx, $"[{divider.Label}]", frame.Bounds.X + 6, divider.Y + 10, _smallFont, XStringFormats.CenterLeft);
                }
            }
        }

        var nodePen = new XPen(NodeStroke, 1.5);
        var fill = new XSolidBrush(NodeFill);
        foreach (var participant in layout.Participants)
        {
            gfx.DrawLine(lifePen, participant.CenterX, participant.LifelineTop, participant.CenterX, participant.LifelineBottom);
            foreach (var box in new[] { participant.Header, participant.Footer })
            {
                gfx.DrawRoundedRectangle(nodePen, fill, Rect(box), new XSize(6, 6));
                CenteredText(gfx, participant.Participant.Label, box);
            }
        }

        foreach (var note in layout.Notes)
        {
            gfx.DrawRectangle(framePen, new XSolidBrush(NoteFill), Rect(note.Bounds));
            CenteredText(gfx, note.Note.Text, note.Bounds);
        }

        foreach (var message in layout.Messages)
        {
            if (message.Points.Count < 2)
            {
                continue;
            }

            var pen = new XPen(EdgeColor, 1.5);
            if (message.Message.IsDashed)
            {
                pen.DashStyle = XDashStyle.Dash;
            }

            gfx.DrawLines(pen, XPoints(message.Points));
            Head(gfx, message.HeadMarker, message.Message.Head == SequenceHead.Cross ? FlowArrowHead.Cross : FlowArrowHead.Arrow, message.Message.Head == SequenceHead.Open);
            if (message.LabelBounds is { } label)
            {
                var text = message.Message.Number is { } n ? $"{n}. {message.Message.Text}" : message.Message.Text;
                CenteredText(gfx, text, label);
            }
        }
    }

    private void DrawNode(XGraphics gfx, LaidOutNode node, XPen pen, XBrush fill, XPen detailPen)
    {
        var b = node.Bounds;
        switch (node.Node.Shape)
        {
            case FlowNodeShape.InitialState:
                gfx.DrawEllipse(new XSolidBrush(EdgeColor), Rect(b));
                return;
            case FlowNodeShape.FinalState:
                gfx.DrawEllipse(pen, Rect(b));
                gfx.DrawEllipse(new XSolidBrush(EdgeColor), new XRect(b.X + 4, b.Y + 4, b.Width - 8, b.Height - 8));
                return;
            case FlowNodeShape.Entity:
                gfx.DrawRectangle(pen, fill, Rect(b));
                var headerHeight = Math.Min(b.Height, Measure(node.Node.Label).H + 20);
                CenteredText(gfx, node.Node.Label, new DiagramRect(b.X, b.Y, b.Width, headerHeight));
                if (node.Node.Attributes.Count > 0)
                {
                    gfx.DrawLine(detailPen, b.X, b.Y + headerHeight, b.Right, b.Y + headerHeight);
                    var attributeY = b.Y + headerHeight + 8 + (TextLineHeight / 2);
                    foreach (var attribute in node.Node.Attributes)
                    {
                        foreach (var line in attribute.Split('\n'))
                        {
                            Text(gfx, line, b.X + 12, attributeY, _font, XStringFormats.CenterLeft);
                            attributeY += TextLineHeight;
                        }
                    }
                }
                return;
            case FlowNodeShape.Rounded:
                gfx.DrawRoundedRectangle(pen, fill, Rect(b), new XSize(16, 16));
                break;
            case FlowNodeShape.Stadium:
                gfx.DrawRoundedRectangle(pen, fill, Rect(b), new XSize(b.Height, b.Height));
                break;
            case FlowNodeShape.Circle:
                gfx.DrawEllipse(pen, fill, Rect(b));
                break;
            case FlowNodeShape.Diamond:
                gfx.DrawPolygon(pen, fill, [new XPoint(b.X + (b.Width / 2), b.Y), new XPoint(b.Right, b.Y + (b.Height / 2)), new XPoint(b.X + (b.Width / 2), b.Bottom), new XPoint(b.X, b.Y + (b.Height / 2))], XFillMode.Winding);
                break;
            case FlowNodeShape.Hexagon:
                var inset = Math.Min(b.Width * 0.15, b.Height / 2);
                gfx.DrawPolygon(
                    pen,
                    fill,
                    [
                        new XPoint(b.X + inset, b.Y),
                        new XPoint(b.Right - inset, b.Y),
                        new XPoint(b.Right, b.Y + (b.Height / 2)),
                        new XPoint(b.Right - inset, b.Bottom),
                        new XPoint(b.X + inset, b.Bottom),
                        new XPoint(b.X, b.Y + (b.Height / 2)),
                    ],
                    XFillMode.Winding);
                break;
            case FlowNodeShape.Subroutine:
                gfx.DrawRectangle(pen, fill, Rect(b));
                gfx.DrawLine(detailPen, b.X + 8, b.Y, b.X + 8, b.Bottom);
                gfx.DrawLine(detailPen, b.Right - 8, b.Y, b.Right - 8, b.Bottom);
                break;
            case FlowNodeShape.Cylinder:
                var ry = Math.Min(10, b.Height / 6);
                gfx.DrawRectangle(pen, fill, new XRect(b.X, b.Y + ry, b.Width, b.Height - (2 * ry)));
                gfx.DrawEllipse(pen, fill, new XRect(b.X, b.Bottom - (2 * ry), b.Width, 2 * ry));
                gfx.DrawRectangle(fill, new XRect(b.X + 1, b.Y + ry, b.Width - 2, b.Height - (3 * ry)));
                gfx.DrawEllipse(pen, fill, new XRect(b.X, b.Y, b.Width, 2 * ry));
                break;
            default:
                gfx.DrawRectangle(pen, fill, Rect(b));
                break;
        }

        CenteredText(gfx, node.Node.Label, b);
    }

    private static void Head(XGraphics gfx, IReadOnlyList<DiagramPoint> geometry, FlowArrowHead head, bool open)
    {
        if (geometry.Count == 0 || head == FlowArrowHead.None)
        {
            return;
        }

        if (head is FlowArrowHead.One or FlowArrowHead.ZeroOrOne or FlowArrowHead.Many or FlowArrowHead.ZeroOrMany)
        {
            if (geometry.Count >= 2) Cardinality(gfx, geometry[0], geometry[1], head);
            return;
        }

        if (geometry.Count == 3)
        {
            gfx.DrawPolygon(new XPen(EdgeColor, 1), open ? new XSolidBrush(White) : new XSolidBrush(EdgeColor), XPoints(geometry), XFillMode.Winding);
            return;
        }

        var minX = geometry.Min(p => p.X);
        var maxX = geometry.Max(p => p.X);
        var minY = geometry.Min(p => p.Y);
        var maxY = geometry.Max(p => p.Y);
        var pen = new XPen(EdgeColor, 1.5);
        if (head == FlowArrowHead.Circle)
        {
            gfx.DrawEllipse(pen, new XSolidBrush(White), new XRect(minX, minY, maxX - minX, maxY - minY));
        }
        else
        {
            gfx.DrawLine(pen, minX, minY, maxX, maxY);
            gfx.DrawLine(pen, minX, maxY, maxX, minY);
        }
    }

    private static void Cardinality(XGraphics gfx, DiagramPoint tip, DiagramPoint previous, FlowArrowHead head)
    {
        var dx = previous.X - tip.X;
        var dy = previous.Y - tip.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length < 1e-9) return;
        var ux = dx / length;
        var uy = dy / length;
        var pen = new XPen(EdgeColor, 1.5);
        XPoint At(double along, double across = 0) =>
            new(tip.X + (ux * along) - (uy * across), tip.Y + (uy * along) + (ux * across));
        void Bar(double offset) => gfx.DrawLine(pen, At(offset, -6), At(offset, 6));

        if (head is FlowArrowHead.Many or FlowArrowHead.ZeroOrMany)
        {
            gfx.DrawLine(pen, At(12), At(0, -6));
            gfx.DrawLine(pen, At(12), At(0));
            gfx.DrawLine(pen, At(12), At(0, 6));
        }
        else Bar(6);

        if (head is FlowArrowHead.ZeroOrOne or FlowArrowHead.ZeroOrMany)
        {
            var center = At(head == FlowArrowHead.ZeroOrOne ? 17 : 21);
            gfx.DrawEllipse(pen, new XSolidBrush(White), new XRect(center.X - 3.5, center.Y - 3.5, 7, 7));
        }
        else Bar(head == FlowArrowHead.One ? 14 : 20);
    }

    private void CenteredText(XGraphics gfx, string text, DiagramRect bounds)
    {
        var lines = text.Split('\n');
        var center = bounds.Center;
        for (var i = 0; i < lines.Length; i++)
        {
            Text(gfx, lines[i], center.X, center.Y + ((i - ((lines.Length - 1) / 2d)) * TextLineHeight), _font, XStringFormats.Center);
        }
    }

    private static void Text(XGraphics gfx, string text, double x, double y, XFont font, XStringFormat format) =>
        gfx.DrawString(PdfText.Safe(text), font, new XSolidBrush(TextColor), new XPoint(x, y), format);

    private static XRect Rect(DiagramRect r) => new(r.X, r.Y, r.Width, r.Height);

    private static XPoint[] XPoints(IReadOnlyList<DiagramPoint> points) => [.. points.Select(p => new XPoint(p.X, p.Y))];
}
