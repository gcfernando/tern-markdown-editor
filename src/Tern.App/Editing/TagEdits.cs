using System.Text.RegularExpressions;
using Tern.Domain.Text;
using Tern.Markdown;

namespace Tern.App.Editing;

/// <summary>Changes only a simple tags field, preserving unknown metadata and comments.</summary>
public static class TagEdits
{
    public static TextEdit? Create(string source, MarkdownAnalysis analysis, string input, string newline)
    {
        var tags = input.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.TrimStart('#')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (tags.Any(t => t.Length > 80 || !t.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '/' or ' '))) return null;
        var field = "tags: [" + string.Join(", ", tags.Select(t => "\"" + t + "\"")) + "]";
        if (analysis.FrontMatter is not { } metadata)
            return TextEdit.Insert(0, "---" + newline + field + newline + "---" + newline + newline);
        var block = source[metadata.Range.Start..metadata.Range.End];
        var matches = Regex.Matches(block, @"(?m)^(?:tags|tag)\s*:[^\r\n]*", RegexOptions.IgnoreCase);
        if (matches.Count > 1) return null;
        if (matches.Count == 0)
        {
            if (Regex.IsMatch(block, @"(?im)^\s+(tags|tag)\s*:|^\s*<<:|[&*][\w-]+")) return null;
            var firstBreak = block.IndexOf('\n');
            return firstBreak < 0 ? null : TextEdit.Insert(metadata.Range.Start + firstBreak + 1, field + newline);
        }
        var match = matches[0];
        var value = match.Value[(match.Value.IndexOf(':') + 1)..].Trim();
        var after = block[(match.Index + match.Length)..].TrimStart('\r', '\n');
        if (value.Length == 0 || value.IndexOfAny(['#', '&', '*', '{', '}', '|', '>']) >= 0
            || after.Length > 0 && char.IsWhiteSpace(after[0])) return null;
        return new TextEdit(metadata.Range.Start + match.Index, match.Length, field);
    }
}
