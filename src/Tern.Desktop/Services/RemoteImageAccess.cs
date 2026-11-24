using System.Security.Cryptography;
using System.Text;
using Tern.App.Settings;

namespace Tern.Desktop.Services;

/// <summary>Shared consent decision for every remote-image consumer, including background exports.</summary>
public sealed class RemoteImageAccess(AppSettings settings)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _session = new(StringComparer.Ordinal);
    private readonly object _trustGate = new();
    private bool IsTrusted(string key) { lock (_trustGate) return settings.RemoteImageTrustedFiles.Contains(key, StringComparer.Ordinal); }
    public bool Allows(string documentKey) => !settings.OfflineMode && (settings.RemoteImages == RemoteImagePolicy.Always
        || settings.RemoteImages == RemoteImagePolicy.Ask && (_session.ContainsKey(documentKey)
            || IsTrusted(documentKey)));

    public void AllowOnce(string documentKey) => _session[documentKey] = 0;
    public void AllowAlways(string documentKey)
    {
        lock (_trustGate)
            if (!settings.RemoteImageTrustedFiles.Contains(documentKey, StringComparer.Ordinal)) settings.RemoteImageTrustedFiles.Add(documentKey);
    }

    public static string DocumentKey(string? filePath, Guid sessionId)
    {
        if (filePath is null || Tern.Filesystem.FileIdentity.TryGetCreationTicks(filePath) is not { } created)
            return "session:" + sessionId.ToString("N");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(filePath).ToUpperInvariant() + "|" + created)));
    }
}
