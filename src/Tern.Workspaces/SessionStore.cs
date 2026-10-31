using System.Text.Json;

namespace Tern.Workspaces;

public sealed record TabState(string Path, int CaretOffset, double ScrollOffset);

public sealed record FileViewState(int CaretOffset, double ScrollOffset);

/// <summary>Window geometry hint; the app validates it against the current monitors before applying.</summary>
public sealed record WindowLayoutHint(double? Left, double? Top, double? Width, double? Height, bool Maximized, double? SidebarWidth, bool SidebarVisible);

public sealed record SessionSnapshot(
    string? LastWorkspace,
    IReadOnlyList<TabState> Tabs,
    string? ActiveTab,
    WindowLayoutHint? Window,
    IReadOnlyDictionary<string, FileViewState> FileViews)
{
    public int? ActiveTabIndex { get; init; }
    public IReadOnlyList<string> OpenWorkspaceRoots { get; init; } = [];
    public IReadOnlyList<string> OpenFolderRoots { get; init; } = [];
    public static SessionSnapshot Empty { get; } = new(null, [], null, null, new Dictionary<string, FileViewState>());
}

/// <summary>
/// Persists open tabs, active tab, per-file caret/scroll, window layout and last workspace as <c>session.json</c>.
/// Atomic writes, versioned, tolerant loading; values are sanitised so a hand-edited file cannot crash the app.
/// </summary>
public sealed class SessionStore
{
    public const int CurrentVersion = 1;
    public const string FileName = "session.json";
    public const int MaxTabs = 200;
    public const int MaxFileViews = 500;
    public const int MaxWorkspaces = 32;

    private readonly string _path;
    private readonly Lock _gate = new();

    public SessionStore(string baseDirectory)
    {
        _path = Path.Combine(baseDirectory, FileName);
    }

    public Exception? LastSaveError { get; private set; }

    public StoreLoadStatus LastLoadStatus { get; private set; } = StoreLoadStatus.Missing;

    public SessionSnapshot Load()
    {
        lock (_gate)
        {
            var status = JsonStateFile.Read(_path, CurrentVersion, out var document);
            LastLoadStatus = status;
            if (document is null)
            {
                return SessionSnapshot.Empty;
            }

            using (document)
            {
                return Parse(document.RootElement);
            }
        }
    }

    public bool Save(SessionSnapshot session)
    {
        lock (_gate)
        {
            try
            {
                using var buffer = new MemoryStream();
                using (var writer = new Utf8JsonWriter(buffer, JsonStateFile.WriterOptions))
                {
                    Write(writer, session);
                }

                AtomicFile.WriteAllBytes(_path, buffer.ToArray());
                LastSaveError = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LastSaveError = ex;
                return false;
            }
        }
    }

