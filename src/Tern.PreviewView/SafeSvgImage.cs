using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

namespace Tern.PreviewView;

/// <summary>A bounded, non-interactive SVG image reader. It never resolves a URI or executes document code.</summary>
internal static partial class SafeSvgImage
{
    private const int MaxBytes = 1024 * 1024;
    private const int MaxNodes = 10_000;
    private const string SvgNamespace = "http://www.w3.org/2000/svg";

    public static bool TryCreate(byte[] data, out Control? control)
    {
        control = null;
        try
        {
            var drawing = Parse(data, new Budget(), 0);
            control = new SvgControl(drawing);
            return true;
        }
        catch (Exception ex) when (ex is XmlException or FormatException or ArgumentException or InvalidOperationException or OverflowException or NotSupportedException)
        {
            return false;
        }
    }

    private sealed class Budget
    {
        public int Bytes;
        public int Nodes;
        public int Operations;
        public int TextCharacters;
    }

    private sealed record Drawing(double Width, double Height, SvgNode Root, string Name);

    private static Drawing Parse(byte[] data, Budget budget, int imageDepth)
    {
        if (imageDepth > 8 || data.Length == 0 || data.Length > MaxBytes || (budget.Bytes += data.Length) > MaxBytes) throw Invalid();
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxBytes,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        using var stream = new MemoryStream(data, writable: false);
        using (var validation = XmlReader.Create(stream, settings))
        {
            while (validation.Read())
            {
                if (validation.Depth > 32) throw Invalid();
                if (validation.NodeType == XmlNodeType.Element && (++budget.Nodes > MaxNodes || validation.AttributeCount > 64)) throw Invalid();
            }
        }

        stream.Position = 0;
        using var reader = XmlReader.Create(stream, settings);
        var root = XDocument.Load(reader).Root ?? throw Invalid();
        if (root.Name.LocalName != "svg" || root.Name.NamespaceName is not ("" or SvgNamespace)) throw Invalid();
        var context = new ParseContext(budget, imageDepth);
        context.CollectDefinitions(root);
        var viewBox = Values(Attr(root, "viewBox"));
        if (viewBox.Length is not (0 or 4) || viewBox.Length == 4 && (viewBox[2] <= 0 || viewBox[3] <= 0)) throw Invalid();
        var width = Length(Attr(root, "width"), viewBox.Length == 4 ? viewBox[2] : 300);
        var height = Length(Attr(root, "height"), viewBox.Length == 4 ? viewBox[3] : 150);
        if (width < 0.001 || height < 0.001 || width > 4096 || height > 4096 || width * height > 16_000_000) throw Invalid();
        var viewport = Matrix.Identity;
        if (viewBox.Length == 4)
        {
            var aspect = Attr(root, "preserveAspectRatio") ?? "xMidYMid meet";
            if (aspect is not ("xMidYMid meet" or "xMidYMid" or "none")) throw Invalid();
            var sx = width / viewBox[2];
            var sy = height / viewBox[3];
            if (aspect != "none") sx = sy = Math.Min(sx, sy);
            viewport = Matrix.CreateScale(sx, sy) * Matrix.CreateTranslation(-viewBox[0] * sx + (width - viewBox[2] * sx) / 2, -viewBox[1] * sy + (height - viewBox[3] * sy) / 2);
            ValidateMatrix(viewport);
        }

        var node = context.Read(root, Paint.Default) ?? throw Invalid();
        var name = Attr(root, "aria-label") ?? root.Elements().FirstOrDefault(element => element.Name.LocalName == "title")?.Value ?? "SVG image";
        return new Drawing(width, height, new GroupNode(viewport, 1, null, [node]), name);
    }

    private sealed class ParseContext(Budget budget, int imageDepth)
    {
        private readonly Dictionary<string, XElement> _gradients = new(StringComparer.Ordinal);
        private readonly Dictionary<string, XElement> _clips = new(StringComparer.Ordinal);
        private readonly Dictionary<string, XElement> _filters = new(StringComparer.Ordinal);

