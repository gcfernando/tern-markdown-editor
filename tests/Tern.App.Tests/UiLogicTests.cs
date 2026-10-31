using Tern.App.Commands;
using Tern.App.Editing;
using Tern.App.Palette;
using Tern.App.Panels;
using Tern.Markdown;
using Tern.Search;
using Tern.Workspaces;

namespace Tern.App.Tests;

public sealed class UiLogicTests
{
    [Fact]
    public void Bold_wraps_selection()
    {
        var result = FormattingService.Execute(CommandIds.Bold, "hello world", 0, 5, null);

        Assert.NotNull(result);
        Assert.Equal("**hello** world", Tern.Domain.Text.TextEdit.ApplyAll("hello world", result.Edits));
    }

    [Fact]
    public void Unknown_command_is_not_a_formatting_command()
    {
        Assert.False(FormattingService.IsFormattingCommand(CommandIds.Save));
        Assert.Null(FormattingService.Execute(CommandIds.Save, "x", 0, 0, null));
    }

    [Fact]
    public void Insert_table_at_end_of_paragraph_adds_blank_line_and_table()
    {
        var result = FormattingService.InsertTable("Intro", 5, 0, "\n");
        var text = Tern.Domain.Text.TextEdit.ApplyAll("Intro", result.Edits);

        Assert.StartsWith("Intro\n\n| Column 1", text, StringComparison.Ordinal);
        Assert.Equal("Column 1", text.Substring(result.SelectionStart, result.SelectionLength));
    }

    [Fact]
    public void Task_toggle_flips_checkbox_on_caret_line()
    {
        const string Text = "- [ ] ship it\n";
        var analysis = MarkdownAnalyzer.Analyze(Text, MarkdownProfile.Extended);
        var result = FormattingService.Execute(CommandIds.ToggleTaskAtCaret, Text, 2, 0, analysis);

        Assert.NotNull(result);
        Assert.Equal("- [x] ship it\n", Tern.Domain.Text.TextEdit.ApplyAll(Text, result.Edits));
    }

    [Fact]
    public async Task In_file_find_honours_case_whole_word_and_regex()
    {
        const string Text = "Cat cat concat";

        var plain = await InFileFinder.FindAsync(Text, new FindOptions("cat"));
        var cased = await InFileFinder.FindAsync(Text, new FindOptions("cat", MatchCase: true));
        var word = await InFileFinder.FindAsync(Text, new FindOptions("cat", WholeWord: true));
        var regex = await InFileFinder.FindAsync(Text, new FindOptions("c(a)t", Replacement: "$1", UseRegex: true));

        Assert.Equal(3, plain.Matches.Count);
        Assert.Equal(2, cased.Matches.Count);
        Assert.Equal(2, word.Matches.Count);
        Assert.Equal("a", regex.Matches[0].Replacement);
    }

    [Fact]
    public async Task In_file_find_reports_bad_regex()
    {
        var result = await InFileFinder.FindAsync("x", new FindOptions("(", UseRegex: true));

        Assert.NotNull(result.Error);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public async Task Find_navigation_wraps()
    {
        var result = await InFileFinder.FindAsync("a b a", new FindOptions("a"));

        Assert.Equal(1, result.IndexAtOrAfter(1));
        Assert.Equal(0, result.IndexAtOrAfter(5));
        Assert.Equal(1, result.IndexBefore(0));
    }

    [Fact]
    public void Palette_ranks_prefix_first_and_filters_by_every_word()
    {
        var registry = CommandRegistry.CreateDefault();

        var all = CommandPaletteModel.Filter(registry, "");
        var filtered = CommandPaletteModel.Filter(registry, "save");
        var none = CommandPaletteModel.Filter(registry, "zzzz-nothing");

        Assert.Equal(registry.Commands.Count, all.Count);
        Assert.NotEmpty(filtered);
        var matchingFeatures = Tern.App.Help.FeatureCatalog.Search(registry, "save").Select(f => f.LaunchCommandId ?? f.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(filtered, e => Assert.Contains(e.CommandId, matchingFeatures));
        Assert.Contains(filtered, e => e.CommandId == CommandIds.ChangeLineEnding);
        Assert.StartsWith("Save", filtered[0].Title, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(none);
    }

    [Fact]
    public async Task Document_panel_summarises_outline_info_and_tags()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("a.md", "---\ntags: [alpha, beta]\n---\n# Title\n\n## Part\nsome words [x](https://e.com) [y](missing.md)\n");
        var tab = await f.Shell.OpenFileAsync(path);
        Assert.NotNull(tab);
        await Task.Delay(500);

        var panel = DocumentPanel.Build(tab, null);

        Assert.Equal(["Title", "Part"], panel.Outline.Select(o => o.Text));
        Assert.Equal(2, panel.Links.Count);
        Assert.Equal(1, panel.LinksChecked);
        Assert.Equal(["alpha", "beta"], panel.Tags);
        Assert.True(panel.Info.Words > 0);
        Assert.Equal(1, panel.Info.ReadMinutes);
    }

    [Fact]
    public void Age_is_described_in_words()
    {
        var now = DateTime.UtcNow;

        Assert.Equal("Not saved yet", DocumentPanel.DescribeAge(null, now));
        Assert.Equal("just now", DocumentPanel.DescribeAge(now.AddSeconds(-5), now));
        Assert.Equal("5 min ago", DocumentPanel.DescribeAge(now.AddMinutes(-5), now));
        Assert.Equal("3 days ago", DocumentPanel.DescribeAge(now.AddDays(-3), now));
    }

    [Fact]
    public async Task Task_index_counts_open_tasks_and_prefers_unsaved_text()
    {
        using var f = new ShellFixture();
        var a = await f.WriteAsync("a.md", "- [ ] one\n- [x] two\n");
        var b = await f.WriteAsync("b.md", "- [ ] three\n");
        var files = new[] { new WorkspaceFile(a, "a.md", 0, DateTime.UtcNow), new WorkspaceFile(b, "b.md", 0, DateTime.UtcNow) };
        var buffers = new Dictionary<string, string> { [b] = "- [x] three\n- [ ] four\n" };

        var entries = await TaskIndex.ScanAsync(files, MarkdownProfile.Extended, buffers);

        Assert.Equal(4, entries.Count);
        Assert.Equal(2, TaskIndex.OpenCount(entries));
        Assert.Contains(entries, e => e.Text.Contains("four", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Ctrl+OemQuestion", "Ctrl+/")]
    [InlineData("Ctrl+OemComma", "Ctrl+,")]
    [InlineData("Ctrl+Alt+D9", "Ctrl+Alt+9")]
    [InlineData("Ctrl+Shift+P", "Ctrl+Shift+P")]
    public void Describe_turns_stored_gestures_into_readable_text(string stored, string shown)
    {
        Assert.Equal(shown, Gesture.Describe(stored));
    }
}

