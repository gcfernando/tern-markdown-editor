using System.Text.Json;

namespace Tern.Desktop.Services;

public interface IAIProviderAdapter
{
    string Id { get; }
    string Name { get; }
    string Describe { get; }
    string CreateRequest(string model, string reviewedMessage);
    string ReadResponse(JsonElement response);
}

public static class AIProviders
{
    public static IReadOnlyList<IAIProviderAdapter> All { get; } = [new ChatCompletionsAdapter(), new OllamaChatAdapter(), new AnthropicMessagesAdapter()];
    public static IAIProviderAdapter Get(string id) => All.FirstOrDefault(p => p.Id == id)
        ?? throw new ArgumentException("Select a supported provider protocol.", nameof(id));

    private sealed class ChatCompletionsAdapter : IAIProviderAdapter
    {
        public string Id => "chat-completions";
        public string Name => "OpenAI-compatible chat";
        public string Describe => "Non-streaming text chat completions. Use the full chat/completions endpoint for your cloud or local service. Model and key come from that service; tools and images are not supported.";
        public string CreateRequest(string model, string reviewedMessage) => JsonSerializer.Serialize(new { model, stream = false,
            messages = new[] { new { role = "user", content = reviewedMessage } } });
        public string ReadResponse(JsonElement response)
        {
            if (response.TryGetProperty("choices", out var completed) && completed.ValueKind == JsonValueKind.Array && completed.GetArrayLength() > 0
                && completed[0].TryGetProperty("finish_reason", out var reason) && reason.GetString() == "length")
                throw new InvalidOperationException("The AI answer was cut short by the model's output limit. Your note has not changed. Try a smaller note or another model.");
            if (response.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                return content.GetString() ?? string.Empty;
            throw new InvalidOperationException("The response does not contain chat-completions text. Select the matching protocol and full endpoint.");
        }
    }

    private sealed class OllamaChatAdapter : IAIProviderAdapter
    {
        public string Id => "ollama-chat";
        public string Name => "Ollama native chat";
        public string Describe => "Non-streaming native Ollama text chat. Use the full /api/chat endpoint and an installed model name. A local server must already be running; Tern does not download models.";
        public string CreateRequest(string model, string reviewedMessage) => JsonSerializer.Serialize(new { model, stream = false,
            messages = new[] { new { role = "user", content = reviewedMessage } } });
        public string ReadResponse(JsonElement response)
        {
            if (response.TryGetProperty("done_reason", out var reason) && reason.GetString() == "length")
                throw new InvalidOperationException("The AI answer was cut short. Your note has not changed. Try a smaller note or another model.");
            if (response.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                return content.GetString() ?? string.Empty;
            throw new InvalidOperationException("The response does not contain native Ollama chat text. Check the protocol and full /api/chat endpoint.");
        }
    }

    private sealed class AnthropicMessagesAdapter : IAIProviderAdapter
    {
        public string Id => "anthropic-messages";
        public string Name => "Claude · Anthropic Messages API";
        public string Describe => "Use https://api.anthropic.com/v1/messages, an Anthropic API key and an available Claude model ID. A Claude consumer subscription does not supply API credentials. Sends one reviewed text message; no tools or document access.";
        public string CreateRequest(string model, string reviewedMessage) => JsonSerializer.Serialize(new { model, max_tokens = 1024, stream = false,
            messages = new[] { new { role = "user", content = reviewedMessage } } });
        public string ReadResponse(JsonElement response)
        {
            if (response.TryGetProperty("stop_reason", out var reason) && reason.GetString() == "max_tokens")
                throw new InvalidOperationException("The AI answer reached its output limit. Your note has not changed. Try a smaller note or another model.");
            if (response.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                var parts = content.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Object
                    && p.TryGetProperty("type", out var type) && type.GetString() == "text"
                    && p.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    .Select(p => p.GetProperty("text").GetString()).ToArray();
                if (parts.Length > 0) return string.Join("\n", parts);
            }
            throw new InvalidOperationException("The response does not contain Claude Messages text. Check the Anthropic protocol, endpoint and model.");
        }
    }
}

public sealed record AIConnectionProfile(string Id, string Name, string Provider, string Endpoint, string Model,
    string? ServiceId = null, bool UseCustomEndpoint = false, string? DiscoveryEndpoint = null, string? WorkspaceId = null);

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
        using var input = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length > 1_048_576) throw new InvalidOperationException("The AI connection metadata file exceeds its size limit.");
        var profiles = JsonSerializer.Deserialize<AIConnectionProfile[]>(input) ?? [];
        foreach (var profile in profiles) Validate(profile);
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
            || string.IsNullOrWhiteSpace(profile.Model) || profile.Model.Length > 256
            || !Uri.TryCreate(profile.Endpoint, UriKind.Absolute, out var endpoint) || !AIConnectionClient.IsAllowedEndpoint(endpoint))
            throw new InvalidOperationException("An AI connection profile is invalid. Its original metadata file has been preserved.");
        _ = AIProviders.Get(profile.Provider);
        var serviceId = AIServices.ServiceFor(profile);
        var errors = AIConnectionRules.Errors(new AIConnectionInputs(serviceId, profile.Provider, profile.Endpoint, profile.Model,
            AIServices.Get(serviceId).RequiresCredential ? "metadata-validation-only" : "", profile.DiscoveryEndpoint ?? "", profile.WorkspaceId ?? ""));
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
