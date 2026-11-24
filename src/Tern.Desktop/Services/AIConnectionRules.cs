namespace Tern.Desktop.Services;

public sealed record AIServiceDefinition(string Id, string Name, string Protocol, string Endpoint, string? ModelsEndpoint,
    bool RequiresCredential, string Prerequisites, string Documentation);

public static class AIServices
{
    public static IReadOnlyList<AIServiceDefinition> All { get; } =
    [
        new("openai", "OpenAI API", "chat-completions", "https://api.openai.com/v1/chat/completions", "https://api.openai.com/v1/models", true,
            "Use an API key and a model available to your API account. API usage and billing are managed separately from signing into the ChatGPT website.", "https://developers.openai.com/api/docs/quickstart"),
        new("anthropic", "Anthropic API (Claude)", "anthropic-messages", "https://api.anthropic.com/v1/messages", "https://api.anthropic.com/v1/models", true,
            "Use an Anthropic API key and an available Claude model. This connection uses the Messages API and the supported 2023-06-01 API version.", "https://platform.claude.com/docs/en/api/overview"),
        new("ollama", "Ollama (local)", "ollama-chat", "http://localhost:11434/api/chat", "http://localhost:11434/api/tags", false,
            "Start your local Ollama server and install a model. Local Ollama normally needs no API key. Tern does not install or start models.", "https://docs.ollama.com/quickstart"),
        new("deepseek", "DeepSeek API", "chat-completions", "https://api.deepseek.com/chat/completions", "https://api.deepseek.com/models", true,
            "Use a DeepSeek API key and an available model, such as deepseek-flash or deepseek-v4-pro. API usage is billed by DeepSeek.", "https://api-docs.deepseek.com/"),
        new("compatible", "Custom AI Connection", "chat-completions", "", null, false,
            "Connect your own AI service. You need: 1. its full API endpoint URL, 2. its protocol, 3. the exact model ID, and 4. an API key if it requires one. Bearer tokens, Anthropic API keys and unauthenticated local servers are supported; other authentication flows need their own integration. Validate connection sends a short test message so you can confirm it works before saving.", "https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create"),
    ];

    public static AIServiceDefinition Get(string id) => All.FirstOrDefault(s => s.Id == id)
        ?? throw new ArgumentException("Choose a supported AI service.", nameof(id));

    public static string ServiceFor(AIConnectionProfile profile) => profile.ServiceId ?? (profile.Provider switch
    {
        "anthropic-messages" when profile.Endpoint == Get("anthropic").Endpoint => "anthropic",
        "ollama-chat" => "ollama",
        "chat-completions" when profile.Endpoint == Get("openai").Endpoint => "openai",
        "chat-completions" when profile.Endpoint == Get("deepseek").Endpoint => "deepseek",
        _ => "compatible",
    });

    public static string DefaultEndpoint(string serviceId, string protocol) => serviceId == "ollama" && protocol == "chat-completions"
        ? "http://localhost:11434/v1/chat/completions" : Get(serviceId).Endpoint;
}

public sealed record AIConnectionInputs(string ServiceId, string Protocol, string Endpoint, string Model, string Credential,
    string DiscoveryEndpoint = "", string WorkspaceId = "", int? TimeoutSeconds = null)
{
    public override string ToString() => $"{ServiceId} · {Protocol} · {Endpoint} · {Model} · credential hidden";
}

public static class AIConnectionRules
{
    public const int DefaultSendCharacters = 256_000;
    public const int MinimumSendCharacters = 8_000;
    public const int MaximumSendCharacters = 1_000_000;
    public const string SendCharactersProblem = "Enter a number from 8,000 to 1,000,000.";
    private static readonly System.Text.RegularExpressions.Regex SendCharactersText = new(@"^(\d{1,3}(,\d{3})+|\d+)$",
        System.Text.RegularExpressions.RegexOptions.ECMAScript | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static bool IsValidSendCharacters(int? value) => value is >= MinimumSendCharacters and <= MaximumSendCharacters;
    public static int NormalizeSendCharacters(int? value) => IsValidSendCharacters(value) ? value!.Value : DefaultSendCharacters;
    public static string FormatSendCharacters(int value) => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Reads a whole number, with or without thousands commas, and accepts it only inside the allowed range.</summary>
    public static bool TryParseSendCharacters(string? text, out int value)
    {
        value = 0;
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length is 0 or > 12 || !SendCharactersText.IsMatch(trimmed)) return false;
        return int.TryParse(trimmed.Replace(",", "", StringComparison.Ordinal), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out value) && IsValidSendCharacters(value);
    }

