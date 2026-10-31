using System.Text.Json;

namespace Tern.Workspaces;

/// <summary>Remembers only the image files explicitly linked to each local note.</summary>
public sealed class LinkedImageStore
{
    private const int MaxDocuments = 500;
    public const int MaxImages = 128;
    private readonly string _path;
    private readonly Dictionary<string, string[]> _files = new(WorkspacePaths.Comparer);

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
        }
    }

    public string? LastSaveError { get; private set; }

    public IReadOnlyList<string> Get(string path) => _files.TryGetValue(Path.GetFullPath(path), out var files) ? files : [];

    public bool Save(string path, IReadOnlyList<string> files)
    {
        try
        {
            var key = Path.GetFullPath(path);
            var selected = files.Where(ValidPath).Select(Path.GetFullPath).Distinct(WorkspacePaths.Comparer).Take(MaxImages).ToArray();
            if (LastSaveError is null && _files.TryGetValue(key, out var previous) && previous.SequenceEqual(selected, WorkspacePaths.Comparer)) return true;
            if (!_files.ContainsKey(key) && _files.Count >= MaxDocuments)
                throw new IOException("The linked-image history is full (500 documents).");
            _files[key] = selected;
            AtomicFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(new { version = 1, files = _files }));
            LastSaveError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { LastSaveError = ex.Message; return false; }
    }

    private static bool ValidPath(string path) => path.Length is > 0 and <= 4096
        && path.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathFullyQualified(path);
}
