using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class ShortcutTransactionTests
{
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Menu_and_Settings_shortcut_edits_are_private_until_applied_and_Cancel_keeps_last_apply(bool fromSettings, bool apply)
    {
        using var f = new DesktopFixture();
        var before = f.Shell.Registry.GetGesture(CommandIds.ViewReading);
        var opening = f.Window.RunCommandAsync(fromSettings ? CommandIds.Settings : CommandIds.Shortcuts);
        SettingsWindow? settings = null;
        if (fromSettings)
        {
            settings = Assert.Single(f.Window.OwnedWindows.OfType<SettingsWindow>());
            Find<TextBox>(settings, "Find a setting").Text = "keyboard";
            Pump(settings);
            Click(Find<Button>(settings, "Edit keyboard shortcuts"));
        }
        var owner = (Window?)settings ?? f.Window;
        var window = Assert.Single(owner.OwnedWindows.OfType<ShortcutsWindow>());
        try
        {
            Assert.True(await window.TryAssignAsync(CommandIds.ViewReading, "Ctrl+Alt+F9"));
            Assert.Equal(before, f.Shell.Registry.GetGesture(CommandIds.ViewReading));
            if (apply)
            {
                Click(Find<Button>(window, "Apply"));
                Assert.True(await window.TryAssignAsync(CommandIds.ViewReading, "Ctrl+Alt+F10"));
            }
            Click(Find<Button>(window, "Cancel"));
            await Settle();
            if (settings is not null) Click(Find<Button>(settings, "Done"));
            await opening.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(apply ? "Ctrl+Alt+F9" : before, f.Shell.Registry.GetGesture(CommandIds.ViewReading));
            Assert.Equal(apply ? "Ctrl+Alt+F9" : null,
                f.Shell.Settings.KeyBindings.GetValueOrDefault(CommandIds.ViewReading));
        }
        finally
        {
            window.Close();
            if (settings?.IsVisible == true)
            {
                settings.Close();
                foreach (var prompt in settings.OwnedWindows.OfType<PromptWindow>().ToArray())
                    Click(Find<Button>(prompt, "Discard changes"));
                await Settle();
            }
        }
    }

    [AvaloniaFact]
    public async Task Failed_shortcut_Apply_preserves_live_assignments_and_pending_edits_for_retry()
    {
        using var f = new DesktopFixture();
        var before = f.Shell.Registry.GetGesture(CommandIds.ViewReading);
        var fail = true;
        var window = new ShortcutsWindow(f.Shell.Registry, commit: () => { if (fail) throw new IOException("Synthetic sharing failure."); });
        window.Show(f.Window);
        try
        {
            await window.TryAssignAsync(CommandIds.ViewReading, "Ctrl+Alt+F9");
            Click(Find<Button>(window, "Done"));
            Assert.True(window.IsVisible);
            Assert.Equal(before, f.Shell.Registry.GetGesture(CommandIds.ViewReading));
            Assert.Contains("could not", window.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Ctrl+Alt+F9", window.WorkingRegistry.GetGesture(CommandIds.ViewReading));
            fail = false;
            Click(Find<Button>(window, "Done"));
            Assert.False(window.IsVisible);
            Assert.Equal("Ctrl+Alt+F9", f.Shell.Registry.GetGesture(CommandIds.ViewReading));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Reset_all_requires_danger_confirmation_and_stays_pending_until_Apply()
    {
        using var f = new DesktopFixture();
        f.Shell.Registry.ForceBinding(CommandIds.ViewReading, "Ctrl+Alt+F9");
        var window = new ShortcutsWindow(f.Shell.Registry);
        window.Show(f.Window);
        try
        {
            Click(Find<Button>(window, "Reset all shortcuts"));
            var prompt = Assert.Single(window.OwnedWindows.OfType<PromptWindow>());
            var reset = Find<Button>(prompt, "Reset all shortcuts");
            Assert.Contains("danger", reset.Classes);
            Assert.False(reset.IsDefault);
            Click(Find<Button>(prompt, "Cancel"));
            await Settle();
            Assert.Equal("Ctrl+Alt+F9", window.WorkingRegistry.GetGesture(CommandIds.ViewReading));
            Click(Find<Button>(window, "Reset all shortcuts"));
            prompt = Assert.Single(window.OwnedWindows.OfType<PromptWindow>());
            Click(Find<Button>(prompt, "Reset all shortcuts"));
            await Settle();
            Assert.Equal("Ctrl+Alt+F9", f.Shell.Registry.GetGesture(CommandIds.ViewReading));
            Assert.Equal(CommandRegistry.CreateDefault().GetGesture(CommandIds.ViewReading),
                window.WorkingRegistry.GetGesture(CommandIds.ViewReading));
            Click(Find<Button>(window, "Cancel"));
            Assert.Equal("Ctrl+Alt+F9", f.Shell.Registry.GetGesture(CommandIds.ViewReading));
        }
        finally { window.Close(); }
    }

    private static T Find<T>(Visual root, string name) where T : Control => root.GetVisualDescendants().OfType<T>()
        .Single(control => AutomationProperties.GetName(control) == name || control is Button button && Equals(button.Content, name));
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static async Task Settle() { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
}
