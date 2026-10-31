using System.Globalization;
using System.Text;

namespace Tern.Markdown;

/// <summary>
/// Heading anchor generation following GitHub's documented behaviour: lower-case, drop punctuation
/// other than hyphens and underscores, spaces become hyphens, duplicates get -1, -2, ... suffixes.
/// </summary>
public sealed class HeadingAnchors
{
    private readonly Dictionary<string, int> _seen = new(StringComparer.Ordinal);

    public string Next(string headingText)
    {
        var slug = Slugify(headingText);
        if (!_seen.TryGetValue(slug, out var count))
        {
            _seen[slug] = 1;
            return slug;
        }

        // A generated suffix may collide with a real heading such as "a-1"; keep counting until free.
        string candidate;
        do
        {
            candidate = $"{slug}-{count}";
            count++;
        }
        while (_seen.ContainsKey(candidate));

        _seen[slug] = count;
        _seen[candidate] = 1;
        return candidate;
    }

    public static string Slugify(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.Trim().EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsLetterOrDigit(rune) || category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
            {
                builder.Append(Rune.ToLowerInvariant(rune).ToString());
            }
            else if (rune.Value is '-' or '_')
            {
                builder.Append((char)rune.Value);
            }
            else if (rune.Value == ' ' || rune.Value == '\t')
            {
                builder.Append('-');
            }
        }

        return builder.ToString();
    }
}
