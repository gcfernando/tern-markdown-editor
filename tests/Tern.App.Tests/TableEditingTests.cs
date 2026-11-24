using Tern.App.Commands;
using Tern.App.Editing;
using Tern.Markdown;

namespace Tern.App.Tests;

public sealed class TableEditingTests
{
    private const string Source = "before\n\n|A|B|C|\n|---|---:|:---:|\n|z|10|r|\n|a|2|s|\n\nafter";

    public static IEnumerable<object[]> Operations() => TableEditingService.Commands.Select(id => new object[] { id });

    [Theory]
    [MemberData(nameof(Operations))]
    public void Every_table_operation_returns_one_edit_and_preserves_surrounding_prose(string id)
    {
        var analysis = MarkdownAnalyzer.Analyze(Source, MarkdownProfile.Gfm);
        var result = TableEditingService.Execute(id, Source, Source.IndexOf("10", StringComparison.Ordinal), analysis, "\n");
        Assert.NotNull(result);
        Assert.Single(result.Edits);
        var changed = result.ApplyTo(Source);
        Assert.NotEqual(Source, changed);
        Assert.StartsWith("before\n\n", changed, StringComparison.Ordinal);
        Assert.EndsWith("\n\nafter", changed, StringComparison.Ordinal);
        Assert.Single(MarkdownAnalyzer.Analyze(changed, MarkdownProfile.Gfm).Tables);
        Assert.InRange(result.SelectionStart + result.SelectionLength, 0, changed.Length);
    }

    [Fact]
    public void Header_row_insertion_and_deletion_preserve_the_other_cells()
    {
        var analysis = MarkdownAnalyzer.Analyze(Source, MarkdownProfile.Gfm);
        var above = TableEditingService.Execute(CommandIds.TableInsertRowAbove, Source, Source.IndexOf('A'), analysis, "\n")!.ApplyTo(Source);
        var table = MarkdownTable.TryParse(above[above.IndexOf('|')..above.LastIndexOf("\n\nafter", StringComparison.Ordinal)])!;
        Assert.All(table.Header, value => Assert.Equal("", value));
        Assert.Equal(new[] { "A", "B", "C" }, table.Rows[0]);
        var deleted = TableEditingService.Execute(CommandIds.TableDeleteRow, Source, Source.IndexOf('A'), analysis, "\n")!.ApplyTo(Source);
        Assert.Equal("z", MarkdownTable.TryParse(deleted[deleted.IndexOf('|')..deleted.LastIndexOf("\n\nafter", StringComparison.Ordinal)])!.Header[0]);
    }

    [Fact]
    public void Cell_navigation_skips_the_separator_and_adds_a_row_only_at_the_end()
    {
        var analysis = MarkdownAnalyzer.Analyze(Source, MarkdownProfile.Gfm);
        var headerEnd = Source.IndexOf('C');
        var next = TableEditingService.Navigate(Source, headerEnd, analysis, "\r\n", backward: false)!;
        var changed = next.ApplyTo(Source);
        Assert.Equal("z", changed.Substring(next.SelectionStart, next.SelectionLength));
        Assert.Contains("\r\n", changed, StringComparison.Ordinal);
        var cells = TableEditor.Cells(Source, analysis.Tables.Single());
        var append = TableEditingService.Navigate(Source, cells[^1].Range.Start, analysis, "\n", backward: false)!;
        Assert.Single(append.Edits);
        var appended = append.ApplyTo(Source);
        Assert.Equal(4, MarkdownAnalyzer.Analyze(appended, MarkdownProfile.Gfm).Tables.Single().Rows);
        Assert.Equal(0, append.SelectionLength);
    }

    [Fact]
    public void Structural_guards_refuse_last_column_and_unknown_actions()
    {
        const string text = "| A |\n| --- |";
        var analysis = MarkdownAnalyzer.Analyze(text, MarkdownProfile.Gfm);
        Assert.Null(TableEditingService.Execute(CommandIds.TableDeleteColumn, text, 2, analysis, "\n"));
        Assert.Null(TableEditingService.Execute(CommandIds.TableDeleteRow, text, 2, analysis, "\n"));
        Assert.Null(TableEditingService.Execute("format.table.unknown", text, 2, analysis, "\n"));
    }
}
