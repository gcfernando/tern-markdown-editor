using System.Text.Json;

namespace Tern.Workspaces;

public enum RecentKind
{
    File,
    Workspace,
    Url,
}

public sealed record RecentEntry(string Path, DateTime LastOpenedUtc, bool Pinned);

/// <summary>
/// Recently opened files, workspaces and URL references with pinning. Stored as <c>recent.json</c> in the base directory
/// (the app passes <c>%APPDATA%\Tern</c>), written atomically, tolerant of a missing, damaged or newer file,
/// and bounded: pinned entries are never evicted, unpinned ones are trimmed oldest-first.
/// </summary>
public sealed class RecentItemsStore
{
    public const int CurrentVersion = 1;
    public const string FileName = "recent.json";
    private const int MaxPinned = 100;

    private readonly string _path;
    private readonly int _maxFiles;
    private readonly int _maxWorkspaces;
    private readonly int _maxUrls;
    private readonly Func<DateTime> _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<RecentKind, List<RecentEntry>> _items = new()
    {
        [RecentKind.File] = [],
        [RecentKind.Workspace] = [],
        [RecentKind.Url] = [],
    };

    public RecentItemsStore(string baseDirectory, int maxFiles = 30, int maxWorkspaces = 15, Func<DateTime>? clock = null, int maxUrls = 30)
    {
        _path = Path.Combine(baseDirectory, FileName);
        _maxFiles = Math.Max(1, maxFiles);
        _maxWorkspaces = Math.Max(1, maxWorkspaces);
        _maxUrls = Math.Max(1, maxUrls);
        _clock = clock ?? (() => DateTime.UtcNow);
        LoadStatus = Load();
    }

    public StoreLoadStatus LoadStatus { get; }
    public event EventHandler? Changed;
    public void ClearAll()
    {
        lock (_gate)
        {
            foreach (var list in _items.Values) list.Clear();
            Persist();
        }
    }

    /// <summary>The last write failure, if any. In-memory state stays correct even when the disk write fails.</summary>
    public Exception? LastSaveError { get; private set; }

    public IReadOnlyList<RecentEntry> Files => Get(RecentKind.File);

    public IReadOnlyList<RecentEntry> Workspaces => Get(RecentKind.Workspace);

    public IReadOnlyList<RecentEntry> Urls => Get(RecentKind.Url);

    /// <summary>Entries of one kind: pinned first, then most recently opened first.</summary>
    public IReadOnlyList<RecentEntry> Get(RecentKind kind)
    {
        lock (_gate)
        {
            return [.. _items[kind].OrderByDescending(e => e.Pinned).ThenByDescending(e => e.LastOpenedUtc)];
        }
    }

    public void AddFile(string path) => Add(RecentKind.File, path);

    public void AddWorkspace(string path) => Add(RecentKind.Workspace, path);

    public void AddUrl(string url) => Add(RecentKind.Url, url);

    /// <summary>Records that an item was just opened, moving it to the top and keeping its pinned state.</summary>
    public void Add(RecentKind kind, string path)
    {
        var normalized = Normalize(kind, path);
        lock (_gate)
        {
            var list = _items[kind];
            var index = IndexOf(kind, list, normalized);
            var pinned = index >= 0 && list[index].Pinned;
            if (index >= 0)
            {
                list.RemoveAt(index);
            }

            list.Add(new RecentEntry(normalized, _clock(), pinned));
            Trim(kind);
            Persist();
        }
    }

    /// <returns>False when the item is not in the list, or the pin limit has been reached.</returns>
    public bool SetPinned(RecentKind kind, string path, bool pinned)
    {
        var normalized = Normalize(kind, path);
        lock (_gate)
        {
            var list = _items[kind];
            var index = IndexOf(kind, list, normalized);
            if (index < 0 || (pinned && !list[index].Pinned && list.Count(e => e.Pinned) >= MaxPinned))
            {
                return false;
            }

            list[index] = list[index] with { Pinned = pinned };
            Trim(kind);
            Persist();
            return true;
        }
    }

    public bool Remove(RecentKind kind, string path)
    {
        var normalized = Normalize(kind, path);
        lock (_gate)
        {
            var index = IndexOf(kind, _items[kind], normalized);
            if (index < 0)
            {
                return false;
            }

            _items[kind].RemoveAt(index);
            Persist();
            return true;
        }
    }

