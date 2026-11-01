using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.Services;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class MCPWindowConsentTests
{
    [AvaloniaFact]
    public async Task Tool_arguments_changed_after_review_never_reach_the_server_and_actions_stay_visible()
    {
        using var fixture = new DesktopFixture(); var calls = 0;
        using var client = new MCPConnectionClient(new Handler(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); var root = body.RootElement;
            var method = root.GetProperty("method").GetString();
            if (method == "notifications/initialized") return new HttpResponseMessage(HttpStatusCode.Accepted);
            object result;
            if (method == "initialize") result = new { protocolVersion = MCPConnectionClient.SupportedProtocol, serverInfo = new { name = "Test server", version = "1" }, capabilities = new { tools = new { } } };
            else if (method == "tools/list") result = new { tools = new[] { new { name = "echo", description = "Echo only", inputSchema = new { type = "object" } } } };
            else { calls++; result = new { content = Array.Empty<object>() }; }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = root.GetProperty("id").GetInt32(), result }), Encoding.UTF8, "application/json") };
        }));
        var window = new MCPConnectionsWindow(client); window.Show(fixture.Window);
        try
        {
            var connect = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Connect and discover tools"));
            connect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var picker = window.GetVisualDescendants().OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == "Discovered MCP tools");
            for (var pass = 0; pass < 100 && picker.SelectedItem is null; pass++)
            { Dispatcher.UIThread.RunJobs(); await Task.Delay(10, TestContext.Current.CancellationToken); }
            Assert.IsType<MCPTool>(picker.SelectedItem);
            var arguments = window.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Exact JSON arguments to send");
            arguments.Text = "{\"text\":\"reviewed\"}"; Dispatcher.UIThread.RunJobs();
            var consent = window.GetVisualDescendants().OfType<CheckBox>().Single(); consent.IsChecked = true;
            var run = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Run reviewed tool")); Assert.True(run.IsEnabled);
            arguments.Text = "{\"text\":\"not approved\"}";
            run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs(); await Task.Delay(30, TestContext.Current.CancellationToken);
            Assert.Equal(0, calls); Assert.False(consent.IsChecked);
            foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(b => b == run || Equals(b.Content, "Close") || Equals(b.Content, "Cancel operation")))
                Assert.DoesNotContain(button.GetVisualAncestors(), ancestor => ancestor is ScrollViewer);
        }
        finally { window.Close(); }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
}
