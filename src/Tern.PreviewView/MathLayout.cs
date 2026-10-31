using Avalonia.Media;
using Tern.Rendering.Mathematics;

namespace Tern.PreviewView;

/// <summary>Turns the parsed formula tree from <c>Tern.Rendering.Mathematics</c> into drawable boxes.</summary>
internal sealed class MathLayout(double baseSize)
{
    private const string SerifFamily = "Cambria Math, STIX Two Math, Latin Modern Math, Times New Roman, Noto Serif, serif";

    private static readonly FontFamily Serif = new(SerifFamily);
    private static readonly FontFamily Sans = new("Segoe UI, Arial, Noto Sans, sans-serif");
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, DejaVu Sans Mono, monospace");

    private readonly double _baseSize = baseSize;

    public static MathBox Build(MathNode root, double size) => new MathLayout(size).Layout(root, size, 0);

    private static Typeface TypefaceFor(MathStyle style) => style switch
    {
        MathStyle.Italic => new Typeface(Serif, FontStyle.Italic),
        MathStyle.Bold => new Typeface(Serif, FontStyle.Normal, FontWeight.Bold),
        MathStyle.Calligraphic => new Typeface(Serif, FontStyle.Italic, FontWeight.SemiBold),
        MathStyle.Fraktur => new Typeface(Serif, FontStyle.Italic, FontWeight.Bold),
        MathStyle.SansSerif => new Typeface(Sans),
        MathStyle.Monospace => new Typeface(Mono),
        _ => new Typeface(Serif),
    };

    private static MathRunKind? KindOf(MathNode node) => node switch
    {
        MathRun run => run.Kind,
        MathScripts scripts => KindOf(scripts.Base),
        MathGroup { Children.Count: > 0 } group => KindOf(group.Children[0]),
        _ => null,
    };

    private static MathRunKind? LastKindOf(MathNode node) => node switch
    {
        MathRun run => run.Kind,
        MathScripts scripts => LastKindOf(scripts.Base),
        MathGroup { Children.Count: > 0 } group => LastKindOf(group.Children[^1]),
        _ => null,
    };

    private MathBox Layout(MathNode node, double size, int level)
    {
        return node switch
        {
            MathRun run => LayoutRun(run, size, level),
            MathGroup group => LayoutGroup(group, size, level),
            MathFraction fraction => LayoutFraction(fraction, size, level),
            MathRoot root => new MathRadicalBox(Layout(root.Radicand, size, level), root.Index is null ? null : Layout(root.Index, size * 0.55, level + 1), size),
            MathScripts scripts => LayoutScripts(scripts, size, level),
            MathDelimited delimited => LayoutDelimited(delimited, size, level),
            MathMatrix matrix => LayoutMatrix(matrix, size, level),
            MathAccent accent => new MathAccentBox(Layout(accent.Base, size, level), accent.Mark, size),
            MathSpace space => new MathSpaceBox(space.Em * size),
            _ => new MathSpaceBox(0),
        };
    }

    private MathBox LayoutRun(MathRun run, double size, int level)
    {
        var effective = run.Kind == MathRunKind.BigOperator && level == 0 ? size * 1.35 : size;
        var box = new MathTextBox(run.Text, TypefaceFor(run.Style), effective);
        if (run.Kind != MathRunKind.BigOperator || level != 0)
        {
            return box;
        }

        // Large operators are centred on the math axis rather than sitting on the baseline.
        var shift = (box.Height / 2) - (size * 0.28) - box.Descent;
        return new MathStackBox([(box, 0, shift)]);
    }

    private MathBox LayoutGroup(MathGroup group, double size, int level)
    {
        if (group.Children.Any(c => c is MathLineBreak))
        {
            var lines = new List<List<MathNode>> { new() };
            foreach (var child in group.Children)
            {
                if (child is MathLineBreak)
                {
                    lines.Add([]);
                }
                else
                {
                    lines[^1].Add(child);
                }
            }

            var items = new List<(MathBox, double, double)>();
            double y = 0;
            MathBox? previous = null;
            foreach (var line in lines)
            {
                var box = LayoutRow(line, size, level);
                if (previous is not null)
                {
                    y += previous.Descent + (size * 0.3) + box.Ascent;
                }

                items.Add((box, 0, y));
                previous = box;
            }

            return new MathStackBox(items);
        }

        return LayoutRow(group.Children, size, level);
    }

