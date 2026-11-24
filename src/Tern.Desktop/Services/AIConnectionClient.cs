using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tern.App;

namespace Tern.Desktop.Services;

/// <summary>Explicit, one-shot OpenAI-compatible chat completion. No persistence or logging.</summary>
public sealed class AIConnectionClient : IDisposable
{
    public Func<bool>? NetworkAllowed { get; set; }
    private void EnsureNetworkAllowed()
    { if (NetworkAllowed?.Invoke() == false) throw new InvalidOperationException("Offline mode blocks AI requests. Turn Offline off to connect."); }
    private readonly HttpClient _http;
    private const int MaximumResponseBytes = 1_048_576;

    private static readonly Lazy<HttpMessageHandler> SharedTransport = new(() => new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = true, Credentials = null,
        MaxConnectionsPerServer = 4,
    });

    public AIConnectionClient() : this(SharedTransport.Value, false) { }

    internal AIConnectionClient(HttpMessageHandler handler) : this(handler, true) { }

    private AIConnectionClient(HttpMessageHandler handler, bool disposeHandler)
    {
        _http = new HttpClient(handler, disposeHandler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static bool IsAllowedEndpoint(Uri endpoint) => endpoint.IsAbsoluteUri && string.IsNullOrEmpty(endpoint.UserInfo)
        && string.IsNullOrEmpty(endpoint.Query) && string.IsNullOrEmpty(endpoint.Fragment)
        && (endpoint.Scheme == Uri.UriSchemeHttps || endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback);

    public Task<string> SendAsync(Uri endpoint, string model, string apiKey, string reviewedMessage, CancellationToken cancellationToken) =>
        SendAsync(endpoint, model, apiKey, reviewedMessage, null, cancellationToken);

    public async Task<string> SendAsync(Uri endpoint, string model, string apiKey, string reviewedMessage, IAIProviderAdapter? provider, CancellationToken cancellationToken, string? workspaceId = null, int? timeoutSeconds = null)
    {
        if (string.IsNullOrWhiteSpace(reviewedMessage)) throw new ArgumentException("Enter a message to send.", nameof(reviewedMessage));
        return await SendAsync(endpoint, model, apiKey, AIRequest.ConnectionTest(reviewedMessage), provider, cancellationToken, workspaceId, timeoutSeconds).ConfigureAwait(false);
    }

    public Task<string> SendAsync(Uri endpoint, string model, string apiKey, AIRequest request, CancellationToken cancellationToken) =>
        SendAsync(endpoint, model, apiKey, request, null, cancellationToken);

    public async Task<string> SendAsync(Uri endpoint, string model, string apiKey, AIRequest request, IAIProviderAdapter? provider, CancellationToken cancellationToken, string? workspaceId = null, int? timeoutSeconds = null)
    {
        try
        {
            return await SendCoreAsync(endpoint, model, apiKey, request, provider, workspaceId, timeoutSeconds, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new InvalidOperationException("The request timed out. The provider may already have received it; check before sending again."); }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("Connection failed. Check the endpoint, TLS certificate, local server and network connection.");
        }
        catch (IOException)
        {
            throw new InvalidOperationException("The response could not be read. Check the service connection and try again.");
        }
    }

    private async Task<string> SendCoreAsync(Uri endpoint, string model, string apiKey, AIRequest request, IAIProviderAdapter? provider, string? workspaceId, int? timeoutSeconds, CancellationToken cancellationToken)
    {
        if (!IsAllowedEndpoint(endpoint)) throw new ArgumentException("Use HTTPS, or HTTP on localhost only. Credentials, query strings and fragments cannot be part of the address.", nameof(endpoint));
        if (string.IsNullOrWhiteSpace(model) || model.Length > 256) throw new ArgumentException("Enter a model name of up to 256 characters.", nameof(model));
        if (apiKey.Length > 4096 || apiKey.Any(c => c < 33 || c > 126)) throw new ArgumentException("The API key must be at most 4,096 printable characters without spaces or line breaks.", nameof(apiKey));
        ArgumentNullException.ThrowIfNull(request);
        if (!request.HasUserContent) throw new ArgumentException("Enter a message to send.", nameof(request));
        if (request.CharacterCount > AIConnectionRules.MaximumSendCharacters) throw new ArgumentException("Enter a request of up to 1,000,000 characters, including Tern's instructions.", nameof(request));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestTimeout(endpoint, timeoutSeconds));
        cancellationToken = deadline.Token;
        var adapter = provider ?? AIProviders.All[0];
        var instructionRole = AIProviders.InstructionRole(adapter, endpoint, model);
        var token = deadline.Token;
        async Task<JsonDocument> PostAsync(string body)
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
            AddAuthentication(httpRequest, adapter.Id, apiKey, workspaceId);
            httpRequest.Content = new StringContent(body, Encoding.UTF8, "application/json");
            EnsureNetworkAllowed();
            using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            return await ReadJsonAsync(response, apiKey, token).ConfigureAwait(false);
        }
        var claude = adapter.Id == "anthropic-messages";
        var modelKey = endpoint.AbsoluteUri + "\n" + model;
        JsonDocument document;
        try
        {
            document = await PostAsync(claude && ClaudeReplyLimits.TryGetValue(modelKey, out var known)
                ? AIProviders.ClaudeRequest(model, request, known) : adapter.CreateRequest(model, request, instructionRole)).ConfigureAwait(false);
        }
        catch (AIServiceException error) when (claude && error.ReplyTokenLimit is >= 256)
        {
            // An older model has a lower reply cap. This says nothing about the size of the text that was sent.
            ClaudeReplyLimits[modelKey] = error.ReplyTokenLimit.Value;
            document = await PostAsync(AIProviders.ClaudeRequest(model, request, error.ReplyTokenLimit.Value)).ConfigureAwait(false);
        }
        using var json = document;
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException(UnreadableReply);
        if (AIProviders.PromptWasCut(adapter, endpoint, json.RootElement, request.CharacterCount))
            throw new AIServiceException(AIServiceException.TooMuchTextMessage, "The local model read less text than it was sent.", 413, sizeRejected: true);
        return adapter.ReadResponse(json.RootElement);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> ClaudeReplyLimits = new(StringComparer.Ordinal);

    // A long reply can take minutes on any service, so remote and local connections wait the same time.
    private const int DefaultRequestSeconds = 300;

    internal static TimeSpan RequestTimeout(Uri endpoint, int? seconds)
    {
        if (seconds is < 1 or > 3600) throw new ArgumentOutOfRangeException(nameof(seconds));
        return TimeSpan.FromSeconds(seconds ?? DefaultRequestSeconds);
    }

    public const string ValidationMessage = "Reply with exactly: OK.";
    private const string UnreadableReply = "The AI service sent a reply Tern could not read. Try again, or check this connection in Settings. Your note has not changed.";

    /// <summary>Explicit live generation probe. Never includes the user's message or document.</summary>
    public async Task<AIConnectionCheck> ValidateAsync(AIConnectionInputs input, CancellationToken cancellationToken)
    {
        var errors = AIConnectionRules.Errors(input);
        if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors.Values), nameof(input));
        var answer = await SendAsync(new Uri(input.Endpoint), input.Model, input.Credential, ValidationMessage,
            AIProviders.Get(input.Protocol), cancellationToken, input.WorkspaceId, input.TimeoutSeconds).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(answer)) throw new InvalidOperationException("The service returned an empty test response. Generation could not be verified.");
        return new AIConnectionCheck(DateTimeOffset.Now, "Generation endpoint responded", input.Credential.Length == 0
            ? "No credential required by this request" : "Supplied credential accepted for generation",
            "Configured model generated text", "Passed - live test response received", []);
    }

    public async Task<AIConnectionCheck> CheckAsync(AIConnectionInputs input, CancellationToken cancellationToken)
    {
        var errors = AIConnectionRules.Errors(input);
        if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors.Values), nameof(input));
        var modelsEndpoint = AIConnectionRules.ModelListEndpoint(input);
        if (modelsEndpoint is null)
            return new AIConnectionCheck(DateTimeOffset.Now, "Not tested — no documented model-list URL configured", "Not tested",
                "Model not verified; manual IDs are allowed", "Not tested; no generation request was made", []);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestTimeout(new Uri(input.Endpoint), input.TimeoutSeconds));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, modelsEndpoint);
            AddAuthentication(request, input.Protocol, input.Credential, input.WorkspaceId);
            EnsureNetworkAllowed();
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            using var json = await ReadJsonAsync(response, input.Credential, deadline.Token).ConfigureAwait(false);
            var root = json.RootElement;
            var native = root.TryGetProperty("models", out var rows);
            if (!native && !root.TryGetProperty("data", out rows) || rows.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("The model list could not be read because its format is unsupported. Model and generation remain unverified.");
            var models = rows.EnumerateArray().Select(item =>
                item.TryGetProperty(native ? "name" : "id", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null)
                .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!).Distinct(StringComparer.Ordinal).ToArray();
            var listed = models.Contains(input.Model, StringComparer.Ordinal)
                || (input.ServiceId == "ollama" && !input.Model.Contains(':', StringComparison.Ordinal) && models.Contains(input.Model + ":latest", StringComparer.Ordinal));
            var localMissing = input.ServiceId == "ollama" && native && !listed;
            return new AIConnectionCheck(DateTimeOffset.Now, "Model-list endpoint responded",
                input.Credential.Length == 0 ? "No credential sent" : "Model listing succeeded with supplied credential; generation access not tested",
                listed ? "Model is listed by this service" : localMissing ? "Model is not installed on this Ollama server" : "Model not verified — not in this possibly incomplete list; manual IDs are allowed",
                "Not tested; listing models does not prove generation compatibility or available credits", models, localMissing);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new InvalidOperationException("The connection check timed out. No generation request was made."); }
        catch (HttpRequestException) { throw new InvalidOperationException("Cannot reach the service. Check its address, network, local server and TLS certificate."); }
        catch (IOException) { throw new InvalidOperationException("The model list could not be read. No generation request was made."); }
    }

    private static void AddAuthentication(HttpRequestMessage request, string protocol, string apiKey, string? workspaceId)
    {
        if (protocol == "anthropic-messages")
        {
            request.Headers.Add("anthropic-version", "2023-06-01");
            if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Add("x-api-key", apiKey);
            if (!string.IsNullOrWhiteSpace(workspaceId)) request.Headers.Add("anthropic-workspace-id", workspaceId);
        }
        else if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, string apiKey, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidOperationException("The service response exceeds the 1 MiB limit.");
        using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (bytes.Length + count > MaximumResponseBytes) throw new InvalidOperationException("The service response exceeds the 1 MiB limit.");
            bytes.Write(buffer, 0, count);
        }
        if (!response.IsSuccessStatusCode)
            throw ServiceError(response, Encoding.UTF8.GetString(bytes.ToArray()), apiKey);
        try { return JsonDocument.Parse(bytes.ToArray()); }
        catch (JsonException) { throw new InvalidOperationException(UnreadableReply); }
    }

    internal static AIServiceException ServiceError(HttpResponseMessage response, string body, string secret)
    {
        var requestId = new[] { "x-request-id", "request-id", "anthropic-request-id" }
            .Select(name => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var retry = response.Headers.RetryAfter;
        var retryAt = retry?.Date ?? (retry?.Delta is { } delay ? DateTimeOffset.UtcNow + delay : (DateTimeOffset?)null);
        return AIServiceException.From((int)response.StatusCode, body, secret, requestId, retryAt);
    }

    public void Dispose() => _http.Dispose();
}
