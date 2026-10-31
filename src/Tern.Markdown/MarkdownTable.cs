using System.Globalization;
using System.Text;
using Tern.Domain.Text;

namespace Tern.Markdown;

public enum ColumnAlignment
{
    None,
    Left,
    Center,
    Right,
}

/// <summary>An editable GFM pipe table. Cell text is kept verbatim (including escapes) so round-trips never alter content.</summary>
public sealed class MarkdownTable
{
    public string Indent { get; set; } = string.Empty;

    public List<string> Header { get; } = [];

    public List<ColumnAlignment> Alignments { get; } = [];

    public List<List<string>> Rows { get; } = [];

    public int Columns => Header.Count;

    public static MarkdownTable? TryParse(string source)
    {
        var lines = source.Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
            .Where(l => l.Trim().Length > 0)
            .ToList();
        if (lines.Count < 2)
        {
            return null;
        }

        var header = SplitCells(lines[0]);
        var delimiters = SplitCells(lines[1]);
        if (header.Count == 0 || delimiters.Count != header.Count || !delimiters.All(IsDelimiterCell))
        {
            return null;
        }

        var table = new MarkdownTable { Indent = new string(' ', Math.Min(3, lines[0].Length - lines[0].TrimStart().Length)) };
        table.Header.AddRange(header);
        table.Alignments.AddRange(delimiters.Select(ParseAlignment));
        foreach (var line in lines.Skip(2))
        {
            var cells = SplitCells(line);
            while (cells.Count < table.Columns)
            {
                cells.Add(string.Empty);
            }

            table.Rows.Add(cells.Take(table.Columns).ToList());
        }

        return table;
    }

    public string Render(string newline = "\n")
    {
        var widths = new int[Columns];
        for (var c = 0; c < Columns; c++)
        {
            widths[c] = Math.Max(3, DisplayWidth(Header[c]));
            foreach (var row in Rows)
            {
                widths[c] = Math.Max(widths[c], DisplayWidth(row[c]));
            }
        }

        var builder = new StringBuilder();
        AppendRow(builder, Header, widths);
        builder.Append(newline).Append(Indent).Append('|');
        for (var c = 0; c < Columns; c++)
        {
            var dashes = new string('-', widths[c]);
            builder.Append(' ').Append(Alignments[c] switch
            {
                ColumnAlignment.Left => ":" + dashes[1..],
                ColumnAlignment.Right => dashes[1..] + ":",
                ColumnAlignment.Center => ":" + dashes[2..] + ":",
                _ => dashes,
            }).Append(" |");
        }

        foreach (var row in Rows)
        {
            builder.Append(newline);
            AppendRow(builder, row, widths);
        }

        return builder.ToString();
    }

    public void InsertRow(int index)
    {
        Rows.Insert(Math.Clamp(index, 0, Rows.Count), Enumerable.Repeat(string.Empty, Columns).ToList());
    }

    public void DeleteRow(int index)
    {
        if (index >= 0 && index < Rows.Count)
        {
            Rows.RemoveAt(index);
        }
    }

    public void InsertColumn(int index, string header = "")
    {
        index = Math.Clamp(index, 0, Columns);
        Header.Insert(index, header);
        Alignments.Insert(index, ColumnAlignment.None);
        foreach (var row in Rows)
        {
            row.Insert(index, string.Empty);
        }
    }

    /// <summary>A table must keep at least one column, so deleting the last one is refused.</summary>
    public bool DeleteColumn(int index)
    {
        if (Columns <= 1 || index < 0 || index >= Columns)
        {
            return false;
        }

        Header.RemoveAt(index);
        Alignments.RemoveAt(index);
        foreach (var row in Rows)
        {
            row.RemoveAt(index);
        }

        return true;
    }

    public void MoveColumn(int from, int to)
    {
        if (from < 0 || from >= Columns || to < 0 || to >= Columns || from == to)
        {
            return;
        }

        Move(Header, from, to);
        Move(Alignments, from, to);
        foreach (var row in Rows)
        {
            Move(row, from, to);
        }
    }

    public void SetAlignment(int column, ColumnAlignment alignment)
    {
        if (column >= 0 && column < Columns)
        {
            Alignments[column] = alignment;
        }
    }