    private MathBox LayoutRow(IReadOnlyList<MathNode> children, double size, int level)
    {
        var boxes = new List<MathBox>(children.Count * 2);
        MathRunKind? previousKind = null;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var kind = KindOf(child);
            if (i > 0 && level < 2)
            {
                var gap = SpaceBetween(previousKind, kind, children[i - 1]);
                if (gap > 0)
                {
                    boxes.Add(new MathSpaceBox(gap * size));
                }
            }

            boxes.Add(Layout(child, size, level));
            previousKind = LastKindOf(child);
        }

        return new MathRowBox(boxes);
    }

    private static double SpaceBetween(MathRunKind? previous, MathRunKind? next, MathNode previousNode)
    {
        if (previousNode is MathSpace || previous is null || next is null)
        {
            return 0;
        }

        if (next == MathRunKind.Relation || previous == MathRunKind.Relation)
        {
            return 0.28;
        }

        if (next == MathRunKind.Operator)
        {
            return previous is MathRunKind.Operator or MathRunKind.Punctuation or MathRunKind.Delimiter ? 0 : 0.22;
        }

        if (previous == MathRunKind.Operator)
        {
            // A prefix sign such as the minus in "(-x)" binds tightly; only a binary operator gets space on both sides.
            return 0.22;
        }

        return previous switch
        {
            MathRunKind.Punctuation => 0.17,
            MathRunKind.FunctionName => next is MathRunKind.Delimiter ? 0 : 0.17,
            MathRunKind.BigOperator => 0.17,
            _ => 0,
        };
    }

    private MathBox LayoutFraction(MathFraction fraction, double size, int level)
    {
        var partSize = level == 0 ? size : size * 0.85;
        var numerator = Layout(fraction.Numerator, partSize, level + 1);
        var denominator = Layout(fraction.Denominator, partSize, level + 1);
        var axis = size * 0.28;
        var thickness = fraction.HasRule ? Math.Max(1, size * 0.06) : 0;
        var gap = size * 0.16;
        var width = Math.Max(numerator.Width, denominator.Width) + (size * 0.3);
        var numeratorBaseline = -(axis + (thickness / 2) + gap + numerator.Descent);
        var denominatorBaseline = -axis + (thickness / 2) + gap + denominator.Ascent;
        var items = new List<(MathBox, double, double)>
        {
            (numerator, (width - numerator.Width) / 2, numeratorBaseline),
            (denominator, (width - denominator.Width) / 2, denominatorBaseline),
        };

        if (fraction.HasRule)
        {
            items.Add((new MathRuleBox(width, thickness, axis), 0, 0));
        }

        return new MathStackBox(items).Padded(0, 0, width);
    }

    private MathBox LayoutScripts(MathScripts scripts, double size, int level)
    {
        var baseBox = Layout(scripts.Base, size, level);
        var scriptSize = Math.Max(size * 0.7, _baseSize * 0.45);
        var sup = scripts.Superscript is null ? null : Layout(scripts.Superscript, scriptSize, level + 1);
        var sub = scripts.Subscript is null ? null : Layout(scripts.Subscript, scriptSize, level + 1);

        if (scripts.UseLimits)
        {
            var gap = size * 0.1;
            var width = Math.Max(baseBox.Width, Math.Max(sup?.Width ?? 0, sub?.Width ?? 0));
            var items = new List<(MathBox, double, double)> { (baseBox, (width - baseBox.Width) / 2, 0) };
            if (sup is not null)
            {
                items.Add((sup, (width - sup.Width) / 2, -(baseBox.Ascent + gap + sup.Descent)));
            }

            if (sub is not null)
            {
                items.Add((sub, (width - sub.Width) / 2, baseBox.Descent + gap + sub.Ascent));
            }

            return new MathStackBox(items);
        }

        var raise = Math.Max(size * 0.38, baseBox.Ascent - (size * 0.3));
        var lower = Math.Max(size * 0.18, baseBox.Descent + (size * 0.1));
        if (sup is not null && sub is not null)
        {
            var clearance = (raise - sup.Descent) - (sub.Ascent - lower);
            var minimum = size * 0.12;
            if (clearance < minimum)
            {
                lower += minimum - clearance;
            }
        }

        var parts = new List<(MathBox, double, double)> { (baseBox, 0, 0) };
        double extra = size * 0.04;
        if (sup is not null)
        {
            parts.Add((sup, baseBox.Width + extra, -raise));
        }

        if (sub is not null)
        {
            parts.Add((sub, baseBox.Width + extra, lower));
        }

        return new MathStackBox(parts).Padded(0, 0, baseBox.Width + extra + Math.Max(sup?.Width ?? 0, sub?.Width ?? 0));
    }

    private MathBox LayoutDelimitedBody(MathBox body, string left, string right, double size)
    {
        var axis = size * 0.28;
        // Fences are symmetric about the axis and a little taller than the content.
        var halfAbove = body.Ascent - axis;
        var halfBelow = body.Descent + axis;
        var height = (Math.Max(halfAbove, halfBelow) * 2) + (size * 0.2);
        height = Math.Max(height, size * 1.2);
        var parts = new List<MathBox>();
        if (left.Length > 0)
        {
            parts.Add(new MathFenceBox(left, height, axis, size));
        }

        parts.Add(body);
        if (right.Length > 0)
        {
            parts.Add(new MathFenceBox(right, height, axis, size));
        }

        return new MathRowBox(parts);
    }

    private MathBox LayoutDelimited(MathDelimited delimited, double size, int level) =>
        LayoutDelimitedBody(Layout(delimited.Body, size, level), Normalize(delimited.Left), Normalize(delimited.Right), size);

    private static string Normalize(string fence) => fence is "." or "" ? string.Empty : fence;

    private MathBox LayoutMatrix(MathMatrix matrix, double size, int level)
    {
        var columns = matrix.Rows.Count == 0 ? 0 : matrix.Rows.Max(r => r.Count);
        var cells = matrix.Rows.Select(r => r.Select(c => Layout(c, size, level + 1)).ToList()).ToList();
        var widths = new double[columns];
        foreach (var row in cells)
        {
            for (var c = 0; c < row.Count; c++)
            {
                widths[c] = Math.Max(widths[c], row[c].Width);
            }
        }

        double ColumnGap(int column) => matrix.Kind switch
        {
            MathMatrixKind.Aligned => column % 2 == 0 ? size * 0.1 : size * 1.5,
            _ => size * 1,
        };

        var xs = new double[columns];
        double x = 0;
        for (var c = 0; c < columns; c++)
        {
            xs[c] = x;
            x += widths[c] + (c < columns - 1 ? ColumnGap(c) : 0);
        }

        var totalWidth = x;
        var rowGap = size * 0.35;
        var items = new List<(MathBox, double, double)>();
        double y = 0;
        var rowBaselines = new List<double>();
        for (var r = 0; r < cells.Count; r++)
        {
            var ascent = cells[r].Count == 0 ? 0 : cells[r].Max(c => c.Ascent);
            var descent = cells[r].Count == 0 ? 0 : cells[r].Max(c => c.Descent);
            ascent = Math.Max(ascent, size * 0.8);
            descent = Math.Max(descent, size * 0.2);
            if (r > 0)
            {
                y += rowGap;
            }

            var baseline = y + ascent;
            rowBaselines.Add(baseline);
            for (var c = 0; c < cells[r].Count; c++)
            {
                var cell = cells[r][c];
                var offset = matrix.Kind switch
                {
                    MathMatrixKind.Cases => 0,
                    MathMatrixKind.Aligned => c % 2 == 0 ? widths[c] - cell.Width : 0,
                    _ => (widths[c] - cell.Width) / 2,
                };
                items.Add((cell, xs[c] + offset, baseline));
            }

            y += ascent + descent;
        }

        // Centre the block on the math axis: shift every row so the block's midpoint sits at the axis.
        var axis = size * 0.28;
        var shift = -((y / 2) + axis);
        var shifted = items.Select(i => (i.Item1, i.Item2, i.Item3 + shift)).ToList();
        var body = new MathStackBox(shifted).Padded((y / 2) + axis, (y / 2) - axis, totalWidth);
        var inner = LayoutDelimitedBody(body, Normalize(matrix.Left), Normalize(matrix.Right), size);
        return matrix.Kind == MathMatrixKind.Cases ? new MathRowBox([inner, new MathSpaceBox(size * 0.1)]) : inner;
    }
}
