using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Tern.Rendering.Mermaid;

namespace Tern.PreviewView;

/// <summary>Draws a laid out Mermaid flowchart or sequence diagram natively.</summary>
internal sealed class DiagramControl : Control, IWidthAware
{
    private double _availableWidth = double.PositiveInfinity;

    public double AvailableWidth
    {
        set
        {
            if (Math.Abs(value - _availableWidth) > 0.5 || double.IsInfinity(_availableWidth))
            {
                _availableWidth = value;
                InvalidateMeasure();
            }
        }
    }

    private const double DiagramFontSize = 16;
    private const double MinScale = 0.85;
    private static readonly Lazy<Typeface> Face = new(() =>
    {
        foreach (var family in new[] { "Trebuchet MS", "Verdana", "Arial", "Noto Sans", "Inter" })
        {
            var candidate = new Typeface(new FontFamily(family));
            if (FontManager.Current.TryGetGlyphTypeface(candidate, out _)) return candidate;
        }
        return new Typeface(FontFamily.Default);
    });

    internal string TypefaceFamilyName => Face.Value.GlyphTypeface.FamilyName;

    private readonly FlowchartLayout? _flow;
    private readonly SequenceLayout? _sequence;
    private readonly ChartLayout? _chart;
    private double _scale = 1;
    private bool _fitToWidth;

    internal bool FitToWidth
    {
        get => _fitToWidth;
        set { if (_fitToWidth == value) return; _fitToWidth = value; InvalidateMeasure(); }
    }

    public DiagramControl(MermaidParseResult parsed)
    {
        _flow = parsed.Flowchart?.Layout(Measure);
        _sequence = parsed.Sequence?.Layout(Measure);
        _chart = parsed.Chart?.Layout(Measure);
        Description = parsed.Flowchart?.Description ?? parsed.Chart?.Description ?? "Sequence diagram";
        Focusable = false;
    }

    public string Description { get; }
    public double DiagramWidth => _flow?.Width ?? _sequence?.Width ?? _chart?.Width ?? 0;

    public double DiagramHeight => _flow?.Height ?? _sequence?.Height ?? _chart?.Height ?? 0;

    /// <summary>Number of laid out nodes (flowchart) or participants (sequence); useful for assertions.</summary>
    public int ElementCount => _flow?.Nodes.Count ?? _sequence?.Participants.Count ?? _chart?.ElementCount ?? 0;

    public bool IsEntityRelationship => _flow?.Nodes.Any(node => node.Node.Shape == FlowNodeShape.Entity) == true;

    private static (double Width, double Height) Measure(string text)
    {
        var formatted = Format(text, Brushes.Black);
        return (formatted.WidthIncludingTrailingWhitespace, formatted.Height);
    }

    private static FormattedText Format(string text, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(text, System.Globalization.CultureInfo.CurrentUICulture, Avalonia.Media.FlowDirection.LeftToRight,
            new Typeface(Face.Value.FontFamily, FontStyle.Normal, weight), DiagramFontSize, brush);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (DiagramWidth <= 0)
        {
            return default;
        }

        var available = double.IsFinite(_availableWidth) ? _availableWidth : double.IsInfinity(availableSize.Width) ? DiagramWidth : availableSize.Width;
        _scale = FitToWidth ? Math.Min(1, available / DiagramWidth) : Math.Clamp(available / DiagramWidth, MinScale, 1);
        return new Size(Math.Ceiling(DiagramWidth * _scale), Math.Ceiling(DiagramHeight * _scale));
    }