    /// <summary>Stable sort; columns whose every non-empty cell is a number compare numerically.</summary>
    public void SortBy(int column, bool ascending)
    {
        if (column < 0 || column >= Columns)
        {
            return;
        }

        var numeric = Rows.Where(r => r[column].Trim().Length > 0)
            .All(r => double.TryParse(r[column].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _));
        IOrderedEnumerable<List<string>> ordered;
        if (numeric)
        {
            double Key(List<string> r) => double.TryParse(r[column].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NegativeInfinity;
            ordered = ascending ? Rows.OrderBy(Key) : Rows.OrderByDescending(Key);
        }
        else
        {
            ordered = ascending
                ? Rows.OrderBy(r => r[column].Trim(), StringComparer.CurrentCultureIgnoreCase)
                : Rows.OrderByDescending(r => r[column].Trim(), StringComparer.CurrentCultureIgnoreCase);
        }

        var sorted = ordered.ToList();
        Rows.Clear();
        Rows.AddRange(sorted);
    }

    /// <summary>Parses tab- or comma-separated clipboard data (as copied from a spreadsheet) into a table.</summary>
    public static MarkdownTable FromDelimited(string data)
    {
        var lines = data.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries);
        var delimiter = lines.Length > 0 && lines[0].Contains('\t', StringComparison.Ordinal) ? '\t' : ',';
        var grid = lines.Select(l => l.Split(delimiter).Select(c => c.Trim().Replace("|", "\\|", StringComparison.Ordinal)).ToList()).ToList();
        var width = Math.Max(1, grid.Count == 0 ? 1 : grid.Max(r => r.Count));
        var table = new MarkdownTable();
        for (var c = 0; c < width; c++)
        {
            table.Header.Add(grid.Count > 0 && c < grid[0].Count ? grid[0][c] : string.Empty);
            table.Alignments.Add(ColumnAlignment.None);
        }

        foreach (var row in grid.Skip(1))
        {
            table.Rows.Add(Enumerable.Range(0, width).Select(c => c < row.Count ? row[c] : string.Empty).ToList());
        }

        return table;
    }

    private void AppendRow(StringBuilder builder, List<string> cells, int[] widths)
    {
        builder.Append(Indent).Append('|');
        for (var c = 0; c < Columns; c++)
        {
            var cell = cells[c];
            var padding = widths[c] - DisplayWidth(cell);
            var (left, right) = Alignments[c] switch
            {
                ColumnAlignment.Right => (padding, 0),
                ColumnAlignment.Center => (padding / 2, padding - padding / 2),
                _ => (0, padding),
            };
            builder.Append(' ').Append(' ', left).Append(cell).Append(' ', right).Append(" |");
        }
    }

    private static void Move<T>(List<T> list, int from, int to)
    {
        var item = list[from];
        list.RemoveAt(from);
        list.Insert(to, item);
    }

    private static List<string> SplitCells(string line)
    {
        var trimmed = line.Trim();
        var cells = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (c == '\\' && i + 1 < trimmed.Length)
            {
                current.Append(c).Append(trimmed[++i]);
            }
            else if (c == '|')
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        cells.Add(current.ToString().Trim());

        // The optional leading and trailing pipes produce empty edge cells that are not columns.
        if (trimmed.StartsWith('|') && cells.Count > 0)
        {
            cells.RemoveAt(0);
        }

        if (trimmed.EndsWith('|') && !trimmed.EndsWith("\\|", StringComparison.Ordinal) && cells.Count > 0)
        {
            cells.RemoveAt(cells.Count - 1);
        }

        return cells;
    }

    private static bool IsDelimiterCell(string cell)
    {
        var t = cell.Trim();
        if (t.StartsWith(':'))
        {
            t = t[1..];
        }

        if (t.EndsWith(':'))
        {
            t = t[..^1];
        }

        return t.Length > 0 && t.All(c => c == '-');
    }

    private static ColumnAlignment ParseAlignment(string cell)
    {
        var t = cell.Trim();
        return (t.StartsWith(':'), t.EndsWith(':')) switch
        {
            (true, true) => ColumnAlignment.Center,
            (true, false) => ColumnAlignment.Left,
            (false, true) => ColumnAlignment.Right,
            _ => ColumnAlignment.None,
        };
    }

    /// <summary>Terminal-cell width: East Asian wide characters and emoji take two columns, combining marks none.</summary>
    public static int DisplayWidth(string text)
    {
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
            {
                continue;
            }

            width += IsWide(rune.Value) ? 2 : 1;
        }

        return width;
    }

    private static bool IsWide(int cp) =>
        cp is (>= 0x1100 and <= 0x115F) or (>= 0x2E80 and <= 0xA4CF) or (>= 0xAC00 and <= 0xD7A3)
            or (>= 0xF900 and <= 0xFAFF) or (>= 0xFE30 and <= 0xFE6F) or (>= 0xFF00 and <= 0xFF60)
            or (>= 0xFFE0 and <= 0xFFE6) or (>= 0x1F300 and <= 0x1F64F) or (>= 0x1F900 and <= 0x1F9FF)
            or (>= 0x20000 and <= 0x3FFFD);
}

public static class TableEditor
{
    public static TableInfo? FindAt(MarkdownAnalysis analysis, int offset) =>
        analysis.Tables.FirstOrDefault(t => offset >= t.Range.Start && offset <= t.Range.End);

    /// <summary>Re-renders the table found at <paramref name="info"/> after applying <paramref name="change"/>; returns null if the source is not a parseable table.</summary>
    public static TextEdit? Apply(string text, TableInfo info, string newline, Action<MarkdownTable> change)
    {
        var end = Math.Min(info.Range.End, text.Length);
        var table = MarkdownTable.TryParse(text[info.Range.Start..end]);
        if (table is null)
        {
            return null;
        }

        change(table);
        return new TextEdit(info.Range.Start, end - info.Range.Start, table.Render(newline));
    }
}
