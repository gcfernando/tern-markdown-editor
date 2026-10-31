using System.Net;
using System.Text;

namespace Tern.Rendering.Html;

internal enum HtmlTokenKind
{
    Text,
    StartTag,
    EndTag,
}

internal readonly record struct HtmlToken(HtmlTokenKind Kind, string Name, IReadOnlyList<HtmlAttr> Attributes, string Text);

/// <summary>
/// A small, forgiving HTML tokenizer that follows the HTML5 tokenisation rules closely enough that it
/// agrees with browsers about where tags, comments and raw-text elements begin and end. It never throws.
/// Comments, doctypes, processing instructions and CDATA are dropped. The content of raw-text elements
/// (script, style, ...) is discarded because none of them is ever allowed through.
/// </summary>
internal static class HtmlTokenizer
{
    private const int MaxTokens = 500_000;

    private static readonly HashSet<string> RawTextElements = new(StringComparer.Ordinal)
    {
        "script", "style", "xmp", "iframe", "noembed", "noframes", "textarea", "title",
    };

    public static List<HtmlToken> Tokenize(string input)
    {
        var tokens = new List<HtmlToken>();
        var text = new StringBuilder();
        var i = 0;
        var n = input.Length;

        void FlushText()
        {
            if (text.Length > 0)
            {
                tokens.Add(new HtmlToken(HtmlTokenKind.Text, string.Empty, [], HtmlEntities.Decode(text.ToString())));
                text.Clear();
            }
        }

        while (i < n && tokens.Count < MaxTokens)
        {
            if (input[i] != '<')
            {
                var next = input.IndexOf('<', i);
                if (next < 0)
                {
                    next = n;
                }

                text.Append(input, i, next - i);
                i = next;
                continue;
            }

            if (i + 1 >= n)
            {
                text.Append('<');
                i++;
                continue;
            }

            var d = input[i + 1];
            if (char.IsAsciiLetter(d))
            {
                if (!TryReadStartTag(input, ref i, out var name, out var attributes))
                {
                    // An unterminated tag swallows the rest of the input in browsers; keeping it as inert text is the safe equivalent.
                    text.Append(input, i, n - i);
                    i = n;
                    continue;
                }

                FlushText();
                tokens.Add(new HtmlToken(HtmlTokenKind.StartTag, name, attributes, string.Empty));
                if (name == "plaintext")
                {
                    i = n;
                }
                else if (RawTextElements.Contains(name))
                {
                    i = SkipRawText(input, i, name);
                    tokens.Add(new HtmlToken(HtmlTokenKind.EndTag, name, [], string.Empty));
                }
            }
            else if (d == '/')
            {
                if (i + 2 < n && char.IsAsciiLetter(input[i + 2]))
                {
                    var close = input.IndexOf('>', i);
                    if (close < 0)
                    {
                        text.Append(input, i, n - i);
                        i = n;
                        continue;
                    }

                    var j = i + 2;
                    while (j < close && !IsSpace(input[j]) && input[j] != '/')
                    {
                        j++;
                    }

                    FlushText();
                    tokens.Add(new HtmlToken(HtmlTokenKind.EndTag, input[(i + 2)..j].ToLowerInvariant(), [], string.Empty));
                    i = close + 1;
                }
                else
                {
                    i = SkipThroughGreaterThan(input, i + 2);
                }
            }
            else if (d == '!')
            {
                i = string.CompareOrdinal(input, i, "<!--", 0, 4) == 0 ? SkipComment(input, i + 4) : SkipThroughGreaterThan(input, i + 2);
            }
            else if (d == '?')
            {
                i = SkipThroughGreaterThan(input, i + 2);
            }
            else
            {
                text.Append('<');
                i++;
            }
        }

        FlushText();
        return tokens;
    }

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';

    private static int SkipThroughGreaterThan(string input, int from)
    {
        var close = input.IndexOf('>', from);
        return close < 0 ? input.Length : close + 1;
    }

    private static int SkipComment(string input, int from)
    {
        if (from < input.Length && input[from] == '>')
        {
            return from + 1;
        }

        if (string.CompareOrdinal(input, from, "->", 0, 2) == 0)
        {
            return from + 2;
        }

        var j = from;
        while (true)
        {
            var dashes = input.IndexOf("--", j, StringComparison.Ordinal);
            if (dashes < 0)
            {
                return input.Length;
            }

            if (dashes + 2 < input.Length && input[dashes + 2] == '>')
            {
                return dashes + 3;
            }

            if (dashes + 3 < input.Length && input[dashes + 2] == '!' && input[dashes + 3] == '>')
            {
                return dashes + 4;
            }

            j = dashes + 1;
        }
    }

    private static int SkipRawText(string input, int from, string name)
    {
        var closer = "</" + name;
        var j = from;
        while (true)
        {
            var found = input.IndexOf(closer, j, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return input.Length;
            }

            var after = found + closer.Length;
            if (after >= input.Length || IsSpace(input[after]) || input[after] is '/' or '>')
            {
                return SkipThroughGreaterThan(input, after);
            }

            j = after;
        }
    }

