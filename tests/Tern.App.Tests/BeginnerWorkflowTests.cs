using Tern.App.Commands;
using Tern.App.Editing;
using Tern.App.Shell;
using Tern.Domain.Documents;
using Tern.Domain.Text;

namespace Tern.App.Tests;

public sealed class BeginnerWorkflowTests
{
    [Theory]
    [InlineData(CommandIds.ExportHtml)]
    [InlineData(CommandIds.ExportPdf)]
    [InlineData(CommandIds.Print)]
    public void Output_readiness_distinguishes_opened_files_from_empty_drafts(string id)
    {
        foreach (var text in new[] { "", " \r\n\t" })
        {
            var draft = new DocumentSession(null, text, TextFileFormat.NewFileDefault, null);
            Assert.False(DocumentCommandPolicy.CanExecute(id, new(draft)));
            Assert.Contains("write something", DocumentCommandPolicy.DisabledReason(id, new(draft)));
            var opened = new DocumentSession("opened.md", text, TextFileFormat.NewFileDefault, null);
            Assert.True(DocumentCommandPolicy.CanExecute(id, new(opened)));
        }
        var meaningful = new DocumentSession(null, "A note", TextFileFormat.NewFileDefault, null);
        Assert.True(DocumentCommandPolicy.CanExecute(id, new(meaningful)));
        Assert.False(DocumentCommandPolicy.CanExecute(id, new(meaningful, IsBusy: true)));
    }

    [Fact]
    public async Task Initial_placeholder_is_replaced_but_deliberate_blank_tabs_are_kept()
    {
        using var f = new ShellFixture();
        var initial = f.Shell.NewStartupDocument();
        var deliberate = f.Shell.NewDocument();
        f.Shell.ActiveTab = initial;
        var opened = await f.Shell.OpenFileAsync(await f.WriteAsync("opened.md", "# Opened"));
        Assert.NotNull(opened);
        Assert.DoesNotContain(initial, f.Shell.Tabs);
        Assert.Contains(deliberate, f.Shell.Tabs);
        Assert.Equal(2, f.Shell.Tabs.Count);
        Assert.Equal(0, f.Dialogs.SavePrompts);
    }

    [Theory]
    [InlineData(SaveChoice.Cancel, false)]
    [InlineData(SaveChoice.Discard, true)]
    [InlineData(SaveChoice.Save, true)]
    public async Task Edited_startup_note_obeys_save_discard_cancel(SaveChoice choice, bool succeeds)
    {
        using var f = new ShellFixture();
        var initial = f.Shell.NewStartupDocument();
        initial.ApplyEdits([TextEdit.Insert(0, "My work")]);
        f.Dialogs.SaveChoice = choice;
        f.Dialogs.SavePath = Path.Combine(f.Docs, "saved-start.md");
        var result = await f.Shell.OpenFileAsync(await f.WriteAsync("opened.md", "New file"));
        Assert.Equal(succeeds, result is not null);
        Assert.Equal(1, f.Dialogs.SavePrompts);
        if (!succeeds) Assert.Equal("My work", Assert.Single(f.Shell.Tabs).CurrentText);
        if (choice == SaveChoice.Save) Assert.Equal("My work", await File.ReadAllTextAsync(f.Dialogs.SavePath));
    }

    [Fact]
    public async Task Failed_open_keeps_the_startup_note_even_after_discard_approval()
    {
        using var f = new ShellFixture();
        var initial = f.Shell.NewStartupDocument();
        initial.ApplyEdits([TextEdit.Insert(0, "Keep me")]);
        f.Dialogs.SaveChoice = SaveChoice.Discard;
        Assert.Null(await f.Shell.OpenFileAsync(Path.Combine(f.Docs, "missing.md")));
        Assert.Same(initial, Assert.Single(f.Shell.Tabs));
        Assert.Equal("Keep me", initial.CurrentText);
        Assert.Contains("Check", Assert.Single(f.Dialogs.Messages));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void Code_block_fences_preserve_selected_text_and_newline(string newline)
    {
        var text = "value ``` quoted";
        var result = FormattingService.Execute(CommandIds.InsertCodeBlock, text, 0, text.Length, null, newline)!;
        var formatted = result.ApplyTo(text);
        Assert.StartsWith("````" + newline, formatted);
        Assert.Equal(text, formatted.Substring(result.SelectionStart, result.SelectionLength));
        Assert.EndsWith("````" + newline + newline, formatted);
    }

    [Theory]
    [InlineData(CommandIds.ToggleBullet)]
    [InlineData(CommandIds.ToggleNumbered)]
    [InlineData(CommandIds.ToggleTask)]
    [InlineData(CommandIds.ToggleQuote)]
    public void Repeated_line_formatting_keeps_all_selected_lines(string id)
    {
        const string text = "first\nsecond";
        var first = FormattingService.Execute(id, text, 0, text.Length, null)!;
        var formatted = first.ApplyTo(text);
        var second = FormattingService.Execute(id, formatted, first.SelectionStart, first.SelectionLength, null)!;
        Assert.Equal(text, second.ApplyTo(formatted));
        var empty = FormattingService.Execute(id, "", 0, 0, null)!;
        Assert.Equal(0, empty.SelectionLength);
        Assert.Equal(empty.ApplyTo("").Length, empty.SelectionStart);
    }

    [Theory]
    [InlineData("some `code` here")]
    [InlineData("`edge`")]
    [InlineData("ordinary")]
    public void Inline_code_uses_safe_delimiters_and_repeated_command_removes_them(string text)
    {
        var first = FormattingService.Execute(CommandIds.InlineCode, text, 0, text.Length, null)!;
        var formatted = first.ApplyTo(text);
        var second = FormattingService.Execute(CommandIds.InlineCode, formatted, first.SelectionStart, first.SelectionLength, null)!;
        Assert.Equal(text, second.ApplyTo(formatted));
        var html = Markdig.Markdown.ToHtml(formatted);
        Assert.Contains("<code>", html);
        Assert.DoesNotContain("<em>", html);
    }

    [Fact]
    public void Heading_shift_commands_move_one_level_and_respect_boundaries()
    {
        var first = FormattingService.Execute(CommandIds.HeadingDown, "Title", 0, 0, null)!;
        Assert.Equal("# Title", first.ApplyTo("Title"));
        var second = FormattingService.Execute(CommandIds.HeadingDown, "# Title", 2, 0, null)!;
        Assert.Equal("## Title", second.ApplyTo("# Title"));
        Assert.Equal("# Title", FormattingService.Execute(CommandIds.HeadingUp, "## Title", 3, 0, null)!.ApplyTo("## Title"));
        Assert.Equal("Title", FormattingService.Execute(CommandIds.HeadingUp, "# Title", 2, 0, null)!.ApplyTo("# Title"));
    }
}
