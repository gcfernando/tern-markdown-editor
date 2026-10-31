using System.Text.RegularExpressions;
using Tern.Domain.Text;

namespace Tern.Markdown;

public enum LinePrefix
{
    Quote,
    Bullet,
    Numbered,
    Task,
}

/// <summary>Selection-aware formatting commands. All of them are pure: they return edits, the editor applies them as one undo step.</summary>
public static partial class Formatting
{
    [GeneratedRegex(@"^(?<indent>[ \t]*)(?<hashes>#{1,6})[ \t]+")]
    private static partial Regex AtxPrefix();

    [GeneratedRegex(@"^(?<indent>[ \t]*)>[ ]?")]
    private static partial Regex QuotePrefix();

    [GeneratedRegex(@"^(?<indent>[ \t]*)[-*+][ \t]+(?!\[[ xX]\][ \t])")]
    private static partial Regex BulletPrefix();

    [GeneratedRegex(@"^(?<indent>[ \t]*)\d{1,9}[.)][ \t]+")]
    private static partial Regex NumberedPrefix();

    [GeneratedRegex(@"^(?<indent>[ \t]*)[-*+][ \t]+\[[ xX]\][ \t]+")]
    private static partial Regex TaskPrefix();

    public static EditResult ToggleInline(string text, int start, int length, string open, string? close = null)
    {
        close ??= open;
        var end = start + length;
        var beforeStart = start - open.Length;

        if (beforeStart >= 0 && end + close.Length <= text.Length
            && string.CompareOrdinal(text, beforeStart, open, 0, open.Length) == 0
            && string.CompareOrdinal(text, end, close, 0, close.Length) == 0
            && !IsPartOfLongerRun(text, beforeStart, open, end, close))
        {
            return new EditResult(
                [TextEdit.Delete(beforeStart, open.Length), TextEdit.Delete(end, close.Length)],
                beforeStart,
                length);
        }

        if (length >= open.Length + close.Length
            && string.CompareOrdinal(text, start, open, 0, open.Length) == 0
            && string.CompareOrdinal(text, end - close.Length, close, 0, close.Length) == 0)
        {
            return new EditResult(
                [TextEdit.Delete(start, open.Length), TextEdit.Delete(end - close.Length, close.Length)],
                start,
                length - open.Length - close.Length);
        }

        return new EditResult(
            [TextEdit.Insert(start, open), TextEdit.Insert(end, close)],
            start + open.Length,
            length);
    }

    // "*x*" inside "**x**" must not be mistaken for emphasis markers of the shorter type.
    private static bool IsPartOfLongerRun(string text, int openStart, string open, int closeStart, string close)
    {
        if (open.Length != 1 || close.Length != 1 || open[0] != close[0] || open[0] is not ('*' or '_' or '~'))
        {
            return false;
        }

        var before = openStart > 0 && text[openStart - 1] == open[0];
        var after = closeStart + 1 < text.Length && text[closeStart + 1] == close[0];
        return before && after;
    }

    public static EditResult InsertLink(string text, int start, int length, string url = "url")
    {
        var label = length > 0 ? text.Substring(start, length) : string.Empty;
        var replacement = $"[{label}]({url})";
        var urlStart = start + 1 + label.Length + 2;
        return new EditResult([new TextEdit(start, length, replacement)], urlStart, url.Length);
    }

