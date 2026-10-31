using Tern.Domain.Text;
using Tern.Markdown;

namespace Tern.UnitTests.Markdown;

public class EditTests
{
    private static string Apply(string text, EditResult r) => r.ApplyTo(text);

    [Fact]
    public void Toggling_a_task_changes_one_character_and_nothing_else()
    {
        var text = "intro\r\n\r\n- [ ] a **b**\r\n- [x] c\r\n";
        var analysis = MarkdownAnalyzer.Analyze(text, MarkdownProfile.Gfm);

        var checkedText = TextEdit.ApplyAll(text, [TaskEdits.Toggle(text, analysis.Tasks[0])]);
        Assert.Equal("intro\r\n\r\n- [x] a **b**\r\n- [x] c\r\n", checkedText);

        var uncheckedText = TextEdit.ApplyAll(text, [TaskEdits.Toggle(text, analysis.Tasks[1])]);
        Assert.Equal("intro\r\n\r\n- [ ] a **b**\r\n- [ ] c\r\n", uncheckedText);
    }

    [Fact]
    public void Toggling_on_a_stale_analysis_is_refused_rather_than_corrupting_text()
    {
        var text = "- [ ] a\n";
        var analysis = MarkdownAnalyzer.Analyze(text, MarkdownProfile.Gfm);

        Assert.Throws<ArgumentException>(() => TaskEdits.Toggle("zzzzzzzzzz\n", analysis.Tasks[0]));
    }

    [Fact]
    public void Toggle_by_line_ignores_non_task_lines()
    {
        var text = "- a\n- [ ] b\n";
        var analysis = MarkdownAnalyzer.Analyze(text, MarkdownProfile.Gfm);

        Assert.Null(TaskEdits.ToggleOnLine(text, analysis, 1));
        Assert.NotNull(TaskEdits.ToggleOnLine(text, analysis, 2));
    }

    [Fact]
    public void Tables_are_parsed_formatted_and_keep_escapes()
    {
        var table = MarkdownTable.TryParse("|Name|Qty|\n|:--|--:|\n|a\\|b|1|\n|longer name|22|\n")!;

        Assert.Equal(["Name", "Qty"], table.Header);
        Assert.Equal([ColumnAlignment.Left, ColumnAlignment.Right], table.Alignments);
        Assert.Equal("a\\|b", table.Rows[0][0]);
        Assert.Equal(
            "| Name        | Qty |\n| :---------- | --: |\n| a\\|b        |   1 |\n| longer name |  22 |",
            table.Render());
    }

    [Fact]
    public void Formatting_a_formatted_table_is_idempotent()
    {
        var once = MarkdownTable.TryParse("a|b\n-|-\n1|2\n")!.Render("\r\n");
        var twice = MarkdownTable.TryParse(once)!.Render("\r\n");

        Assert.Equal(once, twice);
        Assert.Contains("\r\n", once, StringComparison.Ordinal);
    }

    [Fact]
    public void Wide_characters_are_aligned_by_display_width()
    {
        var rendered = MarkdownTable.TryParse("|a|b|\n|-|-|\n|中文|x|\n")!.Render();
        var lines = rendered.Split('\n');

        Assert.Equal(MarkdownTable.DisplayWidth(lines[0]), MarkdownTable.DisplayWidth(lines[2]));
    }

    [Fact]
    public void Rows_and_columns_can_be_inserted_deleted_moved_and_sorted()
    {
        var table = MarkdownTable.TryParse("|n|v|\n|-|-|\n|b|10|\n|a|9|\n|c|100|\n")!;

        table.SortBy(1, true);
        Assert.Equal(["a", "b", "c"], table.Rows.Select(r => r[0]));
        table.SortBy(0, false);
        Assert.Equal(["c", "b", "a"], table.Rows.Select(r => r[0]));

        table.InsertRow(1);
        Assert.Equal(["", ""], table.Rows[1]);
        table.DeleteRow(1);
        table.InsertColumn(1, "mid");
        Assert.Equal(["n", "mid", "v"], table.Header);
        table.MoveColumn(2, 0);
        Assert.Equal(["v", "n", "mid"], table.Header);
        Assert.True(table.DeleteColumn(0));
        Assert.True(table.DeleteColumn(0));
        Assert.False(table.DeleteColumn(0));
        Assert.Single(table.Header);
    }