    public override void Render(DrawingContext context)
    {
        var foreground = PreviewTheme.Resolve(this, PreviewKeys.Foreground);
        var background = PreviewTheme.Resolve(this, PreviewKeys.Background);
        var dark = background is ISolidColorBrush solid && solid.Color.R + solid.Color.G + solid.Color.B < 384;
        var border = new SolidColorBrush(Color.Parse(dark ? "#8B949E" : "#57606A"));
        var accent = border;
        var fill = new SolidColorBrush(Color.Parse(dark ? "#21262D" : "#FFFFFF"));
        var muted = PreviewTheme.Resolve(this, PreviewKeys.Muted);
        var palette = new Palette(foreground, border, accent, fill, muted);

        using (context.PushTransform(Matrix.CreateScale(_scale, _scale)))
        {
            if (_flow is not null)
            {
                DrawFlow(context, _flow, palette);
            }
            else if (_sequence is not null)
            {
                DrawSequence(context, _sequence, palette);
            }
            else if (_chart is not null) DrawChart(context, _chart, palette);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ((IResourceHost)this).ResourcesChanged += OnResourcesChanged;
        ActualThemeVariantChanged += OnThemeChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ((IResourceHost)this).ResourcesChanged -= OnResourcesChanged;
        ActualThemeVariantChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnResourcesChanged(object? sender, ResourcesChangedEventArgs e) => InvalidateVisual();

    private void OnThemeChanged(object? sender, EventArgs e) => InvalidateVisual();

    private readonly record struct Palette(IBrush Foreground, IBrush Border, IBrush Accent, IBrush Fill, IBrush Muted);

    private static Point P(DiagramPoint p) => new(p.X, p.Y);

    private static void DrawChart(DrawingContext context, ChartLayout layout, Palette palette)
    {
        foreach (var primitive in layout.Primitives)
        {
            switch (primitive)
            {
                case ChartText text:
                    DrawCentered(context, text.Text, R(text.Bounds), palette.Foreground, text.Bold ? FontWeight.SemiBold : FontWeight.Normal);
                    break;
                case ChartBox box:
                    context.DrawRectangle(box.PaletteIndex < 0 ? palette.Fill : new SolidColorBrush(Color.Parse(ChartPalette.Get(box.PaletteIndex))), null, R(box.Bounds));
                    break;
                case ChartPolygon polygon:
                    FillPolygon(context, polygon.Points, new SolidColorBrush(Color.Parse(ChartPalette.Get(polygon.PaletteIndex))), new Pen(palette.Border, 1));
                    break;
                case ChartLine line:
                    context.DrawLine(new Pen(palette.Muted, 0.6), P(line.From), P(line.To));
                    break;
            }
        }
    }

    private static Rect R(DiagramRect r) => new(r.X, r.Y, r.Width, r.Height);

    private static void DrawCentered(DrawingContext context, string text, Rect bounds, IBrush brush, FontWeight weight = FontWeight.Normal)
    {
        if (text.Length == 0)
        {
            return;
        }

        var formatted = Format(text, brush, weight);
        formatted.TextAlignment = TextAlignment.Center;
        formatted.MaxTextWidth = Math.Max(1, bounds.Width);
        context.DrawText(formatted, new Point(bounds.X, bounds.Y + Math.Max(0, (bounds.Height - formatted.Height) / 2)));
    }

    private static void DrawLabel(DrawingContext context, string text, DiagramRect? bounds, Palette palette)
    {
        if (bounds is not { } b || text.Length == 0)
        {
            return;
        }

        var rect = R(b);
        context.DrawRectangle(palette.Fill, null, rect.Inflate(2), 3, 3);
        DrawCentered(context, text, rect, palette.Foreground);
    }

    private static void DrawFlow(DrawingContext context, FlowchartLayout layout, Palette palette)
    {
        var frame = new Pen(palette.Border, 1, DashStyle.Dash);
        foreach (var subgraph in layout.Subgraphs)
        {
            var rect = R(subgraph.Bounds);
            context.DrawRectangle(null, frame, rect, 6, 6);
            var title = Format(subgraph.Subgraph.Title, palette.Muted, FontWeight.SemiBold);
            title.MaxTextWidth = Math.Max(1, rect.Width - 16);
            title.MaxLineCount = 1;
            title.Trimming = TextTrimming.CharacterEllipsis;
            context.DrawText(title, new Point(rect.X + 8, rect.Y + 5));
        }

        foreach (var edge in layout.Edges)
        {
            if (edge.LabelLeader.Count > 1)
            {
                var leader = new StreamGeometry();
                using (var path = leader.Open())
                {
                    path.BeginFigure(P(edge.LabelLeader[0]), false);
                    foreach (var point in edge.LabelLeader.Skip(1)) path.LineTo(P(point));
                    path.EndFigure(false);
                }
                context.DrawGeometry(null, new Pen(palette.Muted, 0.8, DashStyle.Dot), leader);
                context.DrawEllipse(palette.Muted, null, P(edge.LabelLeader[0]), 2, 2);
            }
            DrawEdge(context, edge, palette);
        }

        foreach (var node in layout.Nodes)
        {
            DrawNode(context, node, palette);
        }

        foreach (var edge in layout.Edges)
        {
            DrawLabel(context, edge.Edge.StartLabel ?? string.Empty, edge.StartLabelBounds, palette);
            DrawLabel(context, edge.Edge.EndLabel ?? string.Empty, edge.EndLabelBounds, palette);
            if (edge.Edge.Label is { Length: > 0 } label)
            {
                DrawLabel(context, label, edge.LabelBounds, palette);
            }
        }
    }

    private static void DrawEdge(DrawingContext context, LaidOutEdge edge, Palette palette)
    {
        if (edge.Edge.Style == FlowLineStyle.Invisible || edge.Points.Count < 2)
        {
            return;
        }

        var thickness = edge.Edge.Style == FlowLineStyle.Thick ? 3 : 1.5;
        var pen = new Pen(palette.Foreground, thickness, edge.Edge.Style == FlowLineStyle.Dotted ? DashStyle.Dash : null, PenLineCap.Flat, PenLineJoin.Round);
        var geometry = RoundedPath(edge.Points);
        context.DrawGeometry(null, pen, geometry);
        DrawHead(context, edge.StartArrow, edge.Edge.StartHead, palette);
        DrawHead(context, edge.EndArrow, edge.Edge.EndHead, palette);
    }

    private static StreamGeometry RoundedPath(IReadOnlyList<DiagramPoint> points)
    {
        var geometry = new StreamGeometry();
        using var path = geometry.Open();
        path.BeginFigure(P(points[0]), false);
        foreach (var segment in DiagramEdgePath.Build(points))
        {
            if (segment.Control is { } control) path.QuadraticBezierTo(P(control), P(segment.End));
            else path.LineTo(P(segment.End));
        }
        path.EndFigure(false);
        return geometry;
    }

    private static void DrawHead(DrawingContext context, IReadOnlyList<DiagramPoint> points, FlowArrowHead head, Palette palette)
    {
        if (points.Count >= 2 && head is FlowArrowHead.One or FlowArrowHead.ZeroOrOne or FlowArrowHead.Many or FlowArrowHead.ZeroOrMany)
        {
            DrawCardinality(context, P(points[0]), P(points[1]), head, palette);
            return;
        }

        if (head == FlowArrowHead.None || points.Count < 3)
        {
            return;
        }

        var pen = new Pen(palette.Foreground, 1.5);
        switch (head)
        {
            case FlowArrowHead.OpenArrow:
                context.DrawLine(pen, P(points[1]), P(points[0]));
                context.DrawLine(pen, P(points[0]), P(points[2]));
                break;
            case FlowArrowHead.HollowTriangle:
            case FlowArrowHead.HollowDiamond:
                FillPolygon(context, points, palette.Fill, pen);
                break;
            case FlowArrowHead.Arrow:
                FillPolygon(context, points, palette.Foreground);
                break;
            case FlowArrowHead.Circle when points.Count >= 4:
                {
                    var box = BoundingBox(points);
                    context.DrawEllipse(palette.Fill, pen, box.Center, box.Width / 2, box.Height / 2);
                    break;
                }

            case FlowArrowHead.Cross when points.Count >= 4:
                {
                    var box = BoundingBox(points);
                    context.DrawLine(pen, box.TopLeft, box.BottomRight);
                    context.DrawLine(pen, box.TopRight, box.BottomLeft);
                    break;
                }

            default:
                FillPolygon(context, points, palette.Foreground);
                break;
        }
    }

    private static void DrawCardinality(DrawingContext context, Point tip, Point previous, FlowArrowHead head, Palette palette)
    {
        var direction = new Vector(tip.X - previous.X, tip.Y - previous.Y);
        if (direction.Length < 0.001) return;
        direction /= direction.Length;
        var across = new Vector(-direction.Y, direction.X);
        var pen = new Pen(palette.Foreground, 1.35);
        void Bar(double offset)
        {
            var center = tip - direction * offset;
            context.DrawLine(pen, center - across * 5, center + across * 5);
        }

        if (head is FlowArrowHead.Many or FlowArrowHead.ZeroOrMany)
        {
            var stem = tip - direction * 12;
            context.DrawLine(pen, stem, tip - across * 6);
            context.DrawLine(pen, stem, tip + across * 6);
            context.DrawLine(pen, stem, tip);
            if (head == FlowArrowHead.Many) Bar(20);
            else context.DrawEllipse(palette.Fill, pen, tip - direction * 21, 3.5, 3.5);
        }
        else
        {
            Bar(6);
            if (head == FlowArrowHead.One) Bar(14);
            else context.DrawEllipse(palette.Fill, pen, tip - direction * 17, 3.5, 3.5);
        }
    }

    private static Rect BoundingBox(IReadOnlyList<DiagramPoint> points)
    {
        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxY = points.Max(p => p.Y);
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private static void FillPolygon(DrawingContext context, IReadOnlyList<DiagramPoint> points, IBrush brush, IPen? pen = null)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(P(points[0]), true);
            for (var i = 1; i < points.Count; i++)
            {
                g.LineTo(P(points[i]));
            }

            g.EndFigure(true);
        }

        context.DrawGeometry(brush, pen, geometry);
    }

    private static void DrawNode(DrawingContext context, LaidOutNode laidOut, Palette palette)
    {
        var rect = R(laidOut.Bounds);
        var pen = new Pen(palette.Accent, 1.2);
        var fill = palette.Fill;
        if (laidOut.Node.Shape is FlowNodeShape.Rectangle or FlowNodeShape.Rounded or FlowNodeShape.Subroutine or FlowNodeShape.Entity)
        {
            context.DrawRectangle(new SolidColorBrush(Colors.Black, 0.18), null,
                new Rect(rect.X + 2, rect.Y + 3, rect.Width, rect.Height), 2, 2);
        }
        switch (laidOut.Node.Shape)
        {
            case FlowNodeShape.InitialState:
                context.DrawEllipse(palette.Foreground, null, rect.Center, rect.Width / 2, rect.Height / 2);
                return;
            case FlowNodeShape.FinalState:
                context.DrawEllipse(fill, pen, rect.Center, rect.Width / 2, rect.Height / 2);
                context.DrawEllipse(palette.Foreground, null, rect.Center, rect.Width / 2 - 4, rect.Height / 2 - 4);
                return;
            case FlowNodeShape.Class:
                context.DrawRectangle(fill, pen, rect);
                var classHeader = Format(laidOut.Node.Label, palette.Foreground).Height + 20;
                DrawCentered(context, laidOut.Node.Label, new Rect(rect.X, rect.Y, rect.Width, classHeader), palette.Foreground, FontWeight.SemiBold);
                var classY = rect.Y + classHeader;
                foreach (var members in new[] { laidOut.Node.Attributes, laidOut.Node.Methods })
                {
                    context.DrawLine(pen, new Point(rect.X, classY), new Point(rect.Right, classY));
                    var memberText = string.Join('\n', members);
                    var memberHeight = Math.Max(20, memberText.Length == 0 ? 0 : Format(memberText, palette.Foreground).Height + 16);
                    if (memberText.Length > 0) context.DrawText(Format(memberText, palette.Foreground), new Point(rect.X + 12, classY + 8));
                    classY += memberHeight;
                }
                return;
            case FlowNodeShape.Entity:
                context.DrawRectangle(fill, pen, rect);
                if (laidOut.Node.Attributes.Count > 0)
                {
                    var headerHeight = Format(laidOut.Node.Label, palette.Foreground, FontWeight.SemiBold).Height + 20;
                    DrawCentered(context, laidOut.Node.Label, new Rect(rect.X + 12, rect.Y + 8, rect.Width - 24, headerHeight - 16), palette.Foreground, FontWeight.SemiBold);
                    context.DrawLine(pen, new Point(rect.Left, rect.Top + headerHeight), new Point(rect.Right, rect.Top + headerHeight));
                    var attributes = Format(string.Join('\n', laidOut.Node.Attributes), palette.Foreground);
                    attributes.MaxTextWidth = Math.Max(1, rect.Width - 24);
                    context.DrawText(attributes, new Point(rect.X + 12, rect.Y + headerHeight + 8));
                    return;
                }

                break;
            case FlowNodeShape.Rectangle:
                context.DrawRectangle(fill, pen, rect);
                break;
            case FlowNodeShape.Rounded:
                context.DrawRectangle(fill, pen, rect, 8, 8);
                break;
            case FlowNodeShape.Stadium:
                context.DrawRectangle(fill, pen, rect, rect.Height / 2, rect.Height / 2);
                break;
            case FlowNodeShape.Circle:
                context.DrawEllipse(fill, pen, rect.Center, rect.Width / 2, rect.Height / 2);
                break;
            case FlowNodeShape.Diamond:
                FillPolygon(context, [new(rect.Center.X, rect.Top), new(rect.Right, rect.Center.Y), new(rect.Center.X, rect.Bottom), new(rect.Left, rect.Center.Y)], fill, pen);
                break;
            case FlowNodeShape.Hexagon:
                {
                    var inset = Math.Min(rect.Width / 4, rect.Height / 2);
                    FillPolygon(context, [new(rect.Left + inset, rect.Top), new(rect.Right - inset, rect.Top), new(rect.Right, rect.Center.Y), new(rect.Right - inset, rect.Bottom), new(rect.Left + inset, rect.Bottom), new(rect.Left, rect.Center.Y)], fill, pen);
                    break;
                }

            case FlowNodeShape.Subroutine:
                context.DrawRectangle(fill, pen, rect);
                context.DrawLine(pen, new Point(rect.Left + 8, rect.Top), new Point(rect.Left + 8, rect.Bottom));
                context.DrawLine(pen, new Point(rect.Right - 8, rect.Top), new Point(rect.Right - 8, rect.Bottom));
                break;
            case FlowNodeShape.Cylinder:
                DrawCylinder(context, rect, fill, pen);
                break;
        }

        var text = rect;
        if (laidOut.Node.Shape == FlowNodeShape.Cylinder)
        {
            var cap = Math.Min(rect.Height * 0.2, 10);
            text = new Rect(rect.X, rect.Y + (cap * 1.5), rect.Width, Math.Max(0, rect.Height - (cap * 2.5)));
        }

        text = new Rect(text.X + 12, text.Y + 8, Math.Max(1, text.Width - 24), Math.Max(1, text.Height - 16));
        DrawCentered(context, laidOut.Node.Label, text, palette.Foreground);
    }

    private static void DrawCylinder(DrawingContext context, Rect rect, IBrush fill, IPen pen)
    {
        var cap = Math.Min(rect.Height * 0.2, 10);
        var rx = rect.Width / 2;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(rect.Left, rect.Top + cap), true);
            g.ArcTo(new Point(rect.Right, rect.Top + cap), new Size(rx, cap), 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(rect.Right, rect.Bottom - cap));
            g.ArcTo(new Point(rect.Left, rect.Bottom - cap), new Size(rx, cap), 0, false, SweepDirection.Clockwise);
            g.EndFigure(true);
        }

