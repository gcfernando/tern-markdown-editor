using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Tern.Rendering.Mathematics;

namespace Tern.PreviewView;

/// <summary>Brushes and pens used while drawing a formula.</summary>
internal sealed class MathPaint(IBrush brush)
{
    public IBrush Brush { get; } = brush;

    public Pen Stroke(double thickness) => new(Brush, Math.Max(0.8, thickness), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
}

/// <summary>A laid out piece of a formula. The origin passed to <see cref="Draw"/> is the left edge on the baseline.</summary>
internal abstract class MathBox
{
    public double Width { get; protected set; }

    public double Ascent { get; protected set; }

    public double Descent { get; protected set; }

    public double Height => Ascent + Descent;

    public abstract void Draw(DrawingContext context, Point origin, MathPaint paint);
}

internal sealed class MathTextBox : MathBox
{
    private readonly FormattedText _text;

    public MathTextBox(string text, Typeface typeface, double size)
    {
        _text = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, Brushes.Black);
        Width = _text.WidthIncludingTrailingWhitespace;
        Ascent = _text.Baseline;
        Descent = Math.Max(0, _text.Height - _text.Baseline);
    }

    public override void Draw(DrawingContext context, Point origin, MathPaint paint)
    {
        _text.SetForegroundBrush(paint.Brush);
        context.DrawText(_text, new Point(origin.X, origin.Y - Ascent));
    }
}

internal sealed class MathSpaceBox : MathBox
{
    public MathSpaceBox(double width) => Width = width;

    public override void Draw(DrawingContext context, Point origin, MathPaint paint)
    {
    }
}

internal sealed class MathRowBox : MathBox
{
    private readonly List<(MathBox Box, double X)> _items = [];

    public MathRowBox(IEnumerable<MathBox> boxes)
    {
        double x = 0;
        foreach (var box in boxes)
        {
            _items.Add((box, x));
            x += box.Width;
            Ascent = Math.Max(Ascent, box.Ascent);
            Descent = Math.Max(Descent, box.Descent);
        }

        Width = x;
    }

    public override void Draw(DrawingContext context, Point origin, MathPaint paint)
    {
        foreach (var (box, x) in _items)
        {
            box.Draw(context, new Point(origin.X + x, origin.Y), paint);
        }
    }
}

internal sealed class MathStackBox : MathBox
{
    private readonly List<(MathBox Box, double X, double Y)> _items = [];

    /// <summary>Boxes placed at explicit offsets from the baseline (positive Y is down).</summary>
    public MathStackBox(IEnumerable<(MathBox Box, double X, double Y)> items)
    {
        foreach (var item in items)
        {
            _items.Add(item);
            Width = Math.Max(Width, item.X + item.Box.Width);
            Ascent = Math.Max(Ascent, item.Box.Ascent - item.Y);
            Descent = Math.Max(Descent, item.Box.Descent + item.Y);
        }
    }

    public MathStackBox Padded(double ascent, double descent, double width)
    {
        Ascent = Math.Max(Ascent, ascent);
        Descent = Math.Max(Descent, descent);
        Width = Math.Max(Width, width);
        return this;
    }

    public override void Draw(DrawingContext context, Point origin, MathPaint paint)
    {
        foreach (var (box, x, y) in _items)
        {
            box.Draw(context, new Point(origin.X + x, origin.Y + y), paint);
        }
    }
}

internal sealed class MathRuleBox : MathBox
{
    private readonly double _thickness;
    private readonly double _axis;

    public MathRuleBox(double width, double thickness, double axis)
    {
        Width = width;
        _thickness = thickness;
        _axis = axis;
        Ascent = axis + (thickness / 2);
        Descent = Math.Max(0, (thickness / 2) - axis);
    }

    public override void Draw(DrawingContext context, Point origin, MathPaint paint)
    {
        var y = origin.Y - _axis;
        context.DrawLine(new Pen(paint.Brush, _thickness), new Point(origin.X, y), new Point(origin.X + Width, y));
    }
}

internal sealed class MathRadicalBox : MathBox
{
    private readonly MathBox _radicand;
    private readonly MathBox? _index;
    private readonly double _signWidth;
    private readonly double _thickness;
    private readonly double _indexX;

    public MathRadicalBox(MathBox radicand, MathBox? index, double size)
    {
        _radicand = radicand;
        _index = index;
        _thickness = Math.Max(1, size * 0.06);
        var gap = size * 0.14;
        var signWidth = size * 0.85;
        _indexX = 0;
        if (index is not null)
        {
            _indexX = Math.Max(0, index.Width - (signWidth * 0.45));
        }

        _signWidth = signWidth;
        Width = _indexX + signWidth + radicand.Width + (size * 0.1);
        Ascent = radicand.Ascent + gap + _thickness;
        Descent = radicand.Descent + (size * 0.05);
        Gap = gap;
    }

