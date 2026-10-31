using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Tern.Desktop.Services;

public sealed record MCPServerInfo(string Name, string Version, string ProtocolVersion, bool HasTools);
public sealed record MCPTool(string Name, string Description, string InputSchema)
{
    public override string ToString() => Name;
}

/// <summary>Explicit MCP 2025-11-25 Streamable HTTP client. No automatic tool calls, document access, or logging.</summary>
public sealed class MCPConnectionClient : IDisposable
{
    public const string SupportedProtocol = "2025-11-25";
    public const int MaximumResponseBytes = 1_048_576;
    public const int MaximumArgumentCharacters = 32_768;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Uri? _endpoint;
    private string _key = "";
    private string? _session;
    private int _nextId;
    private bool _initialized;
    private bool _hasTools;
    private readonly HashSet<string> _knownTools = new(StringComparer.Ordinal);
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public MCPConnectionClient() : this(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false, Credentials = null }) { }
    internal MCPConnectionClient(HttpMessageHandler handler) => _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<MCPServerInfo> InitializeAsync(Uri endpoint, string key, CancellationToken cancellationToken = default)
    {
        if (!AIConnectionClient.IsAllowedEndpoint(endpoint)) throw new ArgumentException("Use HTTPS, or HTTP on localhost only, without URL credentials, query or fragment.", nameof(endpoint));
        if (key.Length > 4096 || key.Any(c => c < 33 || c > 126)) throw new ArgumentException("Use a printable bearer key without spaces or line breaks, up to 4,096 characters.", nameof(key));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _endpoint = endpoint; _key = key; _session = null; _initialized = false; _knownTools.Clear();
            using var deadline = Deadline(cancellationToken);
            var result = await RequestAsync("initialize", new
            { protocolVersion = SupportedProtocol, capabilities = new { }, clientInfo = new { name = "Tern", version = "1.0.1" } }, deadline.Token, initializing: true).ConfigureAwait(false);
            if (String(result, "protocolVersion", 32) != SupportedProtocol)
                throw new InvalidOperationException("This server did not negotiate MCP 2025-11-25. Use a server with that compatibility mode; the newer 2026 protocol is not supported in this build.");
            if (!result.TryGetProperty("serverInfo", out var info) || info.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("The server initialization response has no server information.");
            _hasTools = result.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Object
                && capabilities.TryGetProperty("tools", out var toolCapability) && toolCapability.ValueKind == JsonValueKind.Object;
            await NotifyAsync("notifications/initialized", new { }, deadline.Token).ConfigureAwait(false);
            _initialized = true;
            return new MCPServerInfo(String(info, "name", 256), String(info, "version", 256), SupportedProtocol, _hasTools);
        }
        catch { _initialized = false; _key = ""; _session = null; throw; }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<MCPTool>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            _knownTools.Clear();
            if (!_hasTools) return [];
            using var deadline = Deadline(cancellationToken);
            var tools = new List<MCPTool>(); var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            for (var page = 0; page < 20; page++)
            {
                var result = await RequestAsync("tools/list", cursor is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["cursor"] = cursor }, deadline.Token).ConfigureAwait(false);
                if (!result.TryGetProperty("tools", out var array) || array.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("The server returned an invalid tools list.");
                foreach (var tool in array.EnumerateArray())
                {
                    var name = String(tool, "name", 128);
                    if (name.Length == 0 || tools.Any(t => t.Name == name)) throw new InvalidOperationException("The server returned a missing or duplicate tool name.");
                    if (!tool.TryGetProperty("inputSchema", out var schema) || schema.ValueKind != JsonValueKind.Object)
                        throw new InvalidOperationException("A tool has no JSON object input schema.");
                    tools.Add(new MCPTool(name, OptionalString(tool, "description", 8192), schema.GetRawText()));
                    if (tools.Count > 200) throw new InvalidOperationException("This server exceeds the 200-tool discovery limit. Use a smaller server or filtered endpoint.");
                }
                cursor = OptionalString(result, "nextCursor", 4096);
                if (cursor.Length == 0)
                {
                    _knownTools.Clear(); foreach (var tool in tools) _knownTools.Add(tool.Name);
                    return tools;
                }
                if (!cursors.Add(cursor)) throw new InvalidOperationException("The server repeated a pagination cursor; discovery stopped safely.");
            }
            throw new InvalidOperationException("Tool discovery exceeded its 20-page limit.");
        }
        finally { _gate.Release(); }
    }

    /// <summary>The UI must review the exact tool name, endpoint, and arguments before calling this method.</summary>
    public async Task<string> CallToolAsync(MCPTool tool, string reviewedArgumentsJson, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (reviewedArgumentsJson.Length > MaximumArgumentCharacters) throw new ArgumentException("Tool arguments exceed 32,768 characters.", nameof(reviewedArgumentsJson));
        using var arguments = JsonDocument.Parse(reviewedArgumentsJson);
        if (arguments.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Tool arguments must be a JSON object.", nameof(reviewedArgumentsJson));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            if (!_knownTools.Contains(tool.Name)) throw new InvalidOperationException("Discover and select this tool on the current server before calling it.");
            using var deadline = Deadline(cancellationToken);
            var result = await RequestAsync("tools/call", new { name = tool.Name, arguments = arguments.RootElement }, deadline.Token).ConfigureAwait(false);
            return result.GetRawText();
        }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_endpoint is not null && _initialized && _session is not null)
            {
                using var deadline = Deadline(cancellationToken);
                using var request = NewRequest(HttpMethod.Delete);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                // A server may decline explicit session termination with 405; local secrets are cleared regardless.
            }
        }
        catch (HttpRequestException) { throw new InvalidOperationException("The server could not confirm session closure. Local connection data was cleared."); }
        catch (IOException) { throw new InvalidOperationException("The session closure response could not be read. Local connection data was cleared."); }
        finally { _initialized = false; _knownTools.Clear(); _key = ""; _session = null; _endpoint = null; _gate.Release(); }
    }

    private void EnsureInitialized()
    { if (!_initialized) throw new InvalidOperationException("Connect and initialize this MCP server first."); }
    private static CancellationTokenSource Deadline(CancellationToken token)
    { var source = CancellationTokenSource.CreateLinkedTokenSource(token); source.CancelAfter(TimeSpan.FromSeconds(60)); return source; }

    private HttpRequestMessage NewRequest(HttpMethod method)
    {
        var request = new HttpRequestMessage(method, _endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (_key.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        if (_session is not null) request.Headers.Add("MCP-Session-Id", _session);
        request.Headers.Add("MCP-Protocol-Version", SupportedProtocol);
        return request;
    }

    private async Task NotifyAsync(string method, object parameters, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Post);
        request.Content = JsonContent(new { jsonrpc = "2.0", method, @params = parameters });
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        if ((int)response.StatusCode != 202) throw new InvalidOperationException("The server did not acknowledge the MCP notification with HTTP 202.");
    }

    private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct, bool initializing = false)
    {
        var id = ++_nextId;
        try
        {
            using var request = NewRequest(HttpMethod.Post);
            request.Content = JsonContent(new { jsonrpc = "2.0", id, method, @params = parameters });
            using var response = await SendAsync(request, ct).ConfigureAwait(false);
            if (initializing && response.Headers.TryGetValues("MCP-Session-Id", out var sessions))
            {
                var session = sessions.Single();
                if (session.Length is 0 or > 4096 || session.Any(c => c < 33 || c > 126)) throw new InvalidOperationException("The server supplied an invalid session identifier.");
                _session = session;
            }
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not ("application/json" or "text/event-stream")) throw new InvalidOperationException("The server must return JSON or an SSE response stream.");
            using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return mediaType == "text/event-stream"
                ? await ReadEventResponseAsync(input, id, ct).ConfigureAwait(false)
                : ParseResponse(await ReadBoundedAsync(input, ct).ConfigureAwait(false), id) ?? throw new InvalidOperationException("The server returned a response for another request.");
        }
        catch (HttpRequestException) { throw new InvalidOperationException("MCP connection failed. Check the endpoint, TLS certificate and server availability."); }
        catch (IOException) { throw new InvalidOperationException("The MCP response could not be read. Reconnect before retrying; tool effects may already have occurred."); }
        catch (OperationCanceledException)
        {
            // The older compatibility protocol uses an explicit cancellation notification.
            // Its acknowledgement cannot guarantee that a remote effect was stopped.
            if (_initialized && method != "initialize")
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await NotifyAsync("notifications/cancelled", new { requestId = id, reason = "The client stopped waiting." }, stop.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or OperationCanceledException) { }
            }
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode; response.Dispose();
            if (status == 404) { _initialized = false; _knownTools.Clear(); throw new InvalidOperationException("The MCP endpoint or session expired. Reconnect to start a new session; a tool call is never automatically retried."); }
            throw new InvalidOperationException($"MCP server returned HTTP {status}. Check its endpoint and authentication. Redirects are not followed; OAuth is not supported.");
        }
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) { response.Dispose(); throw new InvalidOperationException("The MCP response exceeds 1 MiB."); }
        return response;
    }

    private static StringContent JsonContent(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    private static string String(JsonElement value, string property, int limit)
    {
        var result = OptionalString(value, property, limit);
        return result.Length == 0 ? throw new InvalidOperationException("The MCP server response is missing required information.") : result;
    }
    private static string OptionalString(JsonElement value, string property, int limit)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var field)) return "";
        if (field.ValueKind != JsonValueKind.String) throw new InvalidOperationException("The MCP server returned an invalid text field.");
        var text = field.GetString() ?? "";
        if (text.Length > limit) throw new InvalidOperationException("An MCP metadata field exceeds its display limit.");
        return text;
    }
    private static JsonElement? ParseResponse(byte[] bytes, int id)
    {
        using var message = JsonDocument.Parse(bytes);
        var root = message.RootElement;
        if (root.ValueKind != JsonValueKind.Object || OptionalString(root, "jsonrpc", 8) != "2.0") throw new InvalidOperationException("The MCP server did not return JSON-RPC 2.0.");
        if (root.TryGetProperty("method", out _))
        {
            if (root.TryGetProperty("id", out _)) throw new InvalidOperationException("This client does not support server-initiated requests, sampling, roots, or elicitation.");
            return null; // Untrusted server notifications never trigger actions.
        }
        if (!root.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.Number || !responseId.TryGetInt32(out var number) || number != id) return null;
        if (root.TryGetProperty("error", out _)) throw new InvalidOperationException("The MCP request was rejected by the server. Check the selected tool and arguments; remote diagnostic text is not copied into errors.");
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("The MCP response has no result object.");
        return result.Clone();
    }
    private static async Task<byte[]> ReadBoundedAsync(Stream input, CancellationToken ct)
    {
        using var output = new MemoryStream(); var buffer = new byte[8192];
        while (true)
        {
            var count = await input.ReadAsync(buffer, ct).ConfigureAwait(false); if (count == 0) return output.ToArray();
            if (output.Length + count > MaximumResponseBytes) throw new InvalidOperationException("The MCP response exceeds 1 MiB.");
            output.Write(buffer, 0, count);
        }
    }
    private static async Task<JsonElement> ReadEventResponseAsync(Stream input, int id, CancellationToken ct)
    {
        var buffer = new byte[8192]; var total = 0; var afterCarriageReturn = false;
        using var line = new MemoryStream(); using var data = new MemoryStream();
        while (true)
        {
            var count = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (count == 0) throw new InvalidOperationException("The SSE stream ended before its matching response. Reconnect; no automatic replay is performed.");
            total += count;
            if (total > MaximumResponseBytes) throw new InvalidOperationException("The MCP event stream exceeds 1 MiB.");
            for (var i = 0; i < count; i++)
            {
                if (afterCarriageReturn && buffer[i] == 10) { afterCarriageReturn = false; continue; }
                afterCarriageReturn = buffer[i] == 13;
                if (buffer[i] is not (10 or 13)) { line.WriteByte(buffer[i]); continue; }
                var text = StrictUtf8.GetString(line.ToArray()); line.SetLength(0);
                if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
                if (text.Length == 0)
                {
                    if (data.Length == 0) continue;
                    var result = ParseResponse(data.ToArray(), id); data.SetLength(0);
                    if (result is { } found) return found;
                }
                else if (text.StartsWith("data:", StringComparison.Ordinal))
                {
                    var value = text.AsSpan(5); if (value.Length > 0 && value[0] == ' ') value = value[1..];
                    if (value.Length == 0) continue;
                    if (data.Length > 0) data.WriteByte(10);
                    var encoded = Encoding.UTF8.GetBytes(value.ToString()); data.Write(encoded);
                }
            }
        }
    }
    public void Dispose() { _key = ""; _session = null; _initialized = false; _http.Dispose(); }
}
