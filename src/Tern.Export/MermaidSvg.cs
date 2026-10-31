using System.Globalization;
using System.Text;
using Tern.Rendering.Mermaid;

namespace Tern.Export;

/// <summary>Draws Mermaid layouts as inline SVG. The output uses only plain shapes and text, no scripts, links, images or styles attributes.</summary>
internal static class MermaidSvg
{
    private const double LineHeight = 18;

    public static string Render(FlowchartLayout layout, string description)
    {
        var svg = new SvgWriter(layout.Width, layout.Height, description);

        foreach (var sub in layout.Subgraphs)
        {
            var b = sub.Bounds;
            svg.Rect(b, "d-sub", 6);
            svg.Text(sub.Subgraph.Title, b.X + 8, b.Y + 14, "d-title", "start");
        }

        foreach (var edge in layout.Edges)
        {
            if (edge.Edge.Style == FlowLineStyle.Invisible || edge.Points.Count < 2)
            {
                continue;
            }

            var cls = "d-edge" + edge.Edge.Style switch
            {
                FlowLineStyle.Dotted => " d-dotted",
                FlowLineStyle.Thick => " d-thick",
                _ => string.Empty,
            };
            if (edge.LabelLeader.Count > 1) svg.Polyline(edge.LabelLeader, "d-label-leader");
            svg.CurvedEdge(edge.Points, cls);
            svg.Head(edge.StartArrow, edge.Edge.StartHead);
            svg.Head(edge.EndArrow, edge.Edge.EndHead);
        }

        foreach (var node in layout.Nodes)
        {
            DrawNode(svg, node);
        }

        foreach (var edge in layout.Edges)
        {
            if (edge.StartLabelBounds is { } start && edge.Edge.StartLabel is { } startText) { svg.Rect(start, "d-label-bg", 3); svg.CenteredText(startText, start); }
            if (edge.EndLabelBounds is { } end && edge.Edge.EndLabel is { } endText) { svg.Rect(end, "d-label-bg", 3); svg.CenteredText(endText, end); }
            if (edge.LabelBounds is { } label && edge.Edge.Label is { Length: > 0 } text)
            {
                svg.Rect(label, "d-label-bg", 3);
                svg.CenteredText(text, label);
            }
        }

        return svg.Finish();
    }

    public static string Render(ChartLayout layout, string description)
    {
        var svg = new SvgWriter(layout.Width, layout.Height, description);
        foreach (var primitive in layout.Primitives)
        {
            switch (primitive)
            {
                case ChartText text: svg.CenteredText(text.Text, text.Bounds); break;
                case ChartBox box: svg.ChartRect(box.Bounds, box.PaletteIndex); break;
                case ChartPolygon polygon: svg.ChartPolygon(polygon.Points, polygon.PaletteIndex); break;
                case ChartLine line: svg.Line(line.From.X, line.From.Y, line.To.X, line.To.Y, "d-life"); break;
            }
        }
        return svg.Finish();
    }

    public static string Render(SequenceLayout layout, string description)
    {
        var svg = new SvgWriter(layout.Width, layout.Height, description);

        foreach (var frame in layout.Frames)
        {
            svg.Rect(frame.Bounds, "d-frame", 3);
            var title = frame.Label.Length == 0 ? frame.Kind : $"{frame.Kind} [{frame.Label}]";
            svg.Text(title, frame.Bounds.X + 6, frame.Bounds.Y + 14, "d-title", "start");
            foreach (var divider in frame.Dividers)
            {
                svg.Line(frame.Bounds.X, divider.Y, frame.Bounds.Right, divider.Y, "d-life");
                if (divider.Label.Length > 0)
                {
                    svg.Text($"[{divider.Label}]", frame.Bounds.X + 6, divider.Y + 14, "d-title", "start");
                }
            }
        }

        foreach (var participant in layout.Participants)
        {
            svg.Line(participant.CenterX, participant.LifelineTop, participant.CenterX, participant.LifelineBottom, "d-life");
            var radius = participant.Participant.IsActor ? participant.Header.Height / 2 : 4;
            svg.Rect(participant.Header, "d-node", radius);
            svg.CenteredText(participant.Participant.Label, participant.Header);
            svg.Rect(participant.Footer, "d-node", radius);
            svg.CenteredText(participant.Participant.Label, participant.Footer);
        }

        foreach (var note in layout.Notes)
        {
            svg.Rect(note.Bounds, "d-note", 2);
            svg.CenteredText(note.Note.Text, note.Bounds);
        }

        foreach (var message in layout.Messages)
        {
            var dashed = message.Message.IsDashed ? " d-dotted" : string.Empty;
            svg.Polyline(message.Points, "d-edge" + dashed);
            if (message.HeadMarker.Count > 0)
            {
                svg.Head(message.HeadMarker, message.Message.Head == SequenceHead.Cross ? FlowArrowHead.Cross : FlowArrowHead.Arrow, open: message.Message.Head == SequenceHead.Open);
            }

            if (message.LabelBounds is { } label)
            {
                var text = message.Message.Number is { } n ? $"{n}. {message.Message.Text}" : message.Message.Text;
                svg.CenteredText(text, label);
            }
        }

        return svg.Finish();
    }

