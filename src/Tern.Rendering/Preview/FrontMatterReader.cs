using System.Text.RegularExpressions;

namespace Tern.Rendering.Preview;

/// <summary>A deliberately small reader for the top level of YAML front matter: enough to show "key: value" properties.</summary>
internal static partial class FrontMatterReader
{
    public static IReadOnlyList<FrontMatterProperty> Read(string yaml)
    {
        var lines = yaml.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var properties = new List<FrontMatterProperty>();
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            var match = KeyPattern().Match(line);
            i++;
            if (!match.Success)
            {
                continue;
            }

            var key = Unquote(match.Groups["key"].Value.Trim());
            var value = match.Groups["value"].Value.Trim();
            var continuation = new List<string>();
            while (i < lines.Length && (lines[i].Length == 0 || lines[i][0] is ' ' or '\t' || lines[i].StartsWith("- ", StringComparison.Ordinal) || lines[i] == "-"))
            {
                continuation.Add(lines[i]);
                i++;
            }

            while (continuation.Count > 0 && string.IsNullOrWhiteSpace(continuation[^1]))
            {
                continuation.RemoveAt(continuation.Count - 1);
            }

            var items = continuation.Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
            if (value.Length == 0 && items.Count > 0 && items.All(c => c == "-" || c.StartsWith("- ", StringComparison.Ordinal)))
            {
                value = string.Join(", ", items.Select(c => Unquote(c.TrimStart('-').Trim())));
            }
            else if (value is "|" or ">" or "|-" or ">-" or "|+" or ">+")
            {
                value = string.Join(value[0] == '|' ? "\n" : " ", items);
            }
            else if (value.Length == 0 && items.Count > 0)
            {
                value = string.Join("; ", items);
            }
            else
            {
                value = Unquote(value);
            }

            properties.Add(new FrontMatterProperty(key, value));
        }

        return properties;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'') ? value[1..^1] : value;

    [GeneratedRegex(@"^(?<key>[^\s#:\-][^:]*?|-[^\s:][^:]*?)\s*:(?:\s+(?<value>.*)|\s*)$")]
    private static partial Regex KeyPattern();
}
