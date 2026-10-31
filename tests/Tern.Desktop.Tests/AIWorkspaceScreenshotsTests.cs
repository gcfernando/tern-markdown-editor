using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.Services;
using Tern.Desktop.Themes;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class AIWorkspaceScreenshotsTests
{
    [AvaloniaFact]
    public async Task Writing_settings_connections_and_guide_render_at_normal_and_compact_sizes()
    {
        using var f = new DesktopFixture(directory =>
        {
            new AIProfileStore(directory).Save([
                new("11111111111111111111111111111111", "Local drafts", "ollama-chat", "http://localhost:11434/api/chat", "llama3.2"),
                new("22222222222222222222222222222222", "Writing service", "chat-completions", "https://example.test/v1/chat/completions", "writing-model"),
            ]);
            return new AIWorkspaceService(directory, new EmptyVault());
        });
        ThemeManager.Apply(Application.Current!, Tern.App.Settings.ThemeChoice.Midnight);
        f.Shell.NewDocument("# Project notes\r\n\r\n## Next steps\r\n\r\n- [ ] Review the draft\r\n- [ ] Share the project plan\r\n");
        await f.Window.RebuildPreviewAsync();
        f.Window.ShowAIWritingPrompt();
        f.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Find<TextBox>(f.Window, "AI writing question").Text = "Turn these notes into a clear project plan.\nKeep the existing tasks and add a short introduction.";
        await Capture(f.Window, "writing-prompt");
        f.Window.HideOverlay();
        var settings = new SettingsWindow(f.Shell.Settings, f.Shell.Registry, configureAI: _ => Task.CompletedTask,
            aiProfiles: () => f.Window.AIWorkspace.Profiles, initialCategory: "AI connections");
        settings.Show(f.Window);
        await Capture(settings, "settings-ai");
        settings.Close();
        var connections = new AIConnectionsWindow(f.Shell.Paths.SettingsDirectory, new EmptyVault());
        connections.Show(f.Window);
        await Capture(connections, "connections");
        connections.Width = 640; connections.Height = 500;
        await Capture(connections, "connections-compact");
        connections.Close();
        var guide = new HelpWindow(f.Shell.Registry, answerAI: (_, _) => Task.FromResult(
            "Start with a short summary of the meeting. Group the decisions under a clear heading, then list each action with its owner and due date.\n\nIn Tern, select a saved AI connection in the main toolbar and press Alt+Space. Ask it to turn the current note into an action plan while keeping names and dates. Check the result in Split view; Undo restores the previous draft."));
        guide.Show(f.Window);
        Find<TextBox>(guide, "Search feature help").Text = "How do I turn meeting notes into an action plan?";
        Find<Button>(guide, "Ask question").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Capture(guide, "guide-answer");
        guide.Width = 720; guide.Height = 600;
        await Capture(guide, "guide-compact");
        guide.Close();
    }

    private static T Find<T>(Visual root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetName(c) == name);
    private static async Task Capture(Window window, string name)
    {
        await Task.Delay(100, TestContext.Current.CancellationToken);
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Tern.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var folder = Path.Combine(root.FullName, "artifacts", "screenshots", "ai-workflow");
        Directory.CreateDirectory(folder);
        using var bitmap = window.CaptureRenderedFrame(); Assert.NotNull(bitmap);
        using var output = File.Create(Path.Combine(folder, name + ".png")); bitmap.Save(output, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
    private sealed class EmptyVault : IAICredentialStore
    {
        public string? Read(string profileId) => null;
        public void Save(string profileId, string secret) { }
        public void Remove(string profileId) { }
    }
}