        public void CollectDefinitions(XElement root)
        {
            foreach (var element in root.DescendantsAndSelf())
            {
                ValidateElement(element);
                if (element.Name.LocalName == "tspan" && element.Parent?.Name.LocalName is not ("text" or "tspan")) throw Invalid();
                if (element.Name.LocalName is not ("svg" or "g" or "rect" or "circle" or "ellipse" or "path" or "line" or "polygon" or "polyline"
                    or "text" or "tspan" or "image" or "defs" or "linearGradient" or "stop" or "clipPath" or "title" or "desc" or "filter" or "feGaussianBlur")) throw Invalid();
                if (element.Name.LocalName is not ("linearGradient" or "clipPath" or "filter")) continue;
                var id = Attr(element, "id") ?? throw Invalid();
                if (id.Length > 128 || id.Length == 0) throw Invalid();
                var definitions = element.Name.LocalName == "linearGradient" ? _gradients : element.Name.LocalName == "clipPath" ? _clips : _filters;
                if (!definitions.TryAdd(id, element)) throw Invalid();
            }
        }

        public SvgNode? Read(XElement element, Paint inherited)
        {
            ValidateElement(element);
            var name = element.Name.LocalName;
            if (name is "title" or "desc") return null;
            if (name == "filter") { _ = Blur(element); return null; }
            if (name is "defs" or "linearGradient" or "clipPath")
            {
                ValidateDefinition(element);
                return null;
            }

            var paint = inherited.Read(element);
            double? blur = null;
            if (Attr(element, "filter") is { } filter)
            {
                if (name != "text" || !_filters.TryGetValue(LocalReference(filter), out var definition)) throw Invalid();
                blur = Blur(definition);
            }
            var transform = Transform(Attr(element, "transform"));
            var opacity = Unit(Attr(element, "opacity"), 1);
            Geometry? clip = null;
            if (Attr(element, "clip-path") is { } clipping)
            {
                var id = LocalReference(clipping);
                if (!_clips.TryGetValue(id, out var definition)) throw Invalid();
                clip = Clip(definition);
            }

            if (name is "svg" or "g")
            {
                if (name == "svg" && element.Parent is not null) throw Invalid();
                var children = element.Elements().Select(child => Read(child, paint)).OfType<SvgNode>().ToArray();
                return new GroupNode(transform, opacity, clip, children);
            }

            if (name == "text")
            {
                if ((budget.TextCharacters += element.Value.Length) > 65_536 || element.Value.Length > 8192) throw Invalid();
                if (element.HasElements)
                {
                    if (blur is not null || Attr(element, "textLength") is not null) throw Invalid();
                    var runs = new List<SvgNode>();
                    double penX = Length(Attr(element, "dx")), penY = Length(Attr(element, "dy"));
                    var count = 0;
                    AddRuns(element, paint, Shift(element, paint.FontSize), 1);
                    var offset = paint.Anchor switch { "middle" => penX / 2, "end" => penX, _ => 0 };
                    var placement = Matrix.CreateTranslation(Length(Attr(element, "x")) - offset, Length(Attr(element, "y")));
                    ValidateMatrix(placement);
                    return new GroupNode(transform, opacity, clip, [new GroupNode(placement, 1, null, runs)]);

                    void AddRuns(XElement container, Paint inheritedPaint, double baselineShift, double inheritedOpacity)
                    {
                        foreach (var node in container.Nodes())
                        {
                            if (node is XText value)
                            {
                                if (++count > 1024) throw Invalid();
                                var fill = Brush(inheritedPaint.Fill, inheritedPaint.FillOpacity, inheritedPaint.Color);
                                var formatted = new FormattedText(value.Value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                    new Typeface(new FontFamily(inheritedPaint.Family), inheritedPaint.FontStyle, inheritedPaint.Weight), inheritedPaint.FontSize, fill ?? Brushes.Transparent);
                                runs.Add(new GroupNode(Matrix.Identity, inheritedOpacity, null,
                                    [new TextNode(value.Value, penX, penY - baselineShift, 0, inheritedPaint with { Anchor = "start" }, fill, null)]));
                                penX += formatted.WidthIncludingTrailingWhitespace;
                                if (!double.IsFinite(penX) || Math.Abs(penX) > 1_000_000) throw Invalid();
                            }
                            else if (node is XElement span)
                            {
                                if (span.Name.LocalName != "tspan" || span.Attributes().Any(attribute => attribute.Name.LocalName is "x" or "y" or "rotate" or "textLength" or "lengthAdjust" or "text-anchor" or "transform" or "filter" or "clip-path" or "dominant-baseline")) throw Invalid();
                                var spanPaint = inheritedPaint.Read(span);
                                penX += Length(Attr(span, "dx"));
                                penY += Length(Attr(span, "dy"));
                                if (Math.Abs(penX) > 1_000_000 || Math.Abs(penY) > 1_000_000) throw Invalid();
                                AddRuns(span, spanPaint, baselineShift + Shift(span, spanPaint.FontSize), inheritedOpacity * Unit(Attr(span, "opacity"), 1));
                            }
                            else if (node is not XComment) throw Invalid();
                        }
                    }
                }
                var text = new TextNode(element.Value, Length(Attr(element, "x")) + Length(Attr(element, "dx")), Length(Attr(element, "y")) + Length(Attr(element, "dy")) - Shift(element, paint.FontSize),
                    Length(Attr(element, "textLength")), paint, Brush(paint.Fill, paint.FillOpacity, paint.Color), blur);
                return new GroupNode(transform, opacity, clip, [text]);
            }

            if (name == "image")
            {
                var href = Attr(element, "href") ?? throw Invalid();
                const string prefix = "data:image/svg+xml;base64,";
                if (!href.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || href.Length > MaxBytes * 2) throw Invalid();
                var nested = Parse(Convert.FromBase64String(href[prefix.Length..]), budget, imageDepth + 1);
                var width = Length(Attr(element, "width"), nested.Width);
                var height = Length(Attr(element, "height"), nested.Height);
                if (width <= 0 || height <= 0 || width > 4096 || height > 4096) throw Invalid();
                var fit = Math.Min(width / nested.Width, height / nested.Height);
                var placement = Matrix.CreateScale(fit, fit) * Matrix.CreateTranslation(Length(Attr(element, "x")) + (width - nested.Width * fit) / 2,
                    Length(Attr(element, "y")) + (height - nested.Height * fit) / 2) * transform;
                ValidateMatrix(placement);
                return new GroupNode(placement, opacity, clip, [nested.Root]);
            }

            var geometry = Shape(element, budget);
            if (geometry is null) throw Invalid();
            return new ShapeNode(transform, opacity, clip, geometry, Brush(paint.Fill, paint.FillOpacity, paint.Color),
                Brush(paint.Stroke, paint.StrokeOpacity, paint.Color) is { } stroke ? new Pen(stroke, paint.StrokeWidth) : null);
        }

