using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Settings;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class SettingsEditSafetyTests
{
    [AvaloniaTheory]
    [InlineData("Find a setting", "font")]
    [InlineData("Editor font", "Consolas")]
    [InlineData("Workspace exclusion patterns", "drafts/**")]
    public async Task Enter_in_settings_text_fields_never_commits_or_closes(string name, string text)
    {
        using var f = new DesktopFixture();
        var commits = 0;
        var window = new SettingsWindow(new AppSettings(), commit: () => commits++);
        window.Show(f.Window);
        try
        {
            if (name != "Find a setting") Find<TextBox>(window, "Find a setting").Text = name == "Editor font" ? "font" : name;
            Pump(window);
            var input = Find<TextBox>(window, name);
            input.Focus(); input.Text = text; input.CaretIndex = text.Length;
            window.KeyPress(Key.Enter, RawInputModifiers.None, default, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, default, null);
            Pump(window);
            Assert.True(window.IsVisible);
            Assert.Equal(0, commits);
            if (input.AcceptsReturn) Assert.Contains('\n', input.Text!);
        }
        finally { await DiscardAndClose(window); }
    }

    [AvaloniaTheory]
    [InlineData("cancel")]
    [InlineData("escape")]
    [InlineData("close")]
    public async Task Dismissing_pending_settings_requires_an_explicit_discard_and_keeps_last_applied_values(string method)
    {
        using var f = new DesktopFixture();
        var live = new AppSettings();
        var window = new SettingsWindow(live);
        window.Show(f.Window);
        try
        {
            window.WorkingSettings.WordWrap = false;
            Click(Find<Button>(window, "Apply"));
            Assert.False(live.WordWrap);
            window.WorkingSettings.WordWrap = true;
            if (method == "cancel") Click(Find<Button>(window, "Cancel"));
            else if (method == "close") window.Close();
            else
            {
                Find<TextBox>(window, "Find a setting").Focus();
                window.KeyPress(Key.Escape, RawInputModifiers.None, default, null);
                window.KeyRelease(Key.Escape, RawInputModifiers.None, default, null);
            }
            var prompt = Assert.Single(window.OwnedWindows.OfType<PromptWindow>());
            var discard = Find<Button>(prompt, "Discard changes");
            Assert.Contains("danger", discard.Classes);
            Assert.False(discard.IsDefault);
            Assert.True(Find<Button>(prompt, "Keep editing").IsDefault);
            Click(Find<Button>(prompt, "Keep editing"));
            await Settle();
            Assert.True(window.IsVisible);
            Assert.True(window.WorkingSettings.WordWrap);
            Assert.False(live.WordWrap);
            window.Close();
            prompt = Assert.Single(window.OwnedWindows.OfType<PromptWindow>());
            Click(Find<Button>(prompt, "Discard changes"));
            await Settle();
            Assert.False(window.IsVisible);
            Assert.False(live.WordWrap);
        }
        finally { await DiscardAndClose(window); }
    }

    [AvaloniaFact]
    public async Task Saved_workspace_queries_remain_private_after_apply_and_cancel_preserves_applied_queries()
    {
        using var f = new DesktopFixture();
        var live = new AppSettings { SavedWorkspaceSearches = { [f.Docs] = ["tag:planning"] } };
        var window = new SettingsWindow(live);
        window.Show(f.Window);
        try
        {
            window.WorkingSettings.SavedWorkspaceSearches[f.Docs].Add("path:drafts");
            Assert.Single(live.SavedWorkspaceSearches[f.Docs]);
            Click(Find<Button>(window, "Apply"));
            Assert.Equal(["tag:planning", "path:drafts"], live.SavedWorkspaceSearches[f.Docs]);
            window.WorkingSettings.SavedWorkspaceSearches[f.Docs].Clear();
            await DiscardAndClose(window);
            Assert.Equal(["tag:planning", "path:drafts"], live.SavedWorkspaceSearches[f.Docs]);
        }
        finally { await DiscardAndClose(window); }
    }

    [AvaloniaFact]
    public async Task Category_reset_is_unavailable_for_cross_category_search_results()
    {
        using var f = new DesktopFixture();
        var window = new SettingsWindow(new AppSettings());
        window.Show(f.Window);
        try
        {
            var reset = Find<Button>(window, "Reset this category to defaults");
            Assert.True(reset.IsEnabled);
            Find<TextBox>(window, "Find a setting").Text = "font";
            Pump(window);
            Assert.False(reset.IsEnabled);
            Find<TextBox>(window, "Find a setting").Text = "";
            Pump(window);
            Assert.True(reset.IsEnabled);
        }
        finally { await DiscardAndClose(window); }
    }

    private static async Task DiscardAndClose(Window window)
    {
        if (!window.IsVisible) return;
        window.Close();
        foreach (var prompt in window.OwnedWindows.OfType<PromptWindow>().ToArray())
            Click(Find<Button>(prompt, "Discard changes"));
        await Settle();
    }
    private static async Task Settle() { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
    private static T Find<T>(Visual root, string name) where T : Control => root.GetVisualDescendants().OfType<T>()
        .Single(control => AutomationProperties.GetName(control) == name || control is Button button && Equals(button.Content, name));
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
}
