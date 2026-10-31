using System.Text;

namespace Tern.Markdown;

public enum LinkTargetKind
{
    Empty,
    Anchor,
    External,
    Relative,
    RootRelative,
    Absolute,
    WikiLink,
}

/// <summary>A link destination split into the pieces the workspace cares about.</summary>
/// <param name="Path">Percent-decoded path, using forward slashes as written. Empty for pure anchors.</param>
public sealed record LinkTarget(LinkTargetKind Kind, string Path, string? Fragment)
{
    public static LinkTarget Parse(string destination, bool isWikiLink = false)
    {
        var text = destination.Trim();
        if (text.Length == 0)
        {
            return new LinkTarget(LinkTargetKind.Empty, string.Empty, null);
        }

        if (text[0] == '#')
        {
            return new LinkTarget(LinkTargetKind.Anchor, string.Empty, Decode(text[1..]));
        }

        if (!isWikiLink && HasScheme(text))
        {
            return new LinkTarget(LinkTargetKind.External, text, null);
        }

        string? fragment = null;
        var hash = text.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            fragment = Decode(text[(hash + 1)..]);
            text = text[..hash];
        }

        // A query string means nothing for local files; dropping it keeps "a.md?x=1" resolvable.
        var query = isWikiLink ? -1 : text.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            text = text[..query];
        }

        var path = isWikiLink ? text.Trim() : Decode(text);
        var kind = isWikiLink
            ? LinkTargetKind.WikiLink
            : path.StartsWith('/') || path.StartsWith('\\')
                ? LinkTargetKind.RootRelative
                : System.IO.Path.IsPathRooted(path) ? LinkTargetKind.Absolute : LinkTargetKind.Relative;
        return new LinkTarget(kind, path, fragment);
    }

    /// <summary>Writes a file path back as a Markdown destination: forward slashes, with only the characters that would break parsing escaped.</summary>
    public static string FormatPath(string path, string? fragment = null)
    {
        var builder = new StringBuilder(path.Length + 8);
        foreach (var c in path.Replace('\\', '/'))
        {
            switch (c)
            {
                case ' ':
                    builder.Append("%20");
                    break;
                case '(':
                    builder.Append("%28");
                    break;
                case ')':
                    builder.Append("%29");
                    break;
                case '<':
                    builder.Append("%3C");
                    break;
                case '>':
                    builder.Append("%3E");
                    break;
                case '%':
                    builder.Append("%25");
                    break;
                case '#':
                    builder.Append("%23");
                    break;
                case '?':
                    builder.Append("%3F");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        if (!string.IsNullOrEmpty(fragment))
        {
            builder.Append('#').Append(fragment.Replace(" ", "%20", StringComparison.Ordinal));
        }

        return builder.ToString();
    }

    private static bool HasScheme(string text)
    {
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || !char.IsAsciiLetter(text[0]))
        {
            return false;
        }

        // "C:\x" and "C:/x" are drive paths, not schemes.
        if (colon == 1 && text.Length > 2 && (text[2] == '\\' || text[2] == '/'))
        {
            return false;
        }

        for (var i = 1; i < colon; i++)
        {
            var c = text[i];
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }
}