    private static SessionSnapshot Parse(JsonElement root)
    {
        var tabs = new List<TabState>();
        var tabPaths = new HashSet<string>(WorkspacePaths.Comparer);
        foreach (var element in JsonStateFile.GetArray(root, "tabs"))
        {
            var path = JsonStateFile.GetString(element, "path");
            if (!TryNormalizeStoredPath(path, out var normalized) || tabs.Count >= MaxTabs)
            {
                continue;
            }

            tabPaths.Add(normalized);

            tabs.Add(new TabState(path!, Math.Max(0, JsonStateFile.GetInt(element, "caret", 0)), Math.Max(0, JsonStateFile.GetDouble(element, "scroll") ?? 0)));
        }

        var active = JsonStateFile.GetString(root, "activeTab");
        if (!TryNormalizeStoredPath(active, out var activePath) || !tabPaths.Contains(activePath))
        {
            active = null;
        }

        var views = new Dictionary<string, FileViewState>(WorkspacePaths.Comparer);
        var viewPaths = new HashSet<string>(WorkspacePaths.Comparer);
        foreach (var element in JsonStateFile.GetArray(root, "fileViews"))
        {
            var path = JsonStateFile.GetString(element, "path");
            if (TryNormalizeStoredPath(path, out var normalized) && views.Count < MaxFileViews && viewPaths.Add(normalized))
            {
                views[path!] = new FileViewState(Math.Max(0, JsonStateFile.GetInt(element, "caret", 0)), Math.Max(0, JsonStateFile.GetDouble(element, "scroll") ?? 0));
            }
        }

        WindowLayoutHint? window = null;
        if (root.TryGetProperty("window", out var w) && w.ValueKind == JsonValueKind.Object)
        {
            window = new WindowLayoutHint(
                JsonStateFile.GetDouble(w, "left"),
                JsonStateFile.GetDouble(w, "top"),
                Positive(JsonStateFile.GetDouble(w, "width")),
                Positive(JsonStateFile.GetDouble(w, "height")),
                JsonStateFile.GetBool(w, "maximized", false),
                Positive(JsonStateFile.GetDouble(w, "sidebarWidth")),
                JsonStateFile.GetBool(w, "sidebarVisible", true));
        }

        var workspaceRoots = new List<string>();
        foreach (var item in JsonStateFile.GetArray(root, "openWorkspaces"))
        {
            if (item.ValueKind != JsonValueKind.String || !TryNormalizeStoredPath(item.GetString(), out var normalized)) continue;
            if (workspaceRoots.Count < MaxWorkspaces && !workspaceRoots.Contains(normalized, WorkspacePaths.Comparer)) workspaceRoots.Add(normalized);
        }
        var previousWorkspace = JsonStateFile.GetString(root, "lastWorkspace");
        var folderRoots = new List<string>();
        foreach (var item in JsonStateFile.GetArray(root, "openFolders"))
        {
            if (item.ValueKind != JsonValueKind.String || !TryNormalizeStoredPath(item.GetString(), out var normalized)) continue;
            if (folderRoots.Count < MaxWorkspaces && !folderRoots.Contains(normalized, WorkspacePaths.Comparer)) folderRoots.Add(normalized);
        }
        if (!TryNormalizeStoredPath(previousWorkspace, out _)) previousWorkspace = null;
        var activeIndex = JsonStateFile.GetInt(root, "activeTabIndex", -1);
        return new SessionSnapshot(previousWorkspace, tabs, active, window, views)
        { OpenWorkspaceRoots = workspaceRoots, OpenFolderRoots = folderRoots,
            ActiveTabIndex = activeIndex >= 0 && activeIndex < tabs.Count ? activeIndex : null };
    }

    private static double? Positive(double? value) => value is > 0 ? value : null;

    /// <summary>Malformed records grant no path identity; retain valid legacy spellings in the snapshot.</summary>
    private static bool TryNormalizeStoredPath(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
        try { normalized = WorkspacePaths.Normalize(path); return true; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static void Write(Utf8JsonWriter writer, SessionSnapshot session)
    {
        writer.WriteStartObject();
        writer.WriteNumber("version", CurrentVersion);
        if (session.LastWorkspace is not null)
        {
            writer.WriteString("lastWorkspace", session.LastWorkspace);
        }

        if (session.ActiveTab is not null)
        {
            writer.WriteString("activeTab", session.ActiveTab);
        }

        if (session.ActiveTabIndex is { } activeIndex && activeIndex >= 0 && activeIndex < session.Tabs.Count)
            writer.WriteNumber("activeTabIndex", activeIndex);

        writer.WriteStartArray("openWorkspaces");
        foreach (var workspace in session.OpenWorkspaceRoots.Distinct(WorkspacePaths.Comparer).Take(MaxWorkspaces)) writer.WriteStringValue(workspace);
        writer.WriteEndArray();

        writer.WriteStartArray("openFolders");
        foreach (var folder in session.OpenFolderRoots.Distinct(WorkspacePaths.Comparer).Take(MaxWorkspaces)) writer.WriteStringValue(folder);
        writer.WriteEndArray();

        writer.WriteStartArray("tabs");
        foreach (var tab in session.Tabs.Take(MaxTabs))
        {
            writer.WriteStartObject();
            writer.WriteString("path", tab.Path);
            writer.WriteNumber("caret", tab.CaretOffset);
            writer.WriteNumber("scroll", tab.ScrollOffset);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("fileViews");
        foreach (var (path, view) in session.FileViews.Take(MaxFileViews))
        {
            writer.WriteStartObject();
            writer.WriteString("path", path);
            writer.WriteNumber("caret", view.CaretOffset);
            writer.WriteNumber("scroll", view.ScrollOffset);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        if (session.Window is { } w)
        {
            writer.WriteStartObject("window");
            WriteOptional(writer, "left", w.Left);
            WriteOptional(writer, "top", w.Top);
            WriteOptional(writer, "width", w.Width);
            WriteOptional(writer, "height", w.Height);
            writer.WriteBoolean("maximized", w.Maximized);
            WriteOptional(writer, "sidebarWidth", w.SidebarWidth);
            writer.WriteBoolean("sidebarVisible", w.SidebarVisible);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } v && double.IsFinite(v))
        {
            writer.WriteNumber(name, v);
        }
    }
}
