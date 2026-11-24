using System.Text.Json;
using Tern.App;

namespace Tern.Desktop.Services;

public interface IAIProviderAdapter
{
    string Id { get; }
    string Name { get; }
    string Describe { get; }
    string CreateRequest(string model, string reviewedMessage);
    string CreateRequest(string model, AIRequest request) => CreateRequest(model, request.Text);
    string CreateRequest(string model, AIRequest request, string instructionRole) => CreateRequest(model, request);
    string ReadResponse(JsonElement response);
}

public static class AIProviders
{
    public static IReadOnlyList<IAIProviderAdapter> All { get; } = [new ChatCompletionsAdapter(), new OllamaChatAdapter(), new AnthropicMessagesAdapter()];
    public static IAIProviderAdapter Get(string id) => All.FirstOrDefault(p => p.Id == id)
        ?? throw new ArgumentException("Select a supported provider protocol.", nameof(id));

    internal static string InstructionRole(IAIProviderAdapter adapter, Uri endpoint, string model)
    {
        // Official OpenAI reasoning guidance documents developer instead of system.
        // An OpenAI-compatible protocol alone does not establish this capability.
        if (adapter.Id != "chat-completions" || endpoint.Scheme != Uri.UriSchemeHttps || !endpoint.IsDefaultPort
            || !endpoint.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase) || endpoint.AbsolutePath != "/v1/chat/completions") return "system";
        if (model == "o1-preview" || model.StartsWith("o1-preview-", StringComparison.Ordinal)
            || model == "o1-mini" || model.StartsWith("o1-mini-", StringComparison.Ordinal))
            throw new InvalidOperationException("This older model cannot accept Tern's instructions. Choose a newer model before sending.");
        return System.Text.RegularExpressions.Regex.IsMatch(model, @"^(?:o1(?:-|$)|o3(?:-|$)|o4-mini(?:-|$)|gpt-[56](?:[.-]|$))",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) ? "developer" : "system";
    }

    internal static string ClaudeRequest(string model, string reviewedMessage, int replyTokens) => ClaudeRequest(model, AIRequest.CurrentText(reviewedMessage), replyTokens);
    internal static string ClaudeRequest(string model, AIRequest request, int replyTokens) => JsonSerializer.Serialize(new { model, max_tokens = replyTokens, stream = false,
        system = request.Instructions, messages = new[] { new { role = "user", content = request.UserInput } } });

    /// <summary>A local model that read far fewer tokens than the text can hold cut the prompt. Small counts can come from its prompt cache.</summary>
    internal static bool PromptWasCut(IAIProviderAdapter provider, Uri endpoint, JsonElement response, int characters)
    {
        JsonElement count = default;
        var known = provider.Id switch
        {
            "ollama-chat" => response.TryGetProperty("prompt_eval_count", out count),
            "chat-completions" when endpoint.IsLoopback => response.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("prompt_tokens", out count),
            _ => false,
        };
        return known && count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out var tokens) && tokens >= 512 && tokens * 16 < characters;
    }

    /// <summary>Reads a text field without failing when a service sends another JSON type.</summary>
    private static bool Says(JsonElement element, string name, string value) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String && field.GetString() == value;

    /// <summary>A present but empty message is an empty answer, which callers explain. Null means the model wrote nothing.</summary>
    private static string? MessageText(JsonElement holder)
    {
        if (holder.ValueKind != JsonValueKind.Object || !holder.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content)) return null;
        return content.ValueKind switch { JsonValueKind.String => content.GetString() ?? "", JsonValueKind.Null => "", _ => null };
    }

    private sealed class ChatCompletionsAdapter : IAIProviderAdapter
    {
        public string Id => "chat-completions";
        public string Name => "OpenAI-compatible chat";
        public string Describe => "Non-streaming text chat completions. Use the full chat/completions endpoint for your cloud or local service. Model and key come from that service; tools and images are not supported.";
        public string CreateRequest(string model, string reviewedMessage) => CreateRequest(model, AIRequest.CurrentText(reviewedMessage));
        public string CreateRequest(string model, AIRequest request) => CreateRequest(model, request, "system");
        public string CreateRequest(string model, AIRequest request, string instructionRole) => JsonSerializer.Serialize(new { model, stream = false,
            messages = new[] { new { role = instructionRole, content = request.Instructions }, new { role = "user", content = request.UserInput } } });
        public string ReadResponse(JsonElement response)
        {
            if (response.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                if (Says(choices[0], "finish_reason", "length"))
                    throw new InvalidOperationException("The request failed because the AI answer was cut short by the model's output limit. Your note has not changed. Try a smaller note or another model.");
                if (MessageText(choices[0]) is { } text) return text;
            }
            throw new InvalidOperationException("The request failed because the response does not contain chat-completions text. Select the matching protocol and full endpoint.");
        }
    }

    private sealed class OllamaChatAdapter : IAIProviderAdapter
    {
        public string Id => "ollama-chat";
        public string Name => "Ollama native chat";
        public string Describe => "Non-streaming native Ollama text chat. Use the full /api/chat endpoint and an installed model name. A local server must already be running; Tern does not download models.";
        // Without this, Ollama can silently drop the start of a prompt that is larger than its default context.
        public string CreateRequest(string model, string reviewedMessage) => CreateRequest(model, AIRequest.CurrentText(reviewedMessage));
        public string CreateRequest(string model, AIRequest request) => JsonSerializer.Serialize(new { model, stream = false,
            options = new { num_ctx = ContextFor(request.CharacterCount) },
            messages = new[] { new { role = "system", content = request.Instructions }, new { role = "user", content = request.UserInput } } });

        private static int ContextFor(int characters)
        {
            var needed = characters / 3L * 2 + 2048; // Room for the prompt and a reply of similar length.
            var context = 4096;
            while (context < needed && context < 1 << 20) context *= 2;
            return context;
        }
        public string ReadResponse(JsonElement response)
        {
            if (Says(response, "done_reason", "length"))
                throw new InvalidOperationException("The request failed because the AI answer was cut short. Your note has not changed. Try a smaller note or another model.");
            if (MessageText(response) is { } text) return text;
            throw new InvalidOperationException("The request failed because the response does not contain native Ollama chat text. Check the protocol and full /api/chat endpoint.");
        }
    }

    private sealed class AnthropicMessagesAdapter : IAIProviderAdapter
    {
        public string Id => "anthropic-messages";
        // Long answers, reports and rewritten notes need room. This is a reply size, separate from the connection's send value.
        // Older models have a lower cap. AIConnectionClient retries with the cap the service reports.
        private const int MaximumReplyTokens = 16_000;
        public string Name => "Claude · Anthropic Messages API";
        public string Describe => "Use https://api.anthropic.com/v1/messages, an Anthropic API key and an available Claude model ID. A Claude consumer subscription does not supply API credentials. Sends one reviewed text message; no tools or document access.";
        public string CreateRequest(string model, string reviewedMessage) => ClaudeRequest(model, reviewedMessage, MaximumReplyTokens);
        public string CreateRequest(string model, AIRequest request) => ClaudeRequest(model, request, MaximumReplyTokens);
        public string ReadResponse(JsonElement response)
        {
            if (Says(response, "stop_reason", "max_tokens"))
                throw new InvalidOperationException("The request failed because the AI answer reached its output limit. Your note has not changed. Try a smaller note or another model.");
            if (response.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                var parts = content.EnumerateArray().Where(p => Says(p, "type", "text")
                    && p.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    .Select(p => p.GetProperty("text").GetString()).ToArray();
                if (parts.Length > 0) return string.Join("\n", parts);
                if (content.GetArrayLength() == 0) return "";
            }
            throw new InvalidOperationException("The request failed because the response does not contain Claude Messages text. Check the Anthropic protocol, endpoint and model.");
        }
    }
}