    private static void DrawNode(SvgWriter svg, LaidOutNode node)
    {
        var b = node.Bounds;
        switch (node.Node.Shape)
        {
            case FlowNodeShape.InitialState:
                svg.Ellipse(b, "d-head");
                return;
            case FlowNodeShape.FinalState:
                svg.Ellipse(b, "d-node");
                svg.Ellipse(new(b.X + 4, b.Y + 4, b.Width - 8, b.Height - 8), "d-head");
                return;
            case FlowNodeShape.Class:
                svg.Rect(b, "d-node", 0);
                var classHeader = node.Node.Label.Split('\n').Length * LineHeight + 20;
                svg.CenteredText(node.Node.Label, new(b.X, b.Y, b.Width, classHeader));
                var classY = b.Y + classHeader;
                foreach (var members in new[] { node.Node.Attributes, node.Node.Methods })
                {
                    svg.Line(b.X, classY, b.Right, classY, "d-edge");
                    var memberLines = members.SelectMany(member => member.Split('\n')).ToArray();
                    var memberHeight = Math.Max(20, memberLines.Length * LineHeight + (memberLines.Length == 0 ? 0 : 16));
                    foreach (var member in memberLines) { svg.Text(member, b.X + 12, classY + 22, "d-text", "start"); classY += LineHeight; }
                    classY += memberHeight - memberLines.Length * LineHeight;
                }
                return;
            case FlowNodeShape.Entity:
                svg.Rect(b, "d-node", 0);
                var headerHeight = Math.Min(b.Height, (node.Node.Label.Split('\n').Length * LineHeight) + 20);
                svg.CenteredText(node.Node.Label, new DiagramRect(b.X, b.Y, b.Width, headerHeight));
                if (node.Node.Attributes.Count > 0)
                {
                    svg.Line(b.X, b.Y + headerHeight, b.Right, b.Y + headerHeight, "d-edge d-entity-divider");
                    var attributeY = b.Y + headerHeight + 8 + 14;
                    foreach (var attribute in node.Node.Attributes)
                    {
                        foreach (var line in attribute.Split('\n'))
                        {
                            svg.Text(line, b.X + 12, attributeY, "d-text d-entity-attribute", "start");
                            attributeY += LineHeight;
                        }
                    }
                }
                return;
            case FlowNodeShape.Rounded:
                svg.Rect(b, "d-node", 8);
                break;
            case FlowNodeShape.Stadium:
                svg.Rect(b, "d-node", b.Height / 2);
                break;
            case FlowNodeShape.Circle:
                svg.Ellipse(b, "d-node");
                break;
            case FlowNodeShape.Diamond:
                svg.Polygon([new DiagramPoint(b.X + (b.Width / 2), b.Y), new DiagramPoint(b.Right, b.Y + (b.Height / 2)), new DiagramPoint(b.X + (b.Width / 2), b.Bottom), new DiagramPoint(b.X, b.Y + (b.Height / 2))], "d-node");
                break;
            case FlowNodeShape.Hexagon:
                var inset = Math.Min(b.Width * 0.15, b.Height / 2);
                svg.Polygon(
                    [
                        new DiagramPoint(b.X + inset, b.Y),
                        new DiagramPoint(b.Right - inset, b.Y),
                        new DiagramPoint(b.Right, b.Y + (b.Height / 2)),
                        new DiagramPoint(b.Right - inset, b.Bottom),
                        new DiagramPoint(b.X + inset, b.Bottom),
                        new DiagramPoint(b.X, b.Y + (b.Height / 2)),
                    ],
                    "d-node");
                break;
            case FlowNodeShape.Subroutine:
                svg.Rect(b, "d-node", 0);
                svg.Line(b.X + 8, b.Y, b.X + 8, b.Bottom, "d-edge");
                svg.Line(b.Right - 8, b.Y, b.Right - 8, b.Bottom, "d-edge");
                break;
            case FlowNodeShape.Cylinder:
                svg.Cylinder(b, "d-node");
                break;
            default:
                svg.Rect(b, "d-node", 0);
                break;
        }

        svg.CenteredText(node.Node.Label, b);
    }

