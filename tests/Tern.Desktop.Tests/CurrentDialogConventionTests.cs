using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.App.Help;
using Tern.App.Settings;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public class CurrentDialogConventionTests
{
    private static T Find<T>(Visual window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetName(c) == name);
    private static async Task Settle() { await Task.Delay(100); Dispatcher.UIThread.RunJobs(); }

    [AvaloniaFact]
    public async Task Shared_dialogs_resize_without_maximize_or_minimize()
    {
        using var fixture = new DesktopFixture();
        Window[] windows = [new SettingsWindow(new AppSettings()), new ShortcutsWindow(fixture.Shell.Registry), new HelpWindow(fixture.Shell.Registry), new AboutWindow(), new ResetWindow(fixture.Shell), new PromptWindow("Unsaved changes", "Save the document?", ["Save", "Don't save", "Cancel"], 0, 2)];
        foreach (var window in windows)
        {
            window.Show(fixture.Window);
            await Settle();
            Assert.True(window.CanResize);
            Assert.False(window.CanMaximize);
            Assert.False(window.CanMinimize);
            Assert.True(window.MinWidth > 0 && window.MinHeight > 0);
            Assert.True(window.Width <= window.MaxWidth && window.Height <= window.MaxHeight);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task About_is_minimal_by_default_and_preserves_expandable_notices()
    {
        using var fixture = new DesktopFixture();
        var window = new AboutWindow();
        window.Show(fixture.Window);
        await Settle();
        var details = Find<Expander>(window, "Third-party details");
        Assert.False(details.IsExpanded);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Developed by Gehan Fernando" && t.IsEffectivelyVisible);
        details.IsExpanded = true;
        await Settle();
        Assert.All(AboutWindow.ThirdParty, component => Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains(component.Name, StringComparison.Ordinal) == true));
        window.Close();
    }

    [AvaloniaFact]
    public async Task All_configurable_settings_have_controls_and_new_preferences_edit_real_values()
    {
        using var fixture = new DesktopFixture();
        var settings = new AppSettings();
        var window = new SettingsWindow(settings, fixture.Shell.Registry);
        window.Show(fixture.Window);
        await Settle();
        Assert.All(FeatureCatalog.SettingsFeatures, f => Assert.NotNull(Find<Control>(window, f.Name)));
        Assert.True(settings.PreviewFullWidth);
        Find<CheckBox>(window, "Use full preview width").IsChecked = false;
        Assert.False(settings.PreviewFullWidth);
        Find<NumericUpDown>(window, "Interface text scale").Value = 1.25m;
        Assert.Equal(1.25, settings.UiScale);
        Find<NumericUpDown>(window, "Library sidebar width").Value = 320;
        Assert.Equal(320, settings.SidebarWidth);
        Find<CheckBox>(window, "Show Library sidebar").IsChecked = false;
        Assert.False(settings.SidebarVisible);
        Find<TextBox>(window, "Find a setting").Text = "Workspace exclusion patterns";
        await Settle();
        var exclusions = Find<TextBox>(window, "Workspace exclusion patterns");
        exclusions.Focus();
        exclusions.Text = "drafts/\nprivate/**\n";
        Find<Button>(window, "Close settings").Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { "drafts/", "private/**" }, settings.WorkspaceExclusions);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Guided_launch_is_explicit_and_never_runs_a_data_changing_command()
    {
        using var fixture = new DesktopFixture();
        var launched = new List<string>();
        var window = new HelpWindow(fixture.Shell.Registry, CommandIds.Settings, id => { launched.Add(id); return Task.CompletedTask; });
        window.Show(fixture.Window);
        await Settle();
        Assert.Empty(launched);
        Find<Button>(window, "Open guided feature").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(new[] { CommandIds.Settings }, launched);
        Assert.True(window.IsVisible);
        window.Close();

        var destructive = new HelpWindow(fixture.Shell.Registry, CommandIds.ClearRecent, id => { launched.Add(id); return Task.CompletedTask; });
        destructive.Show(fixture.Window);
        await Settle();
        Assert.False(Find<Button>(destructive, "Open guided feature").IsVisible);
        Find<Button>(destructive, "Open guided feature").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(new[] { CommandIds.Settings }, launched);
        destructive.Close();
    }

    [AvaloniaFact]
    public async Task Reset_requires_selected_scope_and_keeps_unsaved_document_text()
    {
        using var fixture = new DesktopFixture();
        var tab = fixture.Shell.NewDocument("Keep this draft");
        var window = new ResetWindow(fixture.Shell);
        window.Show(fixture.Window);
        await Settle();
        var recent = Find<CheckBox>(window, "Recent history (including pinned references)");
        var reset = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Reset selected"));
        recent.IsChecked = false;
        Assert.False(reset.IsEnabled);
        recent.IsChecked = true;
        Assert.True(reset.IsEnabled);
        reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Keep this draft", tab.CurrentText);
        Assert.Contains(tab, fixture.Shell.Tabs);
    }
}
