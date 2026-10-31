using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Tern.Export;

/// <summary>The visual attributes of a run of text. <see cref="Link"/> is 0 for none, otherwise a 1-based index into the renderer's link table.</summary>
internal readonly record struct TextStyle(bool Bold, bool Italic, bool Mono, bool Strike, bool Underline, bool Highlight, int Script, int Link);

internal enum AtomKind
{
    Word,
    Space,
    LineBreak,
    Image,
}

/// <summary>An image ready to be drawn, with its natural size in points.</summary>
internal sealed class PdfImage(XImage image, double width, double height)
{
    public XImage Image { get; } = image;

    public double Width { get; } = width;

    public double Height { get; } = height;
}

/// <summary>The smallest unit of inline layout: a word, a collapsible space, a forced line break or an image.</summary>
internal sealed record Atom(AtomKind Kind, string Text, TextStyle Style, PdfImage? Image = null);

/// <summary>An atom positioned on a line. <see cref="Height"/> is only meaningful for images.</summary>
internal sealed class Placed(Atom atom, XFont? font, double fontSize, double x, double width, double height)
{
    public Atom Atom { get; } = atom;

    public XFont? Font { get; } = font;

    public double FontSize { get; } = fontSize;

    public double X { get; set; } = x;

    public double Width { get; } = width;

    public double Height { get; } = height;
}

/// <summary>One laid out line. <see cref="Baseline"/> is the distance from the top of the line to the text baseline.</summary>
internal sealed class LineBox
{
    public List<Placed> Items { get; } = [];

    public double Width { get; set; }

    public double Height { get; set; }

    public double Baseline { get; set; }
}

/// <summary>Font lookup, text measuring and line breaking.</summary>
internal sealed class PdfTypography : IDisposable
{
    public const double LineSpacing = 1.4;
    private const double BaselineFactor = 1.05;
    private const double DescentFactor = LineSpacing - BaselineFactor;

    private readonly XGraphics _measure = XGraphics.CreateMeasureContext(new XSize(4000, 4000), XGraphicsUnit.Point, XPageDirection.Downwards);
    private readonly Dictionary<(bool Mono, bool Bold, bool Italic, int Size), XFont> _fonts = [];
    private readonly Dictionary<XFont, Dictionary<string, double>> _widths = [];
    private readonly Dictionary<XFont, double> _spaces = [];

    public XFont Font(TextStyle style, double size)
    {
        var effective = EffectiveSize(style, size);
        var key = (style.Mono, style.Bold, style.Italic, (int)Math.Round(effective * 10));
        if (!_fonts.TryGetValue(key, out var font))
        {
            var family = style.Mono ? SystemFontResolver.MonoFamily : SystemFontResolver.SansFamily;
            var fontStyle = (style.Bold, style.Italic) switch
            {
                (true, true) => XFontStyleEx.BoldItalic,
                (true, false) => XFontStyleEx.Bold,
                (false, true) => XFontStyleEx.Italic,
                _ => XFontStyleEx.Regular,
            };
            font = new XFont(family, effective, fontStyle, new XPdfFontOptions(PdfFontEncoding.Unicode));
            _fonts[key] = font;
        }

        return font;
    }

    public static double EffectiveSize(TextStyle style, double size)
    {
        var effective = style.Mono ? size * 0.9 : size;
        return style.Script != 0 ? effective * 0.7 : effective;
    }

    public double Width(string text, XFont font)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        if (!_widths.TryGetValue(font, out var cache))
        {
            cache = new Dictionary<string, double>(StringComparer.Ordinal);
            _widths[font] = cache;
        }

        if (!cache.TryGetValue(text, out var width))
        {
            width = _measure.MeasureString(text, font).Width;
            if (cache.Count < 50_000)
            {
                cache[text] = width;
            }
        }

