using System.Text.RegularExpressions;
using Tern.Domain.Text;

namespace Tern.Markdown;

public sealed record SectionEdit(IReadOnlyList<TextEdit> Edits, int Caret, string? Refusal = null);

public static class OutlineEdits
{
    public static int End(MarkdownAnalysis analysis, int index, int textLength)
    {
        var level = analysis.Headings[index].Level;
        return analysis.Headings.Skip(index + 1).FirstOrDefault(heading => heading.Level <= level) is { } next
            ? analysis.Lines.LineStart(next.Line) : textLength;
    }

    public static SectionEdit Move(string text, MarkdownAnalysis analysis, int line, bool down, string newline)
    {
        var headings = analysis.Headings;
        var index = headings.ToList().FindIndex(heading => heading.Line == line);
        if (index < 0) return new([], 0, "Select a current heading first.");
        var current = headings[index];
        var start = analysis.Lines.LineStart(current.Line);
        var end = End(analysis, index, text.Length);
        int first, middle, last;
        if (down)
        {
            var next = headings.ToList().FindIndex(index + 1, heading => heading.Level <= current.Level);
            if (next < 0 || headings[next].Level != current.Level) return new([], start, "This section has no following sibling.");
            first = start; middle = end; last = End(analysis, next, text.Length);
        }
        else
        {
            var previous = index - 1;
            while (previous >= 0 && headings[previous].Level > current.Level) previous--;
            if (previous < 0 || headings[previous].Level != current.Level) return new([], start, "This section has no previous sibling.");
            first = analysis.Lines.LineStart(headings[previous].Line); middle = start; last = end;
        }
        var left = text[first..middle];
        var right = text[middle..last];
        // A final section without a terminator needs a separator when moved before another heading.
        var separator = right.EndsWith('\n') || right.EndsWith('\r') ? ""
            : left.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : left.EndsWith('\r') ? "\r" : newline;
        var replacement = right + separator + left;
        if (last == text.Length && !text.EndsWith('\n') && !text.EndsWith('\r'))
            replacement = replacement[..^(separator.Length)];
        return new([new TextEdit(first, last - first, replacement)], down ? first + right.Length + separator.Length : first);
    }

    public static SectionEdit Shift(string text, MarkdownAnalysis analysis, int line, int delta)
    {
        var headings = analysis.Headings;
        var index = headings.ToList().FindIndex(heading => heading.Line == line);
        if (index < 0 || delta is not (-1 or 1)) return new([], 0, "Select a current heading first.");
        var end = End(analysis, index, text.Length);
        var subtree = headings.Skip(index).TakeWhile(heading => heading.Range.Start < end).ToArray();
        if (subtree.Any(heading => heading.Level + delta is < 1 or > 6))
            return new([], headings[index].Range.Start, "Every heading in the section must remain between levels 1 and 6.");
        var edits = new List<TextEdit>();
        foreach (var heading in subtree)
        {
            var start = heading.Range.Start;
            var source = text[start..heading.Range.End];
            var atx = Regex.Match(source, @"^ {0,3}(#{1,6})(?=\s|$)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            if (atx.Success)
            {
                var marker = atx.Groups[1];
                edits.Add(new(start + marker.Index, marker.Length, new string('#', heading.Level + delta)));
                continue;
            }
            if (source.StartsWith("<h", StringComparison.OrdinalIgnoreCase))
            {
                var closing = source.LastIndexOf("</h" + heading.Level, StringComparison.OrdinalIgnoreCase);
                if (source.Length < 4 || source[2] != '0' + heading.Level || closing < 0)
                    return new([], start, "This HTML heading cannot be safely changed. Edit its tags in Source.");
                edits.Add(new(start + 2, 1, (heading.Level + delta).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                edits.Add(new(start + closing + 3, 1, (heading.Level + delta).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                continue;
            }
            var lastLine = analysis.Lines.LineOf(Math.Max(start, heading.Range.End - 1));
            var underline = text[analysis.Lines.LineStart(lastLine)..heading.Range.End].Trim();
            if (lastLine <= heading.Line || underline.Length == 0 || underline.Any(character => character != '=' && character != '-'))
                return new([], start, "This heading cannot be safely changed. Edit it in Source.");
            var content = text[start..analysis.Lines.LineStart(lastLine)].TrimEnd('\r', '\n')
                .Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');
            edits.Add(new(start, heading.Range.Length, new string('#', heading.Level + delta) + " " + content));
        }
        return new(edits.OrderBy(edit => edit.Start).ToArray(), headings[index].Range.Start);
    }
}
