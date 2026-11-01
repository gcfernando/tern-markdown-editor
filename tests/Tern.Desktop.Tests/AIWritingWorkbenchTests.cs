using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Settings;
using Tern.Desktop.Services;
using Tern.Desktop.Themes;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class AIWritingWorkbenchTests
{
    [AvaloniaTheory]
    [InlineData(false, 1240, 850)]
    [InlineData(true, 1240, 850)]
    [InlineData(false, 640, 500)]
    [InlineData(true, 640, 500)]
    public void Writing_workbench_fills_available_space_and_keeps_actions_visible(bool light, int width, int height)
    {
        using var fixture = new DesktopFixture();
        ThemeManager.Apply(Application.Current!, light ? ThemeChoice.Paper : ThemeChoice.Midnight);
        var workspace = new AIWorkspaceService(fixture.AppData, new Vault()); workspace.Reload();
        var panel = new AIPromptPanel("Project plan", workspace, (_, _, _) => throw new InvalidOperationException("Unexpected generation"),
            () => { }, () => Task.CompletedTask, _ => { });
        var window = new Window { Width = width, Height = height, Content = panel };
        window.Show(fixture.Window);
        try
        {
            Pump(window);
            Assert.InRange(panel.Bounds.Width, Math.Min(1120, width - 48) - 1, Math.Min(1120, width - 48) + 1);
            Assert.True(Find<TextBox>(panel, "AI writing question").Bounds.Height >= 280);
            foreach (var button in panel.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible))
            {
                var point = button.TranslatePoint(default, window)!.Value;
                Assert.InRange(point.X, 0, width); Assert.InRange(point.Y, 0, height);
                Assert.True(point.X + button.Bounds.Width <= width + 1);
                Assert.True(point.Y + button.Bounds.Height <= height + 1);
            }
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Tern.slnx"))) root = root.Parent;
            Assert.NotNull(root);
            var folder = Path.Combine(root.FullName, "artifacts", "screenshots", "writing-workbench"); Directory.CreateDirectory(folder);
            using var image = window.CaptureRenderedFrame(); Assert.NotNull(image);
            image.Save(Path.Combine(folder, $"writing-{light}-{width}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task MCP_selection_disables_writing_and_opens_only_the_selected_tool_workspace()
    {
        using var fixture = new DesktopFixture(); var vault = new Vault();
        var store = new IntegrationProfileStore(fixture.AppData, vault);
        var id = Guid.NewGuid().ToString("N"); store.Save(new(id, "mcp", "Research tools", "https://tools.example/mcp"), "");
        var workspace = new AIWorkspaceService(fixture.AppData, vault); workspace.Reload();
        var generated = 0; string? opened = null; var closed = false;
        var panel = new AIPromptPanel("Private draft", workspace, (_, _, _) => { generated++; return Task.FromResult("Never"); },
            () => closed = true, () => Task.CompletedTask, _ => { }, openTools: profile => { opened = profile; return Task.CompletedTask; });
        var window = new Window { Width = 1100, Height = 800, Content = panel }; window.Show(fixture.Window);
        try
        {
            Pump(window);
            Assert.False(Find<TextBox>(panel, "AI writing question").IsEnabled);
            var generate = Find<Button>(panel, "Generate and update Markdown"); Assert.False(generate.IsEnabled);
            generate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(0, generated);
            var tools = Find<Button>(panel, "Open selected MCP tools"); Assert.True(tools.IsEffectivelyVisible); Assert.True(tools.IsEnabled);
            tools.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Yield();
            Assert.Equal(id, opened); Assert.True(closed); Assert.Equal(0, generated);
        }
        finally { window.Close(); }
    }

    private static T Find<T>(Visual root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetName(c) == name);
    private static void Pump(Window window) { for (var i = 0; i < 4; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); } }
    private sealed class Vault : IAICredentialStore
    {
        private readonly Dictionary<string, string> _values = [];
        public string? Read(string id) => _values.GetValueOrDefault(id);
        public void Save(string id, string secret) => _values[id] = secret;
        public void Remove(string id) => _values.Remove(id);
    }
}
