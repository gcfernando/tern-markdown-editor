using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Tern.App.Commands;
using Tern.App.Settings;
using Tern.Domain.Text;

namespace Tern.Desktop.Tests;

public sealed class DocumentSearchWorkflowTests
{
    [AvaloniaFact]
    public async Task All_matches_remain_highlighted_after_edits_and_navigation_ignores_unrelated_caret_moves()
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.NewDocument("hot cold hot warm hot");
        await f.Window.RunCommandAsync(CommandIds.ViewSource);
        f.Window.ShowFindBar(false);
        await f.Window.SearchAsync("hot");
        var renderer = Assert.Single(f.Window.EditorControl.TextArea.TextView.BackgroundRenderers.OfType<Views.FindMatchRenderer>());
        Assert.Equal(3, renderer.Matches.Count);
        f.Window.EditorControl.Select(18, 0);
        f.Window.FindNext(true);
        Assert.Equal(9, f.Window.EditorControl.SelectionStart);
        Assert.Equal("2 of 3", f.Window.FindStatusText);
        tab.ApplyEdits([TextEdit.Insert(0, "Intro ")]);
        await Settle(f);
        Assert.Equal(3, renderer.Matches.Count);
        Assert.NotNull(renderer.Match);
        Assert.Equal("hot", tab.CurrentText.Substring(renderer.Match.Start, renderer.Match.Length));
        Assert.True(tab.IsDirty);
    }

    [AvaloniaFact]
    public async Task Results_are_clickable_wrap_is_announced_and_invalid_patterns_clear_stale_results()
    {
        using var f = new DesktopFixture();
        f.Shell.NewDocument("cat\ndog\ncat\ncat");
        f.Window.ShowFindBar(false);
        await f.Window.SearchAsync("cat");
        Assert.Equal(3, f.Window.FindResults.ItemCount);
        f.Window.FindResults.SelectedIndex = 2;
        Assert.Equal(12, f.Window.EditorControl.SelectionStart);
        f.Window.FindNext(true);
        Assert.Equal(0, f.Window.EditorControl.SelectionStart);
        Assert.Contains("Wrapped", f.Window.FindStatusText);
        f.Window.RegexToggle.IsChecked = true;
        await f.Window.SearchAsync("(");
        Assert.Contains("Invalid regular expression", f.Window.FindStatusText);
        Assert.Equal(0, f.Window.FindResults.ItemCount);
        Assert.False(f.Window.FindNextButton.IsEnabled);
        Assert.False(f.Window.ReplaceAllButton.IsEnabled);
    }

    [AvaloniaFact]
    public async Task Rapid_queries_and_tab_changes_cannot_navigate_or_replace_using_old_results()
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.NewDocument("alpha beta alpha");
        f.Window.ShowFindBar(true);
        var old = f.Window.SearchAsync("alpha");
        var current = f.Window.SearchAsync("beta");
        await Task.WhenAll(old, current);
        Assert.Equal("beta", f.Window.EditorControl.SelectedText);
        Assert.Equal("1 of 1", f.Window.FindStatusText);
        f.Window.ReplaceBox.Text = "changed";
        var other = f.Shell.NewDocument("untouched");
        f.Window.FindNext(true);
        f.Window.ReplaceCurrent();
        await Settle(f);
        Assert.Equal("untouched", other.CurrentText);
        Assert.Equal("alpha beta alpha", tab.CurrentText);
        Assert.Equal("No results", f.Window.FindStatusText);
    }

    [AvaloniaFact]
    public async Task Reading_search_starts_from_visible_position_instead_of_hidden_editor_selection()
    {
        using var f = new DesktopFixture();
        var source = string.Join("\n\n", Enumerable.Range(1, 100).Select(i => $"Paragraph {i} needle."));
        f.Shell.NewDocument(source);
        await f.Window.RunCommandAsync(CommandIds.ViewReading);
        await f.Window.RebuildPreviewAsync();
        f.Window.PreviewControl.ScrollToSourceLine(121);
        await Settle(f);
        f.Window.EditorControl.Select(0, 0);
        f.Window.ShowFindBar(false);
        await f.Window.SearchAsync("needle");
        await Settle(f);
        Assert.True(f.Window.EditorControl.SelectionStart > source.Length / 2);
        Assert.True(f.Window.FindBox.IsKeyboardFocusWithin);
    }

    [AvaloniaFact]
    public async Task Source_only_destinations_are_explained_and_zero_width_regex_navigation_advances()
    {
        using var f = new DesktopFixture();
        f.Shell.NewDocument("[Visible label](hidden-destination.md)\n\nNext paragraph");
        await f.Window.RunCommandAsync(CommandIds.ViewReading);
        await f.Window.RebuildPreviewAsync();
        f.Window.ShowFindBar(false);
        await f.Window.SearchAsync("hidden-destination");
        await Settle(f);
        Assert.Contains("Source-only match", f.Window.FindStatusText);
        Assert.True(f.Window.FindSourceButton.IsVisible);
        await f.Window.RunCommandAsync(CommandIds.ViewSource);
        f.Window.EditorControl.Select(0, 0);
        f.Window.RegexToggle.IsChecked = true;
        await f.Window.SearchAsync("^");
        f.Window.FindNext(true);
        Assert.Equal(39, f.Window.EditorControl.SelectionStart);
        f.Window.FindNext(true);
        Assert.Equal(40, f.Window.EditorControl.SelectionStart);
        f.Window.FindNext(true);
        Assert.Equal(0, f.Window.EditorControl.SelectionStart);
        Assert.Contains("Wrapped", f.Window.FindStatusText);
    }

    [AvaloniaFact]
    public async Task Replace_all_is_one_undo_and_does_not_save_the_file()
    {
        using var f = new DesktopFixture();
        const string source = "Cat cat concat cat";
        var path = await f.WriteAsync("replace-undo.md", source);
        var tab = (await f.Shell.OpenFileAsync(path))!;
        f.Window.ShowFindBar(true);
        f.Window.WordToggle.IsChecked = true;
        f.Window.ReplaceBox.Text = "dog";
        await f.Window.SearchAsync("cat");
        await f.Window.ReplaceAllAsync();
        Assert.Equal("dog dog concat dog", tab.CurrentText);
        Assert.Equal(source, await File.ReadAllTextAsync(path));
        Assert.True(tab.IsDirty);
        f.Window.EditorControl.Undo();
        Assert.Equal(source, tab.CurrentText);
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public async Task Compact_find_keeps_search_and_navigation_reachable(double scale)
    {
        using var f = new DesktopFixture();
        f.Shell.Settings.UiScale = scale;
        f.Window.ApplySettings();
        f.Window.Width = 900;
        f.Window.Height = 560;
        f.Shell.NewDocument("A needle here and another needle.");
        await f.Window.RunCommandAsync(CommandIds.ViewReading);
        await f.Window.RebuildPreviewAsync();
        f.Window.ShowFindBar(true);
        await f.Window.SearchAsync("needle");
        f.Window.FindResults.IsVisible = true;
        f.Window.FindResultsToggle.IsChecked = true;
        await Settle(f);
        foreach (var control in new Control[] { f.Window.FindBox, f.Window.FindNextButton, f.Window.FindPreviousButton, f.Window.ReplaceAllButton })
        {
            Assert.True(control.IsEffectivelyVisible);
            var origin = control.TranslatePoint(default, f.Window)!.Value;
            Assert.InRange(origin.X, 0, f.Window.Bounds.Width - control.Bounds.Width + 1);
            Assert.InRange(origin.Y, 0, f.Window.Bounds.Height - control.Bounds.Height + 1);
        }
        f.Window.FindNext(true);
        Assert.Equal("2 of 2", f.Window.FindStatusText);
        var folder = Path.Combine(Screenshots.Folder, "search-upgrade");
        Directory.CreateDirectory(folder);
        using var frame = f.Window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(folder, $"compact-{scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}.png"));
        Assert.True(f.Window.PreviewControl.Bounds.Height > 60, $"Reading viewport height: {f.Window.PreviewControl.Bounds.Height}");
    }

    [AvaloniaTheory]
    [InlineData(ThemeChoice.Midnight)]
    [InlineData(ThemeChoice.Paper)]
    public async Task Search_ui_captures_all_mode_results_and_replace_controls(ThemeChoice theme)
    {
        using var f = new DesktopFixture();
        f.Shell.Settings.Theme = theme;
        f.Window.ApplyTheme();
        f.Window.ApplySettings();
        f.Shell.NewDocument("# Search your notes\n\nFind a **search** result here. Another search result follows.\n\n- Search the current note\n- Review each search result\n\n```text\nsearch in code\n```\n");
        f.Window.Width = 1280;
        f.Window.Height = 800;
        foreach (var mode in new[] { CommandIds.ViewSource, CommandIds.ViewSplit, CommandIds.ViewReading })
        {
            await f.Window.RunCommandAsync(mode);
            await f.Window.RebuildPreviewAsync();
            f.Window.ShowFindBar(true);
            await f.Window.SearchAsync("search");
            f.Window.FindResultsToggle.IsChecked = true;
            f.Window.FindResults.IsVisible = true;
            await Settle(f);
            Assert.True(f.Window.FindResults.ItemCount >= 6);
            Assert.True(f.Window.FindNextButton.IsEnabled);
            Assert.True(f.Window.FindBox.IsKeyboardFocusWithin, $"Find lost focus in {mode}; focused: {f.Window.FocusManager?.GetFocusedElement()}");
            var folder = Path.Combine(Screenshots.Folder, "search-upgrade");
            Directory.CreateDirectory(folder);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
            using var frame = f.Window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame.Save(Path.Combine(folder, $"{theme.ToString().ToLowerInvariant()}-{mode.Replace("view.", "")}.png"));
        }
    }

    private static async Task Settle(DesktopFixture f)
    {
        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(50);
            f.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