        private static double Shift(XElement element, double fontSize)
        {
            var value = Attr(element, "baseline-shift");
            var shift = value switch
            {
                null or "baseline" => 0,
                "sub" => -fontSize * .2,
                "super" => fontSize * .4,
                _ when value.EndsWith('%') => Scalar(value[..^1]) * fontSize / 100,
                _ => Length(value),
            };
            if (!double.IsFinite(shift) || Math.Abs(shift) > 4096) throw Invalid();
            return shift;
        }

        private void ValidateDefinition(XElement element)
        {
            foreach (var item in element.DescendantsAndSelf())
            {
                ValidateElement(item);
                if (item.Name.LocalName is not ("defs" or "linearGradient" or "stop" or "clipPath" or "rect" or "path" or "circle" or "ellipse" or "polygon" or "polyline" or "line" or "g" or "filter" or "feGaussianBlur")) throw Invalid();
                if (item.Name.LocalName == "filter") _ = Blur(item);
            }
        }

        private static double Blur(XElement definition)
        {
            var children = definition.Elements().ToArray();
            if (children.Length != 1 || children[0].Name.LocalName != "feGaussianBlur" || children[0].HasElements
                || Attr(children[0], "in") is { } input && input != "SourceGraphic") throw Invalid();
            var values = Values(Attr(children[0], "stdDeviation"));
            if (values.Length is not (1 or 2) || values.Length == 2 && values[0] != values[1] || values[0] < 0 || values[0] > 32) throw Invalid();
            return values[0];
        }

        private Geometry Clip(XElement definition)
        {
            if (Attr(definition, "clipPathUnits") is { } units && units != "userSpaceOnUse") throw Invalid();
            var geometry = new GeometryGroup { FillRule = FillRule.NonZero };
            foreach (var child in definition.Elements())
            {
                var shape = Shape(child, budget) ?? throw Invalid();
                shape.Transform = new MatrixTransform(Transform(Attr(child, "transform")));
                geometry.Children.Add(shape);
            }

            geometry.Transform = new MatrixTransform(Transform(Attr(definition, "transform")));
            return geometry;
        }

