using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.App.Settings;
using Tern.App.Help;

namespace Tern.Desktop.Tests;

public sealed class MenuGroupingTests
{
    [AvaloniaFact]
    public void Installed_help_paths_match_actual_nested_menu_routes()
    {
        using var f = new DesktopFixture();
        var help = FeatureCatalog.Search(f.Shell.Registry, null);
        var actual = f.Window.ApplicationMenu.Items.OfType<MenuItem>()
            .SelectMany(root => Paths(root, "")).ToDictionary(pair => pair.id, pair => pair.path);
        foreach (var id in new[] { CommandIds.NewWorkspaceFile, CommandIds.NewFolder, CommandIds.OpenFolder,
                     CommandIds.OpenWorkspace, CommandIds.CreateWorkspace, CommandIds.PromoteFolder, CommandIds.CloseWorkspace,
                     CommandIds.CloseAllWorkspaces, CommandIds.Rename, CommandIds.MoveDocument, CommandIds.DuplicateDocument,
                     CommandIds.RevealInExplorer, CommandIds.ShowHistory, CommandIds.GitDiff, CommandIds.ReloadFromDisk,
                     CommandIds.ReviewSaveFragments, CommandIds.WorkspacePrivacy, CommandIds.ClearFileHistory,
                     CommandIds.ClearWorkspaceHistory, CommandIds.ReopenWithEncoding, CommandIds.ChangeEncoding, CommandIds.ChangeLineEnding,
                     CommandIds.HeadingUp, CommandIds.HeadingDown, CommandIds.InsertTable, CommandIds.LibraryFiles, CommandIds.LibraryRecent })
            Assert.Equal(actual[id], Assert.Single(help, entry => entry.Id == id).MenuPath);

        static IEnumerable<(string id, string path)> Paths(MenuItem item, string parent)
        {
            var title = item.Header!.ToString()!.Replace("_", "", StringComparison.Ordinal);
            var path = parent.Length == 0 ? title : parent + " → " + title;
            if (item.Tag is string id && id is not ("System" or "Paper" or "Midnight")) yield return (id, path);
            foreach (var child in item.Items.OfType<MenuItem>())
                foreach (var pair in Paths(child, path)) yield return pair;
        }
    }

    [AvaloniaFact]
    public async Task Grouped_document_actions_keep_real_target_and_reload_behavior()
    {
        using var f = new DesktopFixture();
        var file = Root(f, "File");
        var document = Group(file, "Document actions");
        var recovery = Group(file, "History and recovery");
        var reveal = Command(document, CommandIds.RevealInExplorer);
        var reload = Command(recovery, CommandIds.ReloadFromDisk);
        Assert.True(document.IsEnabled);
        Assert.False(reveal.IsEnabled);
        Assert.False(reload.IsEnabled);
        var path = await f.WriteAsync("grouped-menu.md", "Original\r\n");
        var tab = await f.Shell.OpenFileAsync(path);
        Settle(f.Window);
        Assert.True(reveal.IsEnabled);
        Assert.True(reload.IsEnabled);
        reveal.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(path, Assert.Single(f.Launcher.Revealed));
        await File.WriteAllTextAsync(path, "Changed outside Tern\r\n");
        reload.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        for (var attempt = 0; attempt < 100 && tab!.CurrentText != "Changed outside Tern\r\n"; attempt++)
        { await Task.Delay(10, TestContext.Current.CancellationToken); Settle(f.Window); }
        Assert.Equal("Changed outside Tern\r\n", tab!.CurrentText);
        Assert.Equal(tab.CurrentText, f.Window.EditorControl.Text);
        Assert.Equal("Changed outside Tern\r\n", await File.ReadAllTextAsync(path));
        Assert.False(tab.IsDirty);
    }

