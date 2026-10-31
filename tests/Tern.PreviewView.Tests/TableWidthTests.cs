using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;

namespace Tern.PreviewView.Tests;

public sealed class TableWidthTests
{
    private const string Sample = "| A 1 | B 2 | C 3 |\n|:---|:---:|---:|\n| A | B | C |\n| D | E | F |\n";

    [AvaloniaTheory]
    [InlineData(440)]
    [InlineData(900)]
    [InlineData(1600)]
    public void Short_tables_fill_the_preview_width_with_balanced_columns(double width)
    {
        var (window, view) = Harness.Show(Harness.Build(Sample), width, 600);
        try
        {
            var table = Assert.Single(Harness.All<TablePanel>(view));
            var frame = Assert.Single(Harness.All<FitScroller>(view));
            Assert.InRange(table.ColumnWidths.Sum() + 1, view.Panel.Bounds.Width - 2, view.Panel.Bounds.Width + 2);
            Assert.InRange(frame.Scroller.Extent.Width, frame.Scroller.Viewport.Width - 1, frame.Scroller.Viewport.Width + 1);
            Assert.True(table.ColumnWidths.Max() - table.ColumnWidths.Min() < 10);
            var cells = Harness.All<RichTextBlock>(table).ToArray();
            Assert.Equal(TextAlignment.Left, cells[0].TextAlignment);
            Assert.Equal(TextAlignment.Center, cells[1].TextAlignment);
            Assert.Equal(TextAlignment.Right, cells[2].TextAlignment);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Table_reflows_after_resizing_and_obeys_the_optional_reading_column()
    {
        var (window, view) = Harness.Show(Harness.Build(Sample), 700, 600);
        try
        {
            var table = Assert.Single(Harness.All<TablePanel>(view));
            var initial = table.ColumnWidths.Sum();
            window.Width = 1200; Harness.Pump(window);
            Assert.True(table.ColumnWidths.Sum() > initial + 400);
            view.UseFullWidth = false; view.ReadingWidth = 480; Harness.Pump(window);
            Assert.InRange(table.ColumnWidths.Sum() + 1, 479, 481);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Wide_tables_keep_words_readable_and_scroll_instead_of_clipping()
    {
        const string markdown = "| Responsibility | Technologies | Architecture |\n|---|---|---|\n| Responsibilities | Technologies | Architecture |\n";
        var (window, view) = Harness.Show(Harness.Build(markdown), 260, 600);
        try
        {
            var frame = Assert.Single(Harness.All<FitScroller>(view));
            Assert.True(frame.Scroller.Extent.Width > frame.Scroller.Viewport.Width);
            Assert.All(Harness.All<RichTextBlock>(frame), cell => Assert.Single(cell.TextLayout.TextLines));
        }
        finally { window.Close(); }
    }
}
