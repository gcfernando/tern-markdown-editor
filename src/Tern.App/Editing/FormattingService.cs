using Tern.App.Commands;
using Tern.Domain.Text;
using Tern.Markdown;

namespace Tern.App.Editing;

/// <summary>Maps editor commands (bold, lists, tables...) to structured edits. Pure, so the same logic is tested without a window.</summary>
public static class FormattingService
{
    private static readonly HashSet<string> Handled =
    [
        CommandIds.Bold, CommandIds.Italic, CommandIds.InlineCode, CommandIds.Strikethrough, CommandIds.InsertLink,
        CommandIds.HeadingUp, CommandIds.HeadingDown, CommandIds.ToggleQuote, CommandIds.ToggleBullet,
        CommandIds.ToggleNumbered, CommandIds.ToggleTask, CommandIds.InsertTable, CommandIds.ToggleTaskAtCaret,
        CommandIds.InsertCodeBlock, CommandIds.InsertRule, CommandIds.Heading1, CommandIds.Heading2, CommandIds.Heading3,
    ];

    public static bool IsFormattingCommand(string commandId) => Handled.Contains(commandId);

    /// <summary>Returns the edits for a formatting command, or null when it does not apply (unknown command, no task on the line).</summary>
    public static EditResult? Execute(string commandId, string text, int selectionStart, int selectionLength, MarkdownAnalysis? analysis, string newline = "\n")
    {
        var start = Math.Clamp(selectionStart, 0, text.Length);
        var length = Math.Clamp(selectionLength, 0, text.Length - start);
        return commandId switch
        {
            CommandIds.Bold => Formatting.ToggleInline(text, start, length, "**"),
            CommandIds.Italic => Formatting.ToggleInline(text, start, length, "*"),
            CommandIds.InlineCode => ToggleInlineCode(text, start, length),
            CommandIds.Strikethrough => Formatting.ToggleInline(text, start, length, "~~"),
            CommandIds.InsertLink => Formatting.InsertLink(text, start, length),
            CommandIds.Heading1 => Formatting.SetHeadingLevel(text, start, 1),
            CommandIds.Heading2 => Formatting.SetHeadingLevel(text, start, 2),
            CommandIds.Heading3 => Formatting.SetHeadingLevel(text, start, 3),
            CommandIds.HeadingUp => Formatting.ShiftHeadingLevel(text, start, -1),
            CommandIds.HeadingDown => Formatting.ShiftHeadingLevel(text, start, 1),
            CommandIds.ToggleQuote => Formatting.ToggleLinePrefix(text, start, length, LinePrefix.Quote),
            CommandIds.ToggleBullet => Formatting.ToggleLinePrefix(text, start, length, LinePrefix.Bullet),
            CommandIds.ToggleNumbered => Formatting.ToggleLinePrefix(text, start, length, LinePrefix.Numbered),
            CommandIds.ToggleTask => Formatting.ToggleLinePrefix(text, start, length, LinePrefix.Task),
            CommandIds.InsertCodeBlock => InsertBlock(text, start, length, newline, code: true),
            CommandIds.InsertRule => InsertBlock(text, start, 0, newline, code: false),
            CommandIds.InsertTable => InsertTable(text, start, length, newline),
            CommandIds.ToggleTaskAtCaret => ToggleTask(text, start, analysis),
            _ => null,
        };
    }

    private static EditResult ToggleInlineCode(string text, int start, int length)
    {
        var selected = text.Substring(start, length);
        var longest = 0; var run = 0;
        foreach (var c in selected) { run = c == '`' ? run + 1 : 0; longest = Math.Max(longest, run); }
        var fence = new string('`', longest + 1);
        var padding = selected.StartsWith('`') || selected.EndsWith('`') ? " " : string.Empty;
        return Formatting.ToggleInline(text, start, length, fence + padding, padding + fence);
    }

    public static EditResult InsertTable(string text, int start, int length, string newline, int columns = 3, int rows = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columns, 20);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, 99);
        var table = new MarkdownTable();
        for (var c = 0; c < columns; c++)
        {
            table.Header.Add("Column " + (c + 1));
            table.Alignments.Add(ColumnAlignment.None);
        }

        for (var r = 0; r < rows; r++)
        {
            table.Rows.Add([.. Enumerable.Repeat(string.Empty, columns)]);
        }

        var rendered = table.Render(newline);
        var lines = new LineIndex(text);
        var line = lines.LineOf(start);
        var atLineStart = start == lines.LineStart(line);
        var restOfLine = text[(start + length)..lines.LineEnd(text, lines.LineOf(start + length))];

        // A table must begin on its own line and be separated from a preceding paragraph by a blank line.
        var prefix = atLineStart ? string.Empty : newline + newline;
        var suffix = restOfLine.Trim().Length == 0 ? newline : newline + newline;
        var insertion = prefix + rendered.TrimEnd('\r', '\n') + suffix;
        return new EditResult([new TextEdit(start, length, insertion)], start + prefix.Length + 2, "Column 1".Length);
    }

    public static EditResult InsertBlock(string text, int start, int length, string newline, bool code)
    {
        var selected = text.Substring(start, length);
        // A fence longer than any run in the selection cannot be closed by its contents.
        var longest = 0; var run = 0;
        foreach (var c in selected) { run = c == '`' ? run + 1 : 0; longest = Math.Max(longest, run); }
        var fence = new string('`', Math.Max(3, longest + 1));
        var prefix = start == 0 ? string.Empty : newline + newline;
        var body = code ? fence + newline + selected + (selected.EndsWith(newline, StringComparison.Ordinal) ? string.Empty : newline) + fence : "---";
        var insertion = prefix + body + newline + newline;
        return new EditResult([new TextEdit(start, length, insertion)],
            start + prefix.Length + (code ? fence.Length + newline.Length : body.Length + newline.Length + newline.Length), code ? selected.Length : 0);
    }

    private static EditResult? ToggleTask(string text, int caret, MarkdownAnalysis? analysis)
    {
        if (analysis is null)
        {
            return null;
        }

        var edit = TaskEdits.ToggleOnLine(text, analysis, analysis.Lines.LineOf(caret));
        return edit is null ? null : new EditResult([edit.Value], caret, 0);
    }
}
