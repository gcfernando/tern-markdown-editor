using System.Text;
using System.Text.RegularExpressions;

namespace Tern.Rendering.Html;

/// <summary>An attribute that survived sanitisation. The value is the decoded text, not escaped markup.</summary>
public readonly record struct HtmlAttr(string Name, string Value);

/// <summary>What a <see cref="HtmlPiece"/> represents.</summary>
public enum HtmlPieceKind
{
    /// <summary>Plain text (already decoded; escape it when writing HTML).</summary>
    Text,

    /// <summary>An opening tag that will be matched by a later <see cref="Close"/>.</summary>
    Open,

    /// <summary>A closing tag matching the nearest unmatched <see cref="Open"/>.</summary>
    Close,

    /// <summary>An element without content such as <c>br</c>, <c>hr</c>, <c>img</c>.</summary>
    Void,
}

/// <summary>One item of sanitised HTML. A sequence returned by <see cref="HtmlSanitizer.Parse"/> is always correctly nested.</summary>
/// <param name="Kind">The kind of piece.</param>
/// <param name="Tag">Lower-case tag name; empty for text.</param>
/// <param name="Attributes">Allow-listed attributes, in source order.</param>
/// <param name="Text">The text for <see cref="HtmlPieceKind.Text"/> pieces.</param>
public sealed record HtmlPiece(HtmlPieceKind Kind, string Tag, IReadOnlyList<HtmlAttr> Attributes, string Text)
{
    /// <summary>Returns the value of the named attribute, or null.</summary>
    public string? GetAttribute(string name)
    {
        foreach (var attribute in Attributes)
        {
            if (attribute.Name == name)
            {
                return attribute.Value;
            }
        }

        return null;
    }
}

/// <summary>A single sanitised tag, used for inline HTML where open and close tags arrive separately.</summary>
/// <param name="IsClosing">True for an end tag.</param>
/// <param name="Name">Lower-case tag name.</param>
/// <param name="Attributes">Allow-listed attributes (empty for end tags).</param>
public sealed record HtmlTag(bool IsClosing, string Name, IReadOnlyList<HtmlAttr> Attributes)
{
    /// <summary>Returns the value of the named attribute, or null.</summary>
    public string? GetAttribute(string name) => Attributes.Where(a => a.Name == name).Select(a => (string?)a.Value).FirstOrDefault();
}

/// <summary>What kind of reference a URL attribute holds.</summary>
public enum HtmlUrlKind
{
    /// <summary>The <c>href</c> of an anchor.</summary>
    Link,

    /// <summary>The <c>src</c> of an image.</summary>
    Image,
}

/// <summary>Lets the caller veto or rewrite a URL that already passed the built-in scheme checks. Return null to drop the attribute.</summary>
public delegate string? HtmlUrlPolicy(HtmlUrlKind kind, string url);

/// <summary>Limits and hooks for <see cref="HtmlSanitizer"/>.</summary>
public sealed record HtmlSanitizerOptions
{
    /// <summary>Optional extra URL decision, applied after the built-in checks.</summary>
    public HtmlUrlPolicy? UrlPolicy { get; init; }

    /// <summary>Input beyond this many characters is ignored.</summary>
    public int MaxInputLength { get; init; } = 1_000_000;

    /// <summary>Elements nested deeper than this are unwrapped (their text is kept).</summary>
    public int MaxDepth { get; init; } = 64;

    /// <summary>Output stops after this many pieces.</summary>
    public int MaxPieces { get; init; } = 200_000;
}

/// <summary>
/// Neutralises raw HTML found in Markdown. Only an explicit allow-list of formatting tags and attributes survives;
/// scripts, styles, frames, forms, SVG and every event handler or inline style are removed, URLs are restricted
/// to http(s)/mailto links and scheme-less image paths, and the output is always well nested.
/// </summary>
public sealed partial class HtmlSanitizer
{
    private static readonly HashSet<string> AllowedTags = new(StringComparer.Ordinal)
    {
        "b", "i", "em", "strong", "u", "s", "del", "ins", "mark", "sub", "sup", "kbd", "code", "pre", "br", "hr", "p", "div", "span",
        "details", "summary", "blockquote", "ul", "ol", "li", "table", "thead", "tbody", "tfoot", "tr", "th", "td", "caption",
        "colgroup", "col", "h1", "h2", "h3", "h4", "h5", "h6", "a", "img", "figure", "figcaption", "abbr", "small", "cite", "q",
        "samp", "var", "dl", "dt", "dd",
    };

