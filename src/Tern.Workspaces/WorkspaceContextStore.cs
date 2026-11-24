using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tern.Workspaces;

/// <summary>
/// Per-user working context (open tabs, carets, view mode) kept under the app's local state folder, keyed by a hash of the
/// workspace root, so the shared <c>.tern-workspace.json</c> in a synced or read-only workspace is never rewritten on close.
/// </summary>
public sealed class WorkspaceContextStore(string baseDirectory)
{
    public const string DirectoryName = "workspace-contexts";
    public const int CurrentVersion = 1;
    private sealed record Stored(int Version, string Root, WorkspaceWorkingContext? Context);

    private string DirectoryPath => Path.Combine(baseDirectory, DirectoryName);

    public string PathFor(string root)
    {
        var key = WorkspacePaths.Normalize(root);
        if (WorkspacePaths.IsCaseInsensitive) key = key.ToUpperInvariant();
        return Path.Combine(DirectoryPath, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant() + ".json");
    }

    /// <summary>The stored context, or null when none exists or the file is unreadable (a damaged cache must never block opening).</summary>
    public WorkspaceWorkingContext? Load(string root)
    {
        try
        {
            var path = PathFor(root);
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > WorkspaceDefinition.MaxDefinitionBytes) return null;
            var stored = JsonSerializer.Deserialize<Stored>(stream);
            return stored is { Version: CurrentVersion } ? stored.Context : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    public bool Exists(string root) => File.Exists(PathFor(root));

    /// <summary>Atomically writes the context. Throws IO/Unauthorized exceptions the caller reports.</summary>
    public void Save(string root, WorkspaceWorkingContext context)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Stored(CurrentVersion, WorkspacePaths.Normalize(root), context));
        if (bytes.Length > WorkspaceDefinition.MaxDefinitionBytes)
            throw new InvalidDataException("The workspace context exceeds the supported metadata size. Close some saved-document tabs before retrying.");
        AtomicFile.WriteAllBytes(PathFor(root), bytes);
    }

    /// <summary>
    /// Returns the local context. When none exists yet and the shared definition still carries a legacy per-machine context,
    /// copies it here once and then removes it from the shared file (best effort: a read-only workspace keeps its file untouched).
    /// </summary>
    public WorkspaceWorkingContext? LoadOrMigrate(string root, WorkspaceDefinition? definition)
    {
        if (Exists(root)) return Load(root);
        if (definition?.WorkingContext is not { } legacy) return null;
        try
        {
            Save(root, legacy);
            WorkspaceDefinition.RemoveWorkingContext(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or NotSupportedException) { }
        return legacy;
    }
}
