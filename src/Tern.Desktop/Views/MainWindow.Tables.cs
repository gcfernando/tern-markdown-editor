using Tern.App.Editing;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    internal Func<Task<TableDimensions?>>? TableDimensionsPicker { get; set; }

    private async Task InsertTableAsync()
    {
        if (_shown is not { IsReadOnly: false } tab) return;
        var revision = tab.Session.Revision;
        var start = Editor.SelectionStart;
        var length = Editor.SelectionLength;
        var dimensions = TableDimensionsPicker is { } picker ? await picker()
            : await new InsertTableWindow().ShowDialog<TableDimensions?>(this);
        if (dimensions is null) return;
        if (!ReferenceEquals(tab, _shown) || !tab.Session.IsCurrent(revision))
        { _shell.StatusMessage = "The document changed while choosing table dimensions. Insert the table again."; return; }
        ApplyEdit(FormattingService.InsertTable(tab.CurrentText, start, length, tab.Session.Format.NewLineText,
            dimensions.Columns, dimensions.Rows - 1));
        _shell.StatusMessage = $"Inserted {dimensions.Rows} rows (including header) and {dimensions.Columns} columns.";
    }
}
