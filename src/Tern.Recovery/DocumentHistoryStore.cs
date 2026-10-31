using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tern.Recovery;

public sealed record DocumentVersion(DateTime SavedAtUtc, string Text, string Sha256)
{
    public override string ToString() => $"{SavedAtUtc.ToLocalTime():g} · {Text.Length:N0} characters";
}

/// <summary>Local saved-text history, separate from crash recovery. At most 30 versions / 32 MiB per path.</summary>
public sealed class DocumentHistoryStore(string root) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private const long ByteBudget = 32 * 1024 * 1024;
    private const int VersionLimit = 30;

    public async Task CaptureAsync(string path, string text)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await CaptureCoreAsync(path, text).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task CaptureCoreAsync(string path, string text)
    {
        var directory = DirectoryFor(path);
        Directory.CreateDirectory(directory);
        var digest = Hash(text);
        var existing = await ReadCoreAsync(path).ConfigureAwait(false);
        if (existing.Count > 0 && existing[0].Sha256 == digest) return;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new DocumentVersion(DateTime.UtcNow, text, digest));
        if (payload.LongLength > ByteBudget) throw new IOException("This document exceeds the 32 MiB local-history budget.");
        var target = Path.Combine(directory, $"{DateTime.UtcNow.Ticks:D19}-{Guid.NewGuid():N}.json");
        var temporary = target + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, payload).ConfigureAwait(false);
            File.Move(temporary, target);
        }
        finally
        {
            File.Delete(temporary);
        }

        long bytes = 0;
        var count = 0;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.json").OrderByDescending(f => f.Name, StringComparer.Ordinal))
        {
            bytes += file.Length;
            if (++count > VersionLimit || bytes > ByteBudget) file.Delete();
        }
    }

    public async Task<IReadOnlyList<DocumentVersion>> ReadAsync(string path)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await ReadCoreAsync(path).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Removes only application-owned saved versions for one deleted namespace path.</summary>
    public async Task PurgeAsync(string path)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var directory = DirectoryFor(path);
            if (!Directory.Exists(directory)) return;
            for (var ancestor = new DirectoryInfo(directory); ancestor is not null; ancestor = ancestor.Parent)
                if (ancestor.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("Saved-text history cannot be purged through a linked directory.");
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file), @"^\d{19}-[a-f0-9]{32}(?:-[a-f0-9]{32})*\.json$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)) continue;
                if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("A saved-text history entry is a link; it was not removed.");
                File.Delete(file);
            }
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Moves saved-text history with a renamed document, retaining any earlier destination history.</summary>
    public async Task RenameAsync(string oldPath, string newPath)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var source = DirectoryFor(oldPath);
            var destination = DirectoryFor(newPath);
            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(source)) return;
            // Validate both histories before changing either; corrupt versions remain available for inspection.
            await ReadCoreAsync(oldPath).ConfigureAwait(false);
            await ReadCoreAsync(newPath).ConfigureAwait(false);
            if (!Directory.Exists(destination)) { Directory.Move(source, destination); return; }
            foreach (var file in Directory.EnumerateFiles(source, "*.json"))
            {
                var target = Path.Combine(destination, Path.GetFileName(file));
                if (File.Exists(target)) target = Path.Combine(destination, Path.GetFileNameWithoutExtension(file) + "-" + Guid.NewGuid().ToString("N") + ".json");
                File.Move(file, target, overwrite: false);
            }
            // Unknown files are retained; never recursively remove a history directory.
            if (!Directory.EnumerateFileSystemEntries(source).Any()) Directory.Delete(source);
            long bytes = 0;
            var count = 0;
            foreach (var file in new DirectoryInfo(destination).EnumerateFiles("*.json").OrderByDescending(f => f.Name, StringComparer.Ordinal))
            {
                bytes += file.Length;
                if (++count > VersionLimit || bytes > ByteBudget) file.Delete();
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<DocumentVersion>> ReadCoreAsync(string path)
    {
        var directory = DirectoryFor(path);
        if (!Directory.Exists(directory)) return [];
        var result = new List<DocumentVersion>();
        long bytes = 0;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.json").OrderByDescending(f => f.Name, StringComparer.Ordinal).Take(VersionLimit))
        {
            bytes += file.Length;
            if (bytes > ByteBudget) throw new InvalidDataException("Local history exceeds its size budget.");
            var content = await File.ReadAllBytesAsync(file.FullName).ConfigureAwait(false);
            var version = JsonSerializer.Deserialize<DocumentVersion>(content);
            if (version is null || version.Text is null || Hash(version.Text) != version.Sha256)
                throw new InvalidDataException("A local-history version failed its integrity check.");
            result.Add(version);
        }

        return result;
    }

    private string DirectoryFor(string path)
    {
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) full = full.ToUpperInvariant();
        return Path.Combine(root, Hash(full));
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public void Dispose() => _gate.Dispose();
}
