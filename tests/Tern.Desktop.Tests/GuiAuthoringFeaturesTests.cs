using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.App.Editing;
using Tern.Desktop.Views;
using Tern.Markdown;

namespace Tern.Desktop.Tests;

public sealed class GuiAuthoringFeaturesTests
{
    [AvaloniaFact]
    public async Task Link_dialog_result_is_undoable_and_stale_result_cannot_replace_newer_text()
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.NewDocument("chosen label");
        f.Window.EditorControl.Select(0, 12);
        f.Window.LinkInputPicker = _ => Task.FromResult<LinkInput?>(new("label [with] brackets", "notes/my file.md#part"));
        await f.Window.RunCommandAsync(CommandIds.InsertLink);
        Assert.Equal("[label \\[with\\] brackets](notes/my%20file.md#part)", tab.CurrentText);
        await f.Window.RunCommandAsync(CommandIds.Undo);
        Assert.Equal("chosen label", tab.CurrentText);
        var result = new TaskCompletionSource<LinkInput?>();
        f.Window.LinkInputPicker = _ => result.Task;
        var pending = f.Window.RunCommandAsync(CommandIds.InsertLink);
        tab.ApplyEdits([Tern.Domain.Text.TextEdit.Insert(0, "new ")]);
        result.SetResult(new("old", "old.md")); await pending;
        Assert.Equal("new chosen label", tab.CurrentText);
    }

    [AvaloniaFact]
    public async Task Reading_task_changes_only_its_marker_and_stale_preview_is_rejected()
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.NewDocument("# Tasks\n\n- [ ] first\n- [x] second\n");
        await f.Window.RebuildPreviewAsync(); f.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var boxes = f.Window.PreviewControl.GetVisualDescendants().OfType<CheckBox>().ToArray();
        Assert.Equal(2, boxes.Length);
        Assert.True(f.Window.PreviewControl.CanToggleTasks);
        Assert.True(boxes[0].IsHitTestVisible);
        Assert.True(f.Window.CanRunCommand(CommandIds.ToggleTask));
        boxes[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 100 && !tab.CurrentText.Contains("[x] first", StringComparison.Ordinal); i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.Equal("# Tasks\n\n- [x] first\n- [x] second\n", tab.CurrentText);
        await f.Window.RunCommandAsync(CommandIds.Undo);
        Assert.Contains("- [ ] first", tab.CurrentText);
        tab.ApplyEdits([Tern.Domain.Text.TextEdit.Insert(0, "new\n")]);
        boxes[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Contains("- [x] second", tab.CurrentText);
        Assert.Contains("preview changed", f.Shell.StatusMessage);
    }

    [Theory]
    [InlineData("# Note\r\n", "---\r\ntags: [\"new tag\", \"other\"]\r\n---\r\n\r\n# Note\r\n")]
    [InlineData("---\r\n# keep comment\r\ntags: [old]\r\ncustom: untouched\r\n---\r\n# Note", "---\r\n# keep comment\r\ntags: [\"new tag\", \"other\"]\r\ncustom: untouched\r\n---\r\n# Note")]
    public void Tags_preserve_unknown_metadata_and_newline_style(string source, string expected)
    {
        var edit = TagEdits.Create(source, MarkdownAnalyzer.Analyze(source, MarkdownProfile.Gfm), "new tag, other", "\r\n");
        Assert.NotNull(edit);
        Assert.Equal(expected, Tern.Domain.Text.TextEdit.ApplyAll(source, [edit.Value]));
    }

    [Theory]
    [InlineData("tags: &shared [one]")]
    [InlineData("tags:\n  - one")]
    [InlineData("tags: [one] # preserve")]
    public void Complex_tags_require_source_editing(string yaml)
    {
        var text = "---\n" + yaml + "\n---\n# Note";
        Assert.Null(TagEdits.Create(text, MarkdownAnalyzer.Analyze(text, MarkdownProfile.Gfm), "two", "\n"));
    }
}
