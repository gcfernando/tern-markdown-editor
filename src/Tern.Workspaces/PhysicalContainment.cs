using System.Collections.Concurrent;

namespace Tern.Workspaces;

/// <summary>
/// The containment check for one bulk scan. Each file's own link state is always read; its folder's result is reused
/// briefly, so thousands of notes in a folder do not each re-walk every parent directory.
/// </summary>
public sealed class PhysicalContainment(string root)
{
    private const long FolderResultLifetimeMs = 1000;
    private readonly ConcurrentDictionary<string, (bool Contained, long CheckedAt)> _folders = new(WorkspacePaths.Comparer);

    public bool Contains(string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (folder is null) return WorkspacePaths.IsPhysicallyContained(root, path);
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return WorkspacePaths.IsPhysicallyContained(root, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return WorkspacePaths.IsPhysicallyContained(root, path);
        }
        var now = Environment.TickCount64;
        if (_folders.TryGetValue(folder, out var known) && now - known.CheckedAt < FolderResultLifetimeMs) return known.Contained;
        var contained = WorkspacePaths.IsPhysicallyContained(root, folder);
        _folders[folder] = (contained, now);
        return contained;
    }
}
