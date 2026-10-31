using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Tern.Desktop.Services;

/// <summary>Explicit, one-shot OpenAI-compatible chat completion. No persistence or logging.</summary>
public sealed class AIConnectionClient : IDisposable
{
    private readonly HttpClient _http;
    public const int MaximumMessageCharacters = 32_768;
    private const int MaximumResponseBytes = 1_048_576;

    public AIConnectionClient() : this(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false, Credentials = null }) { }

    internal AIConnectionClient(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public static bool IsAllowedEndpoint(Uri endpoint) => endpoint.IsAbsoluteUri && string.IsNullOrEmpty(endpoint.UserInfo)
        && string.IsNullOrEmpty(endpoint.Query) && string.IsNullOrEmpty(endpoint.Fragment)
        && (endpoint.Scheme == Uri.UriSchemeHttps || endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback);

    public Task<string> SendAsync(Uri endpoint, string model, string apiKey, string reviewedMessage, CancellationToken cancellationToken) =>
        SendAsync(endpoint, model, apiKey, reviewedMessage, null, cancellationToken);

    public async Task<string> SendAsync(Uri endpoint, string model, string apiKey, string reviewedMessage, IAIProviderAdapter? provider, CancellationToken cancellationToken, string? workspaceId = null)
    {
        try
        {
            return await SendCoreAsync(endpoint, model, apiKey, reviewedMessage, provider, workspaceId, cancellationToken).ConfigureAwait(false);
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

    private async Task<string> SendCoreAsync(Uri endpoint, string model, string apiKey, string reviewedMessage, IAIProviderAdapter? provider, string? workspaceId, CancellationToken cancellationToken)
    {
        if (!IsAllowedEndpoint(endpoint)) throw new ArgumentException("Use HTTPS, or HTTP on localhost only. Credentials, query strings and fragments cannot be part of the address.", nameof(endpoint));
        if (string.IsNullOrWhiteSpace(model) || model.Length > 256) throw new ArgumentException("Enter a model name of up to 256 characters.", nameof(model));
        if (apiKey.Length > 4096 || apiKey.Any(c => c < 33 || c > 126)) throw new ArgumentException("The API key must be at most 4,096 printable characters without spaces or line breaks.", nameof(apiKey));
        if (string.IsNullOrWhiteSpace(reviewedMessage) || reviewedMessage.Length > MaximumMessageCharacters) throw new ArgumentException("Enter a message of up to 32,768 characters.", nameof(reviewedMessage));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        cancellationToken = deadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        AddAuthentication(request, provider?.Id ?? "chat-completions", apiKey, workspaceId);
        request.Content = new StringContent((provider ?? AIProviders.All[0]).CreateRequest(model, reviewedMessage), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, apiKey, cancellationToken).ConfigureAwait(false);
        return (provider ?? AIProviders.All[0]).ReadResponse(json.RootElement);
    }

    public async Task<AIConnectionCheck> CheckAsync(AIConnectionInputs input, CancellationToken cancellationToken)
    {
        var errors = AIConnectionRules.Errors(input);
        if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors.Values), nameof(input));
        var modelsEndpoint = AIConnectionRules.ModelListEndpoint(input);
        if (modelsEndpoint is null)
            return new AIConnectionCheck(DateTimeOffset.Now, "Not tested — no documented model-list URL configured", "Not tested",
                "Model not verified; manual IDs are allowed", "Not tested; no generation request was made", []);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, modelsEndpoint);
            AddAuthentication(request, input.Protocol, input.Credential, input.WorkspaceId);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            using var json = await ReadJsonAsync(response, input.Credential, cancellationToken).ConfigureAwait(false);
            var root = json.RootElement;
            var native = root.TryGetProperty("models", out var rows);
            if (!native && !root.TryGetProperty("data", out rows) || rows.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("The server responded, but its model list format is unsupported. Model and generation remain unverified.");
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
        return JsonDocument.Parse(bytes.ToArray());
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