    private static bool TryReadStartTag(string s, ref int index, out string name, out List<HtmlAttr> attributes)
    {
        attributes = [];
        var n = s.Length;
        var j = index + 1;
        var nameStart = j;
        while (j < n && !IsSpace(s[j]) && s[j] != '/' && s[j] != '>')
        {
            j++;
        }

        name = s[nameStart..j].ToLowerInvariant();
        while (true)
        {
            while (j < n && (IsSpace(s[j]) || s[j] == '/'))
            {
                j++;
            }

            if (j >= n)
            {
                return false;
            }

            if (s[j] == '>')
            {
                index = j + 1;
                return true;
            }

            var attributeStart = j;
            j++;
            while (j < n && !IsSpace(s[j]) && s[j] is not ('/' or '>' or '='))
            {
                j++;
            }

            var attributeName = s[attributeStart..j].ToLowerInvariant();
            while (j < n && IsSpace(s[j]))
            {
                j++;
            }

            var value = string.Empty;
            if (j < n && s[j] == '=')
            {
                j++;
                while (j < n && IsSpace(s[j]))
                {
                    j++;
                }

                if (j >= n)
                {
                    return false;
                }

                if (s[j] is '"' or '\'')
                {
                    var end = s.IndexOf(s[j], j + 1);
                    if (end < 0)
                    {
                        return false;
                    }

                    value = s[(j + 1)..end];
                    j = end + 1;
                }
                else
                {
                    var valueStart = j;
                    while (j < n && !IsSpace(s[j]) && s[j] != '>')
                    {
                        j++;
                    }

                    value = s[valueStart..j];
                }
            }

            attributes.Add(new HtmlAttr(attributeName, HtmlEntities.Decode(value)));
        }
    }
}

/// <summary>Character reference decoding that is deliberately as eager as a browser: numeric references need no semicolon.</summary>
internal static class HtmlEntities
{
    public static string Decode(string value)
    {
        if (value.Length == 0 || value.IndexOf('&', StringComparison.Ordinal) < 0)
        {
            return RemoveNul(value);
        }

        var builder = new StringBuilder(value.Length);
        var i = 0;
        while (i < value.Length)
        {
            var c = value[i];
            if (c != '&' || i + 1 >= value.Length)
            {
                builder.Append(c);
                i++;
                continue;
            }

            if (value[i + 1] == '#')
            {
                i = DecodeNumeric(value, i, builder);
            }
            else
            {
                i = DecodeNamed(value, i, builder);
            }
        }

        return RemoveNul(builder.ToString());
    }

    private static string RemoveNul(string value) => value.Contains('\0', StringComparison.Ordinal) ? value.Replace("\0", string.Empty, StringComparison.Ordinal) : value;

    private static int DecodeNumeric(string value, int start, StringBuilder builder)
    {
        var j = start + 2;
        var hex = j < value.Length && value[j] is 'x' or 'X';
        if (hex)
        {
            j++;
        }

        var digitsStart = j;
        long number = 0;
        while (j < value.Length && (hex ? char.IsAsciiHexDigit(value[j]) : char.IsAsciiDigit(value[j])))
        {
            if (number <= 0x10FFFF)
            {
                number = number * (hex ? 16 : 10) + Convert.ToInt32(value[j].ToString(), hex ? 16 : 10);
            }

            j++;
        }

        if (j == digitsStart)
        {
            builder.Append('&');
            return start + 1;
        }

        if (j < value.Length && value[j] == ';')
        {
            j++;
        }

        if (number is 0 or > 0x10FFFF || number is >= 0xD800 and <= 0xDFFF)
        {
            builder.Append('\uFFFD');
        }
        else
        {
            builder.Append(char.ConvertFromUtf32((int)number));
        }

        return j;
    }

    private static int DecodeNamed(string value, int start, StringBuilder builder)
    {
        var j = start + 1;
        while (j < value.Length && j - start <= 32 && char.IsAsciiLetterOrDigit(value[j]))
        {
            j++;
        }

        var name = value[(start + 1)..j];
        var hasSemicolon = j < value.Length && value[j] == ';';
        string? replacement = name switch
        {
            "Tab" => "\t",
            "NewLine" => "\n",
            "colon" => ":",
            "lpar" => "(",
            "rpar" => ")",
            "sol" => "/",
            "bsol" => "\\",
            "excl" => "!",
            "num" => "#",
            "lt" => "<",
            "gt" => ">",
            "amp" => "&",
            "quot" => "\"",
            "apos" when hasSemicolon => "'",
            "nbsp" => "\u00a0",
            _ => null,
        };

        if (replacement is null && hasSemicolon && name.Length > 0)
        {
            var decoded = WebUtility.HtmlDecode("&" + name + ";");
            replacement = decoded.Length > 0 && decoded[0] != '&' ? decoded : null;
        }

        if (replacement is null)
        {
            builder.Append('&');
            return start + 1;
        }

        builder.Append(replacement);
        return hasSemicolon ? j + 1 : j;
    }
}
