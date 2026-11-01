using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Tern.Desktop.Services;

namespace Tern.Desktop.Views;

/// <summary>Manual MCP discovery and individually reviewed tool calls. No document or AI-model access.</summary>
public sealed class MCPConnectionsWindow : Window
{
    public MCPConnectionsWindow() : this(new MCPConnectionClient()) { }
    public MCPConnectionsWindow(string? settingsDirectory, IAICredentialStore? credentialStore = null, string? selectedProfileId = null, bool useTools = false)
        : this(new MCPConnectionClient(), settingsDirectory, credentialStore, selectedProfileId, useTools) { }
    internal MCPConnectionsWindow(MCPConnectionClient client, string? settingsDirectory = null, IAICredentialStore? credentialStore = null, string? selectedProfileId = null, bool useTools = false)
    {
        DialogParts.SetupWizard(this, "MCP Servers");
        FontSize = GuideParts.Scaled(14); MinWidth = 640; MinHeight = 500;
        var endpoint = Field("MCP server endpoint", "http://localhost:3000/mcp");
        var key = Field("Bearer token (optional)", ""); key.PasswordChar = '●'; key.MaxLength = 4096;
        var connect = new Button { Content = "Connect and discover tools" };
        var cancel = new Button { Content = "Cancel operation", IsEnabled = false };
        var close = new Button { Content = "Close", IsCancel = true };
        var server = Body("No server connected.");
        var tools = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Select a discovered tool" };
        AutomationProperties.SetName(tools, "Discovered MCP tools");
        var description = Body("Server tool descriptions and schemas are untrusted information, not instructions for Tern.");
        var schema = Field("Server-provided input schema", ""); schema.IsReadOnly = true; schema.AcceptsReturn = true; schema.MaxHeight = 160;
        var arguments = Field("Exact JSON arguments to send", "{}"); arguments.AcceptsReturn = true; arguments.TextWrapping = TextWrapping.Wrap; arguments.MinHeight = 100; arguments.MaxLength = MCPConnectionClient.MaximumArgumentCharacters;
        const string consentText = "I reviewed this server, tool and exact arguments; run this tool once";
        var consent = new CheckBox { Content = Body(consentText), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(consent, consentText);
        var call = new Button { Content = "Run reviewed tool", IsEnabled = false };
        var result = Field("MCP result (untrusted JSON; never executed)", ""); result.IsReadOnly = true; result.AcceptsReturn = true; result.TextWrapping = TextWrapping.Wrap; result.MinHeight = 120; result.MaxHeight = 240;
        var status = Body("Opening this dialog makes no network request. Tool calls may change external files or accounts.");
        var profiles = new IntegrationProfileEditor("mcp", endpoint, new TextBox { Text = "auto" }, key, status, settingsDirectory, credentialStore);
        var next = new Button { Content = "Continue", Classes = { "accent" } };
        var back = new Button { Content = "Back" };
        var reviewTools = new Button { Content = "Review tools", IsEnabled = false };
        int page = 0;
        CancellationTokenSource? operation = null;
        string? connectedEndpoint = null, connectedKey = null;
        (string Endpoint, string Key, string Tool, string Arguments)? reviewed = null;
        (string Endpoint, string Key, string Tool, string Arguments) Inputs() => (endpoint.Text ?? "", key.Text ?? "", (tools.SelectedItem as MCPTool)?.Name ?? "", arguments.Text ?? "");
        void Invalidate() { reviewed = null; consent.IsChecked = false; call.IsEnabled = false; }
        endpoint.TextChanged += (_, _) => Invalidate(); key.TextChanged += (_, _) => Invalidate(); arguments.TextChanged += (_, _) => Invalidate();
        tools.SelectionChanged += (_, _) =>
        {
            Invalidate();
            if (tools.SelectedItem is MCPTool tool) { description.Text = tool.Description; schema.Text = tool.InputSchema; }
            else { description.Text = "Select a discovered tool to review its description and schema."; schema.Text = ""; }
        };
        consent.IsCheckedChanged += (_, _) =>
        {
            reviewed = operation is null && consent.IsChecked == true ? Inputs() : null;
            call.IsEnabled = reviewed is { } review && review.Endpoint == connectedEndpoint && review.Key == connectedKey && tools.SelectedItem is MCPTool;
        };
        async Task RunAsync(Func<CancellationToken, Task> action)
        {
            if (operation is not null) return;
            using var request = new CancellationTokenSource(); operation = request;
            connect.IsEnabled = next.IsEnabled = back.IsEnabled = false; call.IsEnabled = false; cancel.IsEnabled = true; cancel.IsVisible = true;
            try { await action(request.Token); }
            catch (OperationCanceledException) { status.Text = "Canceled or timed out. The server may already have received the request or performed its effects. No tool call is automatically retried."; }
            catch (System.Text.Json.JsonException) { status.Text = "Invalid JSON. Check the argument object or server response."; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { status.Text = ex.Message; }
            finally { operation = null; connect.IsEnabled = next.IsEnabled = back.IsEnabled = true; cancel.IsEnabled = false; cancel.IsVisible = false; Invalidate(); }
        }
        connect.Click += (_, _) => _ = RunAsync(async token =>
        {
            if (!Uri.TryCreate(endpoint.Text, UriKind.Absolute, out var address) || !AIConnectionClient.IsAllowedEndpoint(address))
                throw new ArgumentException("Enter an HTTPS MCP endpoint, or HTTP on localhost, without URL credentials/query/fragment.");
            var target = endpoint.Text ?? ""; var bearer = key.Text ?? "";
            reviewTools.IsEnabled = false;
            connectedEndpoint = null; connectedKey = null; tools.ItemsSource = null; result.Text = "";
            server.Text = "No server connected. Initializing the selected endpoint…";
            status.Text = "Initializing and discovering tools. No tool is being called.";
            var info = await client.InitializeAsync(address, bearer, token);
            var found = await client.ListToolsAsync(token);
            connectedEndpoint = target; connectedKey = bearer;
            reviewTools.IsEnabled = true;
            tools.ItemsSource = found;
            if (found.Count > 0) tools.SelectedIndex = 0;
            server.Text = $"{info.Name} · {info.Version} · MCP {info.ProtocolVersion} · {found.Count} tools";
            status.Text = info.HasTools ? "Connected. Select a tool, read its schema, enter arguments, and review before calling." : "This server does not advertise tool support.";
        });
        call.Click += (_, _) =>
        {
            if (operation is not null || consent.IsChecked != true || reviewed is not { } approved
                || approved != Inputs() || approved.Endpoint != connectedEndpoint || approved.Key != connectedKey || tools.SelectedItem is not MCPTool tool)
            { Invalidate(); status.Text = "The connection, tool or arguments changed. Reconnect if needed and review them again."; return; }
            _ = RunAsync(async token =>
            {
                status.Text = "Running the individually reviewed tool…";
                result.Text = await client.CallToolAsync(tool, approved.Arguments, token);
                status.Text = "Result received. It is displayed as untrusted JSON and is not applied to any document.";
            });
        };
        cancel.Click += (_, _) => operation?.Cancel(); close.Click += (_, _) => Close();
        Closed += async (_, _) =>
        {
            operation?.Cancel(); key.Text = ""; connectedKey = null; reviewed = null; arguments.Text = ""; result.Text = "";
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await client.DisconnectAsync(deadline.Token); }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException) { }
            finally { client.Dispose(); }
        };
        var compatibility = new Expander { Header = "Advanced: server compatibility and limits", HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = Body("Supported: MCP 2025-11-25 over Streamable HTTP, JSON and request-scoped SSE, bearer tokens and manual tools/list/tools/call. Not supported: stdio, OAuth sign-in, server-initiated sampling/elicitation, background event streams, resume/reconnect or the newer 2026 protocol. Limits: 200 tools, 20 pages, 1 MiB response, 32K argument characters and a 60-second deadline.") };
        var pages = new Control[]
        {
            GuideParts.Card(GuideParts.Vertical(GuideParts.Title("Configure your MCP server"), profiles.Content,
                Body("MCP connects tools from a server. Ask its owner for a Streamable HTTP endpoint supporting MCP 2025-11-25 and, if required, a bearer token."),
                Label("Server address", endpoint), Label("Bearer token (stored securely when you save; optional)", key), compatibility)),
            GuideParts.Card(GuideParts.Vertical(GuideParts.Title("Validate and save"),
                Body("Connect and discover tools checks the server address, authentication, protocol and tool listing. It does not call a tool or send document content."),
                server, Body("Once connected, choose Save connection below. The endpoint is saved on this device and any token is protected in Windows Credential Manager."))),
            GuideParts.Vertical(
                GuideParts.Card(GuideParts.Vertical(GuideParts.Title("Review a tool"), Label("Available tool", tools), Label("Tool description", description), Label("Accepted arguments (JSON schema)", schema))),
                GuideParts.Card(GuideParts.Vertical(GuideParts.Title("Try your connection"),
                    Body("Tools may change external data, send messages or incur charges. Review this tool and its exact arguments before running it once. No tools run automatically."),
                    Label("Exact arguments you authorize", arguments), consent, Label("Tool result (review before using)", result))))
        };
        var stack = new StackPanel(); foreach (var item in pages) stack.Children.Add(item);
        var steps = new ContentControl();
        var scroll = new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        void Page(int selected)
        {
            page = selected; for (var i = 0; i < pages.Length; i++) pages[i].IsVisible = i == page;
            steps.Content = GuideParts.ConnectionSteps(page, "Configure", "Validate and save", "Review tools");
            next.IsVisible = page < 2; next.Content = page == 0 ? "Continue" : "Save connection";
            back.IsVisible = page > 0; connect.IsVisible = page >= 1; reviewTools.IsVisible = page == 1; call.IsVisible = page == 2; cancel.IsVisible = operation is not null; scroll.Offset = default;
        }
        reviewTools.Click += (_, _) =>
        {
            if (operation is null && connectedEndpoint == endpoint.Text && connectedKey == key.Text) Page(2);
            else status.Text = "Connect and discover tools before reviewing a tool.";
        };
        next.Click += (_, _) =>
        {
            if (operation is not null) return;
            if (page == 0) { Page(1); return; }
            if (connectedEndpoint != endpoint.Text || connectedKey != key.Text)
            { status.Text = "Connect and discover tools successfully before saving these settings."; return; }
            if (profiles.Save()) Page(2);
        };
        back.Click += (_, _) => { if (operation is null) Page(Math.Max(0, page - 1)); };
        Content = GuideParts.Frame(GuideParts.Vertical(GuideParts.Title("MCP Servers"), steps), scroll,
            GuideParts.Vertical(status, GuideParts.Actions(back, connect, reviewTools, next, call, cancel, close)));
        var selected = selectedProfileId is not null && profiles.Select(selectedProfileId);
        Page(selected && useTools ? 1 : 0);
        if (selected && useTools) status.Text = "Saved MCP server loaded. Connect and discover tools, then choose Review tools. No request has been made.";
    }
    private static TextBox Field(string name, string text)
    { var field = new TextBox { Text = text, PlaceholderText = name, FontSize = GuideParts.Scaled(14) }; AutomationProperties.SetName(field, name); return field; }
    private static Control Label(string name, Control control) => GuideParts.Vertical(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }, control);
    private static TextBlock Body(string text) => new() { Text = text, FontSize = GuideParts.Scaled(14), LineHeight = GuideParts.Scaled(21), TextWrapping = TextWrapping.Wrap };
}
