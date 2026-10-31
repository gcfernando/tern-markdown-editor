using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Tern.App.Commands;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class Oct29TableTests
{
    [AvaloniaTheory]
    [InlineData("0", "2")]
    [InlineData("3.5", "2")]
    [InlineData("101", "2")]
    [InlineData("2", "21")]
    [InlineData("", "1")]
    public void Dialog_refuses_invalid_dimensions(string rows, string columns)
    {
        var dialog = new InsertTableWindow();
        dialog.RowsInput.Text = rows;
        dialog.ColumnsInput.Text = columns;
        Assert.Null(dialog.Dimensions);
        Assert.False(dialog.InsertButton.IsEnabled);
    }

    [AvaloniaFact]
    public async Task Table_inserts_requested_dimensions_at_selection_as_one_undoable_change_and_cancel_keeps_text()
    {
        using var fixture = new DesktopFixture();
        var tab = fixture.Shell.NewDocument("before replace after");
        fixture.Window.EditorControl.Select(7, 7);
        fixture.Window.TableDimensionsPicker = () => Task.FromResult<TableDimensions?>(null);
        await fixture.Window.RunCommandAsync(CommandIds.InsertTable);
        Assert.Equal("before replace after", tab.CurrentText);
        fixture.Window.TableDimensionsPicker = () => Task.FromResult<TableDimensions?>(new(4, 2));
        await fixture.Window.RunCommandAsync(CommandIds.InsertTable);
        Assert.DoesNotContain("replace", tab.CurrentText);
        Assert.Contains("Column 1 | Column 2", tab.CurrentText);
        Assert.Equal(5, tab.CurrentText.Split('\n').Count(line => line.StartsWith('|')));
        await fixture.Window.RunCommandAsync(CommandIds.Undo);
        Assert.Equal("before replace after", tab.CurrentText);
        await fixture.Window.RunCommandAsync(CommandIds.Redo);
        Assert.Contains("Column 2", tab.CurrentText);
    }
}
