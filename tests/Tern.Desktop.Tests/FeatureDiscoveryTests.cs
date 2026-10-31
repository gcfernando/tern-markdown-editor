using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.App.Help;
using Tern.App.Settings;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public class FeatureDiscoveryTests
{
    private static T Find<T>(Visual root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetName(c) == name);
    private static Button Button(Visual root, string content) => root.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, content));
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    private static async Task Settle() { await Task.Delay(100); Dispatcher.UIThread.RunJobs(); }

    [AvaloniaFact]
    public async Task Settings_searches_individual_descriptions_and_opens_contextual_guide()
    {
        using var fixture = new DesktopFixture();
        var settings = new AppSettings();
        var window = new SettingsWindow(settings, fixture.Shell.Registry);
        window.Show(fixture.Window);
        await Settle();
        var categories = Find<ListBox>(window, "Settings categories");
        categories.SelectedItem = "Editor";
        await Settle();
        var search = Find<TextBox>(window, "Find a setting");
        search.Text = "never inserts line breaks";
        await Settle();
        Assert.True(Find<CheckBox>(window, "Wrap long lines").IsEffectivelyVisible);
        Assert.False(Find<ComboBox>(window, "Theme").IsEffectivelyVisible);
        Assert.Contains("1 setting ·", Find<TextBlock>(window, "Settings result count").Text, StringComparison.Ordinal);
        var original = settings.WordWrap;
        Find<CheckBox>(window, "Wrap long lines").IsChecked = !original;
        Assert.Equal(!original, settings.WordWrap);
        Click(Find<Button>(window, "Help for Wrap long lines"));
        await Settle();
        var guide = Assert.Single(window.OwnedWindows.OfType<HelpWindow>());
        Assert.Contains(guide.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Wrap long lines");
        Assert.Contains("Settings", Find<TextBlock>(guide, "Tutorial step").Text, StringComparison.Ordinal);
        guide.Close();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Every_visible_setting_has_a_contextual_help_control()
    {
        using var fixture = new DesktopFixture();
        var window = new SettingsWindow(new AppSettings());
        window.Show(fixture.Window);
        await Settle();
        Assert.All(FeatureCatalog.SettingsFeatures, f => Assert.NotNull(Find<Button>(window, "Help for " + f.Name)));
        var categories = Find<ListBox>(window, "Settings categories");
        categories.SelectedItem = "Files";
        await Settle();
        Assert.True(Find<Button>(window, "Help for " + FeatureCatalog.SettingsFeatures.First(f => f.Category == "Files").Name).IsEffectivelyVisible);
        Assert.False(Find<Button>(window, "Help for " + FeatureCatalog.SettingsFeatures.First(f => f.Category == "Appearance").Name).IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Feature_search_selects_a_subfeature_and_supports_skip_replay_without_editing_documents()
    {
        using var fixture = new DesktopFixture();
        var tab = fixture.Shell.NewDocument("Keep this text");
        var before = tab.CurrentText;
        var window = new HelpWindow(fixture.Shell.Registry);
        window.Show(fixture.Window);
        await Settle();
        Find<TextBox>(window, "Search feature help").Text = "BOM";
        await Settle();
        var results = Find<ListBox>(window, "Feature search results");
        results.SelectedItem = results.Items.OfType<FeatureHelp>().Single(f => f.Id == "editor.encoding");
        Assert.Contains("encoding", Find<TextBlock>(window, "Tutorial step").Text, StringComparison.OrdinalIgnoreCase);
        Click(Button(window, "Skip tutorial"));
        Assert.Contains("skipped", Find<TextBlock>(window, "Tutorial step").Text, StringComparison.Ordinal);
        Assert.False(Button(window, "Next").IsEnabled);
        Click(Button(window, "Replay"));
        Assert.StartsWith("Step 1 of", Find<TextBlock>(window, "Tutorial step").Text, StringComparison.Ordinal);
        Click(Button(window, "Next"));
        Assert.StartsWith("Step 2 of", Find<TextBlock>(window, "Tutorial step").Text, StringComparison.Ordinal);
        Assert.Equal(before, tab.CurrentText);
        Click(Button(window, "Exit guide"));
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task Shortcut_category_details_record_and_clear_are_explicit_and_preserve_binding_until_assigned()
    {
        using var fixture = new DesktopFixture();
        var registry = fixture.Shell.Registry;
        var before = registry.GetGesture(CommandIds.Save);
        var window = new ShortcutsWindow(registry);
        window.Show(fixture.Window);
        await Settle();
        Assert.Contains("Current:", Find<TextBlock>(window, "Current and default shortcut").Text, StringComparison.Ordinal);
        Find<ListBox>(window, "Shortcut categories").SelectedItem = "File";
        Find<TextBox>(window, "Search keyboard shortcuts").Text = "Save";
        await Settle();
        var rows = window.Commands.Items.OfType<ShortcutsWindow.ShortcutRow>().ToArray();
        Assert.All(rows, r => Assert.Equal("File", r.Category));
        window.Commands.SelectedItem = rows.Single(r => r.Id == CommandIds.Save);
        Assert.Contains("Write changed text", Find<TextBlock>(window, "Selected command details").Text, StringComparison.Ordinal);
        Click(Find<Button>(window, "Record shortcut keys"));
        var capture = Find<TextBox>(window, "New shortcut");
        capture.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F12, KeyModifiers = KeyModifiers.Control | KeyModifiers.Alt });
        Assert.Equal("Ctrl+Alt+F12", capture.Text);
        Assert.Equal(before, registry.GetGesture(CommandIds.Save));
        Click(Find<Button>(window, "Clear recorded shortcut"));
        Assert.Empty(capture.Text!);
        Assert.Equal(before, registry.GetGesture(CommandIds.Save));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Inspector_has_four_sections_and_contextual_help_selects_the_exact_feature()
    {
        using var fixture = new DesktopFixture();
        await fixture.Shell.OpenFileAsync(await fixture.WriteAsync("guide.md", "---\ntags: [writing]\n---\n# Heading\n\nA [link](target.md).\n"));
        await Settle();
        var inspector = Find<TabControl>(fixture.Window, "Document inspector sections");
        Assert.Equal(new[] { "Outline", "Links", "Details", "History" }, inspector.Items.OfType<TabItem>().Select(t => t.Header?.ToString()).ToArray());

        async Task Verify(Button button, string featureId)
        {
            Assert.Equal(featureId, button.Tag);
            var feature = FeatureCatalog.Search(fixture.Shell.Registry, null).Single(f => f.Id == featureId);
            Click(button);
            await Settle();
            var guide = Assert.Single(fixture.Window.OwnedWindows.OfType<HelpWindow>());
            var results = Find<ListBox>(guide, "Feature search results");
            Assert.Equal(featureId, Assert.IsType<FeatureHelp>(results.SelectedItem).Id);
            Assert.Contains(guide.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == feature.Name);
            guide.Close();
        }

        await Verify(Find<Button>(fixture.Window, "Source editor help"), CommandIds.ViewSource);
        await Verify(Find<Button>(fixture.Window, "Reading preview help"), CommandIds.ViewReading);
        await Verify(Find<Button>(fixture.Window, "Inspector help"), "inspector.outline");
        inspector.SelectedItem = inspector.Items.OfType<TabItem>().Single(t => Equals(t.Header, "Details"));
        await Settle();
        await Verify(Button(fixture.Window, "Learn about tags"), "inspector.tags");
        inspector.SelectedItem = inspector.Items.OfType<TabItem>().Single(t => Equals(t.Header, "History"));
        await Settle();
        await Verify(Button(fixture.Window, "How version history works"), CommandIds.ShowHistory);
    }

    [AvaloniaFact]
    public async Task Menu_and_toolbar_hints_follow_remapped_and_removed_shortcuts()
    {
        using var fixture = new DesktopFixture();
        Assert.True(fixture.Shell.Registry.SetBinding(CommandIds.Save, "Ctrl+Alt+F12").Succeeded);
        fixture.Window.ApplySettings();
        await Settle();
        var save = fixture.Window.ApplicationMenu.GetLogicalDescendants().OfType<MenuItem>().Single(m => Equals(m.Tag, CommandIds.Save));
        Assert.Equal(Key.F12, save.InputGesture!.Key);
        Assert.Equal(KeyModifiers.Control | KeyModifiers.Alt, save.InputGesture.KeyModifiers);
        var button = Find<Button>(fixture.Window, "Save document");
        Assert.Contains("Ctrl+Alt+F12", Assert.IsType<string>(ToolTip.GetTip(button)), StringComparison.Ordinal);
        fixture.Shell.Registry.ClearBinding(CommandIds.Save);
        fixture.Window.ApplySettings();
        Assert.Null(save.InputGesture);
        Assert.DoesNotContain("Ctrl+S", Assert.IsType<string>(ToolTip.GetTip(button)), StringComparison.Ordinal);
    }
}
