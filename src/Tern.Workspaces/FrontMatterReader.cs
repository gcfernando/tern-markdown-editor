namespace Tern.Workspaces;

/// <summary>The few front-matter fields Tern understands.</summary>
public sealed record FrontMatterFields(string? Title, IReadOnlyList<string> Tags, IReadOnlyList<string> Aliases)
{
    public static FrontMatterFields Empty { get; } = new(null, [], []);
}

/// <summary>
/// Extracts <c>title</c>, <c>tags</c>/<c>tag</c> and <c>aliases</c> from simple YAML front matter. It supports scalars,
/// flow lists (<c>[a, b]</c>), comma separated values and block lists; anything more exotic is ignored rather than guessed at.
/// </summary>
public static class FrontMatterReader
{
    public static FrontMatterFields Read(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return FrontMatterFields.Empty;
        }

        string? title = null;
        var tags = new List<string>();
        var aliases = new List<string>();
        var lines = yaml.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '#' || line[0] == '-')
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            if (key is not ("title" or "tags" or "tag" or "aliases" or "alias"))
            {
                continue;
            }

            List<string> values;
            if (value.Length == 0 || value is "|" or ">" or "|-" or ">-")
            {
                values = ReadBlockList(lines, ref i);
            }
            else
            {
                values = ParseInline(value);
            }

            switch (key)
            {
                case "title":
                    title ??= values.FirstOrDefault(v => v.Length > 0);
                    break;
                case "tags" or "tag":
                    tags.AddRange(values.Select(NormalizeTag).Where(t => t.Length > 0));
                    break;
                default:
                    aliases.AddRange(values);
                    break;
            }
        }

        return new FrontMatterFields(title, tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), aliases);
    }

    private static List<string> ReadBlockList(string[] lines, ref int index)
    {
        var values = new List<string>();
        var j = index + 1;
        for (; j < lines.Length; j++)
        {
            var raw = lines[j].TrimEnd('\r');
            var trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed[0] == '-' && (trimmed.Length == 1 || trimmed[1] == ' '))
            {
                values.Add(Unquote(trimmed[1..].Trim()));
            }
            else if (char.IsWhiteSpace(raw[0]))
            {
                // Folded/literal block scalar content: keep it as one value for scalar keys like title.
                values.Add(trimmed);
            }
            else
            {
                break;
            }
        }

        index = j - 1;
        return values;
    }

    private static List<string> ParseInline(string value)
    {
        if (value.StartsWith('[') && value.EndsWith(']'))
        {
            value = value[1..^1];
            return [.. SplitList(value).Select(Unquote).Where(v => v.Length > 0)];
        }

        var unquoted = Unquote(value);
        // "tags: a, b" is common in the wild; for a quoted scalar the commas are part of the value.
        return value.Length > 0 && (value[0] is '"' or '\'')
            ? [unquoted]
            : [.. SplitList(unquoted).Select(Unquote).Where(v => v.Length > 0)];
    }

    private static IEnumerable<string> SplitList(string value)
    {
        var current = new System.Text.StringBuilder();
        char quote = '\0';
        foreach (var c in value)
        {
            if (quote != '\0')
            {
                current.Append(c);
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                current.Append(c);
            }
            else if (c == ',')
            {
                yield return current.ToString().Trim();
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        yield return current.ToString().Trim();
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }

    private static string NormalizeTag(string tag) => tag.Trim().TrimStart('#');
}
