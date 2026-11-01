using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.Services;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class UnifiedConnectionManagerTests
{
    [AvaloniaFact]
    public void Switching_from_MCP_to_API_deletes_only_the_selected_API_connection()
    {
        using var f = new DesktopFixture(); var vault = new Vault();
        var api = new AIConnectionProfile(Guid.NewGuid().ToString("N"), "Local writing", "ollama-chat", "http://localhost:11434/api/chat", "local-model");
        var apiStore = new AIProfileStore(f.Shell.Paths.SettingsDirectory); apiStore.Save([api]);
        var tools = new IntegrationProfile(Guid.NewGuid().ToString("N"), "mcp", "Research tools", "https://tools.example/mcp");
        var integrations = new IntegrationProfileStore(f.Shell.Paths.SettingsDirectory, vault); integrations.Save(tools, "tool-token");
        using var window = new AIConnectionsWindow(f.Shell.Paths.SettingsDirectory, vault, confirmDelete: _ => Task.FromResult(true), confirmDiscard: _ => Task.FromResult(true));
        window.Show(f.Window);
        try
        {
            Pump(window);
            var saved = Find<ComboBox>(window, "Saved AI connections"); Assert.Equal(2, saved.ItemCount);
            saved.SelectedIndex = 1; Pump(window);
            Assert.Equal("MCP server", Find<ComboBox>(window, "AI service preset").SelectedItem);
            Assert.False(Find<TextBox>(window, "Model name").IsEnabled);
            Assert.Empty(window.OwnedWindows);
            saved.SelectedIndex = 0; Pump(window);
            Assert.True(Find<TextBox>(window, "Model name").IsEnabled);
            Assert.Equal("local-model", Find<TextBox>(window, "Model name").Text);
            Assert.StartsWith("Step 1", Find<TextBlock>(window, "AI setup step").Text, StringComparison.Ordinal);
            Click(Find<Button>(window, "Delete AI connection")); Pump(window);
            Assert.Empty(apiStore.Load()); Assert.Equal(tools.Id, Assert.Single(integrations.Load()).Id);
            Assert.Equal("tool-token", integrations.ReadSecret(tools));
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("copilot")]
    [InlineData("mcp")]
    public async Task Selecting_a_saved_integration_stays_on_selection_until_configure_is_clicked(string kind)
    {
        using var f = new DesktopFixture(); var vault = new Vault(); var transport = new NoNetwork();
        var profile = new IntegrationProfile(Guid.NewGuid().ToString("N"), kind, "Saved connection",
            kind == "mcp" ? "https://tools.example/mcp" : Path.Combine(f.AppData, "copilot.exe"), "auto");
        var store = new IntegrationProfileStore(f.Shell.Paths.SettingsDirectory, vault); store.Save(profile, "saved-test-token");
        var original = File.ReadAllText(store.FilePath);
        using var window = new AIConnectionsWindow(f.Shell.Paths.SettingsDirectory, vault, connectionClient: new AIConnectionClient(transport));
        window.Show(f.Window);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var saved = Find<ComboBox>(window, "Saved AI connections");
                saved.SelectedIndex = -1; Pump(window);
                saved.SelectedIndex = 0; await Settle(window);
                Assert.Empty(window.OwnedWindows);
                Assert.StartsWith("Step 1", Find<TextBlock>(window, "AI setup step").Text, StringComparison.Ordinal);
                Assert.True(Find<Button>(window, "Delete AI connection").IsEnabled);
                var configure = Find<Button>(window, "AI setup next"); Assert.Equal("Configure connection", configure.Content);
                Assert.Equal(original, File.ReadAllText(store.FilePath)); Assert.Equal(0, transport.Calls);
                Click(configure); await Settle(window);
                var editor = Assert.Single(window.OwnedWindows);
                Assert.Equal(kind == "mcp" ? typeof(MCPConnectionsWindow) : typeof(CopilotConnectionWindow), editor.GetType());
                Assert.Equal(profile.Name, Find<TextBox>(editor, "Integration connection name").Text);
                if (kind == "copilot")
                {
                    editor.GetVisualDescendants().OfType<Expander>().Single(e => Equals(e.Header, "Advanced: installed CLI and safety limits")).IsExpanded = true;
                    Pump(editor);
                }
                Assert.Equal(profile.Destination, Find<TextBox>(editor, kind == "mcp" ? "MCP server endpoint" : "Installed native copilot.exe").Text);
                var secret = Find<TextBox>(editor, kind == "mcp" ? "Bearer token (optional)" : "GitHub token with Copilot Requests permission");
                Assert.Equal("saved-test-token", secret.Text); Assert.Equal('●', secret.PasswordChar);
                Assert.Contains(editor.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == (kind == "mcp" ? "Configure your MCP server" : "Configure GitHub Copilot"));
                Assert.False(editor.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Validate connection") || Equals(b.Content, "Connect and discover tools")).IsEffectivelyVisible);
                editor.Close(); await Settle(window);
                Assert.Equal(original, File.ReadAllText(store.FilePath));
                Assert.Equal("saved-test-token", store.ReadSecret(profile)); Assert.Equal(0, transport.Calls);
            }
        }
        finally { foreach (var child in window.OwnedWindows.ToArray()) child.Close(); window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("ollama")]
    [InlineData("compatible")]
    public void Selecting_a_saved_API_stays_on_selection_until_configure_is_clicked(string serviceId)
    {
        using var f = new DesktopFixture(); var vault = new Vault(); var transport = new NoNetwork();
        var service = AIServices.Get(serviceId);
        var profile = new AIConnectionProfile(Guid.NewGuid().ToString("N"), "Saved API", service.Protocol,
            serviceId == "compatible" ? "https://api.example/chat" : service.Endpoint, "saved-model", serviceId);
        var store = new AIProfileStore(f.Shell.Paths.SettingsDirectory); store.Save([profile]); vault.Save(profile.Id, "test-token");
        var original = File.ReadAllText(store.FilePath);
        using var window = new AIConnectionsWindow(f.Shell.Paths.SettingsDirectory, vault, connectionClient: new AIConnectionClient(transport));
        window.Show(f.Window);
        try
        {
            Find<ComboBox>(window, "Saved AI connections").SelectedIndex = 0; Pump(window);
            Assert.StartsWith("Step 1", Find<TextBlock>(window, "AI setup step").Text, StringComparison.Ordinal);
            Assert.Empty(window.OwnedWindows);
            Assert.Equal(service.Name, Find<ComboBox>(window, "AI service preset").SelectedItem);
            Assert.True(Find<Button>(window, "Delete AI connection").IsEnabled);
            Click(Find<Button>(window, "AI setup next")); Pump(window);
            Assert.StartsWith("Step 2", Find<TextBlock>(window, "AI setup step").Text, StringComparison.Ordinal);
            Assert.Equal("saved-model", Find<TextBox>(window, "Model name").Text);
            Assert.Equal(original, File.ReadAllText(store.FilePath)); Assert.Equal(0, transport.Calls);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Service_menu_exposes_all_types_and_switches_configuration_without_network_requests()
    {
        using var f = new DesktopFixture();
        var transport = new NoNetwork();
        using var window = new AIConnectionsWindow(f.Shell.Paths.SettingsDirectory, new Vault(), connectionClient: new AIConnectionClient(transport), confirmDiscard: _ => Task.FromResult(true));
        window.Show(f.Window);
        try
        {
            Pump(window); var menu = Find<ComboBox>(window, "AI service preset");
            var services = menu.Items.OfType<string>().ToArray();
            Assert.Contains("GitHub Copilot", services); Assert.Contains("MCP server", services);
            foreach (var name in new[] { "GitHub Copilot", "MCP server" })
            {
                menu.SelectedIndex = Array.IndexOf(services, name); Pump(window);
                Assert.False(Find<TextBox>(window, "API key (saved only when you choose Save connection)").IsEnabled);
                Assert.True(Find<Button>(window, "AI setup next").IsEnabled);
                var guide = Find<Button>(window, "AI service setup guide");
                Assert.True(guide.IsEffectivelyVisible); Assert.True(guide.IsEnabled);
                Assert.Equal(name == "GitHub Copilot" ? "https://docs.github.com/en/copilot/how-tos/copilot-cli/set-up-copilot-cli/authenticate-copilot-cli" : "https://modelcontextprotocol.io/specification/2025-11-25/basic/transports#streamable-http", ToolTip.GetTip(guide));
                Assert.Equal(0, transport.Calls);
            }
            menu.SelectedIndex = 0; Pump(window);
            Assert.Equal(AIServices.Get("openai").Documentation, ToolTip.GetTip(Find<Button>(window, "AI service setup guide")));
            Click(Find<Button>(window, "AI setup next")); Pump(window);
            Assert.True(Find<TextBox>(window, "Model name").IsEnabled);
            Assert.True(Find<TextBox>(window, "API key (saved only when you choose Save connection)").IsEnabled);
        }
        finally { window.Close(); }
    }

    private static T Find<T>(Window w, string name) where T : Control => w.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetName(c) == name);
    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Settle(Window w) { await Task.Delay(20, TestContext.Current.CancellationToken); Pump(w); }
    private static void Pump(Window w) { w.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private sealed class NoNetwork : System.Net.Http.HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("Unexpected network request"); }
    }
    private sealed class Vault : IAICredentialStore
    {
        private readonly Dictionary<string, string> _values = [];
        public string? Read(string id) => _values.GetValueOrDefault(id);
        public void Save(string id, string value) => _values[id] = value;
        public void Remove(string id) => _values.Remove(id);
    }
}