    private sealed class SvgWriter
    {
        private readonly StringBuilder _builder = new();

        public SvgWriter(double width, double height, string description)
        {
            var w = Num(Math.Max(1, Math.Ceiling(width)));
            var h = Num(Math.Max(1, Math.Ceiling(height)));
            _builder.Append("<svg class=\"diagram\" role=\"img\" aria-label=\"").Append(HtmlText.Escape(description)).Append("\" viewBox=\"0 0 ").Append(w).Append(' ').Append(h)
                .Append("\" width=\"").Append(w).Append("\" height=\"").Append(h).Append("\"><title>").Append(HtmlText.Escape(description)).Append("</title>");
        }

        public string Finish() => _builder.Append("</svg>").ToString();

        public void ChartRect(DiagramRect r, int paletteIndex) =>
            _builder.Append("<rect x=\"").Append(Num(r.X)).Append("\" y=\"").Append(Num(r.Y)).Append("\" width=\"").Append(Num(r.Width)).Append("\" height=\"").Append(Num(r.Height)).Append("\" fill=\"").Append(paletteIndex < 0 ? "none" : ChartPalette.Get(paletteIndex)).Append("\"/>");

        public void ChartPolygon(IReadOnlyList<DiagramPoint> points, int paletteIndex) =>
            _builder.Append("<polygon points=\"").Append(Points(points)).Append("\" fill=\"").Append(ChartPalette.Get(paletteIndex)).Append("\"/>");

        public void Rect(DiagramRect r, string cls, double radius)
        {
            _builder.Append("<rect class=\"").Append(cls).Append("\" x=\"").Append(Num(r.X)).Append("\" y=\"").Append(Num(r.Y)).Append("\" width=\"").Append(Num(r.Width)).Append("\" height=\"").Append(Num(r.Height)).Append('"');
            if (radius > 0)
            {
                _builder.Append(" rx=\"").Append(Num(radius)).Append('"');
            }

            _builder.Append("/>");
        }

        public void Ellipse(DiagramRect r, string cls) =>
            _builder.Append("<ellipse class=\"").Append(cls).Append("\" cx=\"").Append(Num(r.X + (r.Width / 2))).Append("\" cy=\"").Append(Num(r.Y + (r.Height / 2)))
                .Append("\" rx=\"").Append(Num(r.Width / 2)).Append("\" ry=\"").Append(Num(r.Height / 2)).Append("\"/>");

        public void Polygon(IReadOnlyList<DiagramPoint> points, string cls) =>
            _builder.Append("<polygon class=\"").Append(cls).Append("\" points=\"").Append(Points(points)).Append("\"/>");

        public void Polyline(IReadOnlyList<DiagramPoint> points, string cls) =>
            _builder.Append("<polyline class=\"").Append(cls).Append("\" points=\"").Append(Points(points)).Append("\"/>");

        public void CurvedEdge(IReadOnlyList<DiagramPoint> points, string cls)
        {
            _builder.Append("<path class=\"").Append(cls).Append("\" d=\"M ").Append(Points([points[0]]));
            foreach (var segment in DiagramEdgePath.Build(points))
            {
                if (segment.Control is { } control) _builder.Append(" Q ").Append(Points([control, segment.End]));
                else _builder.Append(" L ").Append(Points([segment.End]));
            }
            _builder.Append("\"/>");
        }

        public void Line(double x1, double y1, double x2, double y2, string cls) =>
            _builder.Append("<line class=\"").Append(cls).Append("\" x1=\"").Append(Num(x1)).Append("\" y1=\"").Append(Num(y1)).Append("\" x2=\"").Append(Num(x2)).Append("\" y2=\"").Append(Num(y2)).Append("\"/>");

        public void Cylinder(DiagramRect r, string cls)
        {
            var ry = Math.Min(10, r.Height / 6);
            var rx = r.Width / 2;
            _builder.Append("<path class=\"").Append(cls).Append("\" d=\"M ").Append(Num(r.X)).Append(' ').Append(Num(r.Y + ry))
                .Append(" A ").Append(Num(rx)).Append(' ').Append(Num(ry)).Append(" 0 0 1 ").Append(Num(r.Right)).Append(' ').Append(Num(r.Y + ry))
                .Append(" V ").Append(Num(r.Bottom - ry))
                .Append(" A ").Append(Num(rx)).Append(' ').Append(Num(ry)).Append(" 0 0 1 ").Append(Num(r.X)).Append(' ').Append(Num(r.Bottom - ry))
                .Append(" Z M ").Append(Num(r.X)).Append(' ').Append(Num(r.Y + ry))
                .Append(" A ").Append(Num(rx)).Append(' ').Append(Num(ry)).Append(" 0 0 0 ").Append(Num(r.Right)).Append(' ').Append(Num(r.Y + ry)).Append("\"/>");
        }