    private static readonly HashSet<string> VoidTags = new(StringComparer.Ordinal) { "br", "hr", "img", "col" };

    // Elements whose content must vanish with the tag, because it is code, hidden, or in a foreign-content namespace.
    private static readonly HashSet<string> DropWithContent = new(StringComparer.Ordinal)
    {
        "script", "style", "iframe", "object", "embed", "applet", "svg", "math", "template", "noembed", "noframes",
        "textarea", "title", "plaintext", "xmp", "frameset", "frame", "head", "select", "option", "datalist",
    };

    private static readonly Dictionary<string, string[]> TagAttributes = new(StringComparer.Ordinal)
    {
        ["a"] = ["href", "name"],
        ["img"] = ["src", "alt", "width", "height", "align"],
        ["td"] = ["colspan", "rowspan", "align"],
        ["th"] = ["colspan", "rowspan", "align"],
        ["table"] = ["align", "width"],
        ["tr"] = ["align"],
        ["thead"] = ["align"],
        ["tbody"] = ["align"],
        ["p"] = ["align"],
        ["div"] = ["align"],
        ["h1"] = ["align"],
        ["h2"] = ["align"],
        ["h3"] = ["align"],
        ["h4"] = ["align"],
        ["h5"] = ["align"],
        ["h6"] = ["align"],
        ["details"] = ["open"],
        ["ol"] = ["start"],
        ["col"] = ["span"],
        ["colgroup"] = ["span"],
    };

    private readonly HtmlSanitizerOptions _options;

    /// <summary>Creates a sanitizer with the given options, or the defaults.</summary>
    public HtmlSanitizer(HtmlSanitizerOptions? options = null) => _options = options ?? new HtmlSanitizerOptions();

    /// <summary>A sanitizer with default options.</summary>
    public static HtmlSanitizer Default { get; } = new();

    /// <summary>The tags that can appear in sanitised output.</summary>
    public static IReadOnlySet<string> AllowedTagNames => AllowedTags;

    /// <summary>The attributes that can appear on <paramref name="tag"/> (including the universal <c>title</c>, and <c>rel</c> on links).</summary>
    public static IReadOnlyCollection<string> AllowedAttributeNames(string tag)
    {
        var names = new List<string> { "title", "id" };
        if (TagAttributes.TryGetValue(tag, out var extra))
        {
            names.AddRange(extra);
        }

        if (tag == "a")
        {
            names.Add("rel");
        }

        if (tag == "img")
        {
            names.Add("alt");
        }

        return names;
    }

    /// <summary>Sanitises <paramref name="html"/> and returns safe, balanced HTML text.</summary>
    public string Sanitize(string html) => Serialize(Parse(html));

    /// <summary>Sanitises <paramref name="html"/> into a correctly nested list of pieces.</summary>
    public IReadOnlyList<HtmlPiece> Parse(string html)
    {
        var input = html.Length > _options.MaxInputLength ? html[.._options.MaxInputLength] : html;
        var pieces = new List<HtmlPiece>();
        var stack = new List<string>();
        string? skipping = null;
        var skipDepth = 0;

        foreach (var token in HtmlTokenizer.Tokenize(input))
        {
            if (pieces.Count >= _options.MaxPieces)
            {
                break;
            }

            if (skipping is not null)
            {
                if (token.Name == skipping)
                {
                    skipDepth += token.Kind == HtmlTokenKind.StartTag ? 1 : token.Kind == HtmlTokenKind.EndTag ? -1 : 0;
                    if (skipDepth <= 0)
                    {
                        skipping = null;
                    }
                }

                continue;
            }

            switch (token.Kind)
            {
                case HtmlTokenKind.Text:
                    AppendText(pieces, token.Text);
                    break;
                case HtmlTokenKind.StartTag:
                    if (DropWithContent.Contains(token.Name))
                    {
                        skipping = token.Name;
                        skipDepth = 1;
                    }
                    else if (AllowedTags.Contains(token.Name))
                    {
                        OpenElement(pieces, stack, token);
                    }

                    break;
                case HtmlTokenKind.EndTag:
                    CloseElement(pieces, stack, token.Name);
                    break;
            }
        }

        for (var i = stack.Count - 1; i >= 0; i--)
        {
            pieces.Add(new HtmlPiece(HtmlPieceKind.Close, stack[i], [], string.Empty));
        }

        return pieces;
    }

