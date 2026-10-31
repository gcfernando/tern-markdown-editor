using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Tern.App.Commands;
using Tern.Domain.Text;

namespace Tern.Desktop.Tests;

public sealed class ReadingNavigationTests
{
    private static string LongDocument(string name) => string.Join("\n\n", Enumerable.Range(1, 200).Select(i => $"## {name} section {i}\n\nParagraph {i} with readable content."));

    [AvaloniaFact]
    public async Task Reading_find_and_next_scroll_visible_matches_without_stealing_find_focus()
    {
        using var f = new DesktopFixture();
        var source = LongDocument("Search")
            .Replace("Paragraph 101 with readable content.", "Needle match one.", StringComparison.Ordinal)
            .Replace("Paragraph 151 with readable content.", "Needle match two.", StringComparison.Ordinal);
        var path = await f.WriteAsync("find-reading.md", source);
        var tab = (await f.Shell.OpenFileAsync(path))!;
        await f.Window.RunCommandAsync(CommandIds.ViewReading);
        await f.Window.RebuildPreviewAsync();
        await Settle(f);
        var revision = tab.Session.Revision;
        f.Window.ShowFindBar(false);
        await f.Window.SearchAsync("Needle");
        await f.Window.RebuildPreviewAsync();
        await Settle(f);

        Assert.InRange(f.Window.PreviewControl.TopSourceLine, 400, 406);
        Assert.Equal(source.IndexOf("Needle", StringComparison.Ordinal), f.Window.EditorControl.SelectionStart);
        Assert.Equal("Needle", f.Window.EditorControl.SelectedText);
        Assert.Equal("1 of 2", f.Window.FindStatusText);
        Assert.True(f.Window.FindBox.IsKeyboardFocusWithin);

        f.Window.FindNext(true);
        await Settle(f);
        Assert.InRange(f.Window.PreviewControl.TopSourceLine, 600, 606);
        Assert.Equal(source.LastIndexOf("Needle", StringComparison.Ordinal), f.Window.EditorControl.SelectionStart);
        Assert.Equal("Needle", f.Window.EditorControl.SelectedText);
        Assert.Equal("2 of 2", f.Window.FindStatusText);
        Assert.True(f.Window.FindBox.IsKeyboardFocusWithin);

        f.Window.FindBox.RaiseEvent(new KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Key.Escape });
        await Settle(f);
        Assert.False(f.Window.FindBar.IsVisible);
        Assert.True(f.Window.PreviewControl.IsKeyboardFocusWithin);
        Assert.False(f.Window.EditorHost.IsVisible);
        Assert.Equal(source, tab.CurrentText);
        Assert.Equal(source, await File.ReadAllTextAsync(path));
        Assert.Equal(revision, tab.Session.Revision);
        Assert.False(tab.IsDirty);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reading_navigation_reaches_requested_line_after_same_or_new_tab_preview(bool newTab)
    {
        using var f = new DesktopFixture();
        var firstPath = await f.WriteAsync("first.md", LongDocument("First"));
        var targetPath = newTab ? await f.WriteAsync("target.md", LongDocument("Target")) : firstPath;
        await f.Shell.OpenFileAsync(firstPath);
        await f.Window.RunCommandAsync(CommandIds.ViewReading);
        await f.Window.RebuildPreviewAsync();
        await Settle(f);
        if (!newTab) f.Shell.ActiveTab!.ApplyEdits([TextEdit.Insert(0, "Unsaved introductory paragraph.\n\n")]);
        var targetText = newTab ? await File.ReadAllTextAsync(targetPath) : f.Shell.ActiveTab!.CurrentText;
        var expectedDirty = !newTab;

        Assert.True(await f.Shell.GoToAsync(targetPath, 401, 1));
        var tab = f.Shell.ActiveTab!;
        var revision = tab.Session.Revision;
        var baseline = tab.Session.Baseline;
        await f.Window.RebuildPreviewAsync();
        await Settle(f);

        Assert.InRange(f.Window.PreviewControl.TopSourceLine, 398, 404);
        Assert.Equal(targetText, tab.CurrentText);
        Assert.Equal(revision, tab.Session.Revision);
        Assert.Equal(baseline, tab.Session.Baseline);
        Assert.Equal(expectedDirty, tab.IsDirty);
        Assert.False(f.Window.EditorHost.IsVisible);
        Assert.True(f.Window.PreviewHost.IsVisible);

        // Top-of-file navigation must also survive replacing a model that was scrolled.
        Assert.True(await f.Shell.GoToAsync(targetPath, 1, 1));
        await f.Window.RebuildPreviewAsync();
        await Settle(f);
        Assert.InRange(f.Window.PreviewControl.TopSourceLine, 1, 3);
        Assert.Equal(revision, tab.Session.Revision);
        Assert.Equal(expectedDirty, tab.IsDirty);
    }

    private static async Task Settle(DesktopFixture f)
    {
        for (var i = 0; i < 4; i++)
        {
            f.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
    }
}
