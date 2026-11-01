using System.Net;
using System.Net.Http;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.Services;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

/// <summary>Guided-window checks use isolated metadata and fake vault/transport; no live credentials or providers.</summary>
public class AIGuidedWindowTests
{
    private static T Find<T>(Visual root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetName(c) == name);
    private static Button Button(Visual root, string content) => root.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, content));
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    private static async Task Settle() { await Task.Delay(100); Dispatcher.UIThread.RunJobs(); }
    private static string DirectoryFor(DesktopFixture fixture) => Path.Combine(fixture.AppData, "guided-ai");

    [AvaloniaFact]
    public async Task Presets_select_actual_protocols_endpoints_and_clear_other_provider_credentials()
    {
        using var fixture = new DesktopFixture();
        var vault = new MemoryVault();
        var window = new AIConnectionsWindow(DirectoryFor(fixture), vault, confirmDiscard: _ => Task.FromResult(true));
        window.Show(fixture.Window);
        await Settle();
        var presets = Find<ComboBox>(window, "AI service preset");
        await Settle();
        Assert.Equal("OpenAI API", Assert.IsType<string>(presets.SelectedItem));
        Click(Find<Button>(window, "AI setup next")); await Settle();
        window.GetVisualDescendants().OfType<Expander>().Single(e => Equals(e.Header, "Advanced: service address and protocol")).IsExpanded = true;
        await Settle();
        Assert.Equal("https://api.openai.com/v1/chat/completions", Find<TextBox>(window, "Full AI endpoint").Text);
        presets.SelectedIndex = presets.Items.OfType<string>().ToList().FindIndex(n => n.StartsWith("Anthropic", StringComparison.Ordinal));
        await Settle();
        Assert.Equal("https://api.anthropic.com/v1/messages", Find<TextBox>(window, "Full AI endpoint").Text);
        Assert.Contains("Anthropic", Assert.IsType<string>(Find<ComboBox>(window, "AI provider protocol").SelectedItem), StringComparison.Ordinal);
        Find<TextBox>(window, "API key (saved only when you choose Save connection)").Text = "test-only-key";
        presets.SelectedIndex = presets.Items.OfType<string>().ToList().FindIndex(n => n.StartsWith("Ollama", StringComparison.Ordinal));
        await Settle();
        Assert.Equal("http://localhost:11434/api/chat", Find<TextBox>(window, "Full AI endpoint").Text);
        Assert.Empty(Find<TextBox>(window, "API key (saved only when you choose Save connection)").Text!);
        Assert.Equal(0, vault.Writes);
        Assert.Contains("GitHub Copilot", presets.Items.OfType<string>());
        Assert.Contains("MCP server", presets.Items.OfType<string>());
        window.Close();
    }

    [AvaloniaFact]
    public async Task Workflow_requires_model_before_review_and_persists_keys_only_when_explicitly_saved()
    {
        using var fixture = new DesktopFixture();
        var vault = new MemoryVault();
        var directory = DirectoryFor(fixture);
        var window = new AIConnectionsWindow(directory, vault, confirmDiscard: _ => Task.FromResult(true));
        window.Show(fixture.Window);
        await Settle();
        Click(Find<Button>(window, "AI setup next"));
        Assert.StartsWith("Step 2", Find<TextBlock>(window, "AI setup step").Text, StringComparison.Ordinal);
        Click(Find<Button>(window, "AI setup next"));
        Assert.StartsWith("Step 2", Find<TextBlock>(window, "AI setup step").Text, StringComparison.Ordinal);
        Assert.Contains("model", Find<TextBlock>(window, "AI model error").Text, StringComparison.Ordinal);
        Find<TextBox>(window, "Model name").Text = "test-model";
        await Settle();
        Find<TextBox>(window, "Connection name").Text = "Test connection";
        Find<TextBox>(window, "API key (saved only when you choose Save connection)").Text = "test-only-secret";
        await Settle();
        Assert.Equal(0, vault.Writes);
        Click(Find<Button>(window, "AI setup next"));
        Click(Button(window, "Save connection"));
        Assert.Equal(1, vault.Writes);
        var metadata = await File.ReadAllTextAsync(Path.Combine(directory, "ai-connections.json"));
        Assert.Contains("test-model", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("test-only-secret", metadata, StringComparison.Ordinal);
        Assert.StartsWith("Step 3", Find<TextBlock>(window, "AI setup step").Text, StringComparison.Ordinal);
        Assert.Contains("test-model", Find<TextBlock>(window, "Reviewed AI destination").Text, StringComparison.Ordinal);
        Assert.DoesNotContain("test-only-secret", Find<TextBlock>(window, "Reviewed AI destination").Text, StringComparison.Ordinal);
        Assert.False(Button(window, "Send request").IsEnabled);
        Click(Find<Button>(window, "AI setup back"));
        Assert.Equal("test-model", Find<TextBox>(window, "Model name").Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Stale_review_never_reaches_transport_even_before_deferred_change_events()
    {
        using var fixture = new DesktopFixture();
        var handler = new CountingHandler();
        var window = new AIConnectionsWindow(DirectoryFor(fixture), new MemoryVault(), connectionClient: new AIConnectionClient(handler), confirmDiscard: _ => Task.FromResult(true));
        window.Show(fixture.Window);
        await Settle();
        Click(Find<Button>(window, "AI setup next"));
        Find<TextBox>(window, "Model name").Text = "test-model";
        Find<TextBox>(window, "API key (saved only when you choose Save connection)").Text = "test-only-key";
        await Settle();
        Click(Find<Button>(window, "AI setup next"));
        window.GetVisualDescendants().OfType<Expander>().Single(e => Equals(e.Header, "Try your own message (optional)")).IsExpanded = true;
        await Settle();
        Find<TextBox>(window, "Exact message to send").Text = "Reviewed text";
        var consent = Find<CheckBox>(window, "Send exactly the message above to the endpoint above");
        consent.IsChecked = true;
        Find<TextBox>(window, "Exact message to send").Text = "Unreviewed replacement";
        Click(Button(window, "Send request"));
        await Settle();
        Assert.Equal(0, handler.Calls);
        Assert.False(consent.IsChecked);
        Assert.False(Button(window, "Send request").IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Other_integrations_open_explicitly_and_footer_stays_visible_at_minimum_size()
    {
        using var fixture = new DesktopFixture();
        var launches = new List<Window>();
        var window = new AIConnectionsWindow(DirectoryFor(fixture), new MemoryVault(), owner => { launches.Add(owner); return Task.CompletedTask; }) { Width = 640, Height = 500 };
        window.Show(fixture.Window);
        await Settle();
        Assert.Empty(launches);
        await Settle();
        var services = Find<ComboBox>(window, "AI service preset");
        services.SelectedIndex = services.Items.OfType<string>().ToList().IndexOf("GitHub Copilot");
        await Settle();
        Click(Find<Button>(window, "AI setup next"));
        Assert.Same(window, Assert.Single(launches));
        services.SelectedIndex = services.Items.OfType<string>().ToList().IndexOf("MCP server");
        await Settle();
        Click(Find<Button>(window, "AI setup next"));
        await Settle();
        var mcp = Assert.Single(window.OwnedWindows.OfType<MCPConnectionsWindow>());
        Assert.False(mcp.CanMaximize);
        mcp.Close();
        await Settle();
        services.SelectedIndex = 0; await Settle();
        foreach (var name in new[] { "Back", "Configure connection", "Close" })
        {
            var button = Button(window, name);
            var origin = button.TranslatePoint(default, window)!.Value;
            Assert.InRange(origin.Y, 0, window.ClientSize.Height);
            Assert.True(origin.Y + button.Bounds.Height <= window.ClientSize.Height + 1);
        }
        window.Close();
    }

    private sealed class MemoryVault : IAICredentialStore
    {
        private readonly Dictionary<string, string> _values = [];
        public int Writes { get; private set; }
        public void Save(string profileId, string secret) { _values[profileId] = secret; Writes++; }
        public string? Read(string profileId) => _values.GetValueOrDefault(profileId);
        public void Remove(string profileId) => _values.Remove(profileId);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"test response\"}}]}") });
        }
    }
}
