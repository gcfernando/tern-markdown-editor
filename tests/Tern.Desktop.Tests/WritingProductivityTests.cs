using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.App.Editing;
using Tern.Desktop.Views;
using Tern.Domain.Text;
using Tern.Markdown;
using Tern.Desktop.Services;
using System.Text;

namespace Tern.Desktop.Tests;

public sealed class WritingProductivityTests
{
    private const string TableSource = "before\n\n|A|B|C|\n|---|---:|:---:|\n|z|10|r|\n|a|2|s|\n\nafter";

    public static IEnumerable<object[]> Operations() => TableEditingService.Commands.Select(id => new object[] { id });

    [AvaloniaTheory]
    [MemberData(nameof(Operations))]
    public async Task Every_table_command_is_exposed_and_changes_the_real_buffer_in_one_undo(string id)
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.NewDocument(TableSource);
        await WaitUntil(() => tab.AnalysisRevision == tab.Session.Revision, f.Window);
        f.Window.EditorControl.Select(TableSource.IndexOf("10", StringComparison.Ordinal), 0);
        Assert.True(f.Window.CanRunCommand(id));
        Assert.True(f.Window.FindControl<Button>("TableActions")!.IsVisible);
        Assert.Contains(f.Shell.Registry.Commands, command => command.Id == id);
        var menu = f.Window.EditorControl.ContextMenu!;
        f.Window.EditorControl.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
        var table = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Table"));
        Assert.True(table.IsVisible);
        Assert.Equal(TableEditingService.Commands, table.Items.OfType<MenuItem>().Select(item => item.Tag as string));
        menu.Close();
        Assert.False(f.Window.CanRunCommand(CommandIds.Undo));
        await f.Window.RunCommandAsync(id);
        var changed = tab.CurrentText;
        Assert.NotEqual(TableSource, changed);
        await f.Window.RunCommandAsync(CommandIds.Undo);
        Assert.Equal(TableSource, tab.CurrentText);
        Assert.False(f.Window.CanRunCommand(CommandIds.Undo));
        await f.Window.RunCommandAsync(CommandIds.Redo);
        Assert.Equal(changed, tab.CurrentText);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prepared_table_edits_refuse_a_new_revision_or_selection(bool changeSelection)
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.NewDocument(TableSource);
        await WaitUntil(() => tab.AnalysisRevision == tab.Session.Revision, f.Window);
        f.Window.EditorControl.Select(TableSource.IndexOf("10", StringComparison.Ordinal), 0);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Window.BeforeTableApplyAsync = () => { ready.SetResult(); return release.Task; };
        var operation = f.Window.RunCommandAsync(CommandIds.TableInsertRowBelow);
        await ready.Task;
        if (changeSelection) f.Window.EditorControl.Select(0, 0);
        else tab.ApplyEdits([TextEdit.Insert(0, "new revision\n")]);
        var expected = tab.CurrentText;
        release.SetResult();
        await operation;
        Assert.Equal(expected, tab.CurrentText);
        Assert.Contains("changed", f.Shell.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task Read_only_table_commands_are_disabled_and_keep_the_original_bytes()
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("readonly.md", TableSource);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var tab = (await f.Shell.OpenFileAsync(path))!;
            tab.Session.SetReadOnly(true);
            Assert.True(tab.IsReadOnly);
            await WaitUntil(() => tab.AnalysisRevision == tab.Session.Revision, f.Window);
            f.Window.EditorControl.Select(TableSource.IndexOf("10", StringComparison.Ordinal), 0);
            foreach (var id in TableEditingService.Commands)
            {
                Assert.False(f.Window.CanRunCommand(id));
                await f.Window.RunCommandAsync(id);
            }
            Assert.Equal(TableSource, tab.CurrentText);
            Assert.Equal(TableSource, await File.ReadAllTextAsync(path));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    [AvaloniaTheory]
    [InlineData("Paste as table")]
    [InlineData("Paste plain text")]
    [InlineData("Cancel")]
    public async Task Three_by_three_spreadsheet_paste_offers_a_real_guarded_choice(string choice)
    {
        using var f = new DesktopFixture();
        const string original = "# Note\n\n";
        const string grid = "Name\tScore\tTeam\r\nAnn\t5\tRed\r\nBob\t7\tBlue\r\n";
        var tab = f.Shell.NewDocument(original);
        f.Window.EditorControl.Select(original.Length, 0);
        await f.Window.Clipboard!.SetTextAsync(grid);
        var paste = f.Window.PasteEditorAsync();
        await WaitUntil(() => f.Window.OwnedWindows.OfType<PromptWindow>().Any(), f.Window);
        var prompt = Assert.Single(f.Window.OwnedWindows.OfType<PromptWindow>());
        var button = Assert.Single(prompt.GetVisualDescendants().OfType<Button>(), item => Equals(item.Content, choice));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await paste;
        if (choice == "Cancel") Assert.Equal(original, tab.CurrentText);
        else
        {
            if (choice == "Paste as table")
            {
                var info = Assert.Single(MarkdownAnalyzer.Analyze(tab.CurrentText, MarkdownProfile.Gfm).Tables);
                Assert.Equal((3, 3), (info.Rows, info.Columns));
                Assert.Contains("| Name", tab.CurrentText, StringComparison.Ordinal);
                Assert.Contains("| Bob", tab.CurrentText, StringComparison.Ordinal);
            }
            else Assert.Equal(original + grid, tab.CurrentText);
            await f.Window.RunCommandAsync(CommandIds.Undo);
            Assert.Equal(original, tab.CurrentText);
            Assert.False(f.Window.CanRunCommand(CommandIds.Undo));
        }
    }

    [AvaloniaFact]
    public async Task Plain_paste_never_offers_a_table_or_wraps_a_selected_url()
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.NewDocument("selected");
        f.Window.EditorControl.Select(0, tab.CurrentText.Length);
        await f.Window.Clipboard!.SetTextAsync("https://example.com");
        await f.Window.RunCommandAsync(CommandIds.PastePlain);
        Assert.Equal("https://example.com", tab.CurrentText);
        await f.Window.RunCommandAsync(CommandIds.Undo);
        f.Window.EditorControl.Select(0, tab.CurrentText.Length);
        const string grid = "a\tb\nc\td";
        await f.Window.Clipboard.SetTextAsync(grid);
        f.Window.KeyPress(Key.V, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.V, null);
        await WaitUntil(() => tab.CurrentText == grid, f.Window);
        Assert.Empty(f.Window.OwnedWindows);
        Assert.Equal(CommandIds.PastePlain, f.Shell.Registry.FindCommandFor(new Gesture(true, false, true, false, "V")));
    }