public sealed record AIConnectionProfile(string Id, string Name, string Provider, string Endpoint, string Model,
    string? ServiceId = null, bool UseCustomEndpoint = false, string? DiscoveryEndpoint = null, string? WorkspaceId = null, int? TimeoutSeconds = null, int? SendCharacters = null)
{
    /// <summary>Characters Tern can send to this AI at one time. Older saved connections receive the default.</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(SendCharactersJsonConverter))]
    public int? SendCharacters { get; init; } = AIConnectionRules.NormalizeSendCharacters(SendCharacters);
}

public sealed class AIConnectionsChangedEventArgs(string directory) : EventArgs
{
    public string Directory { get; } = directory;
}

/// <summary>Connection metadata only. Secrets never enter this JSON store.</summary>
public interface IAIProfileStore
{
    IReadOnlyList<AIConnectionProfile> Load();
    void Save(IReadOnlyList<AIConnectionProfile> profiles);
}

public sealed class AIProfileStore(string directory) : IAIProfileStore
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);
    public string FilePath { get; } = Path.Combine(directory, "ai-connections.json");
    public IReadOnlyList<AIConnectionProfile> Load()
    {
        lock (Locks.GetOrAdd(Path.GetFullPath(FilePath), _ => new object())) return LoadCore();
    }
    private IReadOnlyList<AIConnectionProfile> LoadCore()
    {
        if (!File.Exists(FilePath)) return [];
        // Readers must permit atomic replacement while another window saves.
        byte[] saved;
        using (var input = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            if (input.Length > 1_048_576) throw new InvalidOperationException("The AI connection metadata file exceeds its size limit.");
            saved = new byte[input.Length];
            input.ReadExactly(saved);
        }
        var profiles = JsonSerializer.Deserialize<AIConnectionProfile[]>(saved) ?? [];
        foreach (var profile in profiles) Validate(profile);
        if (AIConnectionRules.NeedsSendCharactersUpdate(saved, _ => true))
        {
            // The corrected value still applies this session if the file is read-only. Saving is tried again next time.
            try { SaveCore(profiles); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Trace.TraceWarning("AI connection update was not saved: {0}", ex.Message); }
        }
        return profiles;
    }
    public void Save(IReadOnlyList<AIConnectionProfile> profiles)
    {
        lock (Locks.GetOrAdd(Path.GetFullPath(FilePath), _ => new object())) SaveCore(profiles);
    }
    private void SaveCore(IReadOnlyList<AIConnectionProfile> profiles)
    {
        foreach (var profile in profiles) Validate(profile);
        Directory.CreateDirectory(directory);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(profiles));
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(temporary, FilePath, true); break; }
                catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
                { Thread.Sleep(10 * (attempt + 1)); }
            }
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Nonsecret temporary metadata can be cleaned up on a later run. */ }
        }
    }
    internal static void Validate(AIConnectionProfile? profile)
    {
        if (profile is null || !Guid.TryParseExact(profile.Id, "N", out _) || string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 100
            || string.IsNullOrWhiteSpace(profile.Model) || profile.Model.Length > 256 || !AIConnectionRules.IsValidSendCharacters(profile.SendCharacters)
            || !Uri.TryCreate(profile.Endpoint, UriKind.Absolute, out var endpoint) || !AIConnectionClient.IsAllowedEndpoint(endpoint))
            throw new InvalidOperationException("An AI connection profile is invalid. Its original metadata file has been preserved.");
        _ = AIProviders.Get(profile.Provider);
        var serviceId = AIServices.ServiceFor(profile);
        var errors = AIConnectionRules.Errors(new AIConnectionInputs(serviceId, profile.Provider, profile.Endpoint, profile.Model,
            AIServices.Get(serviceId).RequiresCredential ? "metadata-validation-only" : "", profile.DiscoveryEndpoint ?? "", profile.WorkspaceId ?? "", profile.TimeoutSeconds));
        if (errors.Count > 0) throw new InvalidOperationException("An AI connection profile contains invalid endpoint or protocol settings. Its original metadata file has been preserved.");
    }
}