        context.DrawGeometry(fill, pen, geometry);
        context.DrawEllipse(null, pen, new Point(rect.Center.X, rect.Top + cap), rx, cap);
    }

    private static void DrawSequence(DrawingContext context, SequenceLayout layout, Palette palette)
    {
        var lifeline = new Pen(palette.Border, 1, DashStyle.Dash);
        var boxPen = new Pen(palette.Accent, 1.5);
        foreach (var participant in layout.Participants)
        {
            context.DrawLine(lifeline, new Point(participant.CenterX, participant.LifelineTop), new Point(participant.CenterX, participant.LifelineBottom));
        }

        foreach (var frame in layout.Frames)
        {
            DrawSequenceFrame(context, frame, palette);
        }

        foreach (var participant in layout.Participants)
        {
            foreach (var header in new[] { participant.Header, participant.Footer })
            {
                var rect = R(header);
                context.DrawRectangle(palette.Fill, boxPen, rect, participant.Participant.IsActor ? rect.Height / 2 : 4, participant.Participant.IsActor ? rect.Height / 2 : 4);
                DrawCentered(context, participant.Participant.Label, rect, palette.Foreground, FontWeight.SemiBold);
            }
        }

        foreach (var note in layout.Notes)
        {
            var rect = R(note.Bounds);
            context.DrawRectangle(palette.Fill, new Pen(palette.Muted, 1), rect, 2, 2);
            DrawCentered(context, note.Note.Text, rect, palette.Foreground);
        }

        foreach (var message in layout.Messages)
        {
            DrawMessage(context, message, palette);
        }
    }

    private static void DrawSequenceFrame(DrawingContext context, LaidOutFrame frame, Palette palette)
    {
        var rect = R(frame.Bounds);
        var pen = new Pen(palette.Muted, 1);
        context.DrawRectangle(null, pen, rect, 2, 2);
        var tag = Format(frame.Kind, palette.Foreground, FontWeight.SemiBold);
        var tagRect = new Rect(rect.X, rect.Y, tag.Width + 14, tag.Height + 4);
        context.DrawRectangle(palette.Fill, pen, tagRect);
        context.DrawText(tag, new Point(rect.X + 7, rect.Y + 2));
        if (frame.Label.Length > 0)
        {
            context.DrawText(Format("[" + frame.Label + "]", palette.Muted), new Point(tagRect.Right + 8, rect.Y + 2));
        }

        foreach (var divider in frame.Dividers)
        {
            context.DrawLine(new Pen(palette.Muted, 1, DashStyle.Dash), new Point(rect.Left, divider.Y), new Point(rect.Right, divider.Y));
            if (divider.Label.Length > 0)
            {
                context.DrawText(Format("[" + divider.Label + "]", palette.Muted), new Point(rect.X + 8, divider.Y + 2));
            }
        }
    }

    private static void DrawMessage(DrawingContext context, LaidOutMessage message, Palette palette)
    {
        if (message.Points.Count < 2)
        {
            return;
        }

        var pen = new Pen(palette.Foreground, 1.5, message.Message.IsDashed ? DashStyle.Dash : null, PenLineCap.Flat, PenLineJoin.Round);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(P(message.Points[0]), false);
            for (var i = 1; i < message.Points.Count; i++)
            {
                g.LineTo(P(message.Points[i]));
            }

            g.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);
        var head = message.HeadMarker;
        var solid = new Pen(palette.Foreground, 1.5);
        switch (message.Message.Head)
        {
            case SequenceHead.Filled when head.Count >= 3:
                FillPolygon(context, head, palette.Foreground);
                break;
            case SequenceHead.Open when head.Count >= 3:
                context.DrawLine(solid, P(head[0]), P(head[1]));
                context.DrawLine(solid, P(head[1]), P(head[2]));
                break;
            case SequenceHead.Cross when head.Count >= 3:
                {
                    var box = BoundingBox(head);
                    context.DrawLine(solid, box.TopLeft, box.BottomRight);
                    context.DrawLine(solid, box.TopRight, box.BottomLeft);
                    break;
                }
        }

        var text = message.Message.Number is { } n ? $"{n}. {message.Message.Text}" : message.Message.Text;
        DrawLabel(context, text, message.LabelBounds, palette);
    }
}
