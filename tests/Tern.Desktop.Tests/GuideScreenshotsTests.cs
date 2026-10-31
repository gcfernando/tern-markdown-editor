using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.App.Settings;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public class GuideScreenshotsTests
{
    [AvaloniaFact]
    public async Task Capture_redesigned_dialogs_and_exercise_guide_navigation()
    {
        using var fixture = new DesktopFixture();
        fixture.Shell.Settings.Theme = ThemeChoice.Midnight;
        fixture.Window.ApplyTheme();
        var registry = CommandRegistry.CreateDefault();
        var guide = new HelpWindow(registry);
        Window[] windows = [new SettingsWindow(new AppSettings()), new ShortcutsWindow(registry), new AboutWindow(), guide, new AIConnectionsWindow(Path.Combine(fixture.AppData, "settings"))];
        string[] names = ["settings", "shortcuts", "about", "guide", "ai-connections"];
        var root = FindRoot();
        var output = Path.Combine(root, "artifacts", "screenshots", "issues-v1");
        Directory.CreateDirectory(output);
        for (var i = 0; i < windows.Length; i++)
        {
            var window = windows[i];
            window.Show(fixture.Window);
            await Task.Delay(150);
            Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            using var stream = File.Create(Path.Combine(output, names[i] + ".png"));
            frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            if (window == guide)
            {
                var step = guide.GetVisualDescendants().OfType<TextBlock>().Single(t => AutomationProperties.GetName(t) == "Tutorial step");
                var buttons = guide.GetVisualDescendants().OfType<Button>().ToArray();
                Assert.StartsWith("Step 1 of", step.Text, StringComparison.Ordinal);
                buttons.Single(b => Equals(b.Content, "Next")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.StartsWith("Step 2 of", step.Text, StringComparison.Ordinal);
                buttons.Single(b => Equals(b.Content, "Replay")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.StartsWith("Step 1 of", step.Text, StringComparison.Ordinal);
            }
            if (window is AIConnectionsWindow)
            {
                var send = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Send request"));
                Assert.False(send.IsEnabled);
                var consent = window.GetVisualDescendants().OfType<CheckBox>().Single(c => AutomationProperties.GetName(c) == "Send exactly the message above to the endpoint above");
                consent.IsChecked = true;
                Assert.False(send.IsEnabled); // Empty required settings cannot be bypassed by consent.
                var message = window.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Exact message to send");
                message.Text = "New text requires fresh review";
                // Exercise Send before deferred TextChanged notifications are processed.
                send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var status = window.GetVisualDescendants().OfType<TextBlock>().Single(t => AutomationProperties.GetName(t) == "AI connection status");
                Assert.DoesNotContain("Waiting for the service", status.Text, StringComparison.Ordinal);
                Dispatcher.UIThread.RunJobs();
                Assert.False(consent.IsChecked);
                Assert.False(send.IsEnabled);
            }
            window.Close();
        }
    }

    private static string FindRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "Tern.slnx"))) folder = folder.Parent;
        return folder?.FullName ?? throw new InvalidOperationException("Repository root unavailable for screenshot evidence.");
    }
}