/// <summary>Coordinates atomic metadata replacement with vault updates; a failed vault update restores prior state.</summary>
public sealed class AIConnectionManager(IAIProfileStore profilesStore, IAICredentialStore credentials)
{
    public static event EventHandler<AIConnectionsChangedEventArgs>? ConnectionsChanged;

    public IReadOnlyList<AIConnectionProfile> Save(AIConnectionProfile profile, string? key, IReadOnlyList<AIConnectionProfile> current, bool allowCredentialReuse = false)
    {
        AIProfileStore.Validate(profile);
        if (current.Any(p => p.Id != profile.Id && p.Name.Trim().Equals(profile.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A connection with this name already exists. Choose a different name.", nameof(profile));
        var previous = current.FirstOrDefault(p => p.Id == profile.Id);
        var previousKey = previous is null ? null : credentials.Read(profile.Id);
        if (previous is not null && !string.IsNullOrEmpty(previousKey) && (key is null || key == previousKey)
            && (!AIConnectionRules.SameOrigin(previous.Endpoint, profile.Endpoint) || previous.Provider != profile.Provider
                || AIServices.ServiceFor(previous) != AIServices.ServiceFor(profile)) && !allowCredentialReuse)
            throw new InvalidOperationException("Explicitly approve using the stored credential at the changed destination and protocol, or supply a replacement.");
        var updated = current.Where(p => p.Id != profile.Id).Append(profile).ToArray();
        Update(profile.Id, key ?? credentials.Read(profile.Id) ?? "", current, updated);
        NotifyChanged();
        return updated;
    }
    public IReadOnlyList<AIConnectionProfile> Remove(string id, IReadOnlyList<AIConnectionProfile> current)
    {
        var updated = current.Where(p => p.Id != id).ToArray();
        Update(id, string.Empty, current, updated);
        NotifyChanged();
        return updated;
    }
    private void NotifyChanged()
    {
        if (profilesStore is AIProfileStore store)
            ConnectionsChanged?.Invoke(this, new AIConnectionsChangedEventArgs(Path.GetDirectoryName(store.FilePath)!));
    }
    private void Update(string id, string key, IReadOnlyList<AIConnectionProfile> original, IReadOnlyList<AIConnectionProfile> updated)
    {
        var previousKey = credentials.Read(id);
        profilesStore.Save(updated); // A metadata failure must not touch the vault.
        try
        {
            if (key.Length > 0) credentials.Save(id, key); else credentials.Remove(id);
        }
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or PlatformNotSupportedException)
        {
            var restored = true;
            try { profilesStore.Save(original); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { restored = false; }
            try { if (previousKey is null) credentials.Remove(id); else credentials.Save(id, previousKey); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or PlatformNotSupportedException) { restored = false; }
            throw new InvalidOperationException(restored
                ? "Secure credential storage failed. The previous connection settings were restored."
                : "Secure credential storage failed and restoration was incomplete. Review this connection before using it.");
        }
    }
}