        return width;
    }

    public double SpaceWidth(XFont font)
    {
        if (!_spaces.TryGetValue(font, out var width))
        {
            width = Width("i i", font) - Width("ii", font);
            _spaces[font] = width;
        }

        return width;
    }

    /// <summary>The width a single atom needs, ignoring wrapping.</summary>
    public double AtomWidth(Atom atom, double size, double maxImageWidth)
    {
        switch (atom.Kind)
        {
            case AtomKind.Word:
                return Width(atom.Text, Font(atom.Style, size));
            case AtomKind.Space:
                return SpaceWidth(Font(atom.Style, size));
            case AtomKind.Image:
                return Math.Min(atom.Image!.Width, maxImageWidth);
            default:
                return 0;
        }
    }

    /// <summary>Breaks atoms into lines no wider than <paramref name="width"/>. Words longer than a line are split between characters.</summary>
    public List<LineBox> Break(IReadOnlyList<Atom> atoms, double width, double size, double maxImageHeight)
    {
        var lines = new List<LineBox>();
        var current = new LineBox();
        Atom? pendingSpace = null;

        void Finish()
        {
            Complete(current, size);
            lines.Add(current);
            current = new LineBox();
            pendingSpace = null;
        }

        foreach (var atom in atoms)
        {
            switch (atom.Kind)
            {
                case AtomKind.Space:
                    if (current.Items.Count > 0)
                    {
                        pendingSpace ??= atom;
                    }

                    break;
                case AtomKind.LineBreak:
                    Finish();
                    break;
                case AtomKind.Image:
                    var image = atom.Image!;
                    var scale = Math.Min(1, Math.Min(width / image.Width, maxImageHeight / image.Height));
                    var imageWidth = image.Width * scale;
                    var gap = pendingSpace is null ? 0 : SpaceWidth(Font(pendingSpace.Style, size));
                    if (current.Items.Count > 0 && current.Width + gap + imageWidth > width + 0.01)
                    {
                        Finish();
                        gap = 0;
                    }

                    PlaceSpace(current, pendingSpace, gap, size);
                    pendingSpace = null;
                    current.Items.Add(new Placed(atom, null, size, current.Width, imageWidth, image.Height * scale));
                    current.Width += imageWidth;
                    break;
                default:
                    PlaceWord(atom);
                    break;
            }
        }

        if (current.Items.Count > 0 || lines.Count == 0)
        {
            Finish();
        }

        return lines;

        void PlaceWord(Atom word)
        {
            var font = Font(word.Style, size);
            var effective = EffectiveSize(word.Style, size);
            var text = word.Text;
            while (text.Length > 0)
            {
                var wordWidth = Width(text, font);
                var gap = pendingSpace is null ? 0 : SpaceWidth(Font(pendingSpace.Style, size));
                if (current.Items.Count > 0 && current.Width + gap + wordWidth > width + 0.01)
                {
                    Finish();
                    gap = 0;
                }

                if (wordWidth <= width + 0.01)
                {
                    PlaceSpace(current, pendingSpace, gap, size);
                    pendingSpace = null;
                    current.Items.Add(new Placed(word with { Text = text }, font, effective, current.Width, wordWidth, 0));
                    current.Width += wordWidth;
                    return;
                }

                var take = FitPrefix(text, font, width - current.Width);
                var piece = text[..take];
                var pieceWidth = Width(piece, font);
                current.Items.Add(new Placed(word with { Text = piece }, font, effective, current.Width, pieceWidth, 0));
                current.Width += pieceWidth;
                text = text[take..];
                if (text.Length > 0)
                {
                    Finish();
                }
            }
        }
    }

    public void Dispose() => _measure.Dispose();

    private void PlaceSpace(LineBox line, Atom? space, double gap, double size)
    {
        if (space is null || gap <= 0 || line.Items.Count == 0)
        {
            return;
        }

        line.Items.Add(new Placed(space, Font(space.Style, size), EffectiveSize(space.Style, size), line.Width, gap, 0));
        line.Width += gap;
    }

    /// <summary>The longest prefix (at least one character, never splitting a surrogate pair) that fits in <paramref name="available"/>.</summary>
    private int FitPrefix(string text, XFont font, double available)
    {
        var low = 1;
        var high = text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (Width(text[..mid], font) <= available)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        if (low < text.Length && char.IsHighSurrogate(text[low - 1]) && low > 1)
        {
            low--;
        }

        return low;
    }

    private static void Complete(LineBox line, double size)
    {
        var baseline = size * BaselineFactor;
        var descent = size * DescentFactor;
        foreach (var item in line.Items)
        {
            if (item.Atom.Kind == AtomKind.Image)
            {
                baseline = Math.Max(baseline, item.Height);
                descent = Math.Max(descent, 2);
            }
            else
            {
                baseline = Math.Max(baseline, item.FontSize * BaselineFactor);
                descent = Math.Max(descent, item.FontSize * DescentFactor);
            }
        }

        line.Baseline = baseline;
        line.Height = baseline + descent;
    }
}

/// <summary>Turns arbitrary text into text the embedded fonts can show.</summary>
internal static class PdfText
{
    /// <summary>
    /// Removes control and invisible joiner characters and replaces characters outside the Basic Multilingual Plane
    /// (for example emoji) with '?', because the PDF font encoding used here addresses BMP code points only.
    /// </summary>
    public static string Safe(string? text)
    {
        var cleaned = ExportText.Clean(text);
        var builder = new StringBuilder(cleaned.Length);
        for (var i = 0; i < cleaned.Length; i++)
        {
            var c = cleaned[i];
            if (char.IsHighSurrogate(c) && i + 1 < cleaned.Length && char.IsLowSurrogate(cleaned[i + 1]))
            {
                builder.Append('?');
                i++;
            }
            else if (char.IsSurrogate(c))
            {
                builder.Append('?');
            }
            else if (c is '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF' or '\uFE0E' or '\uFE0F')
            {
                continue;
            }
            else if (c == '\t')
            {
                builder.Append("    ");
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
