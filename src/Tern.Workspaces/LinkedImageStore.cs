using System.Text.Json;

namespace Tern.Workspaces;

/// <summary>Remembers only the image files explicitly linked to each local note.</summary>
public sealed class LinkedImageStore
{
    private const int MaxDocuments = 500;
    public const int MaxImages = 128;
    private readonly string _path;
    private readonly Dictionary<string, string[]> _files = new(WorkspacePaths.Comparer);
    // Creation time of the note each entry was recorded for, so a different file later created at that path does not inherit it.
    private readonly Dictionary<string, long> _stamps = new(WorkspacePaths.Comparer);

    public LinkedImageStore(string directory)
    {
        _path = Path.Combine(directory, "linked-images.json");
        _ = JsonStateFile.Read(_path, 1, out var document);
        if (document is null) return;
        using (document)
        {
            if (!document.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object) return;
            foreach (var entry in files.EnumerateObject().Take(MaxDocuments))
            {
                if (!ValidPath(entry.Name) || entry.Value.ValueKind != JsonValueKind.Array) continue;
                _files[Path.GetFullPath(entry.Name)] = [.. entry.Value.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)
                    .Where(ValidPath).Select(Path.GetFullPath).Distinct(WorkspacePaths.Comparer).Take(MaxImages)];
            }
            if (document.RootElement.TryGetProperty("stamps", out var stamps) && stamps.ValueKind == JsonValueKind.Object)
                foreach (var entry in stamps.EnumerateObject())
                    if (ValidPath(entry.Name) && entry.Value.ValueKind == JsonValueKind.Number && entry.Value.TryGetInt64(out var ticks))
                        _stamps[Path.GetFullPath(entry.Name)] = ticks;
        }
    }

    public string? LastSaveError { get; private set; }

    public IReadOnlyList<string> Get(string path)
    {
        var key = Path.GetFullPath(path);
        if (!_files.TryGetValue(key, out var files)) return [];
        var current = Tern.Filesystem.FileIdentity.TryGetCreationTicks(key);
        if (current is null) return files;
        if (!_stamps.TryGetValue(key, out var stamp)) { _stamps[key] = current.Value; return files; }
        return stamp == current.Value ? files : [];
    }

    public bool Save(string path, IReadOnlyList<string> files)
    {
        try
        {
            var key = Path.GetFullPath(path);
            var selected = files.Where(ValidPath).Select(Path.GetFullPath).Distinct(WorkspacePaths.Comparer).Take(MaxImages).ToArray();
            var current = Tern.Filesystem.FileIdentity.TryGetCreationTicks(key);
            var restamp = current is { } now && (!_stamps.TryGetValue(key, out var known) || known != now);
            if (LastSaveError is null && !restamp && _files.TryGetValue(key, out var previous) && previous.SequenceEqual(selected, WorkspacePaths.Comparer)) return true;
            if (!_files.ContainsKey(key) && _files.Count >= MaxDocuments)
                throw new IOException("The linked-image history is full (500 documents).");
            _files[key] = selected;
            if (current is { } stamp) _stamps[key] = stamp; else _stamps.Remove(key);
            return Persist();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { LastSaveError = ex.Message; return false; }
    }

    /// <summary>
    /// Carries entries to new paths after a rename, move, duplicate or Save As. Entries are restamped from the file now at the new path.
    /// </summary>
    public bool Remap(IEnumerable<(string From, string To)> pairs, bool keepOriginal, Func<string, string>? mapImage = null)
    {
        try
        {
            var changed = false;
            foreach (var (from, to) in pairs)
            {
                var source = Path.GetFullPath(from);
                var target = Path.GetFullPath(to);
                if (!_files.TryGetValue(source, out var files) || WorkspacePaths.Comparer.Equals(source, target)) continue;
                var ticks = Tern.Filesystem.FileIdentity.TryGetCreationTicks(target);
                if (!keepOriginal) { _files.Remove(source); _stamps.Remove(source); }
                if (!_files.ContainsKey(target) && _files.Count >= MaxDocuments) continue;
                _files[target] = mapImage is null ? files : [.. files.Select(mapImage).Where(ValidPath).Select(Path.GetFullPath).Distinct(WorkspacePaths.Comparer)];
                if (ticks is { } stamp) _stamps[target] = stamp; else _stamps.Remove(target);
                changed = true;
            }
            return !changed || Persist();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { LastSaveError = ex.Message; return false; }
    }

    /// <summary>Forgets every entry for a file or for all files below a folder.</summary>
    public bool Forget(string path, bool isFolder)
    {
        var root = Path.GetFullPath(path);
        var doomed = _files.Keys.Where(key => WorkspacePaths.Comparer.Equals(key, root)
            || (isFolder && key.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToList();
        if (doomed.Count == 0) return true;
        foreach (var key in doomed) { _files.Remove(key); _stamps.Remove(key); }
        try { return Persist(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { LastSaveError = ex.Message; return false; }
    }

    private bool Persist()
    {
        AtomicFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(new { version = 1, files = _files, stamps = _stamps }));
        LastSaveError = null;
        return true;
    }

    private static bool ValidPath(string path) => path.Length is > 0 and <= 4096
        && path.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathFullyQualified(path);
}
