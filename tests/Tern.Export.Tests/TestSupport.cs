using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;

namespace Tern.Export.Tests;

/// <summary>A scratch directory under the test output folder, removed on dispose.</summary>
internal sealed class WorkDirectory : IDisposable
{
    public WorkDirectory()
    {
        Path = System.IO.Path.Combine(AppContext.BaseDirectory, "work", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string Write(string relative, byte[] content)
    {
        var full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    public string Write(string relative, string content) => Write(relative, Encoding.UTF8.GetBytes(content));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A locked file must not fail the test run.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class SampleImages
{
    /// <summary>A real 1x1 PNG.</summary>
    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    public static string PngDataUri => "data:image/png;base64," + Convert.ToBase64String(Png);
}

/// <summary>Independent re-tokenisation of exported HTML asserting that nothing executable or external is present.</summary>
internal static partial class StrictExportedHtml
{
    private static readonly HashSet<string> Tags = new(StringComparer.Ordinal)
    {
        "html", "head", "meta", "title", "style", "body", "main",
        "b", "i", "em", "strong", "u", "s", "del", "ins", "mark", "sub", "sup", "kbd", "code", "pre", "br", "hr", "p", "div", "span",
        "details", "summary", "blockquote", "ul", "ol", "li", "table", "thead", "tbody", "tfoot", "tr", "th", "td", "caption", "colgroup", "col",
        "h1", "h2", "h3", "h4", "h5", "h6", "a", "img", "figure", "figcaption", "abbr", "section", "input", "dl", "dt", "dd",
        "svg", "g", "rect", "path", "circle", "ellipse", "polygon", "polyline", "line", "text", "tspan", "defs", "marker", "desc",
    };

    private static readonly HashSet<string> VoidTags = new(StringComparer.Ordinal) { "meta", "br", "hr", "img", "col", "input" };

    [GeneratedRegex("<(/?)([A-Za-z][A-Za-z0-9]*)((?:\"[^\"]*\"|'[^']*'|[^>\"'])*)>", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    [GeneratedRegex("([A-Za-z_:][-A-Za-z0-9_:.]*)(?:\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s\"'>]+)))?", RegexOptions.CultureInvariant)]
    private static partial Regex AttributePattern();

    public static void AssertSafe(string html, string because)
    {
        Assert.DoesNotContain("<!--", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<![CDATA[", html, StringComparison.Ordinal);
        Assert.DoesNotContain('\0', html);
        var stack = new Stack<string>();
        var styleBlocks = 0;
        var cursor = 0;
        const string Doctype = "<!DOCTYPE html>";
        if (html.StartsWith(Doctype, StringComparison.Ordinal))
        {
            cursor = Doctype.Length;
        }

        foreach (Match match in TagPattern().Matches(html))
        {
            if (match.Index < cursor)
            {
                continue;
            }

            var between = html[cursor..match.Index];
            Assert.False(between.Contains('<', StringComparison.Ordinal), $"stray '<' in text near '{Preview(between)}' ({because})");
            cursor = match.Index + match.Length;
            var closing = match.Groups[1].Value.Length > 0;
            var name = match.Groups[2].Value;
            Assert.True(Tags.Contains(name), $"tag <{name}> is not allowed ({because})");
            if (closing)
            {
                Assert.True(stack.Count > 0 && stack.Pop() == name, $"unbalanced </{name}> ({because})");
                continue;
            }

            CheckAttributes(name, match.Groups[3].Value, because);
            if (name == "style")
            {
                styleBlocks++;
                var end = html.IndexOf("</style>", cursor, StringComparison.Ordinal);
                Assert.True(end > 0, "unterminated <style>");
                var css = html[cursor..end];
                Assert.DoesNotContain("@import", css, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("url(", css, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("expression(", css, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("http", css, StringComparison.OrdinalIgnoreCase);
                cursor = end;
                stack.Push(name);
                continue;
            }

            if (!VoidTags.Contains(name) && !match.Value.EndsWith("/>", StringComparison.Ordinal))
            {
                stack.Push(name);
            }
        }

        Assert.True(html[cursor..].IndexOf('<', StringComparison.Ordinal) < 0, $"trailing '<' ({because})");
        Assert.Empty(stack);
        Assert.Equal(1, styleBlocks);
    }

    private static string Preview(string text) => text.Length > 60 ? text[..60] : text;

    private static void CheckAttributes(string tag, string text, string because)
    {
        foreach (Match attribute in AttributePattern().Matches(text))
        {
            var name = attribute.Groups[1].Value;
            var value = DecodeEntities(attribute.Groups[2].Success ? attribute.Groups[2].Value : attribute.Groups[3].Success ? attribute.Groups[3].Value : attribute.Groups[4].Value);
            Assert.False(name.StartsWith("on", StringComparison.OrdinalIgnoreCase), $"event attribute '{name}' ({because})");
            Assert.False(name is "style" or "srcset" or "srcdoc" or "formaction" or "action" or "xlink:href", $"attribute '{name}' not allowed ({because})");
            Assert.DoesNotContain(value, c => char.IsControl(c) && c != '\n' && c != '\t');
            if (tag == "meta")
            {
                Assert.True(name is "charset" or "name" or "content" or "http-equiv", $"meta attribute '{name}'");
                Assert.DoesNotContain("refresh", value, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (name is "href" or "src")
            {
                CheckUrl(tag, name, value, because);
            }
            else if (name is not ("title" or "alt" or "aria-label"))
            {
                Assert.DoesNotMatch(@"(?i)^\s*(javascript|vbscript|data|file):", value);
            }
        }
    }

    private static void CheckUrl(string tag, string name, string value, string because)
    {
        var compact = new string([.. value.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c))]);
        if (tag == "img" && name == "src")
        {
            Assert.Matches("^data:image/(png|jpeg|gif|webp);base64,[A-Za-z0-9+/=]*$", compact);
            return;
        }

        Assert.True(tag == "a" && name == "href", $"<{tag} {name}> must not carry a URL ({because})");
        Assert.Matches("(?i)^(#[^ ]*|https?://[^ ]+|mailto:[^ ]+)$", compact);
    }

    private static string DecodeEntities(string value) => WebUtilityDecode(value);

    private static string WebUtilityDecode(string value) => System.Net.WebUtility.HtmlDecode(value);
}

/// <summary>Reads text back out of PDFs produced with Identity-H fonts by decoding each font's ToUnicode map.</summary>
internal static partial class PdfText
{
    public static PdfDocument Open(byte[] data) => PdfReader.Open(new MemoryStream(data), PdfDocumentOpenMode.Import);

    public static string AllText(byte[] data)
    {
        using var document = Open(data);
        var builder = new StringBuilder();
        for (var i = 0; i < document.PageCount; i++)
        {
            builder.Append(PageText(document.Pages[i])).Append('\n');
        }

        return builder.ToString();
    }

    public static string PageText(PdfPage page)
    {
        var maps = LoadFontMaps(page);
        var builder = new StringBuilder();
        Walk(ContentReader.ReadContent(page), maps, builder, new FontState());
        return builder.ToString();
    }

    private sealed class FontState
    {
        public Dictionary<int, char>? Map { get; set; }
    }

    private static void Walk(CSequence sequence, Dictionary<string, Dictionary<int, char>> maps, StringBuilder builder, FontState state)
    {
        foreach (var item in sequence)
        {
            if (item is not COperator op)
            {
                continue;
            }

            switch (op.OpCode.Name)
            {
                case "Tf" when op.Operands.Count > 0 && op.Operands[0] is CName name:
                    state.Map = maps.GetValueOrDefault(name.Name.TrimStart('/'));
                    break;
                case "Tj" when op.Operands.Count > 0 && op.Operands[0] is CString text:
                    Decode(text.Value, state.Map, builder);
                    break;
                case "TJ" when op.Operands.Count > 0 && op.Operands[0] is CArray array:
                    foreach (var element in array)
                    {
                        if (element is CString part)
                        {
                            Decode(part.Value, state.Map, builder);
                        }
                    }

                    break;
                case "ET":
                    builder.Append(' ');
                    break;
            }
        }
    }

    private static void Decode(string raw, Dictionary<int, char>? map, StringBuilder builder)
    {
        if (map is null)
        {
            builder.Append(raw);
            return;
        }

        for (var i = 0; i + 1 < raw.Length; i += 2)
        {
            var code = (raw[i] << 8) | raw[i + 1];
            builder.Append(map.TryGetValue(code, out var ch) ? ch : '\uFFFD');
        }
    }

    private static Dictionary<string, Dictionary<int, char>> LoadFontMaps(PdfPage page)
    {
        var result = new Dictionary<string, Dictionary<int, char>>(StringComparer.Ordinal);
        var fonts = page.Resources.Elements.GetDictionary("/Font");
        if (fonts is null)
        {
            return result;
        }

        foreach (var key in fonts.Elements.Keys)
        {
            var font = fonts.Elements.GetDictionary(key);
            var toUnicode = font?.Elements.GetDictionary("/ToUnicode");
            if (toUnicode?.Stream is null)
            {
                continue;
            }

            var cmap = Encoding.ASCII.GetString(toUnicode.Stream.UnfilteredValue);
            result[key.TrimStart('/')] = ParseCMap(cmap);
        }

        return result;
    }

    private static Dictionary<int, char> ParseCMap(string cmap)
    {
        var map = new Dictionary<int, char>();
        foreach (Match section in CharSection().Matches(cmap))
        {
            foreach (Match m in BfChar().Matches(section.Groups[1].Value))
            {
                map[int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)] =
                    (char)int.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
        }

        foreach (Match section in RangeSection().Matches(cmap))
        {
            foreach (Match m in BfRange().Matches(section.Groups[1].Value))
            {
                var from = int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var to = int.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var target = int.Parse(m.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                for (var code = from; code <= to; code++)
                {
                    map[code] = (char)(target + code - from);
                }
            }
        }

        return map;
    }

    [GeneratedRegex("beginbfchar(.*?)endbfchar", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex CharSection();

    [GeneratedRegex("beginbfrange(.*?)endbfrange", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex RangeSection();

    [GeneratedRegex("<([0-9A-Fa-f]+)>\\s*<([0-9A-Fa-f]{4})>", RegexOptions.CultureInvariant)]
    private static partial Regex BfChar();

    [GeneratedRegex("<([0-9A-Fa-f]+)>\\s*<([0-9A-Fa-f]+)>\\s*<([0-9A-Fa-f]{4})>", RegexOptions.CultureInvariant)]
    private static partial Regex BfRange();
}