    private double Gap { get; }

    public override void Draw(DrawingContext context, Point origin, MathPaint paint)
    {
        var left = origin.X + _indexX;
        var top = origin.Y - Ascent + (_thickness / 2);
        var bottom = origin.Y + Descent;
        var height = bottom - top;
        var pen = paint.Stroke(_thickness);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(left, top + (height * 0.58)), false);
            g.LineTo(new Point(left + (_signWidth * 0.16), top + (height * 0.5)));
            g.LineTo(new Point(left + (_signWidth * 0.46), bottom));
            g.LineTo(new Point(left + _signWidth, top));
            g.LineTo(new Point(left + _signWidth + _radicand.Width + (Gap * 0.5), top));
            g.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);
        _radicand.Draw(context, new Point(left + _signWidth, origin.Y), paint);
        _index?.Draw(context, new Point(origin.X, top + (height * 0.45) - _index.Descent), paint);
    }
}

internal sealed class MathFenceBox : MathBox
{
    private readonly string _fence;
    private readonly double _thickness;
    private readonly double _axis;

    public MathFenceBox(string fence, double height, double axis, double size)
    {
        _fence = fence;
        _axis = axis;
        _thickness = Math.Max(1, size * 0.07);
        Width = fence switch
        {
            "{" or "}" => size * 0.42,
            "|" => size * 0.22,
            "‖" => size * 0.34,
            _ => size * 0.36,
        };
        Ascent = (height / 2) + axis;
        Descent = (height / 2) - axis;
    }

    public override void Draw(DrawingContext context, Point origin, MathPaint paint)
    {
        var top = origin.Y - Ascent;
        var bottom = origin.Y + Descent;
        var midY = (top + bottom) / 2;
        var x = origin.X;
        var w = Width;
        var pen = paint.Stroke(_thickness);
        var inset = w * 0.2;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            switch (_fence)
            {
                case "(":
                    g.BeginFigure(new Point(x + w - inset, top), false);
                    g.QuadraticBezierTo(new Point(x + inset - (w * 0.15), midY), new Point(x + w - inset, bottom));
                    break;
                case ")":
                    g.BeginFigure(new Point(x + inset, top), false);
                    g.QuadraticBezierTo(new Point(x + w - inset + (w * 0.15), midY), new Point(x + inset, bottom));
                    break;
                case "[":
                    g.BeginFigure(new Point(x + w - inset, top), false);
                    g.LineTo(new Point(x + inset, top));
                    g.LineTo(new Point(x + inset, bottom));
                    g.LineTo(new Point(x + w - inset, bottom));
                    break;
                case "]":
                    g.BeginFigure(new Point(x + inset, top), false);
                    g.LineTo(new Point(x + w - inset, top));
                    g.LineTo(new Point(x + w - inset, bottom));
                    g.LineTo(new Point(x + inset, bottom));
                    break;
                case "⌊":
                    g.BeginFigure(new Point(x + inset, top), false);
                    g.LineTo(new Point(x + inset, bottom));
                    g.LineTo(new Point(x + w - inset, bottom));
                    break;
                case "⌋":
                    g.BeginFigure(new Point(x + w - inset, top), false);
                    g.LineTo(new Point(x + w - inset, bottom));
                    g.LineTo(new Point(x + inset, bottom));
                    break;
                case "⌈":
                    g.BeginFigure(new Point(x + inset, bottom), false);
                    g.LineTo(new Point(x + inset, top));
                    g.LineTo(new Point(x + w - inset, top));
                    break;
                case "⌉":
                    g.BeginFigure(new Point(x + w - inset, bottom), false);
                    g.LineTo(new Point(x + w - inset, top));
                    g.LineTo(new Point(x + inset, top));
                    break;
                case "⟨":
                    g.BeginFigure(new Point(x + w - inset, top), false);
                    g.LineTo(new Point(x + inset, midY));
                    g.LineTo(new Point(x + w - inset, bottom));
                    break;
                case "⟩":
                    g.BeginFigure(new Point(x + inset, top), false);
                    g.LineTo(new Point(x + w - inset, midY));
                    g.LineTo(new Point(x + inset, bottom));
                    break;
                case "{":
                    BraceFigure(g, x, w, top, bottom, midY, opening: true);
                    break;
                case "}":
                    BraceFigure(g, x, w, top, bottom, midY, opening: false);
                    break;
                case "‖":
                    g.BeginFigure(new Point(x + (w * 0.3), top), false);
                    g.LineTo(new Point(x + (w * 0.3), bottom));
                    g.EndFigure(false);
                    g.BeginFigure(new Point(x + (w * 0.7), top), false);
                    g.LineTo(new Point(x + (w * 0.7), bottom));
                    break;
                default:
                    g.BeginFigure(new Point(x + (w / 2), top), false);
                    g.LineTo(new Point(x + (w / 2), bottom));
                    break;
            }

            g.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);
    }

    private static void BraceFigure(StreamGeometryContext g, double x, double w, double top, double bottom, double midY, bool opening)
    {
        // The tip points away from the body; the two arms curve in towards it.
        var outer = opening ? x + (w * 0.2) : x + (w * 0.8);
        var inner = opening ? x + (w * 0.8) : x + (w * 0.2);
        var centre = (outer + inner) / 2;
        var quarter1 = top + ((midY - top) / 2);
        var quarter3 = midY + ((bottom - midY) / 2);
        g.BeginFigure(new Point(inner, top), false);
        g.CubicBezierTo(new Point(centre, top), new Point(centre, top), new Point(centre, quarter1));
        g.LineTo(new Point(centre, midY - (w * 0.2)));
        g.CubicBezierTo(new Point(centre, midY), new Point(outer, midY), new Point(outer, midY));
        g.CubicBezierTo(new Point(outer, midY), new Point(centre, midY), new Point(centre, midY + (w * 0.2)));
        g.LineTo(new Point(centre, quarter3));
        g.CubicBezierTo(new Point(centre, bottom), new Point(centre, bottom), new Point(inner, bottom));
    }
}