    /// <summary>Pasting a bare URL over selected text makes it the link target instead of replacing the text.</summary>
    public static EditResult? PasteUrlOverSelection(string text, int start, int length, string clipboard)
    {
        var url = clipboard.Trim();
        if (length == 0 || url.Contains(' ', StringComparison.Ordinal) || url.Contains('\n', StringComparison.Ordinal)
            || !(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var selected = text.Substring(start, length);
        if (selected.Contains('\n', StringComparison.Ordinal))
        {
            return null;
        }

        var replacement = $"[{selected}]({url})";
        return new EditResult([new TextEdit(start, length, replacement)], start + replacement.Length, 0);
    }

    /// <summary>Sets the ATX heading level of the line containing <paramref name="offset"/>; level 0 makes it a paragraph.</summary>
    public static EditResult SetHeadingLevel(string text, int offset, int level)
    {
        level = Math.Clamp(level, 0, 6);
        var lines = new LineIndex(text);
        var line = lines.LineOf(offset);
        var lineStart = lines.LineStart(line);
        var lineEnd = lines.LineEnd(text, line);
        var content = text[lineStart..lineEnd];

        var match = AtxPrefix().Match(content);
        var existing = match.Success ? match.Length : 0;
        var indent = match.Success ? match.Groups["indent"].Value : string.Empty;
        var prefix = level == 0 ? indent : indent + new string('#', level) + " ";
        var caret = lineStart + prefix.Length + (offset - lineStart - Math.Min(existing, offset - lineStart));
        return new EditResult([new TextEdit(lineStart, existing, prefix)], Math.Clamp(caret, 0, int.MaxValue), 0);
    }

    public static EditResult ShiftHeadingLevel(string text, int offset, int delta)
    {
        var lines = new LineIndex(text);
        var line = lines.LineOf(offset);
        var content = text[lines.LineStart(line)..lines.LineEnd(text, line)];
        var match = AtxPrefix().Match(content);
        var current = match.Success ? match.Groups["hashes"].Length : 0;
        return SetHeadingLevel(text, offset, current + delta);
    }

    public static EditResult ToggleLinePrefix(string text, int start, int length, LinePrefix kind)
    {
        var lines = new LineIndex(text);
        var first = lines.LineOf(start);
        var lastOffset = length > 0 ? start + length - 1 : start;
        var last = lines.LineOf(lastOffset);
        var targets = Enumerable.Range(first, last - first + 1)
            .Select(l => (Line: l, Start: lines.LineStart(l), Content: text[lines.LineStart(l)..lines.LineEnd(text, l)]))
            .Where(l => l.Content.Trim().Length > 0)
            .ToList();
        if (targets.Count == 0)
        {
            targets.Add((first, lines.LineStart(first), string.Empty));
        }

        var pattern = PatternFor(kind);
        var allHave = targets.All(t => pattern.IsMatch(t.Content));
        var edits = new List<TextEdit>();
        var number = 1;
        foreach (var target in targets)
        {
            var match = pattern.Match(target.Content);
            if (allHave)
            {
                // Remove everything after the indentation that the prefix pattern consumed.
                var indentLength = match.Groups["indent"].Length;
                edits.Add(TextEdit.Delete(target.Start + indentLength, match.Length - indentLength));
            }
            else
            {
                var indentLength = LeadingWhitespace(target.Content);
                // Switching list types replaces the old marker rather than stacking markers.
                var existing = kind != LinePrefix.Quote ? ExistingListPrefix(target.Content) : null;
                var removeLength = existing?.Length - existing?.Groups["indent"].Length ?? 0;
                var prefix = kind switch
                {
                    LinePrefix.Quote => "> ",
                    LinePrefix.Bullet => "- ",
                    LinePrefix.Numbered => $"{number++}. ",
                    _ => "- [ ] ",
                };
                var at = kind == LinePrefix.Quote ? target.Start : target.Start + indentLength;
                edits.Add(new TextEdit(at, removeLength, prefix));
            }
        }

        var selectionStart = edits.Min(e => e.Start);
        var net = 0;
        foreach (var e in edits)
        {
            net += e.NewText.Length - e.Length;
        }

        if (length == 0)
        {
            var deltaAtCaret = edits.Where(e => e.Start <= start).Sum(e => e.NewText.Length - e.Length);
            return new EditResult(edits, Math.Max(selectionStart, start + deltaAtCaret), 0);
        }
        // Keep complete selected lines selected so repeating a command toggles the same content.
        var selectionEnd = lines.LineEnd(text, last) + net;
        return new EditResult(edits, selectionStart, Math.Max(0, selectionEnd - selectionStart));
    }

    public static EditResult IndentLines(string text, int start, int length, string unit = "  ")
    {
        var lines = new LineIndex(text);
        var first = lines.LineOf(start);
        var last = lines.LineOf(length > 0 ? start + length - 1 : start);
        var edits = new List<TextEdit>();
        for (var l = first; l <= last; l++)
        {
            if (lines.LineEnd(text, l) > lines.LineStart(l))
            {
                edits.Add(TextEdit.Insert(lines.LineStart(l), unit));
            }
        }

        return Shifted(edits, start, length);
    }

    public static EditResult OutdentLines(string text, int start, int length, int width = 2)
    {
        var lines = new LineIndex(text);
        var first = lines.LineOf(start);
        var last = lines.LineOf(length > 0 ? start + length - 1 : start);
        var edits = new List<TextEdit>();
        for (var l = first; l <= last; l++)
        {
            var s = lines.LineStart(l);
            var count = 0;
            while (count < width && s + count < text.Length && text[s + count] == ' ')
            {
                count++;
            }

            if (count == 0 && s < text.Length && text[s] == '\t')
            {
                count = 1;
            }

            if (count > 0)
            {
                edits.Add(TextEdit.Delete(s, count));
            }
        }

        return Shifted(edits, start, length);
    }

    private static EditResult Shifted(List<TextEdit> edits, int start, int length)
    {
        var newStart = start;
        var newEnd = start + length;
        foreach (var e in edits)
        {
            var delta = e.NewText.Length - e.Length;
            if (e.Start < start || (e.Start == start && length == 0))
            {
                newStart += delta;
                newEnd += delta;
            }
            else if (e.Start < start + length)
            {
                newEnd += delta;
            }
        }

        return new EditResult(edits, Math.Max(0, newStart), Math.Max(0, newEnd - newStart));
    }

    private static Regex PatternFor(LinePrefix kind) => kind switch
    {
        LinePrefix.Quote => QuotePrefix(),
        LinePrefix.Bullet => BulletPrefix(),
        LinePrefix.Numbered => NumberedPrefix(),
        _ => TaskPrefix(),
    };

    private static Match? ExistingListPrefix(string content)
    {
        foreach (var pattern in new[] { TaskPrefix(), BulletPrefix(), NumberedPrefix() })
        {
            var m = pattern.Match(content);
            if (m.Success)
            {
                return m;
            }
        }

        return null;
    }

    private static int LeadingWhitespace(string content)
    {
        var i = 0;
        while (i < content.Length && content[i] is ' ' or '\t')
        {
            i++;
        }

        return i;
    }
}

/// <summary>Enter-key behaviour inside lists and block quotes.</summary>
public static partial class ListContinuation
{
    [GeneratedRegex(@"^(?<indent>[ \t]*)(?<quote>(?:>[ ]?)*)(?:(?<bullet>[-*+])|(?<number>\d{1,9})(?<delim>[.)]))(?<space>[ \t]+)(?<task>\[[ xX]\][ \t]+)?")]
    private static partial Regex ItemPrefix();

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?<quote>(?:>[ ]?)+)")]
    private static partial Regex QuoteOnly();

    /// <summary>
    /// Returns the edit for pressing Enter at <paramref name="caret"/>, or null when the caret is not inside a list item
    /// or quote (the editor should then insert a plain newline).
    /// </summary>
    public static EditResult? OnEnter(string text, int caret, string newline, IReadOnlyList<CodeBlockInfo>? codeBlocks = null)
    {
        if (codeBlocks is not null && codeBlocks.Any(b => b.IsFenced && caret > b.Range.Start && caret < b.Range.End))
        {
            return null;
        }

        var lines = new LineIndex(text);
        var line = lines.LineOf(caret);
        var lineStart = lines.LineStart(line);
        var lineEnd = lines.LineEnd(text, line);
        var content = text[lineStart..lineEnd];

        var item = ItemPrefix().Match(content);
        if (item.Success)
        {
            if (caret - lineStart < item.Length)
            {
                return null;
            }

            var restOfLine = content[item.Length..];
            if (restOfLine.Trim().Length == 0 && caret >= lineEnd)
            {
                // Enter on an empty item ends the list: drop the marker, keep any quote prefix.
                var keep = item.Groups["indent"].Value + item.Groups["quote"].Value;
                return new EditResult([new TextEdit(lineStart, lineEnd - lineStart, keep)], lineStart + keep.Length, 0);
            }

            var marker = item.Groups["number"].Success
                ? $"{int.Parse(item.Groups["number"].Value, System.Globalization.CultureInfo.InvariantCulture) + 1}{item.Groups["delim"].Value}"
                : item.Groups["bullet"].Value;
            var prefix = item.Groups["indent"].Value + item.Groups["quote"].Value + marker + item.Groups["space"].Value
                + (item.Groups["task"].Success ? "[ ] " : string.Empty);
            var insert = newline + prefix;
            return new EditResult([TextEdit.Insert(caret, insert)], caret + insert.Length, 0);
        }

        var quote = QuoteOnly().Match(content);
        if (quote.Success && caret - lineStart >= quote.Length)
        {
            var rest = content[quote.Length..];
            if (rest.Trim().Length == 0 && caret >= lineEnd)
            {
                return new EditResult([new TextEdit(lineStart, lineEnd - lineStart, string.Empty)], lineStart, 0);
            }

            var insert = newline + quote.Value;
            return new EditResult([TextEdit.Insert(caret, insert)], caret + insert.Length, 0);
        }

        return null;
    }
}