    [AvaloniaTheory]
    [InlineData("- alpha\n+ beta\n* gamma")]
    [InlineData("1. alpha\n2) beta\n- [ ] gamma")]
    public async Task List_tab_and_shift_tab_preserve_all_markers_as_one_undo(string source)
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.NewDocument(source);
        Dispatcher.UIThread.RunJobs();
        f.Window.UpdateLayout();
        Assert.True(f.Window.EditorControl.TextArea.Focus());
        f.Window.EditorControl.Select(0, source.Length);
        f.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        var expected = string.Join("\n", source.Split('\n').Select(line => new string(' ', f.Shell.Settings.TabSize) + line));
        Assert.Equal(expected, tab.CurrentText);
        f.Window.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
        Assert.Equal(source, tab.CurrentText);
        await f.Window.RunCommandAsync(CommandIds.Undo);
        Assert.Equal(expected, tab.CurrentText);
        await f.Window.RunCommandAsync(CommandIds.Undo);
        Assert.Equal(source, tab.CurrentText);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Html_paste_uses_real_clipboard_formats_and_plain_paste_bypasses_conversion(bool windows)
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.NewDocument("original");
        const string html = "<h1>Title</h1><p><strong>bold</strong></p><ul><li>item</li></ul>"
            + "<p><a href='https://example.com'>source</a> <code>x()</code></p>"
            + "<table><tr><th>A</th><th>B</th></tr><tr><td>1</td><td>2</td></tr></table>";
        var transfer = new DataTransfer();
        var item = DataTransferItem.CreateText("plain alternative");
        item.Set(windows ? HtmlClipboard.WindowsFormat : HtmlClipboard.MimeFormat,
            windows ? HtmlClipboardTests.WindowsHtml(html) : Encoding.UTF8.GetBytes(html));
        transfer.Add(item);
        await f.Window.Clipboard!.SetDataAsync(transfer);
        f.Window.EditorControl.Select(0, tab.CurrentText.Length);
        await f.Window.PasteEditorAsync();
        var analysis = MarkdownAnalyzer.Analyze(tab.CurrentText, MarkdownProfile.Gfm);
        Assert.Single(analysis.Headings);
        Assert.Single(analysis.Tables);
        Assert.Single(analysis.CodeSpans);
        Assert.Contains("**bold**", tab.CurrentText);
        await f.Window.RunCommandAsync(CommandIds.Undo);
        Assert.Equal("original", tab.CurrentText);
        Assert.False(f.Window.CanRunCommand(CommandIds.Undo));
        f.Window.EditorControl.Select(0, tab.CurrentText.Length);
        await f.Window.PasteEditorAsync(EditorPasteMode.Plain);
        Assert.Equal("plain alternative", tab.CurrentText);
    }

    [AvaloniaFact]
    public async Task Malformed_html_reports_failure_and_leaves_the_selection_and_buffer_unchanged()
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.NewDocument("preserve");
        f.Window.EditorControl.Select(0, tab.CurrentText.Length);
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(HtmlClipboard.WindowsFormat, Encoding.UTF8.GetBytes("invalid")));
        await f.Window.Clipboard!.SetDataAsync(transfer);
        await f.Window.PasteEditorAsync();
        Assert.Equal("preserve", tab.CurrentText);
        Assert.Equal(8, f.Window.EditorControl.SelectionLength);
        Assert.Contains("Nothing was pasted", f.Shell.StatusMessage);
        Assert.Contains("fragment offsets", f.Shell.StatusMessage);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Spelling_suggestions_are_masked_revision_guarded_and_undoable(bool stale)
    {
        using var f = new DesktopFixture();
        const string source = "A mispelled word and `mispelledcode`.";
        var tab = f.Shell.NewDocument(source);
        string? submitted = null;
        f.Window.SpellCheck = (text, _) =>
        {
            submitted = text;
            var start = text.IndexOf("mispelled", StringComparison.Ordinal);
            return start < 0 ? [] : [new SpellingIssue(start, 9, "mispelled", ["misspelled"])];
        };
        f.Shell.Settings.SpellCheckEnabled = true;
        // A tab switch schedules the production debounce and analysis-aware worker.
        var other = f.Shell.NewDocument("other");
        f.Shell.ActiveTab = tab;
        await WaitUntil(() => f.Window.SpellingIssues.Count > 0, f.Window);
        Assert.NotNull(submitted);
        Assert.DoesNotContain("mispelledcode", submitted);
        f.Window.EditorControl.Select(3, 0);
        var menu = f.Window.EditorControl.ContextMenu!;
        f.Window.EditorControl.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
        var spelling = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Spelling"));
        await WaitUntil(() => spelling.Items.Count > 0, f.Window);
        var suggestion = Assert.Single(spelling.Items.OfType<MenuItem>(), item => Equals(item.Header, "misspelled"));
        Assert.True(suggestion.IsEnabled);
        if (stale) tab.ApplyEdits([TextEdit.Insert(0, "new ")]);
        var expected = tab.CurrentText;
        suggestion.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        if (stale)
        {
            Assert.Equal(expected, tab.CurrentText);
            Assert.Contains("changed", f.Shell.StatusMessage);
        }
        else
        {
            Assert.Equal(source.Replace("mispelled word", "misspelled word", StringComparison.Ordinal), tab.CurrentText);
            await f.Window.RunCommandAsync(CommandIds.Undo);
            Assert.Equal(source, tab.CurrentText);
        }
    }

    private static async Task WaitUntil(Func<bool> condition, Window window)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
        Assert.True(condition(), "The expected rendered writing state did not become ready.");
    }
}
