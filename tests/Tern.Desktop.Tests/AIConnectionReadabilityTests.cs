using System.Net;
using System.Net.Http;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.Services;
using Tern.Desktop.Views;
using Tern.App.Settings;
using Tern.Desktop.Themes;

namespace Tern.Desktop.Tests;

public sealed class AIConnectionReadabilityTests
{
    [AvaloniaTheory]
    [InlineData(false, 640, 500, 1.5)]
    [InlineData(true, 640, 500, 1.5)]
    [InlineData(false, 900, 740, 1.0)]
    [InlineData(true, 900, 740, 1.0)]
    [InlineData(false, 900, 740, 1.25)]
    [InlineData(true, 900, 740, 1.25)]
    [InlineData(false, 900, 740, 2.0)]
    [InlineData(true, 900, 740, 2.0)]
    public void Core_steps_explain_required_inputs_and_keep_actions_visible(bool light, int width, int height, double scale)
    {
        using var fixture = new DesktopFixture(); var handler = new NoNetwork();
        fixture.Shell.Settings.UiScale = scale; fixture.Window.ApplySettings();
        ThemeManager.Apply(Application.Current!, light ? ThemeChoice.Paper : ThemeChoice.Midnight);
        var window = new AIConnectionsWindow(Path.Combine(fixture.AppData, "readability"), new MemoryVault(), connectionClient: new AIConnectionClient(handler), confirmDiscard: _ => Task.FromResult(true))
            { Width = width, Height = height, RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        window.Show(fixture.Window);
        try
        {
            Pump(window);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("API account", StringComparison.Ordinal) == true && t.FontSize == 14 * scale);
            Assert.True(Find<TextBlock>(window, "AI setup step").IsEffectivelyVisible);
            Assert.StartsWith("Step 1", Find<TextBlock>(window, "AI setup step").Text, StringComparison.Ordinal);
            Assert.Contains(Text(window), t => t.Contains("OpenAI API", StringComparison.Ordinal));
            Capture(window, $"ai-service-{light}-{width}"); CheckFooter(window);
            Click(Find<Button>(window, "AI setup next")); Pump(window);
            Assert.StartsWith("Step 2", Find<TextBlock>(window, "AI setup step").Text, StringComparison.Ordinal);
            Assert.True(Find<TextBox>(window, "Model name").IsEffectivelyVisible);
            Assert.Equal(14 * scale, Find<TextBox>(window, "Model name").FontSize);
            Assert.Contains(Text(window), t => t.Contains("API key", StringComparison.Ordinal));
            var advanced = window.GetVisualDescendants().OfType<Expander>().Single(e => Equals(e.Header, "Advanced: service address and protocol"));
            Assert.False(advanced.IsExpanded);
            Assert.Equal('●', Find<TextBox>(window, "API key (saved only when you choose Save connection)").PasswordChar);
            Assert.Contains(Text(window), t => t == "Model ID");
            Find<TextBox>(window, "Model name").Text = "test-model";
            Find<TextBox>(window, "API key (saved only when you choose Save connection)").Text = "test-only-key";
            Capture(window, $"ai-configure-{light}-{width}"); CheckFooter(window);
            AssertReachable(window, Find<TextBox>(window, "API key (saved only when you choose Save connection)"));
            Capture(window, $"ai-key-visible-{light}-{width}"); CheckFooter(window);
            advanced.IsExpanded = true; Pump(window); CheckFooter(window);
            AssertReachable(window, Find<TextBox>(window, "Full AI endpoint"));
            Click(Find<Button>(window, "AI setup next")); Pump(window); CheckFooter(window);
            Assert.StartsWith("Step 3", Find<TextBlock>(window, "AI setup step").Text, StringComparison.Ordinal);
            Assert.True(Find<TextBox>(window, "Exact message to send").IsEffectivelyVisible);
            Capture(window, $"ai-review-{light}-{width}");
            Assert.Contains("test-model", Find<TextBlock>(window, "Reviewed AI destination").Text, StringComparison.Ordinal);
            Click(Find<Button>(window, "AI setup back")); Pump(window);
            Assert.Equal("test-model", Find<TextBox>(window, "Model name").Text);
            Assert.Equal(0, handler.Calls);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false, 1.0)]
    [InlineData(true, 1.0)]
    [InlineData(false, 1.5)]
    [InlineData(true, 1.5)]
    public void Copilot_and_MCP_have_persistent_labels_and_wrapping_footer_without_automatic_requests(bool light, double scale)
    {
        using var fixture = new DesktopFixture(); var calls = 0; var handler = new NoNetwork();
        fixture.Shell.Settings.UiScale = scale; fixture.Window.ApplySettings();
        ThemeManager.Apply(Application.Current!, light ? ThemeChoice.Paper : ThemeChoice.Midnight);
        var windows = new Window[] { new CopilotConnectionWindow(new CopilotTextClient((_, _) => { calls++; return Task.FromResult(new CopilotProcessResult(0, "never")); })), new MCPConnectionsWindow(new MCPConnectionClient(handler)) };
        foreach (var window in windows)
        {
            window.Width = 640; window.Height = 500; window.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
            window.Show(fixture.Window);
            try
            {
                Pump(window); CheckFooter(window);
                Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), t => t.FontSize == 14 * scale);
                var text = Text(window).ToArray();
                Assert.Contains(text, t => t.StartsWith("1. ", StringComparison.Ordinal));
                Assert.Contains(text, t => t.StartsWith("2. ", StringComparison.Ordinal));
                Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), t => t.PasswordChar == '●');
                Capture(window, window.GetType().Name + "-default-" + light + "-" + scale);
                foreach (var expander in window.GetVisualDescendants().OfType<Expander>().ToArray()) { expander.IsExpanded = true; Pump(window); CheckFooter(window); }
                Capture(window, window.GetType().Name + "-" + light + "-" + scale);
                Assert.Equal(0, calls); Assert.Equal(0, handler.Calls);
            }
            finally { window.Close(); }
        }
    }

    private static IEnumerable<string> Text(Window window) => window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "");
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetName(c) == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Pump(Window window) { for (var i = 0; i < 5; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); } }
    private static void AssertReachable(Window window, Control control)
    {
        control.BringIntoView(); Pump(window);
        var content = Assert.IsType<Grid>(window.Content).Children.OfType<ScrollViewer>().Single();
        var point = control.TranslatePoint(default, content)!.Value;
        Assert.InRange(point.Y, 0, content.Viewport.Height + 1);
        Assert.True(point.Y + control.Bounds.Height <= content.Viewport.Height + 2, $"Input extends below viewport: {point.Y}+{control.Bounds.Height}>{content.Viewport.Height}");
    }
    private static void CheckFooter(Window window)
    {
        var content = Assert.IsType<Grid>(window.Content).Children.OfType<ScrollViewer>().Single();
        Assert.True(content.Viewport.Height > 40, $"Content viewport is only {content.Viewport.Height} high");
        foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.GetVisualAncestors().All(a => a is not ScrollViewer)))
        {
            var point = button.TranslatePoint(default, window)!.Value;
            Assert.InRange(point.X, 0, window.ClientSize.Width); Assert.InRange(point.Y, 0, window.ClientSize.Height);
            Assert.True(point.X + button.Bounds.Width <= window.ClientSize.Width + 1); Assert.True(point.Y + button.Bounds.Height <= window.ClientSize.Height + 1);
        }
    }
    private static void Capture(Window window, string name)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory); while (root is not null && !File.Exists(Path.Combine(root.FullName, "Tern.slnx"))) root = root.Parent;
        Assert.NotNull(root); var output = Path.Combine(root.FullName, "artifacts", "screenshots", "ai-readability"); Directory.CreateDirectory(output);
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(Path.Combine(output, name + ".png"), new PngBitmapEncoderOptions());
    }
    private sealed class MemoryVault : IAICredentialStore
    { public void Save(string profileId, string secret) { } public string? Read(string profileId) => null; public void Remove(string profileId) { } }
    private sealed class NoNetwork : HttpMessageHandler
    { public int Calls { get; private set; } protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)); } }
}