    [Fact]
    public void Non_tables_are_rejected()
    {
        Assert.Null(MarkdownTable.TryParse("just text\nmore text\n"));
        Assert.Null(MarkdownTable.TryParse("a|b\n-|-|-\n"));
    }

    [Fact]
    public void The_editor_applies_a_table_change_in_place_without_touching_surroundings()
    {
        var text = "before\n\n|a|b|\n|-|-|\n|1|2|\n\nafter\n";
        var analysis = MarkdownAnalyzer.Analyze(text, MarkdownProfile.Gfm);
        var info = TableEditor.FindAt(analysis, text.IndexOf("|1|", StringComparison.Ordinal))!;

        var edit = TableEditor.Apply(text, info, "\n", t => t.InsertRow(1))!.Value;
        var result = TextEdit.ApplyAll(text, [edit]);

        Assert.StartsWith("before\n\n| a   | b   |", result, StringComparison.Ordinal);
        Assert.EndsWith("\n\nafter\n", result, StringComparison.Ordinal);
        Assert.Equal(3, MarkdownAnalyzer.Analyze(result, MarkdownProfile.Gfm).Tables.Single().Rows);
        Assert.Contains("| 1   | 2   |", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Spreadsheet_clipboard_data_becomes_a_table()
    {
        var table = MarkdownTable.FromDelimited("Name\tScore\r\nAnn\t5\r\nBo|b\t7\r\n");

        Assert.Equal(["Name", "Score"], table.Header);
        Assert.Equal("Bo\\|b", table.Rows[1][0]);
    }

    [Fact]
    public void Inline_formatting_wraps_unwraps_and_keeps_selection()
    {
        const string text = "make bold here";
        var wrapped = Formatting.ToggleInline(text, 5, 4, "**");
        Assert.Equal("make **bold** here", Apply(text, wrapped));
        Assert.Equal((7, 4), (wrapped.SelectionStart, wrapped.SelectionLength));

        var wrappedText = Apply(text, wrapped);
        var unwrapped = Formatting.ToggleInline(wrappedText, wrapped.SelectionStart, wrapped.SelectionLength, "**");
        Assert.Equal(text, Apply(wrappedText, unwrapped));
        Assert.Equal((5, 4), (unwrapped.SelectionStart, unwrapped.SelectionLength));
    }

    [Fact]
    public void Selecting_the_markers_themselves_also_unwraps()
    {
        const string text = "a **b** c";
        var result = Formatting.ToggleInline(text, 2, 5, "**");

        Assert.Equal("a b c", Apply(text, result));
    }

    [Fact]
    public void An_empty_selection_inserts_a_pair_with_the_caret_between()
    {
        var result = Formatting.ToggleInline("ab", 1, 0, "`");

        Assert.Equal("a``b", Apply("ab", result));
        Assert.Equal(2, result.SelectionStart);
    }

    [Fact]
    public void Italic_is_not_confused_with_bold_markers()
    {
        const string text = "**word**";
        var result = Formatting.ToggleInline(text, 2, 4, "*");

        Assert.Equal("***word***", Apply(text, result));
    }

    [Fact]
    public void Links_are_inserted_around_the_selection_with_the_url_selected()
    {
        var result = Formatting.InsertLink("see docs now", 4, 4);
        var text = Apply("see docs now", result);

        Assert.Equal("see [docs](url) now", text);
        Assert.Equal("url", text.Substring(result.SelectionStart, result.SelectionLength));
    }

    [Fact]
    public void Pasting_a_url_over_selected_text_makes_a_link()
    {
        var result = Formatting.PasteUrlOverSelection("see docs", 4, 4, "https://example.org/x\r\n")!;

        Assert.Equal("see [docs](https://example.org/x)", Apply("see docs", result));
        Assert.Null(Formatting.PasteUrlOverSelection("see docs", 4, 0, "https://example.org"));
        Assert.Null(Formatting.PasteUrlOverSelection("see docs", 4, 4, "not a url"));
        Assert.Null(Formatting.PasteUrlOverSelection("see docs", 4, 4, "javascript:alert(1)"));
    }

    [Theory]
    [InlineData("text", 1, "# text")]
    [InlineData("## text", 3, "### text")]
    [InlineData("###### text", 0, "###### text")]
    public void Heading_level_can_be_raised(string text, int caret, string expected)
    {
        Assert.Equal(expected, Apply(text, Formatting.ShiftHeadingLevel(text, caret, +1)));
    }

    [Fact]
    public void Heading_level_can_be_lowered_and_removed()
    {
        Assert.Equal("# t", Apply("## t", Formatting.ShiftHeadingLevel("## t", 0, -1)));
        Assert.Equal("t", Apply("# t", Formatting.ShiftHeadingLevel("# t", 0, -1)));
        Assert.Equal("a\n### b\nc", Apply("a\n# b\nc", Formatting.SetHeadingLevel("a\n# b\nc", 3, 3)));
    }

    [Fact]
    public void Line_prefixes_toggle_for_whole_selections()
    {
        const string text = "one\ntwo\n\nthree";
        var quoted = Formatting.ToggleLinePrefix(text, 0, text.Length, LinePrefix.Quote);
        var quotedText = Apply(text, quoted);
        Assert.Equal("> one\n> two\n\n> three", quotedText);
        Assert.Equal(text, Apply(quotedText, Formatting.ToggleLinePrefix(quotedText, 0, quotedText.Length, LinePrefix.Quote)));

        Assert.Equal("1. one\n2. two\n\n3. three", Apply(text, Formatting.ToggleLinePrefix(text, 0, text.Length, LinePrefix.Numbered)));
        Assert.Equal("- [ ] one\n- [ ] two\n\n- [ ] three", Apply(text, Formatting.ToggleLinePrefix(text, 0, text.Length, LinePrefix.Task)));
    }

    [Fact]
    public void Switching_list_type_replaces_the_marker()
    {
        const string text = "- one\n- two";
        var result = Apply(text, Formatting.ToggleLinePrefix(text, 0, text.Length, LinePrefix.Numbered));

        Assert.Equal("1. one\n2. two", result);
        Assert.Equal("- [ ] x", Apply("- x", Formatting.ToggleLinePrefix("- x", 0, 0, LinePrefix.Task)));
    }

    [Fact]
    public void Indent_and_outdent_shift_every_selected_line()
    {
        const string text = "- a\n- b\n\n- c";
        var indented = Apply(text, Formatting.IndentLines(text, 0, text.Length));
        Assert.Equal("  - a\n  - b\n\n  - c", indented);
        Assert.Equal(text, Apply(indented, Formatting.OutdentLines(indented, 0, indented.Length)));
        Assert.Equal("x", Apply("\tx", Formatting.OutdentLines("\tx", 0, 0)));
    }

    [Theory]
    [InlineData("- item", 6, "- item\n- ")]
    [InlineData("* item", 6, "* item\n* ")]
    [InlineData("1. item", 7, "1. item\n2. ")]
    [InlineData("9) item", 7, "9) item\n10) ")]
    [InlineData("  - [x] done", 12, "  - [x] done\n  - [ ] ")]
    [InlineData("> quote", 7, "> quote\n> ")]
    [InlineData("> - nested", 10, "> - nested\n> - ")]
    public void Enter_continues_lists_and_quotes(string text, int caret, string expected)
    {
        var result = ListContinuation.OnEnter(text, caret, "\n")!;

        Assert.Equal(expected, Apply(text, result));
        Assert.Equal(expected.Length, result.SelectionStart);
    }

    [Fact]
    public void Enter_in_the_middle_of_an_item_splits_it()
    {
        var result = ListContinuation.OnEnter("- ab cd", 5, "\n")!;

        Assert.Equal("- ab \n- cd", Apply("- ab cd", result));
    }

    [Fact]
    public void Enter_on_an_empty_item_ends_the_list()
    {
        Assert.Equal("- a\n", Apply("- a\n- ", ListContinuation.OnEnter("- a\n- ", 6, "\n")!));
        Assert.Equal("> a\n", Apply("> a\n> ", ListContinuation.OnEnter("> a\n> ", 6, "\n")!));
    }

    [Fact]
    public void Enter_uses_the_documents_line_ending()
    {
        Assert.Equal("- a\r\n- ", Apply("- a", ListContinuation.OnEnter("- a", 3, "\r\n")!));
    }

    [Fact]
    public void Enter_outside_lists_or_inside_fenced_code_falls_through()
    {
        Assert.Null(ListContinuation.OnEnter("plain", 5, "\n"));
        Assert.Null(ListContinuation.OnEnter("- a", 1, "\n"));

        const string text = "```\n- not a list\n```\n";
        var analysis = MarkdownAnalyzer.Analyze(text, MarkdownProfile.Gfm);
        Assert.Null(ListContinuation.OnEnter(text, 15, "\n", analysis.CodeBlocks));
    }
}
