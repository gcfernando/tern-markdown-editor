using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Settings;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

/// <summary>Current 39-issue dialog evidence. Captures never transmit AI content or modify user data.</summary>
public class CurrentDialogScreenshotsTests
{
    [AvaloniaFact]
    public async Task Capture_current_resizable_dialog_family_and_guided_ai_stages()
    {
        using var fixture = new DesktopFixture();
        fixture.Shell.Settings.Theme = ThemeChoice.Midnight;
        fixture.Window.ApplyTheme();
        var output = Path.Combine(Screenshots.Folder, "current-39");
        Directory.CreateDirectory(output);
        async Task Capture(Window window, string name)
        {
            if (!window.IsVisible) window.Show(fixture.Window);
            await Task.Delay(100);
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.CanResize);
            Assert.False(window.CanMaximize);
            Assert.False(window.CanMinimize);
            using var bitmap = window.CaptureRenderedFrame();
            Assert.NotNull(bitmap);
            using var stream = File.Create(Path.Combine(output, name + ".png"));
            bitmap.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        var settings = new SettingsWindow(new AppSettings(), fixture.Shell.Registry);
        await Capture(settings, "settings");
        Find<TextBox>(settings, "Find a setting").Text = "full preview width";
        await Capture(settings, "settings-preview-layout");
        settings.Close();
        var about = new AboutWindow();
        await Capture(about, "about");
        Find<Expander>(about, "Third-party details").IsExpanded = true;
        about.Height = 650;
        await Capture(about, "about-notices");
        about.Close();
        var reset = new ResetWindow(fixture.Shell);
        await Capture(reset, "reset"); reset.Close();
        var prompt = new PromptWindow("Unsaved changes", "Save changes to “New document” before closing?", ["Save", "Don't save", "Cancel"], 0, 2);
        await Capture(prompt, "unsaved-changes"); prompt.Close();
        var guide = new HelpWindow(fixture.Shell.Registry);
        await Capture(guide, "feature-guide"); guide.Close();
        var shortcuts = new ShortcutsWindow(fixture.Shell.Registry);
        await Capture(shortcuts, "shortcuts"); shortcuts.Close();
        var ai = new AIConnectionsWindow(Path.Combine(fixture.AppData, "current-39-ai"), openCopilot: owner => new CopilotConnectionWindow().ShowDialog(owner));
        await Capture(ai, "ai-choose-service");
        Find<Button>(ai, "AI setup next").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Find<TextBox>(ai, "Model name").Text = "example-model-id";
        await Capture(ai, "ai-configure");
        Find<Button>(ai, "AI setup next").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Find<TextBox>(ai, "Exact message to send").Text = "Summarize the purpose of a Markdown workspace.";
        await Capture(ai, "ai-review");
        Assert.False(Assert.Single(ai.GetVisualDescendants().OfType<CheckBox>()).IsChecked);
        ai.Close();
    }

    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetName(c) == name);
}
