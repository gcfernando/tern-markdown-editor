using System.Text.RegularExpressions;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Tern.Rendering.Resources;

/// <summary>Validates a bounded, inert SVG image without resolving external resources.</summary>
public static class SafeSvgPolicy
{
    /// <summary>Maximum cumulative bytes in an SVG and its embedded SVG images.</summary>
    public const int MaxBytes = 1024 * 1024;
    private const string Namespace = "http://www.w3.org/2000/svg";
    private static readonly HashSet<string> Elements = new(StringComparer.Ordinal)
    { "svg", "g", "rect", "circle", "ellipse", "path", "line", "polygon", "polyline", "text", "tspan", "image", "defs", "linearGradient", "stop", "clipPath", "title", "desc", "filter", "feGaussianBlur" };
    private static readonly HashSet<string> Attributes = new(StringComparer.Ordinal)
    { "width", "height", "viewBox", "preserveAspectRatio", "role", "aria-label", "aria-labelledby", "aria-describedby", "aria-hidden", "id", "x", "y", "x1", "y1", "x2", "y2", "cx", "cy", "r", "rx", "ry", "d", "points", "transform", "fill", "stroke", "color", "stroke-width", "stroke-linecap", "stroke-linejoin", "stroke-miterlimit", "stroke-dasharray", "stroke-dashoffset", "fill-rule", "clip-rule", "opacity", "fill-opacity", "stroke-opacity", "font-family", "font-size", "font-weight", "font-style", "text-anchor", "textLength", "lengthAdjust", "dominant-baseline", "shape-rendering", "text-rendering", "offset", "stop-color", "stop-opacity", "gradientUnits", "gradientTransform", "spreadMethod", "clipPathUnits", "clip-path", "filter", "stdDeviation", "href" };

    /// <summary>Returns false for active content, CSS, external references, or exhausted XML budgets.</summary>
    public static bool IsSafe(byte[] data) => IsSafe(data, out _);

