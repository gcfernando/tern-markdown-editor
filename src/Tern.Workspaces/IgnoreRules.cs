using System.Text;
using System.Text.RegularExpressions;

namespace Tern.Workspaces;

/// <summary>
/// A small gitignore-compatible rule set: <c>#</c> comments, <c>*</c>, <c>**</c>, <c>?</c>, <c>[abc]</c> classes,
/// trailing <c>/</c> for directories only, a leading or inner <c>/</c> to anchor at the root, <c>!</c> negation
/// and backslash escapes. The last matching rule wins. Because ignored directories are never entered,
/// a file inside an ignored directory cannot be re-included (same as git).
/// </summary>
public sealed class IgnoreRules
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private readonly List<Rule> _rules = [];

    public static IgnoreRules Empty { get; } = new();

    public int Count => _rules.Count;

    public static IgnoreRules Parse(string content, bool caseInsensitive)
    {
        var rules = new IgnoreRules();
        foreach (var raw in content.Split('\n'))
        {
            rules.Add(raw.TrimEnd('\r'), caseInsensitive);
        }

        return rules;
    }

    public static IgnoreRules FromPatterns(IEnumerable<string> patterns, bool caseInsensitive)
    {
        var rules = new IgnoreRules();
        foreach (var pattern in patterns)
        {
            rules.Add(pattern, caseInsensitive);
        }

        return rules;
    }

    /// <summary>Adds all rules from another set after the existing ones, so they take precedence.</summary>
    public void AddRange(IgnoreRules other) => _rules.AddRange(other._rules);

    /// <summary>Whether a path relative to the workspace root (forward slashes) is ignored.</summary>
    public bool IsIgnored(string relativePath, bool isDirectory)
    {
        var ignored = false;
        foreach (var rule in _rules)
        {
            if (rule.DirectoryOnly && !isDirectory)
            {
                continue;
            }

            if (rule.Matches(relativePath))
            {
                ignored = !rule.Negated;
            }
        }

        return ignored;
    }

    private void Add(string line, bool caseInsensitive)
    {
        // Trailing spaces are insignificant unless escaped.
        var text = line;
        while (text.EndsWith(' ') && !text.EndsWith("\\ ", StringComparison.Ordinal))
        {
            text = text[..^1];
        }

        if (text.Length == 0 || text[0] == '#')
        {
            return;
        }

        var negated = false;
        if (text[0] == '!')
        {
            negated = true;
            text = text[1..];
        }

        var directoryOnly = false;
        if (text.EndsWith('/'))
        {
            directoryOnly = true;
            text = text.TrimEnd('/');
        }

        if (text.Length == 0)
        {
            return;
        }

        var anchored = text.Contains('/', StringComparison.Ordinal);
        text = text.TrimStart('/');
        if (text.Length == 0)
        {
            return;
        }

        var options = RegexOptions.CultureInvariant | RegexOptions.Compiled;
        if (caseInsensitive)
        {
            options |= RegexOptions.IgnoreCase;
        }

        var body = Translate(text);
        // An unanchored pattern may match at any depth; a directory match also covers everything beneath it.
        var prefix = anchored ? "^" : "^(?:.*/)?";
        _rules.Add(new Rule(new Regex(prefix + body + "(?:/.*)?$", options, RegexTimeout), negated, directoryOnly));
    }

    private static string Translate(string glob)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        i++;
                        var atSegmentStart = i - 1 == 0 || glob[i - 2] == '/';
                        var followedBySlash = i + 1 < glob.Length && glob[i + 1] == '/';
                        if (atSegmentStart && followedBySlash)
                        {
                            i++;
                            builder.Append("(?:.*/)?");
                        }
                        else
                        {
                            builder.Append(".*");
                        }
                    }
                    else
                    {
                        builder.Append("[^/]*");
                    }

                    break;
                case '?':
                    builder.Append("[^/]");
                    break;
                case '[':
                    var close = glob.IndexOf(']', i + 2);
                    if (close < 0)
                    {
                        builder.Append("\\[");
                    }
                    else
                    {
                        var set = glob[(i + 1)..close];
                        if (set.StartsWith('!'))
                        {
                            set = "^" + set[1..];
                        }

                        builder.Append('[').Append(set.Replace("\\", "\\\\", StringComparison.Ordinal)).Append(']');
                        i = close;
                    }

                    break;
                case '\\' when i + 1 < glob.Length:
                    i++;
                    builder.Append(Regex.Escape(glob[i].ToString()));
                    break;
                default:
                    builder.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        return builder.ToString();
    }

    private sealed record Rule(Regex Pattern, bool Negated, bool DirectoryOnly)
    {
        public bool Matches(string relativePath)
        {
            try
            {
                return Pattern.IsMatch(relativePath);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }
    }
}