        private IBrush? Brush(string value, double opacity, string currentColor)
        {
            if (value == "none") return null;
            if (value == "currentColor") value = currentColor;
            if (!value.StartsWith("url(", StringComparison.Ordinal)) return new SolidColorBrush(Color.Parse(value), opacity);
            var id = LocalReference(value);
            if (!_gradients.TryGetValue(id, out var definition)) throw Invalid();
            if (Attr(definition, "gradientUnits") is { } units && units != "objectBoundingBox" || Attr(definition, "gradientTransform") is not null) throw Invalid();
            var brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(Fraction(Attr(definition, "x1"), 0), Fraction(Attr(definition, "y1"), 0), RelativeUnit.Relative),
                EndPoint = new RelativePoint(Fraction(Attr(definition, "x2"), 1), Fraction(Attr(definition, "y2"), 0), RelativeUnit.Relative),
                Opacity = opacity,
            };
            foreach (var stop in definition.Elements())
            {
                if (stop.Name.LocalName != "stop") throw Invalid();
                var color = Color.Parse(Attr(stop, "stop-color") ?? "#000");
                var alpha = Unit(Attr(stop, "stop-opacity"), 1);
                color = Color.FromArgb((byte)Math.Round(color.A * alpha), color.R, color.G, color.B);
                brush.GradientStops.Add(new GradientStop(color, Fraction(Attr(stop, "offset"), 0)));
            }

