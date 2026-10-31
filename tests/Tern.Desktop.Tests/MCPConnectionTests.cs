using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Tern.Desktop.Services;

namespace Tern.Desktop.Tests;

public sealed class MCPConnectionTests
{
    private static readonly Uri Endpoint = new("http://localhost:3000/mcp");

    [Fact]
    public async Task Handshake_session_pagination_and_exact_reviewed_call_follow_the_protocol()
    {
        var methods = new List<string>();
        using var client = new MCPConnectionClient(new Handler(async (request, token) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme); Assert.Equal("artificial-key", request.Headers.Authorization?.Parameter);
            Assert.Contains(request.Headers.Accept, a => a.MediaType == "application/json"); Assert.Contains(request.Headers.Accept, a => a.MediaType == "text/event-stream");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = body.RootElement; var method = root.GetProperty("method").GetString()!; methods.Add(method);
            if (method == "initialize")
            {
                Assert.Equal(MCPConnectionClient.SupportedProtocol, root.GetProperty("params").GetProperty("protocolVersion").GetString());
                Assert.Empty(root.GetProperty("params").GetProperty("capabilities").EnumerateObject());
                var response = Reply(root, Initialization()); response.Headers.Add("MCP-Session-Id", "test-session"); return response;
            }
            Assert.Equal("test-session", Assert.Single(request.Headers.GetValues("MCP-Session-Id")));
            Assert.Equal(MCPConnectionClient.SupportedProtocol, Assert.Single(request.Headers.GetValues("MCP-Protocol-Version")));
            if (method == "notifications/initialized") return new HttpResponseMessage(HttpStatusCode.Accepted);
            if (method == "tools/list")
            {
                var parameters = root.GetProperty("params");
                return parameters.TryGetProperty("cursor", out var cursor)
                    ? (AssertCursor(cursor), Reply(root, new { tools = new[] { Tool("second") } })).Item2
                    : Reply(root, new { tools = new[] { Tool("first") }, nextCursor = "next-page" });
            }
            Assert.Equal("tools/call", method);
            Assert.Equal("second", root.GetProperty("params").GetProperty("name").GetString());
            Assert.Equal("reviewed café 😀", root.GetProperty("params").GetProperty("arguments").GetProperty("text").GetString());
            return Reply(root, new { content = new[] { new { type = "text", text = "result" } }, isError = false });
        }));
        var server = await client.InitializeAsync(Endpoint, "artificial-key", TestContext.Current.CancellationToken);
        Assert.Equal("Test server", server.Name);
        var tools = await client.ListToolsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, tools.Count);
        Assert.Equal(new[] { "initialize", "notifications/initialized", "tools/list", "tools/list" }, methods);
        var result = await client.CallToolAsync(tools[1], "{\"text\":\"reviewed café 😀\"}", TestContext.Current.CancellationToken);
        Assert.Contains("result", result, StringComparison.Ordinal);
        Assert.Equal("tools/call", methods[^1]);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public async Task SSE_notifications_are_inert_and_matching_response_returns_without_waiting_for_EOF(string newline)
    {
        using var client = new MCPConnectionClient(new Handler(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); var root = body.RootElement;
            if (root.GetProperty("method").GetString() == "notifications/initialized") return new HttpResponseMessage(HttpStatusCode.Accepted);
            var id = root.GetProperty("id").GetInt32();
            var response = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = Initialization() });
            var bytes = Encoding.UTF8.GetBytes(("id: prime\ndata:\n\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/tools/list_changed\"}\n\ndata: " + response + "\n\n").Replace("\n", newline, StringComparison.Ordinal));
            var content = new StreamContent(new PrefixThenWaitStream(bytes)); content.Headers.ContentType = new("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));
        var server = await client.InitializeAsync(Endpoint, "", TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal("Test server", server.Name);
    }

    [Theory]
    [InlineData("2026-07-28")]
    [InlineData("2024-11-05")]
    public async Task Unsupported_protocol_is_rejected_without_sending_initialized_or_tools(string version)
    {
        var calls = 0;
        using var client = new MCPConnectionClient(new Handler(async (request, token) =>
        { calls++; using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); return Reply(body.RootElement, new { protocolVersion = version, serverInfo = new { name = "server", version = "1" }, capabilities = new { tools = new { } } }); }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.InitializeAsync(Endpoint, "", TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListToolsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Tool_calls_require_discovery_and_JSON_object_arguments()
    {
        var methods = new List<string>();
        using var client = SimpleClient(methods);
        await client.InitializeAsync(Endpoint, "", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CallToolAsync(new MCPTool("echo", "", "{}"), "{}", TestContext.Current.CancellationToken));
        var tool = Assert.Single(await client.ListToolsAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CallToolAsync(tool, "[]", TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<JsonException>(() => client.CallToolAsync(tool, "invalid", TestContext.Current.CancellationToken));
        Assert.DoesNotContain("tools/call", methods);
    }

    [Fact]
    public async Task Pagination_loop_is_bounded_and_never_calls_a_tool()
    {
        var lists = 0;
        using var client = new MCPConnectionClient(new Handler(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); var root = body.RootElement;
            var method = root.GetProperty("method").GetString();
            if (method == "notifications/initialized") return new HttpResponseMessage(HttpStatusCode.Accepted);
            if (method == "initialize") return Reply(root, Initialization());
            lists++; return Reply(root, new { tools = Array.Empty<object>(), nextCursor = "repeated" });
        }));
        await client.InitializeAsync(Endpoint, "", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListToolsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, lists);
    }

    [Fact]
    public async Task Cancellation_notifies_the_server_without_replaying_the_tool()
    {
        using var cancellation = new CancellationTokenSource(); var calls = 0; var cancelNotifications = 0;
        using var client = new MCPConnectionClient(new Handler(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); var root = body.RootElement; var method = root.GetProperty("method").GetString();
            if (method == "notifications/initialized") return new HttpResponseMessage(HttpStatusCode.Accepted);
            if (method == "notifications/cancelled") { cancelNotifications++; Assert.True(root.GetProperty("params").GetProperty("requestId").GetInt32() > 0); return new HttpResponseMessage(HttpStatusCode.Accepted); }
            if (method == "initialize") return Reply(root, Initialization());
            if (method == "tools/list") return Reply(root, new { tools = new[] { Tool("echo") } });
            calls++; cancellation.Cancel(); await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException("Unreachable");
        }));
        await client.InitializeAsync(Endpoint, "", TestContext.Current.CancellationToken); var tool = Assert.Single(await client.ListToolsAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CallToolAsync(tool, "{}", cancellation.Token));
        Assert.Equal(1, calls); Assert.Equal(1, cancelNotifications);
    }

    [Fact]
    public async Task HTTP_errors_do_not_echo_secret_response_or_transport_details()
    {
        using var client = new MCPConnectionClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("secret remote body") })));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.InitializeAsync(Endpoint, "secret-key", TestContext.Current.CancellationToken));
        Assert.Contains("401", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_stream_response_is_refused_with_bounded_consumption()
    {
        using var client = new MCPConnectionClient(new Handler((_, _) =>
        {
            var content = new StreamContent(new PrefixThenWaitStream(new byte[MCPConnectionClient.MaximumResponseBytes + 1]));
            content.Headers.ContentType = new("application/json"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.InitializeAsync(Endpoint, "", TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
    }

    private static int AssertCursor(JsonElement cursor) { Assert.Equal("next-page", cursor.GetString()); return 0; }
    private static object Initialization() => new { protocolVersion = MCPConnectionClient.SupportedProtocol, serverInfo = new { name = "Test server", version = "1" }, capabilities = new { tools = new { } } };
    private static object Tool(string name) => new { name, description = "A test-only echo tool", inputSchema = new { type = "object", properties = new { text = new { type = "string" } } } };
    private static HttpResponseMessage Reply(JsonElement request, object result) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = request.GetProperty("id").GetInt32(), result }), Encoding.UTF8, "application/json") };
    private static MCPConnectionClient SimpleClient(List<string> methods) => new(new Handler(async (request, token) =>
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); var root = body.RootElement; var method = root.GetProperty("method").GetString()!; methods.Add(method);
        return method == "notifications/initialized" ? new HttpResponseMessage(HttpStatusCode.Accepted)
            : Reply(root, method == "initialize" ? Initialization() : new { tools = new[] { Tool("echo") } });
    }));
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class PrefixThenWaitStream(byte[] prefix) : Stream
    {
        private int _offset;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset == prefix.Length) { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
            var count = Math.Min(buffer.Length, prefix.Length - _offset); prefix.AsMemory(_offset, count).CopyTo(buffer); _offset += count; return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

