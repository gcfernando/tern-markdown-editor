using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Shell;
using Tern.App.Commands;
using Tern.Desktop.ViewModels;
using Tern.Domain.Text;
using Tern.Workspaces;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class BeginnerLibraryTests
{
    [AvaloniaFact]
    public void Add_menu_opens_the_same_URL_import_dialog_without_creating_a_document()
    {
        using var fixture = new DesktopFixture();
        var add = fixture.Window.FindControl<Button>("WorkspaceAddButton")!;
        var flyout = Assert.IsType<MenuFlyout>(add.Flyout);
        var url = Assert.Single(flyout.Items.OfType<MenuItem>(), item => Equals(item.Tag, CommandIds.OpenUrl));
        Assert.Equal("Open from URL…", url.Header);
        url.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Pump(fixture);
        var dialog = Assert.Single(fixture.Window.OwnedWindows.OfType<OpenUrlWindow>());
        Assert.True(dialog.IsVisible);
        Assert.Empty(fixture.Shell.Tabs);
        dialog.Close();
    }

    [AvaloniaFact]
    public async Task Removing_recent_reference_reports_when_the_list_cannot_be_persisted()
    {
        using var fixture = new DesktopFixture();
        var path = await fixture.WriteAsync("kept.md", "Saved text\n");
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "unsaved ")]);
        await fixture.Window.ShowNavAsync("Recent");
        Pump(fixture);
        var recentPath = Path.Combine(AppPaths.Under(fixture.AppData).StateDirectory, RecentItemsStore.FileName);
        using var preventReplacement = new FileStream(recentPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var button = Assert.Single(fixture.Window.FindControl<ListBox>("SideList")!.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b) == "Remove file reference");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(fixture);
        Assert.NotNull(fixture.Shell.Recent.LastSaveError);
        Assert.Contains("for this session", fixture.Shell.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("could not be saved", fixture.Shell.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Shell.Recent.Files, r => r.Path == path);
        Assert.Contains(new RecentItemsStore(AppPaths.Under(fixture.AppData).StateDirectory).Files, r => r.Path == path);
        Assert.Equal("unsaved Saved text\n", tab.CurrentText);
        Assert.True(tab.IsDirty);
        Assert.Equal("Saved text\n", await File.ReadAllTextAsync(path));
    }

    [AvaloniaFact]
    public void Context_guide_stays_open_while_the_main_window_formats_selected_text()
    {
        using var fixture = new DesktopFixture();
        var tab = fixture.Shell.NewDocument("word");
        Pump(fixture);
        var help = Assert.Single(fixture.Window.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b) == "Source editor help");
        help.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(fixture);
        var guide = Assert.Single(fixture.Window.OwnedWindows.OfType<HelpWindow>());
        Assert.True(guide.IsVisible);
        fixture.Window.Activate();
        fixture.Window.EditorControl.Select(0, 4);
        var bold = Assert.Single(fixture.Window.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b) == "Bold selected text");
        var point = bold.TranslatePoint(new Point(bold.Bounds.Width / 2, bold.Bounds.Height / 2), fixture.Window)!.Value;
        fixture.Window.MouseMove(point);
        fixture.Window.MouseDown(point, MouseButton.Left);
        fixture.Window.MouseUp(point, MouseButton.Left);
        Pump(fixture);
        Assert.Equal("**word**", tab.CurrentText);
        Assert.True(guide.IsVisible);
        Assert.Same(fixture.Window, guide.Owner);
        guide.Close();
    }

    [AvaloniaFact]
    public void Format_menu_uses_registered_commands_and_bold_edits_the_real_selection()
    {
        using var fixture = new DesktopFixture();
        var menu = fixture.Window.FindControl<Menu>("ApplicationMenu")!;
        var format = Assert.Single(menu.Items.OfType<MenuItem>(), m => m.Header?.ToString() == "F_ormat");
        var heading = Assert.Single(format.Items.OfType<MenuItem>(), m => Equals(m.Header, "Heading"));
        Assert.Equal(new[] { CommandIds.Heading1, CommandIds.Heading2, CommandIds.Heading3 }, heading.Items.OfType<MenuItem>().Select(i => i.Tag as string));
        var items = format.Items.OfType<MenuItem>().Where(m => m.Tag is string).Concat(heading.Items.OfType<MenuItem>()).ToArray();
        Assert.Equal(new[] { CommandIds.Bold, CommandIds.Italic, CommandIds.InlineCode, CommandIds.Strikethrough, CommandIds.HeadingUp, CommandIds.HeadingDown, CommandIds.ToggleQuote, CommandIds.ToggleBullet, CommandIds.ToggleNumbered, CommandIds.ToggleTask, CommandIds.InsertLink, CommandIds.InsertImage, CommandIds.PasteImage, CommandIds.InsertTable, CommandIds.InsertCodeBlock, CommandIds.InsertRule, CommandIds.Heading1, CommandIds.Heading2, CommandIds.Heading3 }, items.Select(i => i.Tag as string));
        Assert.All(items, item => Assert.True(fixture.Shell.Registry.TryGet((string)item.Tag!, out _)));
        Assert.All(items, item => Assert.False(fixture.Window.CanRunCommand((string)item.Tag!)));
        var tab = fixture.Shell.NewDocument("word");
        Pump(fixture);
        fixture.Window.EditorControl.Select(0, 4);
        var bold = Assert.Single(items, item => Equals(item.Tag, CommandIds.Bold));
        Assert.True(fixture.Window.CanRunCommand(CommandIds.Bold));
        bold.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Pump(fixture);
        Assert.Equal("**word**", tab.CurrentText);
        Assert.True(tab.IsDirty);
    }

    [AvaloniaTheory]
    [InlineData("Files", false)]
    [InlineData("Recent", false)]
    [InlineData("Files", true)]
    [InlineData("Recent", true)]
    public async Task Visible_remove_closes_library_tabs_but_recent_only_forgets_the_reference(string nav, bool missing)
    {
        using var fixture = new DesktopFixture();
        fixture.Dialogs.SaveChoice = Tern.App.Shell.SaveChoice.Discard;
        var path = await fixture.WriteAsync("remove-reference.md", "# Original\n");
        var first = (await fixture.Shell.OpenFileAsync(path))!;
        first.ApplyEdits([TextEdit.Insert(0, "unsaved ")]);
        var second = (await fixture.Shell.OpenFileAsync(await fixture.WriteAsync("active.md", "Active\n")))!;
        if (missing) File.Delete(path);
        await fixture.Window.ShowNavAsync(nav);
        Pump(fixture);
        var list = fixture.Window.FindControl<ListBox>("SideList")!;
        Assert.True(list.IsVisible);
        var row = Assert.Single(list.Items.OfType<SideRow>(), r => r.Path == path);
        Assert.True(row.CanRemoveReference);
        Assert.Equal(missing, row.IsMissing);
        var button = Assert.Single(list.GetVisualDescendants().OfType<Button>(), b => Equals(b.DataContext, row) && AutomationProperties.GetName(b) == "Remove file reference");
        Assert.True(button.IsEffectivelyVisible);
        Assert.NotNull(Assert.IsType<Avalonia.Controls.Shapes.Path>(button.Content).Data);
        Assert.True(button.Bounds.Width >= 32 && button.Bounds.Height >= 36);
        if (nav == "Files" && !missing)
        {
            var folder = Path.Combine(Screenshots.Folder, "beginner-ui");
            Directory.CreateDirectory(folder);
            using var frame = fixture.Window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            using var stream = File.Create(Path.Combine(folder, "library-remove-references.png"));
            frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), fixture.Window)!.Value;
        fixture.Window.MouseMove(point);
        fixture.Window.MouseDown(point, MouseButton.Left);
        fixture.Window.MouseUp(point, MouseButton.Left);
        Pump(fixture);
        Assert.Same(second, fixture.Shell.ActiveTab);
        Assert.Equal(nav == "Recent", fixture.Shell.Tabs.Contains(first));
        Assert.Equal("unsaved # Original\n", first.CurrentText);
        Assert.True(first.IsDirty);
        Assert.Equal(nav == "Recent" ? 0 : 1, fixture.Dialogs.SavePrompts);
        Assert.DoesNotContain(fixture.Shell.Recent.Files, r => r.Path == path);
        Assert.DoesNotContain(list.Items.OfType<SideRow>(), r => r.Path == path);
        var reloaded = new RecentItemsStore(AppPaths.Under(fixture.AppData).StateDirectory);
        Assert.DoesNotContain(reloaded.Files, r => r.Path == path);
        Assert.Equal(!missing, File.Exists(path));
        if (!missing) Assert.Equal("# Original\n", await File.ReadAllTextAsync(path));
    }

    [AvaloniaFact]
    public async Task Empty_library_explains_reference_removal_and_search_rows_do_not_offer_it()
    {
        using var fixture = new DesktopFixture();
        await fixture.Window.ShowNavAsync("Files");
        Pump(fixture);
        Assert.True(fixture.Window.FindControl<TextBlock>("NoWorkspaceText")!.IsVisible);
        var path = await fixture.WriteAsync("find.md", "# Find needle\n");
        await fixture.Shell.OpenFileAsync(path);
        await fixture.Shell.OpenWorkspaceAsync(fixture.Docs);
        fixture.Window.ShowSearchResults("needle");
        Pump(fixture);
        var list = fixture.Window.FindControl<ListBox>("SideList")!;
        Assert.NotEmpty(list.Items.OfType<SideRow>());
        Assert.All(list.Items.OfType<SideRow>(), r => Assert.False(r.CanRemoveReference));
        Assert.DoesNotContain(list.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) == "Remove file reference");
    }

    [AvaloniaFact]
    public async Task Workspace_leaf_close_prompts_and_preserves_the_real_file_and_tree_row()
    {
        using var fixture = new DesktopFixture();
        var path = await fixture.WriteAsync("close.md", "# Saved\n");
        await fixture.Shell.OpenWorkspaceAsync(fixture.Docs);
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "unsaved ")]);
        await fixture.Window.ShowNavAsync("Files");
        Pump(fixture);
        var row = Assert.Single(Assert.Single(fixture.Window.FileTree.Items.OfType<TreeRow>()).Children);
        var close = Assert.Single(fixture.Window.FileTree.GetVisualDescendants().OfType<Button>(), b => Equals(b.DataContext, row) && AutomationProperties.GetName(b) == "Close document");
        fixture.Dialogs.SaveChoice = SaveChoice.Cancel;
        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(fixture);
        Assert.Contains(tab, fixture.Shell.Tabs);
        fixture.Dialogs.SaveChoice = SaveChoice.Discard;
        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(fixture);
        Assert.DoesNotContain(tab, fixture.Shell.Tabs);
        Assert.Single(fixture.Window.FileTree.Items.OfType<TreeRow>());
        Assert.Equal("# Saved\n", await File.ReadAllTextAsync(path));
    }

    private static void Pump(DesktopFixture fixture)
    {
        fixture.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        fixture.Window.UpdateLayout();
    }
}