    /// <summary>True when a saved file has a connection whose value is missing or unusable, so it must be written back once.</summary>
    internal static bool NeedsSendCharactersUpdate(byte[] json, Func<System.Text.Json.JsonElement, bool> hasLimit)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return false;
        return document.RootElement.EnumerateArray().Where(hasLimit).Any(item =>
            !item.TryGetProperty("SendCharacters", out var saved) || saved.ValueKind != System.Text.Json.JsonValueKind.Number
            || !saved.TryGetInt32(out var number) || !IsValidSendCharacters(number));
    }

    public static bool SameOrigin(string first, string second) => Uri.TryCreate(first, UriKind.Absolute, out var a)
        && Uri.TryCreate(second, UriKind.Absolute, out var b) && a.Scheme == b.Scheme
        && a.Host.Equals(b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    public static IReadOnlyDictionary<string, string> Errors(AIConnectionInputs input)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var service = AIServices.Get(input.ServiceId);
        if (input.TimeoutSeconds is < 1 or > 3600) errors["timeout"] = "Choose a timeout from 1 to 3,600 seconds, or use the service default.";
        if (string.IsNullOrWhiteSpace(input.Model) || input.Model.Length > 256)
            errors["model"] = "Enter the model ID supplied by your service (up to 256 characters). A name alone is not proof the model exists.";
        if (!Uri.TryCreate(input.Endpoint, UriKind.Absolute, out var endpoint) || !AIConnectionClient.IsAllowedEndpoint(endpoint))
            errors["endpoint"] = "Enter a full HTTPS API endpoint, or HTTP on localhost. Do not include credentials, a query string or a fragment in the URL.";
        else
        {
            var path = endpoint.AbsolutePath.TrimEnd('/');
            if ((path == "/api/chat" && input.Protocol != "ollama-chat")
                || (path.EndsWith("/chat/completions", StringComparison.Ordinal) && input.Protocol != "chat-completions")
                || (endpoint.Host == "api.openai.com" && path == "/v1/responses")
                || (endpoint.Host == "api.anthropic.com" && path == "/v1/messages" && input.Protocol != "anthropic-messages"))
                errors["endpoint"] = "This endpoint does not match the selected protocol. Responses, Chat Completions, Anthropic Messages and native Ollama need different adapters.";
        }
        if (!AIProviders.All.Any(p => p.Id == input.Protocol)
            || (service.Id is "openai" or "anthropic" or "deepseek" && input.Protocol != service.Protocol)
            || (service.Id == "ollama" && input.Protocol is not ("ollama-chat" or "chat-completions")))
            errors["protocol"] = "Choose a protocol implemented for this service.";
        if (service.RequiresCredential && string.IsNullOrWhiteSpace(input.Credential))
            errors["credential"] = "An API key is required for this service. Enter a key or keep the stored credential.";
        else if (input.Credential.Length > 4096 || input.Credential.Any(c => c < 33 || c > 126))
            errors["credential"] = "The API key contains unsupported whitespace or control characters, or exceeds 4,096 characters.";
        if (input.WorkspaceId.Length > 256 || input.WorkspaceId.Any(c => c < 33 || c > 126))
            errors["workspace"] = "The workspace ID must contain up to 256 printable characters without spaces or line breaks.";
        if (!string.IsNullOrWhiteSpace(input.DiscoveryEndpoint)
            && (!Uri.TryCreate(input.DiscoveryEndpoint, UriKind.Absolute, out var models) || !AIConnectionClient.IsAllowedEndpoint(models)
                || !SameOrigin(input.Endpoint, input.DiscoveryEndpoint)))
            errors["discovery"] = "Use a documented full model-list URL on the same origin as this API endpoint.";
        return errors;
    }

    public static string? ModelListEndpoint(AIConnectionInputs input)
    {
        if (!string.IsNullOrWhiteSpace(input.DiscoveryEndpoint)) return input.DiscoveryEndpoint;
        return input.Endpoint == AIServices.DefaultEndpoint(input.ServiceId, input.Protocol)
            ? AIServices.Get(input.ServiceId).ModelsEndpoint : null;
    }
}

/// <summary>Reads a saved limit. A damaged value becomes empty, so one bad field cannot lose every saved connection.</summary>
internal sealed class SendCharactersJsonConverter : System.Text.Json.Serialization.JsonConverter<int?>
{
    public override int? Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType == System.Text.Json.JsonTokenType.Number && reader.TryGetInt32(out var value)) return value;
        reader.Skip();
        return null;
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, int? value, System.Text.Json.JsonSerializerOptions options)
    {
        if (value is { } number) writer.WriteNumberValue(number);
        else writer.WriteNullValue();
    }
}

public sealed record AIConnectionCheck(DateTimeOffset CheckedAt, string Connectivity, string Authentication, string Model,
    string Generation, IReadOnlyList<string> Models, bool ModelInvalid = false)
{
    public string Describe() => $"Local settings: valid\nConnectivity: {Connectivity}\nAuthentication: {Authentication}\nModel: {Model}\nGeneration: {Generation}\nChecked: {CheckedAt:yyyy-MM-dd HH:mm:ss zzz}";
}