    /// <summary>Flattens a small inert SVG stylesheet subset into presentation attributes, then validates the result.</summary>
    public static bool TryNormalize(byte[] data, out byte[] normalized, out string name)
    {
        normalized = [];
        name = "SVG image";
        if (IsSafe(data, out name)) { normalized = data; return true; }
        try
        {
            if (data.Length == 0 || data.Length > MaxBytes) return false;
            var root = ReadRoot(data);
            if (root.Name.LocalName != "svg" || root.Name.NamespaceName is not ("" or Namespace)) return false;
            var all = root.DescendantsAndSelf().ToArray();
            if (all.Any(element => element.Attributes().Any(attribute => attribute.Value.Length > 65_536
                || attribute.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                || attribute.Name.LocalName == "style"))) return false;
            var styles = all.Where(element => element.Name.LocalName == "style").ToArray();
            if (styles.Length is 0 or > 8) return false;
            var rules = new List<StyleRule>();
            foreach (var style in styles)
            {
                if (style.Name.NamespaceName != root.Name.NamespaceName || style.HasElements
                    || style.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration && attribute.Name.LocalName != "type")
                    || style.Value.Length > 65_536) return false;
                var position = 0;
                foreach (Match rule in Regex.Matches(style.Value, @"([^{}]+)\{([^{}]*)\}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))
                {
                    if (!string.IsNullOrWhiteSpace(style.Value[position..rule.Index])) return false;
                    position = rule.Index + rule.Length;
                    var attributes = new List<(string Name, string Value)>();
                    foreach (var declaration in rule.Groups[2].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var colon = declaration.IndexOf(':');
                        if (colon <= 0) return false;
                        var property = declaration[..colon].Trim();
                        var value = declaration[(colon + 1)..].Trim();
                        if (!StyleProperties.Contains(property) || value.Length is 0 or > 1024
                            || value.Contains("url", StringComparison.OrdinalIgnoreCase)
                            || value.Contains("expression", StringComparison.OrdinalIgnoreCase)
                            || !Regex.IsMatch(value, "\\A[a-zA-Z0-9#(),.%+\\- '\"]+\\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))) return false;
                        attributes.Add((property, value));
                        if (attributes.Count > 32) return false;
                    }
                    foreach (var selector in rule.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries))
                    {
                        if (!Regex.IsMatch(selector, @"\A\.?[a-zA-Z_][a-zA-Z0-9_-]{0,63}\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))) return false;
                        rules.Add(new StyleRule(selector, selector.StartsWith('.') ? 1 : 0, rules.Count, attributes));
                        if (rules.Count > 64) return false;
                    }
                }
                if (!string.IsNullOrWhiteSpace(style.Value[position..])) return false;
            }
            var operations = 0;
            var expandedBytes = data.Length;
            var orderedRules = rules.OrderBy(rule => rule.Specificity).ThenBy(rule => rule.Order).ToArray();
            foreach (var element in all.Where(element => element.Name.LocalName != "style"))
            {
                var classes = (element.Attribute("class")?.Value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (classes.Any(value => !Regex.IsMatch(value, @"\A[a-zA-Z_][a-zA-Z0-9_-]{0,63}\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))) return false;
                foreach (var rule in orderedRules)
                {
                    if (++operations > 100_000) return false;
                    if (rule.Specificity == 1 ? !classes.Contains(rule.Selector[1..], StringComparer.Ordinal) : element.Name.LocalName != rule.Selector) continue;
                    foreach (var attribute in rule.Attributes)
                    {
                        if (++operations > 100_000 || (expandedBytes += attribute.Name.Length + attribute.Value.Length * 6 + 4) > MaxBytes) return false;
                        element.SetAttributeValue(attribute.Name, attribute.Value);
                    }
                }
                element.Attribute("class")?.Remove();
            }
            foreach (var style in styles) style.Remove();
            var candidate = Encoding.UTF8.GetBytes(root.ToString(SaveOptions.DisableFormatting));
            if (!IsSafe(candidate, out name)) return false;
            normalized = candidate;
            return true;
        }
        catch (Exception ex) when (ex is XmlException or FormatException or ArgumentException or InvalidOperationException or RegexMatchTimeoutException) { return false; }
    }

    private sealed record StyleRule(string Selector, int Specificity, int Order, IReadOnlyList<(string Name, string Value)> Attributes);

    private static readonly HashSet<string> StyleProperties = new(StringComparer.Ordinal)
    { "fill", "stroke", "color", "stroke-width", "stroke-linecap", "stroke-linejoin", "stroke-miterlimit", "stroke-dasharray", "stroke-dashoffset", "fill-rule", "clip-rule", "opacity", "fill-opacity", "stroke-opacity", "font-family", "font-size", "font-weight", "font-style", "text-anchor", "dominant-baseline" };

    /// <summary>Validates inert image content and returns its accessible image name.</summary>
    public static bool IsSafe(byte[] data, out string name)
    {
        name = "SVG image";
        try
        {
            var remaining = MaxBytes;
            if (!Validate(data, 0, ref remaining, out var root)) return false;
            var label = root!.Attribute("aria-label")?.Value
                ?? root.Elements().FirstOrDefault(element => element.Name.LocalName == "title")?.Value;
            if (!string.IsNullOrWhiteSpace(label)) name = label;
            return true;
        }
        catch (Exception ex) when (ex is XmlException or FormatException or ArgumentException or InvalidOperationException or RegexMatchTimeoutException) { return false; }
    }

    private static bool Validate(byte[] data, int depth, ref int remaining, out XElement? validatedRoot)
    {
        validatedRoot = null;
        if (depth > 8 || data.Length == 0 || data.Length > remaining) return false;
        remaining -= data.Length;
        var root = ReadRoot(data);
        if (root.Name.LocalName != "svg") return false;
        foreach (var element in root.DescendantsAndSelf())
        {
            if (element.Name.NamespaceName is not ("" or Namespace) || !Elements.Contains(element.Name.LocalName)) return false;
            if (element.Name.LocalName == "tspan" && element.Parent?.Name.LocalName is not ("text" or "tspan")) return false;
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration)
                {
                    if (attribute.Value is not (Namespace or "http://www.w3.org/1999/xlink")) return false;
                    continue;
                }
                var name = attribute.Name.LocalName;
                if (attribute.Name.NamespaceName.Length != 0 && !(name == "href" && attribute.Name.NamespaceName == "http://www.w3.org/1999/xlink")) return false;
                if ((!Attributes.Contains(name) && name is not ("baseline-shift" or "dx" or "dy")) || attribute.Value.Length > 65_536) return false;
                if (name is "baseline-shift" or "dx" or "dy")
                {
                    if (element.Name.LocalName is not ("text" or "tspan")) return false;
                    var value = attribute.Value;
                    if (!(name == "baseline-shift" && value is "sub" or "super" or "baseline")
                        && (!Regex.IsMatch(value, @"\A[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:px|%)?\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))
                            || !double.TryParse(value.Replace("px", "").Replace("%", ""), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var offset)
                            || !double.IsFinite(offset) || Math.Abs(offset) > 4096)) return false;
                }
                if (name == "href")
                {
                    const string prefix = "data:image/svg+xml;base64,";
                    if (element.Name.LocalName != "image" || !attribute.Value.StartsWith(prefix, StringComparison.Ordinal)
                        || !Validate(Convert.FromBase64String(attribute.Value[prefix.Length..]), depth + 1, ref remaining, out _)) return false;
                }
                else if (attribute.Value.Contains("url", StringComparison.OrdinalIgnoreCase))
                {
                    if (name is not ("fill" or "stroke" or "clip-path" or "filter")
                        || !Regex.IsMatch(attribute.Value, @"\Aurl\(#[a-zA-Z_][a-zA-Z0-9_.:-]{0,127}\)\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))) return false;
                }
            }
        }
        validatedRoot = root;
        return true;
    }

    private static XElement ReadRoot(byte[] data)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes };
        using var stream = new MemoryStream(data, writable: false);
        using (var reader = XmlReader.Create(stream, settings))
        {
            var count = 0;
            while (reader.Read())
            {
                if (reader.Depth > 32 || reader.NodeType == XmlNodeType.ProcessingInstruction) throw new FormatException("SVG exceeds its XML limits.");
                if (reader.NodeType == XmlNodeType.Element && (++count > 10_000 || reader.AttributeCount > 64)) throw new FormatException("SVG exceeds its XML limits.");
            }
        }
        stream.Position = 0;
        using var xml = XmlReader.Create(stream, settings);
        return XDocument.Load(xml).Root ?? throw new FormatException("SVG has no root element.");
    }
}