        /// <summary>Draws a marker from layout geometry: a triangle (3 points) or a bounding square (4 points) for circle and cross heads.</summary>
        public void Head(IReadOnlyList<DiagramPoint> geometry, FlowArrowHead head, bool open = false)
        {
            if (geometry.Count == 0 || head == FlowArrowHead.None)
            {
                return;
            }

            if (head is FlowArrowHead.One or FlowArrowHead.ZeroOrOne or FlowArrowHead.Many or FlowArrowHead.ZeroOrMany)
            {
                if (geometry.Count >= 2) Cardinality(geometry[0], geometry[1], head);
                return;
            }

            if (geometry.Count == 3)
            {
                if (head == FlowArrowHead.OpenArrow) Polyline([geometry[1], geometry[0], geometry[2]], "d-head-open");
                else Polygon(geometry, open || head == FlowArrowHead.HollowTriangle ? "d-head-open" : "d-head");
                return;
            }

            if (head is FlowArrowHead.FilledDiamond or FlowArrowHead.HollowDiamond)
            {
                Polygon(geometry, head == FlowArrowHead.FilledDiamond ? "d-head" : "d-node");
                return;
            }

            var minX = geometry.Min(p => p.X);
            var maxX = geometry.Max(p => p.X);
            var minY = geometry.Min(p => p.Y);
            var maxY = geometry.Max(p => p.Y);
            if (head == FlowArrowHead.Circle)
            {
                Ellipse(new DiagramRect(minX, minY, maxX - minX, maxY - minY), "d-head-open");
            }
            else
            {
                Line(minX, minY, maxX, maxY, "d-edge");
                Line(minX, maxY, maxX, minY, "d-edge");
            }
        }

        private void Cardinality(DiagramPoint tip, DiagramPoint previous, FlowArrowHead head)
        {
            var dx = previous.X - tip.X;
            var dy = previous.Y - tip.Y;
            var length = Math.Sqrt((dx * dx) + (dy * dy));
            if (length < 1e-9) return;
            var ux = dx / length;
            var uy = dy / length;
            DiagramPoint At(double along, double across = 0) =>
                new(tip.X + (ux * along) - (uy * across), tip.Y + (uy * along) + (ux * across));
            void Segment(DiagramPoint a, DiagramPoint b, string cls) => Line(a.X, a.Y, b.X, b.Y, cls);
            void Bar(double offset) => Segment(At(offset, -6), At(offset, 6), "d-edge d-er-bar");

            if (head is FlowArrowHead.Many or FlowArrowHead.ZeroOrMany)
            {
                Segment(At(12), At(0, -6), "d-edge d-er-prong");
                Segment(At(12), At(0), "d-edge d-er-prong");
                Segment(At(12), At(0, 6), "d-edge d-er-prong");
            }
            else Bar(6);

            if (head is FlowArrowHead.ZeroOrOne or FlowArrowHead.ZeroOrMany)
            {
                var center = At(head == FlowArrowHead.ZeroOrOne ? 17 : 21);
                Ellipse(new DiagramRect(center.X - 3.5, center.Y - 3.5, 7, 7), "d-head-open d-er-zero");
            }
            else Bar(head == FlowArrowHead.One ? 14 : 20);
        }

        public void Text(string text, double x, double y, string cls, string anchor) =>
            _builder.Append("<text class=\"").Append(cls).Append("\" x=\"").Append(Num(x)).Append("\" y=\"").Append(Num(y)).Append("\" text-anchor=\"").Append(anchor).Append("\">").Append(HtmlText.Escape(text)).Append("</text>");

        public void CenteredText(string text, DiagramRect bounds)
        {
            var lines = text.Split('\n');
            var cx = bounds.X + (bounds.Width / 2);
            var cy = bounds.Y + (bounds.Height / 2);
            for (var i = 0; i < lines.Length; i++)
            {
                var y = cy + ((i - ((lines.Length - 1) / 2d)) * LineHeight);
                _builder.Append("<text class=\"d-text\" x=\"").Append(Num(cx)).Append("\" y=\"").Append(Num(y)).Append("\" text-anchor=\"middle\" dominant-baseline=\"central\">").Append(HtmlText.Escape(lines[i])).Append("</text>");
            }
        }

        private static string Points(IReadOnlyList<DiagramPoint> points) =>
            string.Join(' ', points.Select(p => Num(p.X) + "," + Num(p.Y)));

        private static string Num(double value) =>
            (double.IsFinite(value) ? Math.Round(value, 2) : 0).ToString("0.##", CultureInfo.InvariantCulture);
    }
}
