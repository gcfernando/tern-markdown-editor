using Tern.App.Commands;
using Tern.Domain.Text;
using Tern.Markdown;

namespace Tern.App.Editing;

public static class TableEditingService
{
    public static IReadOnlyList<string> Commands { get; } =
    [
        CommandIds.TableInsertRowAbove, CommandIds.TableInsertRowBelow,
        CommandIds.TableInsertColumnLeft, CommandIds.TableInsertColumnRight,
        CommandIds.TableDeleteRow, CommandIds.TableDeleteColumn,
        CommandIds.TableMoveColumnLeft, CommandIds.TableMoveColumnRight,
        CommandIds.TableAlignLeft, CommandIds.TableAlignCenter, CommandIds.TableAlignRight, CommandIds.TableAlignNone,
        CommandIds.TableSortAscending, CommandIds.TableSortDescending, CommandIds.TableFormat,
    ];

    public static bool Handles(string id) => Commands.Contains(id);

    public static string? DisabledReason(string id, TableInfo? info, int row, int column) =>
        info is null ? "Place the cursor in a Markdown table first." : id switch
        {
            CommandIds.TableDeleteColumn when info.Columns <= 1 => "A table must keep at least one column.",
            CommandIds.TableDeleteRow when row < 0 && info.Rows <= 1 => "A table must keep its header row.",
            CommandIds.TableMoveColumnLeft when column <= 0 => "This is already the first column.",
            CommandIds.TableMoveColumnRight when column >= info.Columns - 1 => "This is already the last column.",
            _ => null,
        };

    public static EditResult? Execute(string id, string text, int caret, MarkdownAnalysis analysis, string newline)
    {
        if (!Handles(id)) return null;
        var info = TableEditor.FindAt(analysis, caret);
        if (info is null || TableEditor.CellAt(text, info, caret) is not { } cell
            || DisabledReason(id, info, cell.Row, cell.Column) is not null) return null;
        var columns = info.Columns;
        var rows = info.Rows;
        var edit = TableEditor.Apply(text, info, newline, table =>
        {
            var row = Math.Max(0, cell.Row);
            switch (id)
            {
                case CommandIds.TableInsertRowAbove:
                    table.InsertRow(row);
                    if (cell.Row < 0)
                    {
                        table.Rows[0] = [.. table.Header];
                        for (var c = 0; c < table.Columns; c++) table.Header[c] = "";
                    }
                    break;
                case CommandIds.TableInsertRowBelow: table.InsertRow(cell.Row + 1); break;
                case CommandIds.TableInsertColumnLeft: table.InsertColumn(cell.Column); break;
                case CommandIds.TableInsertColumnRight: table.InsertColumn(cell.Column + 1); break;
                case CommandIds.TableDeleteRow:
                    if (cell.Row < 0)
                        for (var c = 0; c < table.Columns; c++) table.Header[c] = table.Rows[0][c];
                    table.DeleteRow(row);
                    break;
                case CommandIds.TableDeleteColumn: table.DeleteColumn(cell.Column); break;
                case CommandIds.TableMoveColumnLeft: table.MoveColumn(cell.Column, cell.Column - 1); break;
                case CommandIds.TableMoveColumnRight: table.MoveColumn(cell.Column, cell.Column + 1); break;
                case CommandIds.TableAlignLeft: table.SetAlignment(cell.Column, ColumnAlignment.Left); break;
                case CommandIds.TableAlignCenter: table.SetAlignment(cell.Column, ColumnAlignment.Center); break;
                case CommandIds.TableAlignRight: table.SetAlignment(cell.Column, ColumnAlignment.Right); break;
                case CommandIds.TableAlignNone: table.SetAlignment(cell.Column, ColumnAlignment.None); break;
                case CommandIds.TableSortAscending: table.SortBy(cell.Column, true); break;
                case CommandIds.TableSortDescending: table.SortBy(cell.Column, false); break;
            }
            columns = table.Columns;
            rows = table.Rows.Count + 1;
        });
        if (edit is not { } replacement) return null;
        var updatedInfo = info with { Range = new(0, replacement.NewText.Length), Columns = columns, Rows = rows };
        var target = TableEditor.Cells(replacement.NewText, updatedInfo)
            .OrderBy(candidate => Math.Abs(candidate.Row - cell.Row)).ThenBy(candidate => Math.Abs(candidate.Column - cell.Column)).First();
        return new([replacement], info.Range.Start + target.Range.Start, target.Range.Length);
    }

    public static EditResult? Navigate(string text, int caret, MarkdownAnalysis analysis, string newline, bool backward)
    {
        var info = TableEditor.FindAt(analysis, caret);
        if (info is null || TableEditor.CellAt(text, info, caret) is not { } current) return null;
        var table = MarkdownTable.TryParse(text[info.Range.Start..info.Range.End]);
        if (table is null) return null;
        var index = (current.Row + 1) * table.Columns + current.Column + (backward ? -1 : 1);
        if (index < 0) return new([], info.Range.Start, 0);
        if (index >= (table.Rows.Count + 1) * table.Columns) table.InsertRow(table.Rows.Count);
        var rendered = table.Render(newline);
        var local = info with { Range = new(0, rendered.Length) };
        var target = TableEditor.Cells(rendered, local)[index];
        IReadOnlyList<TextEdit> edits = text.AsSpan(info.Range.Start, info.Range.Length).SequenceEqual(rendered)
            ? [] : [new(info.Range.Start, info.Range.Length, rendered)];
        return new(edits, info.Range.Start + target.Range.Start, target.Range.Length);
    }
}