    [AvaloniaFact]
    public void Nested_heading_and_table_commands_remain_undoable_and_use_registry_gestures()
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.NewDocument("## Keep these words\n");
        Settle(f.Window);
        f.Window.EditorControl.CaretOffset = 5;
        var heading = Command(Group(Root(f, "Format"), "Heading"), CommandIds.HeadingUp);
        heading.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Settle(f.Window);
        Assert.Equal("# Keep these words\n", tab.CurrentText);
        f.Window.EditorControl.Document.UndoStack.Undo();
        Assert.Equal("## Keep these words\n", tab.CurrentText);
        var table = Command(Group(Root(f, "Format"), "Table"), CommandIds.InsertTable);
        Assert.True(table.IsEnabled);
        Assert.NotNull(table.InputGesture);
        Assert.Equal(Key.D4, table.InputGesture.Key);
        Assert.True(f.Shell.Registry.SetBinding(CommandIds.InsertTable, "Ctrl+Alt+F12").Succeeded);
        f.Window.ApplySettings();
        Settle(f.Window);
        Assert.Equal(Key.F12, table.InputGesture!.Key);
        Assert.Equal(KeyModifiers.Control | KeyModifiers.Alt, table.InputGesture.KeyModifiers);
    }

    [AvaloniaFact]
    public void Semantic_groups_have_clear_headers_and_clean_separators_with_common_file_actions_direct()
    {
        using var f = new DesktopFixture();
        foreach (var name in new[] { "File", "Edit", "Format", "View" })
        {
            var root = Root(f, name);
            foreach (var group in new[] { root }.Concat(CommandSurfaceTests.Entries(root).Where(item => item.ItemCount > 0)))
            {
                Assert.IsNotType<Separator>(group.Items[0]);
                Assert.IsNotType<Separator>(group.Items[^1]);
                for (var index = 1; index < group.ItemCount; index++)
                    Assert.False(group.Items[index - 1] is Separator && group.Items[index] is Separator);
                Assert.False(string.IsNullOrWhiteSpace(group.Header?.ToString()));
            }
        }
        var file = Root(f, "File");
        Assert.InRange(file.Items.OfType<MenuItem>().Count(), 1, 20);
        foreach (var id in new[] { CommandIds.NewFile, CommandIds.NewFromTemplate, CommandIds.OpenFile,
                     CommandIds.Save, CommandIds.SaveAs, CommandIds.SaveAll, CommandIds.ExportAs, CommandIds.Print, CommandIds.CloseTab })
            Assert.Single(file.Items.OfType<MenuItem>(), item => Equals(item.Tag, id));
        foreach (var title in new[] { "Folders and workspaces", "Document actions", "History and recovery", "Encoding and line endings" })
            Assert.Equal(title, AutomationProperties.GetName(Group(file, title)));
        var view = Root(f, "View");
        Assert.Equal("Library views", AutomationProperties.GetName(Group(view, "Library views")));
        Assert.Contains(view.Items.OfType<MenuItem>(), item => item.Tag is CommandIds.ToggleSidebar && item.ToggleType == MenuItemToggleType.CheckBox);
    }

    [AvaloniaTheory]
    [InlineData(ThemeChoice.Paper)]
    [InlineData(ThemeChoice.Midnight)]
    public async Task Capture_actual_grouped_menu_popups_for_review(ThemeChoice theme)
    {
        using var f = new DesktopFixture();
        f.Shell.Settings.Theme = theme;
        f.Window.ApplyTheme();
        f.Window.ApplySettings();
        var path = await f.WriteAsync("menu-review.md", "# Review note\n\n| Name | Value |\n| --- | --- |\n| Item | One |\n");
        await f.Shell.OpenFileAsync(path);
        var folder = Path.Combine(Screenshots.Folder, "reported-menus", theme.ToString());
        Directory.CreateDirectory(folder);
        var measurements = new List<object>();
        foreach (var name in new[] { "File", "Edit", "Format", "View" })
        {
            var root = Root(f, name);
            root.IsSubMenuOpen = true;
            await Capture(root, name.ToLowerInvariant());
            foreach (var label in name switch
            {
                "File" => new[] { "Folders and workspaces", "Document actions", "History and recovery", "Encoding and line endings" },
                "Format" => new[] { "Heading", "Table" },
                "View" => new[] { "Library views" },
                _ => Array.Empty<string>(),
            })
            {
                var submenu = Group(root, label);
                submenu.IsSubMenuOpen = true;
                await Capture(submenu, name.ToLowerInvariant() + "-" + label.Replace(' ', '-').ToLowerInvariant());
                submenu.IsSubMenuOpen = false;
            }
            root.IsSubMenuOpen = false;
        }
        File.WriteAllText(Path.Combine(folder, "popup-bounds.json"), System.Text.Json.JsonSerializer.Serialize(measurements));

        async Task Capture(MenuItem owner, string name)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Settle(f.Window);
            var child = owner.Items.OfType<MenuItem>().First();
            var popup = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(child));
            popup.UpdateLayout();
            Assert.True(popup.Bounds.Width > 0 && popup.Bounds.Height > 0);
            var actions = owner.Items.OfType<MenuItem>().ToArray();
            var first = actions[0].TranslatePoint(default, popup);
            var last = actions[^1].TranslatePoint(new Point(0, actions[^1].Bounds.Height), popup);
            measurements.Add(new { menu = name, renderHostWidth = popup.Bounds.Width, renderHostHeight = popup.Bounds.Height,
                firstActionTop = first?.Y, lastActionBottom = last?.Y,
                windowWidth = f.Window.Bounds.Width, windowHeight = f.Window.Bounds.Height });
            using var bitmap = popup.CaptureRenderedFrame();
            Assert.NotNull(bitmap);
            using var stream = File.Create(Path.Combine(folder, name + ".png"));
            bitmap.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
    }

    private static MenuItem Root(DesktopFixture fixture, string name) => Assert.Single(
        fixture.Window.ApplicationMenu.Items.OfType<MenuItem>(), item => item.Header?.ToString()?.Replace("_", "", StringComparison.Ordinal) == name);
    private static MenuItem Group(MenuItem root, string title) => Assert.Single(root.Items.OfType<MenuItem>(), item => Equals(item.Header, title));
    private static MenuItem Command(MenuItem root, string id) => Assert.Single(root.Items.OfType<MenuItem>(), item => Equals(item.Tag, id));
    private static void Settle(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
}