internal sealed class MathAccentBox : MathBox
{
    private readonly MathBox _base;
    private readonly string _mark;
    private readonly double _size;

    public MathAccentBox(MathBox @base, string mark, double size)
    {
        _base = @base;
        _mark = mark;
        _size = size;
        Width = @base.Width;
        var below = mark == "\u0332";
        Ascent = below ? @base.Ascent : @base.Ascent + (size * 0.22);
        Descent = below ? @base.Descent + (size * 0.14) : @base.Descent;
    }

    public override void Draw(DrawingContext context, Point origin, MathPaint paint)
    {
        _base.Draw(context, origin, paint);
        var pen = paint.Stroke(Math.Max(1, _size * 0.06));
        var centre = origin.X + (Width / 2);
        var y = origin.Y - _base.Ascent - (_size * 0.1);
        var half = Math.Clamp(Width * 0.35, _size * 0.18, _size * 0.5);
        switch (_mark)
        {
            case "\u0332":
                var under = origin.Y + _base.Descent + (_size * 0.08);
                context.DrawLine(pen, new Point(origin.X, under), new Point(origin.X + Width, under));
                break;
            case "\u0304":
            case "\u0305":
                context.DrawLine(pen, new Point(origin.X + (_size * 0.05), y), new Point(origin.X + Width - (_size * 0.05), y));
                break;
            case "\u0302":
                var hat = new StreamGeometry();
                using (var g = hat.Open())
                {
                    g.BeginFigure(new Point(centre - half, y + (_size * 0.09)), false);
                    g.LineTo(new Point(centre, y - (_size * 0.04)));
                    g.LineTo(new Point(centre + half, y + (_size * 0.09)));
                    g.EndFigure(false);
                }

                context.DrawGeometry(null, pen, hat);
                break;
            case "\u0303":
                var tilde = new StreamGeometry();
                using (var g = tilde.Open())
                {
                    g.BeginFigure(new Point(centre - half, y + (_size * 0.05)), false);
                    g.CubicBezierTo(new Point(centre - (half * 0.4), y - (_size * 0.08)), new Point(centre + (half * 0.1), y + (_size * 0.14)), new Point(centre + half, y));
                    g.EndFigure(false);
                }

                context.DrawGeometry(null, pen, tilde);
                break;
            case "\u20D7":
                context.DrawLine(pen, new Point(centre - half, y), new Point(centre + half, y));
                context.DrawLine(pen, new Point(centre + half - (_size * 0.1), y - (_size * 0.07)), new Point(centre + half, y));
                context.DrawLine(pen, new Point(centre + half - (_size * 0.1), y + (_size * 0.07)), new Point(centre + half, y));
                break;
            case "\u0307":
                context.DrawEllipse(paint.Brush, null, new Point(centre, y), _size * 0.045, _size * 0.045);
                break;
            case "\u0308":
                context.DrawEllipse(paint.Brush, null, new Point(centre - (_size * 0.1), y), _size * 0.045, _size * 0.045);
                context.DrawEllipse(paint.Brush, null, new Point(centre + (_size * 0.1), y), _size * 0.045, _size * 0.045);
                break;
            default:
                break;
        }
    }
}
