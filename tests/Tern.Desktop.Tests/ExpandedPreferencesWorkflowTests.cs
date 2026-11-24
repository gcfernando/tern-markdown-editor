using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Tern.App.Commands;
using Tern.App.Settings;
using Tern.Desktop.ViewModels;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class ExpandedPreferencesWorkflowTests
{
    [AvaloniaFact]
    public async Task Expanded_controls_apply_to_live_views_preserve_source_and_reset_only_their_category()
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("unchanged-preferences.md", "Keep  spaces\r\n\tand tabs\n");
        var bytes = await File.ReadAllBytesAsync(path);
        var tab = (await f.Shell.OpenFileAsync(path))!;
        var format = tab.Session.Format;
        var window = new SettingsWindow(f.Shell.Settings, initialCategory: "Editor", commit: () =>
        { if (!f.Shell.SaveSettings()) throw new IOException("Preferences failed"); f.Window.ApplySettings(); });
        window.Show(f.Window);
        try
        {
            foreach (var name in new[] { "Show spaces in Source", "Show tabs in Source", "Show line-ending markers", "Show Source column ruler" })
                Find<CheckBox>(window, name).IsChecked = true;
            Find<NumericUpDown>(window, "Source ruler column").Value = 120;
            Find<NumericUpDown>(window, "Source line spacing").Value = 1.5m;
            Find<CheckBox>(window, "Scroll below the last Source line").IsChecked = false;
            Find<CheckBox>(window, "Copy or cut the current line without a selection").IsChecked = false;
            Find<TextBox>(window, "Reading font family").Text = "Consolas";
            Find<CheckBox>(window, "Match case in Find").IsChecked = true;
            Find<CheckBox>(window, "Match whole words in Find").IsChecked = true;
            Find<CheckBox>(window, "Use regular expressions in Find").IsChecked = true;
            Find<CheckBox>(window, "Group Library tasks by file").IsChecked = false;
            Assert.False(f.Shell.Settings.ShowSpaces);
            Assert.Equal(80, f.Shell.Settings.ColumnRulerPosition);
            Click(window, "Apply");
            foreach (var source in new[] { f.Window.EditorControl, f.Window.FindControl<TextEditor>("ReferenceEditor")! })
            {
                Assert.True(source.Options.ShowSpaces && source.Options.ShowTabs && source.Options.ShowEndOfLine);
                Assert.True(source.Options.ShowColumnRulers);
                Assert.Equal([120], source.Options.ColumnRulerPositions);
                Assert.Equal(1.5, source.Options.LineHeightFactor);
                Assert.False(source.Options.AllowScrollBelowDocument);
                Assert.False(source.Options.CutCopyWholeLine);
            }
            Assert.True(f.Window.FindControl<TextEditor>("ReferenceEditor")!.IsReadOnly);
            Assert.Equal("Consolas", f.Window.PreviewControl.FontFamily.Name);
            Assert.True(f.Window.CaseToggle.IsChecked == true && f.Window.WordToggle.IsChecked == true && f.Window.RegexToggle.IsChecked == true);
            Assert.False(f.Window.FindControl<CheckBox>("TaskGroupByFile")!.IsChecked);
            var saved = new SettingsStore(f.Shell.Paths.SettingsDirectory).Load();
            Assert.True(saved.ShowSpaces && saved.ShowTabs && saved.ShowEndOfLine && saved.ShowColumnRuler);
            Assert.Equal(120, saved.ColumnRulerPosition);
            Assert.Equal(1.5, saved.EditorLineHeight);
            Assert.Equal("Consolas", saved.PreviewFontFamily);
            Assert.True(saved.FindMatchCase && saved.FindWholeWord && saved.FindUseRegex);
            Assert.False(saved.TaskGroupByFile);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.Equal(format, tab.Session.Format);
            Assert.False(tab.IsDirty);
            Click(window, "Reset this category to defaults");
            Assert.False(window.WorkingSettings.ShowSpaces || window.WorkingSettings.ShowTabs || window.WorkingSettings.ShowEndOfLine || window.WorkingSettings.ShowColumnRuler);
            Assert.Equal(80, window.WorkingSettings.ColumnRulerPosition);
            Assert.Equal(1.16, window.WorkingSettings.EditorLineHeight);
            Assert.True(window.WorkingSettings.CutCopyWholeLine && window.WorkingSettings.AllowScrollBelowDocument);
            Assert.Equal("Consolas", window.WorkingSettings.PreviewFontFamily);
            Assert.True(window.WorkingSettings.FindMatchCase && window.WorkingSettings.FindWholeWord && window.WorkingSettings.FindUseRegex);
            Assert.True(f.Shell.Settings.ShowSpaces);
            Assert.Equal(120, f.Shell.Settings.ColumnRulerPosition);
        }
        finally { await Discard(window); }
        Assert.True(f.Shell.Settings.ShowSpaces);
        Assert.Equal(120, f.Shell.Settings.ColumnRulerPosition);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [AvaloniaFact]
    public async Task Whole_line_copy_preference_changes_actual_clipboard_and_cut_remains_undoable()
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.NewDocument("alpha\nbeta\n");
        f.Window.EditorControl.Select(6, 0);
        await f.Window.RunCommandAsync(CommandIds.Copy);
        Assert.Equal("beta\n", await f.Window.Clipboard!.TryGetTextAsync());
        f.Shell.Settings.CutCopyWholeLine = false;
        f.Window.ApplySettings();
        await f.Window.Clipboard.SetTextAsync("Keep clipboard");
        await f.Window.RunCommandAsync(CommandIds.Copy);
        Assert.Equal("Keep clipboard", await f.Window.Clipboard.TryGetTextAsync());
        await f.Window.RunCommandAsync(CommandIds.Cut);
        Assert.Equal("alpha\nbeta\n", tab.CurrentText);
        f.Window.EditorControl.Select(6, 4);
        await f.Window.RunCommandAsync(CommandIds.Cut);
        Assert.Equal("beta", await f.Window.Clipboard.TryGetTextAsync());
        Assert.Equal("alpha\n\n", tab.CurrentText);
        f.Window.EditorControl.Document.UndoStack.Undo();
        Assert.Equal("alpha\nbeta\n", tab.CurrentText);
    }

    [AvaloniaFact]
    public async Task Find_preferences_are_shared_saved_and_refresh_options_without_stale_replacement_or_saved_query_text()
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.NewDocument("Cat cat concat\n");
        f.Shell.Settings.FindMatchCase = true;
        f.Shell.Settings.FindWholeWord = true;
        f.Window.ApplySettings();
        f.Window.ShowFindBar(true);
        await f.Window.SearchAsync("cat");
        Assert.Contains("1 of 1", f.Window.FindStatusText);
        f.Window.ReplaceBox.Text = "dog";
        f.Shell.Settings.FindMatchCase = false;
        f.Shell.Settings.FindWholeWord = false;
        f.Window.ApplySettings();
        f.Window.ReplaceCurrent();
        Assert.Equal("Cat cat concat\n", tab.CurrentText);
        await f.Window.SearchAsync("cat");
        Assert.Contains("of 3", f.Window.FindStatusText);
        f.Window.RegexToggle.IsChecked = true;
        f.Window.RegexToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var saved = new SettingsStore(f.Shell.Paths.SettingsDirectory).Load();
        Assert.True(saved.FindUseRegex);
        Assert.False(saved.FindMatchCase || saved.FindWholeWord);
        var json = await File.ReadAllTextAsync(new SettingsStore(f.Shell.Paths.SettingsDirectory).FilePath);
        Assert.DoesNotContain("\"cat\"", json);
        Assert.DoesNotContain("\"dog\"", json);
        f.Shell.Settings.FindUseRegex = false;
        f.Shell.Settings.FindMatchCase = true;
        f.Shell.Settings.FindWholeWord = true;
        f.Window.ApplySettings();
        await f.Window.SearchAsync("cat");
        f.Window.ReplaceCurrent();
        Assert.Equal("Cat dog concat\n", tab.CurrentText);
        f.Window.EditorControl.Document.UndoStack.Undo();
        Assert.Equal("Cat cat concat\n", tab.CurrentText);
    }

    [AvaloniaFact]
    public async Task Library_task_grouping_preference_updates_actual_rows_and_never_changes_task_files()
    {
        using var f = new DesktopFixture();
        var first = await f.WriteAsync("first-task.md", "- [ ] First task\n");
        var second = await f.WriteAsync("second-task.md", "- [ ] Second task\n");
        var firstBytes = await File.ReadAllBytesAsync(first);
        var secondBytes = await File.ReadAllBytesAsync(second);
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));
        await f.Window.ShowNavAsync("Tasks");
        var rows = f.Window.FindControl<ListBox>("SideList")!;
        Assert.Equal(2, rows.Items.OfType<SideRow>().Count(row => row.IsGroup));
        f.Shell.Settings.TaskGroupByFile = false;
        f.Window.ApplySettings();
        Assert.DoesNotContain(rows.Items.OfType<SideRow>(), row => row.IsGroup);
        Assert.Equal(2, rows.Items.OfType<SideRow>().Count(row => row.Task is not null));
        var group = f.Window.FindControl<CheckBox>("TaskGroupByFile")!;
        group.IsChecked = true;
        group.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(2, rows.Items.OfType<SideRow>().Count(row => row.IsGroup));
        Assert.True(new SettingsStore(f.Shell.Paths.SettingsDirectory).Load().TaskGroupByFile);
        Assert.All(rows.Items.OfType<SideRow>().Where(row => row.Task is not null), row => Assert.False(row.Task!.Checked));
        Assert.Equal(firstBytes, await File.ReadAllBytesAsync(first));
        Assert.Equal(secondBytes, await File.ReadAllBytesAsync(second));
    }

    [AvaloniaTheory]
    [InlineData(ThemeChoice.Paper)]
    [InlineData(ThemeChoice.Midnight)]
    public async Task Capture_expanded_preferences_and_actual_source_guides(ThemeChoice theme)
    {
        using var f = new DesktopFixture();
        f.Shell.Settings.Theme = theme;
        f.Shell.Settings.ShowSpaces = true;
        f.Shell.Settings.ShowTabs = true;
        f.Shell.Settings.ShowEndOfLine = true;
        f.Shell.Settings.ShowColumnRuler = true;
        f.Shell.Settings.ColumnRulerPosition = 80;
        f.Shell.Settings.EditorLineHeight = 1.4;
        f.Window.ApplyTheme();
        f.Window.ApplySettings();
        f.Shell.NewDocument("# Visual guides\n\nTwo  spaces and\ta tab.\nThese markers never enter the saved file.\n");
        await f.Window.RunCommandAsync(CommandIds.ViewSource);
        Pump(f.Window);
        var directory = Path.Combine(Screenshots.Folder, "reported-settings-preferences-expanded");
        Directory.CreateDirectory(directory);
        Capture(f.Window, theme + "-source-guides");
        var window = new SettingsWindow(f.Shell.Settings) { Width = 1100, Height = 850 };
        window.Show(f.Window);
        try
        {
            foreach (var (name, file) in new[] { ("Source line spacing", "source-spacing"), ("Reading font", "reading-font"),
                         ("Use regular expressions in Find", "find-options"), ("Group Library tasks by file", "task-grouping") })
            {
                Find<TextBox>(window, "Find a setting").Text = name;
                Pump(window);
                var editor = window.GetVisualDescendants().OfType<Control>().Single(control => AutomationProperties.GetName(control) == name);
                editor.BringIntoView(); Pump(window);
                Assert.True(editor.IsEffectivelyVisible);
                var position = editor.TranslatePoint(default, window);
                Assert.NotNull(position);
                Assert.InRange(position.Value.Y, 0, window.ClientSize.Height - editor.Bounds.Height);
                Capture(window, theme + "-" + file);
            }
        }
        finally { await Discard(window); }

        void Capture(Window target, string name)
        {
            using var bitmap = target.CaptureRenderedFrame();
            Assert.NotNull(bitmap);
            using var stream = File.Create(Path.Combine(directory, name + ".png"));
            bitmap.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
    }

    private static T Find<T>(Visual root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control =>
        AutomationProperties.GetName(control) == name || control is Button button && Equals(button.Content, name));
    private static void Click(Window window, string name) => Find<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Discard(Window window)
    {
        if (!window.IsVisible) return;
        window.Close();
        foreach (var prompt in window.OwnedWindows.OfType<PromptWindow>().ToArray()) Click(prompt, "Discard changes");
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
    }
}
