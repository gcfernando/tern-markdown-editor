using Avalonia.Controls;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Tern.App.Settings;
using Tern.App.Presentation;
using Tern.Workspaces;
using Tern.Markdown;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.App.Palette;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class CommandSurfaceTests
{
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Theme_switch_changes_the_actual_appearance_even_when_setting_is_system(bool dark)
    {
        using var f = new DesktopFixture();
        var application = Application.Current!;
        var previous = application.RequestedThemeVariant;
        try
        {
            f.Shell.Settings.Theme = ThemeChoice.System;
            application.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(dark ? ThemeVariant.Dark : ThemeVariant.Light, f.Window.ActualThemeVariant);
            await f.Window.RunCommandAsync(CommandIds.ToggleTheme);
            Assert.Equal(dark ? ThemeChoice.Paper : ThemeChoice.Midnight, f.Shell.Settings.Theme);
            Assert.Equal(dark ? ThemeVariant.Light : ThemeVariant.Dark, f.Window.ActualThemeVariant);
            await f.Window.RunCommandAsync(CommandIds.ToggleTheme);
            Assert.Equal(dark ? ThemeVariant.Dark : ThemeVariant.Light, f.Window.ActualThemeVariant);
            var system = f.Window.FindControl<MenuItem>("SystemThemeMenu")!;
            system.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(ThemeChoice.System, f.Shell.Settings.Theme);
            Assert.True(system.IsChecked);
            Assert.False(f.Window.FindControl<MenuItem>("PaperThemeMenu")!.IsChecked);
            Assert.False(f.Window.FindControl<MenuItem>("MidnightThemeMenu")!.IsChecked);
        }
        finally { application.RequestedThemeVariant = previous; }
    }

    [AvaloniaFact]
    public async Task View_menu_checks_follow_actual_panels_and_focus_mode()
    {
        using var f = new DesktopFixture();
        f.Shell.NewDocument("A document");
        var menu = f.Window.FindControl<Menu>("ApplicationMenu")!;
        foreach (var (id, name) in new[]
        {
            (CommandIds.ToggleSidebar, "Sidebar"), (CommandIds.ToggleRightPanel, "RightPanel"),
            (CommandIds.ToggleReference, "ReferencePane")
        })
        {
            var item = Entries(menu).Single(entry => Equals(entry.Tag, id));
            var panel = f.Window.FindControl<Control>(name)!;
            Assert.Equal(panel.IsVisible, item.IsChecked);
            await f.Window.RunCommandAsync(id);
            Assert.Equal(panel.IsVisible, item.IsChecked);
            await f.Window.RunCommandAsync(id);
            Assert.Equal(panel.IsVisible, item.IsChecked);
        }
        await f.Window.RunCommandAsync(CommandIds.ToggleFocus);
        Assert.True(Entries(menu).Single(item => Equals(item.Tag, CommandIds.ToggleFocus)).IsChecked);
        Assert.False(Entries(menu).Single(item => Equals(item.Tag, CommandIds.ToggleSidebar)).IsChecked);
        Assert.False(Entries(menu).Single(item => Equals(item.Tag, CommandIds.ToggleRightPanel)).IsChecked);
        await f.Window.RunCommandAsync(CommandIds.ToggleFocus);
        Assert.False(Entries(menu).Single(item => Equals(item.Tag, CommandIds.ToggleFocus)).IsChecked);
    }

    [AvaloniaFact]
    public async Task Recent_submenu_limits_groups_opens_files_and_keeps_clear_last()
    {
        using var f = new DesktopFixture();
        for (var index = 0; index < 12; index++)
        {
            f.Shell.Recent.AddFile(await f.WriteAsync($"recent-{index}.md", $"Document {index}"));
            f.Shell.Recent.AddWorkspace(Path.Combine(f.Docs, $"workspace-{index}"));
        }
        f.Shell.Recent.AddUrl("https://example.com/never-fetch-without-click.md");
        var before = f.Shell.Tabs.Count;
        var menu = f.Window.FindControl<MenuItem>("OpenRecentMenu")!;
        menu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent));
        var entries = menu.Items.OfType<MenuItem>().ToArray();
        var recent = entries.Where(item => item.Tag is RecentEntry).ToArray();
        Assert.Equal(10, recent.Count(item => ((RecentEntry)item.Tag!).Path.EndsWith(".md", StringComparison.Ordinal) && !((RecentEntry)item.Tag!).Path.StartsWith("https:", StringComparison.Ordinal)));
        Assert.Equal(5, recent.Count(item => ((RecentEntry)item.Tag!).Path.Contains("workspace-", StringComparison.Ordinal)));
        Assert.Contains(entries, item => Equals(item.Header, "More…"));
        Assert.Equal(CommandIds.ClearRecent, entries[^1].Tag);
        Assert.Equal(before, f.Shell.Tabs.Count);
        var firstFile = recent.First(item => ((RecentEntry)item.Tag!).Path.EndsWith(".md", StringComparison.Ordinal) && !((RecentEntry)item.Tag!).Path.StartsWith("https:", StringComparison.Ordinal));
        var path = ((RecentEntry)firstFile.Tag!).Path;
        firstFile.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        for (var attempt = 0; attempt < 100 && f.Shell.ActiveTab?.FilePath != path; attempt++)
        { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
        Assert.Equal(path, f.Shell.ActiveTab?.FilePath);
        Assert.Equal(await File.ReadAllTextAsync(path), f.Shell.ActiveTab!.CurrentText);
    }

    [AvaloniaFact]
    public async Task Clipboard_commands_copy_cut_paste_and_select_all_as_undoable_editor_actions()
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.NewDocument("Keep these words");
        f.Window.Editor.Select(5, 5);
        Assert.Equal("these", f.Window.Editor.SelectedText);
        await f.Window.RunCommandAsync(CommandIds.Copy);
        await Task.Delay(30);
        Assert.Equal("these", await f.Window.Clipboard!.TryGetTextAsync());
        await f.Window.RunCommandAsync(CommandIds.Cut);
        await Task.Delay(30); Dispatcher.UIThread.RunJobs();
        Assert.Equal("Keep  words", tab.CurrentText);
        await f.Window.RunCommandAsync(CommandIds.Paste);
        await Task.Delay(30); Dispatcher.UIThread.RunJobs();
        Assert.Equal("Keep these words", tab.CurrentText);
        await f.Window.RunCommandAsync(CommandIds.SelectAll);
        Assert.Equal(tab.CurrentText, f.Window.Editor.SelectedText);
        await f.Window.RunCommandAsync(CommandIds.Undo);
        Assert.Equal("Keep  words", tab.CurrentText);
    }

    [AvaloniaFact]
    public void Settings_enum_choices_render_display_names_and_preserve_typed_selection()
    {
        using var f = new DesktopFixture();
        var settings = new SettingsWindow(new AppSettings());
        try
        {
            settings.Show(f.Window); settings.UpdateLayout();
            var profile = settings.GetVisualDescendants().OfType<ComboBox>()
                .Single(box => AutomationProperties.GetName(box) == "Default profile (new files)");
            Assert.IsType<MarkdownProfile>(profile.SelectedItem);
            Assert.NotNull(profile.ItemTemplate);
            var rendered = Assert.IsType<TextBlock>(profile.ItemTemplate.Build(MarkdownProfile.Gfm));
            Assert.Equal("GitHub Flavored Markdown (GFM)", rendered.Text);
            Assert.Equal(ThemeChoice.System.DisplayName(), Assert.IsType<TextBlock>(
                EnumChoices.Template<ThemeChoice>().Build(ThemeChoice.System)).Text);
            var original = profile.SelectedItem;
            profile.SelectedItem = MarkdownProfile.Extended;
            Assert.Equal(MarkdownProfile.Extended, settings.WorkingSettings.DefaultProfile);
            profile.SelectedItem = original;
        }
        finally { settings.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(CommandIds.Cut, Key.X)]
    [InlineData(CommandIds.Copy, Key.C)]
    [InlineData(CommandIds.Paste, Key.V)]
    [InlineData(CommandIds.SelectAll, Key.A)]
    public async Task Clipboard_shortcuts_honor_removed_and_restored_registry_bindings(string id, Key key)
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.NewDocument("Keep these words");
        await f.Window.RunCommandAsync(CommandIds.ViewSource);
        Dispatcher.UIThread.RunJobs(); f.Window.UpdateLayout();
        f.Window.Editor.Select(5, 5);
        Assert.True(f.Window.Editor.TextArea.Focus());
        await f.Window.Clipboard!.SetTextAsync("replacement");
        f.Shell.Registry.ClearBinding(id);
        Press();
        await Task.Delay(50); Dispatcher.UIThread.RunJobs();
        Assert.Equal("Keep these words", tab.CurrentText);
        Assert.Equal("these", f.Window.Editor.SelectedText);
        Assert.Equal("replacement", await f.Window.Clipboard.TryGetTextAsync());
        Assert.True(f.Shell.Registry.ResetBinding(id).Succeeded);
        Press();
        await Task.Delay(50); Dispatcher.UIThread.RunJobs();
        Assert.Equal(id switch
        {
            CommandIds.Cut => "Keep  words",
            CommandIds.Paste => "Keep replacement words",
            _ => "Keep these words"
        }, tab.CurrentText);
        if (id is CommandIds.Cut or CommandIds.Copy)
            Assert.Equal("these", await f.Window.Clipboard.TryGetTextAsync());
        if (id == CommandIds.SelectAll) Assert.Equal(tab.CurrentText, f.Window.Editor.SelectedText);

        void Press()
        {
            f.Window.KeyPress(key, RawInputModifiers.Control, default, null);
            f.Window.KeyRelease(key, RawInputModifiers.Control, default, null);
        }
    }

    [AvaloniaFact]
    public void Empty_URL_guidance_names_the_three_real_import_controls_without_fetching()
    {
        using var f = new DesktopFixture();
        var window = new OpenUrlWindow(new Tern.Desktop.Services.MarkdownUrlLoader(), cacheRoot: Path.Combine(f.AppData, "url-cache"));
        try
        {
            window.Show(f.Window); window.UpdateLayout();
            var input = window.GetVisualDescendants().OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "Markdown URL");
            input.Text = "https://example.com/document.md";
            input.Text = "";
            var status = window.GetVisualDescendants().OfType<TextBlock>().Single(block => AutomationProperties.GetName(block) == "Markdown URL status");
            foreach (var label in new[] { "Open working copy", "Fetch latest", "Save portable copy…" })
            {
                var button = window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, label));
                Assert.False(button.IsEnabled);
                Assert.Contains(label.TrimEnd('…'), status.Text, StringComparison.Ordinal);
            }
            Assert.False(Directory.Exists(Path.Combine(f.AppData, "url-cache")));
        }
        finally { window.Close(); }
    }

    internal static IEnumerable<MenuItem> Entries(ItemsControl root)
    {
        foreach (var item in root.Items.OfType<MenuItem>())
        {
            yield return item;
            foreach (var child in Entries(item)) yield return child;
        }
    }

    [AvaloniaFact]
    public void Application_and_background_menu_labels_match_the_registry()
    {
        using var f = new DesktopFixture();
        var application = f.Window.FindControl<Menu>("ApplicationMenu")!;
        var background = f.Window.FindControl<ListBox>("SideList")!.ContextMenu!;
        var entries = Entries(application).Concat(Entries(background))
            .Where(item => item.Tag is string id && f.Shell.Registry.TryGet(id, out _)).ToArray();
        Assert.NotEmpty(entries);
        Assert.All(entries, item =>
        {
            Assert.True(f.Shell.Registry.TryGet((string)item.Tag!, out var command));
            Assert.Equal(command.Title, ((string)item.Header!).Replace("_", "", StringComparison.Ordinal));
        });
    }

    [AvaloniaFact]
    public void Every_registry_command_has_an_application_menu_entry()
    {
        using var f = new DesktopFixture();
        var ids = Entries(f.Window.FindControl<Menu>("ApplicationMenu")!)
            .Select(item => item.Tag as string).ToHashSet(StringComparer.Ordinal);
        Assert.All(f.Shell.Registry.Commands, command => Assert.Contains(command.Id, ids));
    }

    [AvaloniaFact]
    public void Welcome_command_buttons_use_canonical_labels()
    {
        using var f = new DesktopFixture();
        var buttons = f.Window.GetVisualDescendants().OfType<Button>().ToArray();
        foreach (var id in new[] { CommandIds.NewFile, CommandIds.OpenFile, CommandIds.OpenFolder, CommandIds.Help, CommandIds.NewFromTemplate })
        {
            Assert.True(f.Shell.Registry.TryGet(id, out var command));
            Assert.Contains(buttons, button => Equals(button.Tag, id) && Equals(button.Content, command.Title));
        }
    }

    [AvaloniaFact]
    public void Palette_keeps_unavailable_commands_with_reasons_and_cannot_execute_them()
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.ActiveTab;
        f.Window.ShowPalette();
        Dispatcher.UIThread.RunJobs(); f.Window.UpdateLayout();
        var picker = f.Window.Picker!;
        var entries = picker.Results.Items.OfType<PickerItem>().ToArray();
        Assert.Equal(CommandPaletteModel.Filter(f.Shell.Registry, "").Count, entries.Length);
        var save = Assert.Single(entries, item => Equals(item.Payload, CommandIds.Save));
        Assert.False(save.IsEnabled);
        Assert.False(string.IsNullOrWhiteSpace(save.DisabledReason));
        Assert.Contains(save.DisabledReason!, save.Detail, StringComparison.Ordinal);
        picker.Results.SelectedItem = save;
        picker.Input.Focus();
        f.Window.KeyPress(Key.Enter, RawInputModifiers.None, default, null);
        f.Window.KeyRelease(Key.Enter, RawInputModifiers.None, default, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(f.Window.IsOverlayOpen);
        Assert.Same(tab, f.Shell.ActiveTab);
        Assert.Empty(f.Window.OwnedWindows);
        Assert.Equal(save.DisabledReason, f.Shell.StatusMessage);
    }
}
