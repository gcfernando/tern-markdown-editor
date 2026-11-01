using System.Text.Json;

namespace Tern.Desktop.Services;

public sealed record IntegrationProfile(string Id, string Kind, string Name, string Destination, string Model = "auto")
{
    public override string ToString() => Name;
}

/// <summary>Nonsecret integration settings. Secrets live only in the credential vault.</summary>
public sealed class IntegrationProfileStore(string directory, IAICredentialStore vault)
{
    public static event EventHandler<AIConnectionsChangedEventArgs>? ConnectionsChanged;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);
    public string FilePath { get; } = Path.Combine(directory, "integration-connections.json");
    private object Gate => Locks.GetOrAdd(Path.GetFullPath(FilePath), _ => new object());
    public IReadOnlyList<IntegrationProfile> Load()
    {
        lock (Gate)
        {
            if (!File.Exists(FilePath)) return [];
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > 1_048_576) throw new InvalidOperationException("Saved integration settings exceed the size limit.");
            var profiles = JsonSerializer.Deserialize<IntegrationProfile[]>(stream) ?? [];
            foreach (var profile in profiles) Validate(profile);
            if (profiles.Select(p => p.Id).Distinct().Count() != profiles.Length) throw new InvalidOperationException("Duplicate saved integration identities.");
            return profiles;
        }
    }
    public string ReadSecret(IntegrationProfile profile)
    {
        Validate(profile);
        return vault.Read(profile.Id) ?? "";
    }
    public void Save(IntegrationProfile profile, string secret)
    {
        Validate(profile);
        if (secret is null || secret.Length > 1280 || secret.Any(c => c < 33 || c > 126)) throw new ArgumentException("Use a token of up to 1,280 printable characters without spaces.");
        lock (Gate)
        {
            var original = Load();
            var previous = original.FirstOrDefault(p => p.Id == profile.Id);
            if (previous is not null && previous.Kind != profile.Kind) throw new InvalidOperationException("The saved connection type cannot change.");
            if (original.Any(p => p.Id != profile.Id && p.Kind == profile.Kind && p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("A connection with this name already exists.");
            Update(profile.Id, secret, original, original.Where(p => p.Id != profile.Id).Append(profile).ToArray());
            ConnectionsChanged?.Invoke(this, new AIConnectionsChangedEventArgs(directory));
        }
    }
    public void Remove(string id)
    {
        lock (Gate)
        {
            var original = Load();
            if (!original.Any(p => p.Id == id)) return;
            Update(id, "", original, original.Where(p => p.Id != id).ToArray());
            ConnectionsChanged?.Invoke(this, new AIConnectionsChangedEventArgs(directory));
        }
    }
    private void Update(string id, string secret, IReadOnlyList<IntegrationProfile> original, IReadOnlyList<IntegrationProfile> updated)
    {
        var oldSecret = vault.Read(id);
        Write(updated);
        try { if (secret.Length == 0) vault.Remove(id); else vault.Save(id, secret); }
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            var restored = true;
            try { Write(original); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { restored = false; }
            try { if (oldSecret is null) vault.Remove(id); else vault.Save(id, oldSecret); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or PlatformNotSupportedException or IOException or UnauthorizedAccessException) { restored = false; }
            throw new InvalidOperationException(restored ? "Secure storage failed. Previous connection settings were restored." : "Secure storage failed and restoration was incomplete. Review saved settings.");
        }
    }
    private void Write(IReadOnlyList<IntegrationProfile> profiles)
    {
        Directory.CreateDirectory(directory);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(profiles)); File.Move(temporary, FilePath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void Validate(IntegrationProfile? profile)
    {
        if (profile is null || !Guid.TryParseExact(profile.Id, "N", out _) || profile.Kind is not ("mcp" or "copilot")
            || string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 100 || string.IsNullOrWhiteSpace(profile.Destination)
            || profile.Destination.Length > 2048 || string.IsNullOrWhiteSpace(profile.Model) || profile.Model.Length > 100)
            throw new InvalidOperationException("Invalid saved integration settings. The original file has been preserved.");
        if (profile.Kind == "mcp" && (!Uri.TryCreate(profile.Destination, UriKind.Absolute, out var uri) || !AIConnectionClient.IsAllowedEndpoint(uri)))
            throw new InvalidOperationException("Use an HTTPS MCP endpoint or HTTP on localhost, without URL credentials, query or fragment.");
        if (profile.Kind == "copilot" && (!Path.IsPathFullyQualified(profile.Destination) || !profile.Destination.EndsWith("copilot.exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Choose the full path to the native copilot.exe.");
    }
}
