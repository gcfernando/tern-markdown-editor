namespace Tern.Workspaces;

public enum WorkspaceChangeKind
{
    Created,
    Changed,
    Deleted,
    Renamed,
}

/// <summary>One coalesced change. For <see cref="WorkspaceChangeKind.Renamed"/>, <see cref="OldPath"/> is where it used to be.</summary>
/// <param name="IsDirectory">Best effort: deletions cannot be classified because the path no longer exists.</param>
public sealed record WorkspaceChange(WorkspaceChangeKind Kind, string Path, string? OldPath = null, bool IsDirectory = false);

/// <summary>A batch of changes. File system notifications are only hints; consumers must re-verify against disk.</summary>
/// <param name="RescanRequired">Events were lost (buffer overflow or watcher failure); the whole workspace must be rescanned.</param>
public sealed record WorkspaceChangeSet(IReadOnlyList<WorkspaceChange> Changes, bool RescanRequired);

/// <summary>
/// Merges a burst of raw file system events into the smallest equivalent set of changes,
/// e.g. create+modify+delete of a scratch file vanishes, and rename chains collapse into one rename.
/// Not thread-safe; callers serialise access.
/// </summary>
public sealed class ChangeCoalescer
{
    private readonly Dictionary<string, Entry> _entries = new(WorkspacePaths.Comparer);
    private long _sequence;

    public bool IsEmpty => _entries.Count == 0 && !RescanRequired;

    public bool RescanRequired { get; private set; }

    public void RequireRescan() => RescanRequired = true;

    public void Created(string path, bool isDirectory)
    {
        if (_entries.TryGetValue(path, out var existing) && existing.Kind == WorkspaceChangeKind.Deleted)
        {
            Set(path, WorkspaceChangeKind.Changed, null, isDirectory);
            return;
        }

        if (existing is null)
        {
            Set(path, WorkspaceChangeKind.Created, null, isDirectory);
        }
    }

    public void Changed(string path, bool isDirectory)
    {
        if (!_entries.ContainsKey(path))
        {
            Set(path, WorkspaceChangeKind.Changed, null, isDirectory);
        }
    }

    public void Deleted(string path)
    {
        if (!_entries.TryGetValue(path, out var existing))
        {
            Set(path, WorkspaceChangeKind.Deleted, null, false);
            return;
        }

        switch (existing.Kind)
        {
            case WorkspaceChangeKind.Created:
                _entries.Remove(path);
                break;
            case WorkspaceChangeKind.Renamed:
                // Moved and then removed: from the outside only the original disappeared.
                _entries.Remove(path);
                Set(existing.OldPath!, WorkspaceChangeKind.Deleted, null, existing.IsDirectory);
                break;
            default:
                Set(path, WorkspaceChangeKind.Deleted, null, existing.IsDirectory);
                break;
        }
    }

    public void Renamed(string oldPath, string newPath, bool isDirectory)
    {
        if (_entries.TryGetValue(oldPath, out var existing))
        {
            switch (existing.Kind)
            {
                case WorkspaceChangeKind.Created:
                    _entries.Remove(oldPath);
                    Set(newPath, WorkspaceChangeKind.Created, null, isDirectory);
                    return;
                case WorkspaceChangeKind.Renamed:
                    _entries.Remove(oldPath);
                    Set(newPath, WorkspaceChangeKind.Renamed, existing.OldPath, isDirectory);
                    return;
                case WorkspaceChangeKind.Deleted:
                    return;
            }

            _entries.Remove(oldPath);
        }

        Set(newPath, WorkspaceChangeKind.Renamed, oldPath, isDirectory);
    }

    /// <summary>Returns everything collected so far, in the order first seen, and resets.</summary>
    public WorkspaceChangeSet Drain()
    {
        var changes = _entries
            .OrderBy(e => e.Value.Sequence)
            .Select(e => new WorkspaceChange(e.Value.Kind, e.Key, e.Value.OldPath, e.Value.IsDirectory))
            .ToList();
        var set = new WorkspaceChangeSet(changes, RescanRequired);
        _entries.Clear();
        RescanRequired = false;
        return set;
    }

    private void Set(string path, WorkspaceChangeKind kind, string? oldPath, bool isDirectory) =>
        _entries[path] = new Entry(kind, oldPath, isDirectory, ++_sequence);

    private sealed record Entry(WorkspaceChangeKind Kind, string? OldPath, bool IsDirectory, long Sequence);
}