            if (brush.GradientStops.Count is 0 or > 256) throw Invalid();
            return brush;
        }
    }

    private sealed record Paint(string Fill, string Stroke, double StrokeWidth, double FillOpacity, double StrokeOpacity,
        string Color, string Family, double FontSize, FontWeight Weight, FontStyle FontStyle, string Anchor)
    {
        public static Paint Default { get; } = new("#000", "none", 1, 1, 1, "#000", "Arial", 16, FontWeight.Normal, FontStyle.Normal, "start");

        public Paint Read(XElement element)
        {
            var family = Attr(element, "font-family") ?? Family;
            if (family.Length > 256 || family.Any(character => !char.IsLetterOrDigit(character) && character is not (' ' or ',' or '-' or '_' or '\'' or '"'))) throw Invalid();
            var weight = Attr(element, "font-weight");
            var anchor = Attr(element, "text-anchor") ?? Anchor;
            if (anchor is not ("start" or "middle" or "end")) throw Invalid();
            var size = Length(Attr(element, "font-size"), FontSize);
            if (size < 1 || size > 1024) throw Invalid();
            var strokeWidth = Length(Attr(element, "stroke-width"), StrokeWidth);
            if (strokeWidth < 0 || strokeWidth > 1024) throw Invalid();
            return this with
            {
                Fill = Attr(element, "fill") ?? Fill, Stroke = Attr(element, "stroke") ?? Stroke, StrokeWidth = strokeWidth,
                FillOpacity = Unit(Attr(element, "fill-opacity"), FillOpacity), StrokeOpacity = Unit(Attr(element, "stroke-opacity"), StrokeOpacity),
                Color = Attr(element, "color") ?? Color, Family = family, FontSize = size,
                Weight = weight is null ? Weight : weight == "bold" || int.TryParse(weight, out var number) && number >= 600 ? FontWeight.Bold : FontWeight.Normal,
                FontStyle = Attr(element, "font-style") is { } style ? style == "italic" ? FontStyle.Italic : FontStyle.Normal : FontStyle,
                Anchor = anchor,
            };
        }
    }

    private abstract class SvgNode(Matrix transform, double opacity, Geometry? clip)
    {
        public void Draw(DrawingContext context)
        {
            using (context.PushTransform(transform))
            using (context.PushOpacity(opacity))
            {
                if (clip is null) DrawContent(context);
                else using (context.PushGeometryClip(clip)) DrawContent(context);
            }
        }

        protected abstract void DrawContent(DrawingContext context);
    }

    private sealed class GroupNode(Matrix transform, double opacity, Geometry? clip, IReadOnlyList<SvgNode> children) : SvgNode(transform, opacity, clip)
    {
        protected override void DrawContent(DrawingContext context)
        {
            foreach (var child in children) child.Draw(context);
        }
    }

    private sealed class ShapeNode(Matrix transform, double opacity, Geometry? clip, Geometry geometry, IBrush? fill, Pen? stroke) : SvgNode(transform, opacity, clip)
    {
        protected override void DrawContent(DrawingContext context) => context.DrawGeometry(fill, stroke, geometry);
    }

    private sealed class TextNode : SvgNode
    {
        private readonly FormattedText? _text;
        private readonly Matrix _placement;
        private readonly BlurEffect? _blur;

        public TextNode(string text, double x, double y, double textLength, Paint paint, IBrush? fill, double? blur) : base(Matrix.Identity, 1, null)
        {
            _placement = Matrix.Identity;
            if (fill is null || text.Length == 0) return;
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily(paint.Family), paint.FontStyle, paint.Weight), paint.FontSize, fill);
            if (blur is not null && (formatted.WidthIncludingTrailingWhitespace > 4096 || formatted.Height > 4096
                || formatted.WidthIncludingTrailingWhitespace * formatted.Height > 16_000_000)) throw Invalid();
            var width = textLength > 0 ? textLength : formatted.WidthIncludingTrailingWhitespace;
            var offset = paint.Anchor switch { "middle" => width / 2, "end" => width, _ => 0 };
            var sx = textLength > 0 && formatted.WidthIncludingTrailingWhitespace > 0 ? textLength / formatted.WidthIncludingTrailingWhitespace : 1;
            _placement = Matrix.CreateScale(sx, 1) * Matrix.CreateTranslation(x - offset, y - formatted.Baseline);
            ValidateMatrix(_placement);
            _text = formatted;
            _blur = blur is { } deviation ? new BlurEffect { Radius = deviation * 3 } : null;
        }

        protected override void DrawContent(DrawingContext context)
        {
            if (_text is null) return;
            using (context.PushTransform(_placement))
            {
                if (_blur is null) context.DrawText(_text, default);
                else using (context.PushEffect(_blur, new Rect(0, 0, _text.WidthIncludingTrailingWhitespace, _text.Height))) context.DrawText(_text, default);
            }
        }
    }

    private sealed class SvgControl : Control
    {
        private readonly Drawing _drawing;

        public SvgControl(Drawing drawing)
        {
            _drawing = drawing;
            AutomationProperties.SetName(this, drawing.Name);
            IsHitTestVisible = false;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var fit = Math.Min(1, Math.Min(availableSize.Width / _drawing.Width, availableSize.Height / _drawing.Height));
            return new Size(_drawing.Width * fit, _drawing.Height * fit);
        }

        public override void Render(DrawingContext context)
        {
            var scale = Math.Min(Bounds.Width / _drawing.Width, Bounds.Height / _drawing.Height);
            using (context.PushClip(new Rect(Bounds.Size)))
            using (context.PushTransform(Matrix.CreateScale(scale, scale))) _drawing.Root.Draw(context);
        }
    }

    private static Geometry? Shape(XElement element, Budget? budget)
    {
        var name = element.Name.LocalName;
        if (budget is not null && ++budget.Operations > MaxNodes) throw Invalid();
        var x = Length(Attr(element, "x"));
        var y = Length(Attr(element, "y"));
        switch (name)
        {
            case "rect":
                var width = Length(Attr(element, "width"));
                var height = Length(Attr(element, "height"));
                if (width < 0 || height < 0) throw Invalid();
                var rx = Length(Attr(element, "rx"), Length(Attr(element, "ry")));
                var ry = Length(Attr(element, "ry"), rx);
                if (rx < 0 || ry < 0) throw Invalid();
                return new RectangleGeometry { Rect = new Rect(x, y, width, height), RadiusX = Math.Min(rx, width / 2), RadiusY = Math.Min(ry, height / 2) };
            case "circle" or "ellipse":
                var radiusX = Length(Attr(element, name == "circle" ? "r" : "rx"));
                var radiusY = name == "circle" ? radiusX : Length(Attr(element, "ry"));
                if (radiusX < 0 || radiusY < 0) throw Invalid();
                return new EllipseGeometry(new Rect(Length(Attr(element, "cx")) - radiusX, Length(Attr(element, "cy")) - radiusY, radiusX * 2, radiusY * 2));
            case "path":
                var data = Attr(element, "d") ?? throw Invalid();
                var numbers = Number().Matches(data);
                if (budget is not null && (budget.Operations += Math.Max(PathCommand().Count(data), (numbers.Count + 1) / 2)) > MaxNodes) throw Invalid();
                foreach (Match number in numbers) _ = Scalar(number.Value);
                var parsed = PathGeometry.Parse(data);
                parsed.FillRule = Attr(element, "fill-rule") switch { "evenodd" => FillRule.EvenOdd, null or "nonzero" => FillRule.NonZero, _ => throw Invalid() };
                return parsed;
            case "polygon" or "polyline":
                var points = Values(Attr(element, "points"));
                if (points.Length < 4 || points.Length % 2 != 0 || budget is not null && (budget.Operations += points.Length / 2) > MaxNodes) throw Invalid();
                var geometry = new StreamGeometry();
                using (var path = geometry.Open())
                {
                    path.BeginFigure(new Point(points[0], points[1]), name == "polygon");
                    for (var index = 2; index < points.Length; index += 2) path.LineTo(new Point(points[index], points[index + 1]));
                    path.EndFigure(name == "polygon");
                }

                return geometry;
            case "line":
                return new LineGeometry(new Point(Length(Attr(element, "x1")), Length(Attr(element, "y1"))), new Point(Length(Attr(element, "x2")), Length(Attr(element, "y2"))));
            default:
                return null;
        }
    }

    private static void ValidateElement(XElement element)
    {
        if (element.Name.NamespaceName is not ("" or SvgNamespace)) throw Invalid();
        foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
        {
            var name = attribute.Name.LocalName;
            if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase) || name is "style" or "class" or "mask" or "rotate"
                || name == "href" && element.Name.LocalName != "image") throw Invalid();
            if (attribute.Value.Length > MaxBytes * 2) throw Invalid();
        }
    }

    private static string? Attr(XElement element, string name) => element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == name)?.Value;

    private static string LocalReference(string value)
    {
        if (!value.StartsWith("url(#", StringComparison.Ordinal) || !value.EndsWith(')')) throw Invalid();
        var id = value[5..^1];
        return id.Length is > 0 and <= 128 && !id.Any(char.IsWhiteSpace) ? id : throw Invalid();
    }

    private static double Length(string? value, double fallback = 0) => value is null ? fallback : Scalar(value.EndsWith("px", StringComparison.Ordinal) ? value[..^2] : value);

    private static double Scalar(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || Math.Abs(number) > 1_000_000) throw Invalid();
        return number;
    }

    private static double Unit(string? value, double fallback) => value is null ? fallback : Math.Clamp(Scalar(value), 0, 1);

    private static double Fraction(string? value, double fallback) => value is null ? fallback : value.EndsWith('%') ? Scalar(value[..^1]) / 100 : Scalar(value);

    private static double[] Values(string? value) => value is null ? [] : value.Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(Scalar).ToArray();

    private static Matrix Transform(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Matrix.Identity;
        var matrix = Matrix.Identity;
        var position = 0;
        foreach (Match match in TransformOperation().Matches(value))
        {
            if (value[position..match.Index].Any(character => !char.IsWhiteSpace(character) && character != ',')) throw Invalid();
            position = match.Index + match.Length;
            var values = Values(match.Groups["values"].Value);
            var operation = match.Groups["name"].Value switch
            {
                "translate" when values.Length is 1 or 2 => Matrix.CreateTranslation(values[0], values.Length == 2 ? values[1] : 0),
                "scale" when values.Length is 1 or 2 => Matrix.CreateScale(values[0], values.Length == 2 ? values[1] : values[0]),
                "matrix" when values.Length == 6 => new Matrix(values[0], values[1], values[2], values[3], values[4], values[5]),
                "rotate" when values.Length == 1 => Matrix.CreateRotation(values[0] * Math.PI / 180),
                _ => throw Invalid(),
            };
            matrix = operation * matrix;
            ValidateMatrix(matrix);
        }

        if (value[position..].Any(character => !char.IsWhiteSpace(character))) throw Invalid();
        return matrix;
    }

    private static void ValidateMatrix(Matrix matrix)
    {
        if (new[] { matrix.M11, matrix.M12, matrix.M21, matrix.M22, matrix.M31, matrix.M32 }.Any(number => !double.IsFinite(number) || Math.Abs(number) > 1_000_000)) throw Invalid();
    }

    private static FormatException Invalid() => new("The SVG uses unsupported or over-budget image content.");

    [GeneratedRegex(@"(?<name>[A-Za-z]+)\s*\((?<values>[^()]*)\)", RegexOptions.CultureInvariant, 200)]
    private static partial Regex TransformOperation();

    [GeneratedRegex(@"[-+]?(?:\d*\.\d+|\d+\.?\d*)(?:[eE][-+]?\d+)?", RegexOptions.CultureInvariant, 200)]
    private static partial Regex Number();

    [GeneratedRegex(@"[MmLlHhVvCcSsQqTtAaZz]", RegexOptions.CultureInvariant, 200)]
    private static partial Regex PathCommand();
}