    /// <summary>Removes unpinned entries of a kind.</summary>
    public void Clear(RecentKind kind)
    {
        lock (_gate)
        {
            _items[kind].RemoveAll(e => !e.Pinned);
            Persist();
        }
    }

    /// <summary>A file was renamed or moved: carry its history along.</summary>
    public void Rename(RecentKind kind, string oldPath, string newPath)
    {
        var from = Normalize(kind, oldPath);
        var to = Normalize(kind, newPath);
        lock (_gate)
        {
            var list = _items[kind];
            var index = IndexOf(kind, list, from);
            if (index < 0)
            {
                return;
            }

            var entry = list[index] with { Path = to };
            list.RemoveAt(index);
            var existing = IndexOf(kind, list, to);
            if (existing >= 0)
            {
                list.RemoveAt(existing);
            }

            list.Add(entry);
            Persist();
        }
    }

    private static string Normalize(RecentKind kind, string path) => kind == RecentKind.Url ? NormalizeUrl(path) : WorkspacePaths.Normalize(path);

    /// <summary>Storage canonicalization only; never permission to fetch. The URL loader validates its network destination on every explicit reopen.</summary>
    public static string NormalizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 8192 || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException("Use an HTTP or HTTPS URL without a username or password.");
        return new UriBuilder(uri) { Host = uri.IdnHost, Fragment = string.Empty }.Uri.AbsoluteUri;
    }

    private static int IndexOf(RecentKind kind, List<RecentEntry> list, string normalized) =>
        list.FindIndex(e => (kind == RecentKind.Url ? StringComparer.Ordinal : WorkspacePaths.Comparer).Equals(e.Path, normalized));

    // Caller holds the lock.
    private void Trim(RecentKind kind)
    {
        var limit = kind switch { RecentKind.File => _maxFiles, RecentKind.Url => _maxUrls, _ => _maxWorkspaces };
        var list = _items[kind];
        var unpinned = list.Where(e => !e.Pinned).OrderByDescending(e => e.LastOpenedUtc).ToList();
        foreach (var doomed in unpinned.Skip(limit))
        {
            list.Remove(doomed);
        }
    }

    private StoreLoadStatus Load()
    {
        var status = JsonStateFile.Read(_path, CurrentVersion, out var document);
        if (document is null)
        {
            return status;
        }

        using (document)
        {
            LoadKind(document.RootElement, "files", RecentKind.File);
            LoadKind(document.RootElement, "workspaces", RecentKind.Workspace);
            LoadKind(document.RootElement, "urls", RecentKind.Url);
        }

        return status;
    }

    private void LoadKind(JsonElement root, string property, RecentKind kind)
    {
        var list = _items[kind];
        foreach (var element in JsonStateFile.GetArray(root, property))
        {
            var path = JsonStateFile.GetString(element, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string normalized;
            try
            {
                normalized = Normalize(kind, path);
            }
            catch (Exception ex) when (ex is ArgumentException or UriFormatException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (IndexOf(kind, list, normalized) >= 0)
            {
                continue;
            }

            var opened = JsonStateFile.GetDate(element, "lastOpenedUtc") ?? DateTime.MinValue;
            list.Add(new RecentEntry(normalized, opened, JsonStateFile.GetBool(element, "pinned", false)));
        }

        Trim(kind);
    }

    // Caller holds the lock.
    private void Persist()
    {
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, JsonStateFile.WriterOptions))
            {
                writer.WriteStartObject();
                writer.WriteNumber("version", CurrentVersion);
                WriteKind(writer, "files", RecentKind.File);
                WriteKind(writer, "workspaces", RecentKind.Workspace);
                WriteKind(writer, "urls", RecentKind.Url);
                writer.WriteEndObject();
            }

            AtomicFile.WriteAllBytes(_path, buffer.ToArray());
            LastSaveError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastSaveError = ex;
        }
    }

    private void WriteKind(Utf8JsonWriter writer, string name, RecentKind kind)
    {
        writer.WriteStartArray(name);
        foreach (var entry in _items[kind].OrderByDescending(e => e.LastOpenedUtc))
        {
            writer.WriteStartObject();
            writer.WriteString("path", entry.Path);
            writer.WriteString("lastOpenedUtc", entry.LastOpenedUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteBoolean("pinned", entry.Pinned);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