    /// <summary>
    /// Parses a single tag such as <c>&lt;kbd&gt;</c> or <c>&lt;/kbd&gt;</c>. Returns false when the text is not exactly one
    /// allow-listed tag, so callers can ignore everything else.
    /// </summary>
    public bool TryParseTag(string tagText, out HtmlTag tag)
    {
        tag = null!;
        var tokens = HtmlTokenizer.Tokenize(tagText.Length > 4096 ? tagText[..4096] : tagText);
        if (tokens.Count != 1)
        {
            return false;
        }

        var token = tokens[0];
        if (token.Kind == HtmlTokenKind.Text || !AllowedTags.Contains(token.Name))
        {
            return false;
        }

        tag = new HtmlTag(token.Kind == HtmlTokenKind.EndTag, token.Name, token.Kind == HtmlTokenKind.EndTag ? [] : SanitizeAttributes(token.Name, token.Attributes));
        return true;
    }

    /// <summary>Writes pieces as HTML, escaping text and attribute values.</summary>
    public static string Serialize(IEnumerable<HtmlPiece> pieces)
    {
        var builder = new StringBuilder();
        foreach (var piece in pieces)
        {
            switch (piece.Kind)
            {
                case HtmlPieceKind.Text:
                    Escape(builder, piece.Text, quotes: false);
                    break;
                case HtmlPieceKind.Close:
                    builder.Append("</").Append(piece.Tag).Append('>');
                    break;
                default:
                    builder.Append('<').Append(piece.Tag);
                    foreach (var attribute in piece.Attributes)
                    {
                        builder.Append(' ').Append(attribute.Name).Append("=\"");
                        Escape(builder, attribute.Value, quotes: true);
                        builder.Append('"');
                    }

                    builder.Append('>');
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>The text content of the pieces with all markup removed; <c>br</c> becomes a newline.</summary>
    public static string ToPlainText(IEnumerable<HtmlPiece> pieces)
    {
        var builder = new StringBuilder();
        foreach (var piece in pieces)
        {
            if (piece.Kind == HtmlPieceKind.Text)
            {
                builder.Append(piece.Text);
            }
            else if (piece.Kind == HtmlPieceKind.Void && piece.Tag == "br")
            {
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }

    private static void Escape(StringBuilder builder, string value, bool quotes)
    {
        foreach (var c in value)
        {
            switch (c)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                case '"' when quotes:
                    builder.Append("&quot;");
                    break;
                case '\'' when quotes:
                    builder.Append("&#39;");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }
    }

    private static void AppendText(List<HtmlPiece> pieces, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (pieces.Count > 0 && pieces[^1].Kind == HtmlPieceKind.Text)
        {
            pieces[^1] = pieces[^1] with { Text = pieces[^1].Text + text };
        }
        else
        {
            pieces.Add(new HtmlPiece(HtmlPieceKind.Text, string.Empty, [], text));
        }
    }

    private void OpenElement(List<HtmlPiece> pieces, List<string> stack, HtmlToken token)
    {
        var name = token.Name;
        var attributes = SanitizeAttributes(name, token.Attributes);
        if (VoidTags.Contains(name))
        {
            pieces.Add(new HtmlPiece(HtmlPieceKind.Void, name, attributes, string.Empty));
            return;
        }

        CloseImplied(pieces, stack, name);
        if (stack.Count >= _options.MaxDepth)
        {
            return;
        }

        stack.Add(name);
        pieces.Add(new HtmlPiece(HtmlPieceKind.Open, name, attributes, string.Empty));
    }

    // Mirrors the parts of HTML's implied end tags that keep lists, tables and paragraphs from nesting into themselves.
    private static void CloseImplied(List<HtmlPiece> pieces, List<string> stack, string opening)
    {
        string[] boundary;
        string[] closes;
        switch (opening)
        {
            case "li":
                closes = ["li"];
                boundary = ["ul", "ol"];
                break;
            case "dt" or "dd":
                closes = ["dt", "dd"];
                boundary = ["dl"];
                break;
            case "td" or "th":
                closes = ["td", "th"];
                boundary = ["tr", "table"];
                break;
            case "tr":
                closes = ["tr", "td", "th"];
                boundary = ["table", "thead", "tbody", "tfoot"];
                break;
            case "p":
                closes = ["p"];
                boundary = ["div", "td", "th", "li", "blockquote", "details", "figure", "table", "dd"];
                break;
            default:
                return;
        }

        for (var i = stack.Count - 1; i >= 0; i--)
        {
            if (boundary.Contains(stack[i]))
            {
                return;
            }

            if (closes.Contains(stack[i]))
            {
                CloseTo(pieces, stack, i);
                return;
            }
        }
    }

    private static void CloseElement(List<HtmlPiece> pieces, List<string> stack, string name)
    {
        var index = stack.LastIndexOf(name);
        if (index >= 0)
        {
            CloseTo(pieces, stack, index);
        }
    }

    private static void CloseTo(List<HtmlPiece> pieces, List<string> stack, int index)
    {
        for (var i = stack.Count - 1; i >= index; i--)
        {
            pieces.Add(new HtmlPiece(HtmlPieceKind.Close, stack[i], [], string.Empty));
        }

        stack.RemoveRange(index, stack.Count - index);
    }

    private List<HtmlAttr> SanitizeAttributes(string tag, IReadOnlyList<HtmlAttr> raw)
    {
        var result = new List<HtmlAttr>();
        TagAttributes.TryGetValue(tag, out var allowed);
        foreach (var attribute in raw)
        {
            if (result.Any(a => a.Name == attribute.Name))
            {
                continue;
            }

            var value = attribute.Value;
            string? clean;
            if (attribute.Name == "id" || (attribute.Name == "name" && tag == "a"))
            {
                clean = IsValidAnchorName(value) ? value : null;
            }
            else if (attribute.Name == "title" || (attribute.Name == "alt" && tag == "img"))
            {
                clean = value.Length > 2000 ? value[..2000] : value;
            }
            else if (allowed is null || !allowed.Contains(attribute.Name))
            {
                continue;
            }
            else
            {
                clean = attribute.Name switch
                {
                    "href" => CheckUrl(value, HtmlUrlKind.Link),
                    "src" => CheckUrl(value, HtmlUrlKind.Image),
                    "width" or "height" => DimensionPattern().IsMatch(value.Trim()) ? value.Trim().ToLowerInvariant() : null,
                    "align" => value.Trim().ToLowerInvariant() is "left" or "right" or "center" or "justify" ? value.Trim().ToLowerInvariant() : null,
                    "colspan" or "rowspan" or "span" => SmallNumberPattern().IsMatch(value.Trim()) ? value.Trim() : null,
                    "start" => SignedNumberPattern().IsMatch(value.Trim()) ? value.Trim() : null,
                    "open" => string.Empty,
                    _ => null,
                };
            }

            if (clean is not null)
            {
                result.Add(new HtmlAttr(attribute.Name, clean));
            }
        }

        if (tag == "a" && result.Any(a => a.Name == "href" && !a.Value.StartsWith('#')))
        {
            result.Add(new HtmlAttr("rel", "noopener noreferrer"));
        }

        return result;
    }

    /// <summary>Bounded inert fragment identifiers; never interpreted as URLs, CSS or code.</summary>
    public static bool IsValidAnchorName(string? value) => value is { Length: > 0 and <= 256 }
        && !value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '<' or '>' or '"' or '\'');

    private string? CheckUrl(string value, HtmlUrlKind kind)
    {
        var url = value.Trim(' ');
        if (url.Length is 0 or > 2048)
        {
            return null;
        }

        var accepted = IsSafeUrl(url, kind);
        if (!accepted || _options.UrlPolicy is null)
        {
            return accepted ? url : null;
        }

        // The policy may rewrite the URL, but what it returns must pass the same scheme rules.
        var rewritten = _options.UrlPolicy(kind, url);
        return rewritten is not null && IsSafeUrl(rewritten, kind) ? rewritten : null;
    }

    private static bool IsSafeUrl(string value, HtmlUrlKind kind)
    {
        var url = value.Trim(' ');
        if (url.Length is 0 or > 2048)
        {
            return false;
        }

        foreach (var c in url)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        // Browsers treat backslashes as slashes, so "\\host" and "/\host" are network paths just like "//host".
        var normalized = url.Replace('\\', '/');
        if (normalized.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        var colon = url.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            var delimiter = url.AsSpan().IndexOfAny('/', '?', '#');
            if (delimiter < 0 || colon < delimiter)
            {
                var scheme = url[..colon].ToLowerInvariant();
                return kind == HtmlUrlKind.Link && scheme is "http" or "https" or "mailto";
            }
        }

        return true;
    }


    [GeneratedRegex(@"^\d{1,4}(px|%)?$")]
    private static partial Regex DimensionPattern();

    [GeneratedRegex(@"^\d{1,3}$")]
    private static partial Regex SmallNumberPattern();

    [GeneratedRegex(@"^-?\d{1,6}$")]
    private static partial Regex SignedNumberPattern();
}
