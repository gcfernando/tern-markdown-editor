using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Tern.Markdown;
using Tern.Workspaces;
using Tern.Domain.Text;

namespace Tern.App.Settings;

public enum ThemeChoice
{
    Midnight,
    Paper,
    System,
    Graphite,
    Harbor,
    Linen,
    Mist,
}

public enum ViewMode
{
    Source,
    Split,
    Reading,
}

public enum RemoteImagePolicy { Ask, Always, Never }

/// <summary>User preferences. Every property has a safe default so a missing or partial file never breaks startup.</summary>
public sealed class AppSettings
{
    public const double MinimumUiScale = 11.0 / 12;
    public const int CurrentVersion = 2;
    public int Version { get; set; } = CurrentVersion;

    public ThemeChoice Theme { get; set; } = ThemeChoice.System;

    public TextEncodingKind DefaultEncoding { get; set; } = TextEncodingKind.Utf8;
    public bool DefaultEncodingHasBom { get; set; }
    public LineEnding DefaultLineEnding { get; set; } = LineEnding.CrLf;

    /// <summary>Only for newly created files; loaded and restored documents retain their own format.</summary>
    [JsonIgnore]
    public TextFileFormat NewFileFormat => TextFileFormat.NewFileDefault with
    {
        Encoding = TextEncodingIds.IsSupported(DefaultEncoding) ? DefaultEncoding : TextEncodingKind.Utf8,
        HasBom = DefaultEncoding is TextEncodingKind.Utf16LittleEndian or TextEncodingKind.Utf16BigEndian
            or TextEncodingKind.Utf32LittleEndian or TextEncodingKind.Utf32BigEndian
            || (DefaultEncoding == TextEncodingKind.Utf8 && DefaultEncodingHasBom),
        DominantLineEnding = DefaultLineEnding is LineEnding.Lf or LineEnding.CrLf or LineEnding.Cr ? DefaultLineEnding : LineEnding.CrLf,
    };

    public string EditorFontFamily { get; set; } = "Cascadia Code, Consolas, Menlo, monospace";

    public double EditorFontSize { get; set; } = 14;

    public double PreviewFontSize { get; set; } = 16;
    public string PreviewFontFamily { get; set; } = "Segoe UI, Noto Sans, Helvetica, Arial, sans-serif";

    public double ReadingWidth { get; set; } = 760;

    public bool PreviewFullWidth { get; set; } = true;

    public bool WordWrap { get; set; } = true;

    public bool ShowLineNumbers { get; set; } = true;

    public bool HighlightCurrentLine { get; set; } = true;
    public bool ShowSpaces { get; set; }
    public bool ShowTabs { get; set; }
    public bool ShowEndOfLine { get; set; }
    public bool ShowColumnRuler { get; set; }
    public int ColumnRulerPosition { get; set; } = 80;
    public double EditorLineHeight { get; set; } = 1.16;
    public bool AllowScrollBelowDocument { get; set; } = true;
    public bool CutCopyWholeLine { get; set; } = true;
    public bool SpellCheckEnabled { get; set; } = true;
    public bool HybridSourceEnabled { get; set; }
    public bool AutoSaveEnabled { get; set; }
    public int AutoSaveIdleSeconds { get; set; } = 30;
    public ExportPreferences ExportPreferences { get; set; } = new();

    public int TabSize { get; set; } = 4;

    /// <summary>Whether the Tab key inserts spaces (true) or a tab character.</summary>
    public bool InsertSpacesForTab { get; set; } = true;

    public MarkdownProfile DefaultProfile { get; set; } = MarkdownProfile.Extended;

    public ViewMode DefaultViewMode { get; set; } = ViewMode.Split;

    public bool RestoreSession { get; set; } = true;
    public bool OnboardingDismissed { get; set; }
    public bool ShowContextGuidance { get; set; } = true;

    /// <summary>Disk budget, in MiB, for the saved-version history of one document (1-1024). Applies after restart.</summary>
    public int HistoryBudgetMiB { get; set; } = 32;
    /// <summary>Reload an unmodified document automatically when it changes on disk; modified ones always ask.</summary>
    public bool AutoReloadCleanDocuments { get; set; } = true;

    public bool EnsureFinalNewlineOnSave { get; set; }

    public bool TrimTrailingWhitespaceOnSave { get; set; }

    public bool SyncScroll { get; set; } = true;

    public bool ReduceMotion { get; set; }

    public double UiScale { get; set; } = 1.0;

    /// <summary>Also multiply the editor and preview text sizes by <see cref="UiScale"/>.</summary>
    public bool ScaleDocumentText { get; set; }

    /// <summary>Multiplier applied to editor and preview font sizes.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double DocumentTextScale => ScaleDocumentText ? Bounded(UiScale, MinimumUiScale, 2.5, 1.0) : 1.0;

    public double SidebarWidth { get; set; } = 280;

    public bool SidebarVisible { get; set; } = true;
    public bool InspectorVisible { get; set; } = true;
    public double InspectorWidth { get; set; } = 260;
    public double SourceShare { get; set; } = .5;

    /// <summary>Overrides of the default key bindings: command id to gesture text such as "Ctrl+Shift+P".</summary>
    public Dictionary<string, string> KeyBindings { get; set; } = [];

    public List<string> WorkspaceExclusions { get; set; } = [];
    public Dictionary<string, List<string>> SavedWorkspaceSearches { get; set; } = [];
    public bool FindMatchCase { get; set; }
    public bool FindWholeWord { get; set; }
    public bool FindUseRegex { get; set; }
    public bool TaskGroupByFile { get; set; } = true;

    public bool AllowExternalLinkOpening { get; set; } = true;

    public RemoteImagePolicy RemoteImages { get; set; } = RemoteImagePolicy.Ask;
    public bool OfflineMode { get; set; }
    public bool KnowledgeAssistantEnabled { get; set; } = true;
    public bool KnowledgeAllowRemote { get; set; }
    /// <summary>Document identity keys approved explicitly by the user; never image URLs.</summary>
    public List<string> RemoteImageTrustedFiles { get; set; } = [];

    /// <summary>Clamps values that a hand-edited file could push to unusable extremes.</summary>
    public void Normalize()
    {
        EditorFontSize = Bounded(EditorFontSize, 8, 48, 14);
        PreviewFontSize = Bounded(PreviewFontSize, 8, 48, 16);
        EditorLineHeight = Bounded(EditorLineHeight, 1, 2, 1.16);
        ColumnRulerPosition = Math.Clamp(ColumnRulerPosition, 40, 240);
        ReadingWidth = Bounded(ReadingWidth, 360, 1600, 760);
        TabSize = Math.Clamp(TabSize, 1, 16);
        AutoSaveIdleSeconds = Math.Clamp(AutoSaveIdleSeconds, 30, 3600);
        ExportPreferences ??= new();
        ExportPreferences.Normalize();
        HistoryBudgetMiB = Math.Clamp(HistoryBudgetMiB, 1, 1024);
        UiScale = Bounded(UiScale, MinimumUiScale, 2.5, 1.0);
        SidebarWidth = Bounded(SidebarWidth, 180, 800, 280);
        InspectorWidth = Bounded(InspectorWidth, 180, 800, 260);
        SourceShare = Bounded(SourceShare, .15, .85, .5);
        if (string.IsNullOrWhiteSpace(EditorFontFamily))
        {
            EditorFontFamily = new AppSettings().EditorFontFamily;
        }
        if (string.IsNullOrWhiteSpace(PreviewFontFamily)) PreviewFontFamily = new AppSettings().PreviewFontFamily;

        KeyBindings ??= [];
        WorkspaceExclusions ??= [];
        SavedWorkspaceSearches ??= [];
        RemoteImageTrustedFiles ??= [];
        if (!Enum.IsDefined(Theme)) Theme = ThemeChoice.System;
        if (!TextEncodingIds.IsSupported(DefaultEncoding)) DefaultEncoding = TextEncodingKind.Utf8;
        DefaultEncodingHasBom = NewFileFormat.HasBom;
        if (DefaultLineEnding is not (LineEnding.Lf or LineEnding.CrLf or LineEnding.Cr)) DefaultLineEnding = LineEnding.CrLf;
        if (!Enum.IsDefined(DefaultProfile)) DefaultProfile = MarkdownProfile.Extended;
        if (!Enum.IsDefined(DefaultViewMode)) DefaultViewMode = ViewMode.Split;
        if (!Enum.IsDefined(RemoteImages)) RemoteImages = RemoteImagePolicy.Ask;
    }

    // Math.Clamp passes NaN straight through, so a non-finite value falls back to the default instead.
    private static double Bounded(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON, atomically, tolerating corrupt or missing files.</summary>
public sealed class SettingsStore(string directory)
{
    private static readonly Lock SaveGate = new();
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string FilePath { get; } = Path.Combine(directory, "settings.json");
    private string BackupPath => FilePath + ".bak";
    private bool _saveBlocked;

    /// <summary>Load recovery or invalid-property details for the startup warning.</summary>
    public string? LastLoadProblem { get; private set; }

    public AppSettings Load()
    {
        lock (SaveGate)
        {
            LastLoadProblem = null;
            _saveBlocked = false;
            try
            {
                var settings = Deserialize(ReadWithRetry(FilePath), out var invalid);
                if (invalid.Count > 0)
                    LastLoadProblem = $"Unrecognized settings values were reset to defaults: {string.Join(", ", invalid)}. Other preferences and shortcuts were kept.";
                return settings;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return LoadBackupOrDefaults(null);
            }
            catch (JsonException ex)
            {
                var problem = "The settings file is invalid: " + ex.Message;
                try
                {
                    var preserved = $"{FilePath}.{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.{Guid.NewGuid():N}.corrupt";
                    File.Move(FilePath, preserved);
                    problem += $" Its original bytes are preserved at '{preserved}'.";
                }
                catch (Exception preserveError) when (preserveError is IOException or UnauthorizedAccessException)
                {
                    _saveBlocked = true;
                    problem += " The original could not be preserved; automatic settings saves are blocked: " + preserveError.Message;
                }
                return LoadBackupOrDefaults(problem);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _saveBlocked = true;
                return LoadBackupOrDefaults("Settings could not be read after retry. The original was not renamed; automatic settings saves are blocked until a successful reload. " + ex.Message);
            }
        }
    }

    private AppSettings LoadBackupOrDefaults(string? problem)
    {
        try
        {
            var bytes = ReadWithRetry(BackupPath);
            var settings = Deserialize(bytes, out var invalid);
            LastLoadProblem = "Settings were restored from backup. " + (problem ?? "The primary settings file was missing.");
            if (invalid.Count > 0)
                LastLoadProblem += $" Unrecognized backup values were defaulted: {string.Join(", ", invalid)}.";
            if (!_saveBlocked)
            {
                try { AtomicFile.WriteAllBytes(FilePath, bytes); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _saveBlocked = true;
                    LastLoadProblem += " The restored preferences are in use, but the primary file could not be repaired: " + ex.Message;
                }
            }
            return settings;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            if (problem is not null) LastLoadProblem = "Settings were restored to defaults. " + problem + " No backup was available.";
            return new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _saveBlocked = true;
            LastLoadProblem = "Settings were restored to defaults. " + problem + " The backup could not be read; automatic settings saves are blocked: " + ex.Message;
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        lock (SaveGate)
        {
            if (_saveBlocked)
                throw new IOException("Automatic settings saves are blocked because the original preferences could not be read or preserved. Reload settings or restart Tern after resolving the reported problem.");
            Migrate(settings);
            settings.Normalize();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, Options);
            if (File.Exists(FilePath))
            {
                var previous = ReadWithRetry(FilePath);
                try { _ = Deserialize(previous, out _); }
                catch (JsonException ex) { throw new IOException("The existing settings file is invalid. Reload settings before replacing it so its original bytes can be preserved.", ex); }
                AtomicFile.WriteAllBytes(BackupPath, previous);
            }
            else if (!File.Exists(BackupPath))
                AtomicFile.WriteAllBytes(BackupPath, bytes);
            AtomicFile.WriteAllBytes(FilePath, bytes);
        }
    }

    private static byte[] ReadWithRetry(string path)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try { return File.ReadAllBytes(path); }
            catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException
                && elapsed.Elapsed < TimeSpan.FromSeconds(3))
            { Thread.Sleep(100); }
        }
    }

    private static AppSettings Deserialize(byte[] bytes, out List<string> invalid)
    {
        var payload = bytes.AsSpan();
        if (payload.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) payload = payload[3..];
        var root = JsonNode.Parse(payload, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) as JsonObject ?? throw new JsonException("Settings must be a JSON object.");
        invalid = [];
        ValidateEnum<ThemeChoice>(root, nameof(AppSettings.Theme), invalid);
        ValidateEnum<LineEnding>(root, nameof(AppSettings.DefaultLineEnding), invalid);
        if (root.TryGetPropertyValue(nameof(AppSettings.DefaultLineEnding), out var ending)
            && Enum.TryParse<LineEnding>(ending?.ToString(), true, out var parsedEnding) && parsedEnding == LineEnding.None)
        { root.Remove(nameof(AppSettings.DefaultLineEnding)); invalid.Add(nameof(AppSettings.DefaultLineEnding)); }
        if (root.TryGetPropertyValue(nameof(AppSettings.DefaultEncoding), out var encoding))
        {
            var text = encoding?.ToString();
            if (!Enum.TryParse<TextEncodingKind>(text, true, out var parsed) || !TextEncodingIds.IsSupported(parsed))
            { root.Remove(nameof(AppSettings.DefaultEncoding)); invalid.Add(nameof(AppSettings.DefaultEncoding)); }
        }
        ValidateEnum<MarkdownProfile>(root, nameof(AppSettings.DefaultProfile), invalid);
        ValidateEnum<ViewMode>(root, nameof(AppSettings.DefaultViewMode), invalid);
        ValidateEnum<RemoteImagePolicy>(root, nameof(AppSettings.RemoteImages), invalid);
        if (!root.ContainsKey(nameof(AppSettings.Version))) root[nameof(AppSettings.Version)] = 1;
        var settings = root.Deserialize<AppSettings>(Options) ?? throw new JsonException("Settings are empty.");
        Migrate(settings);
        settings.Normalize();
        return settings;
    }

    private static void ValidateEnum<T>(JsonObject root, string property, List<string> invalid) where T : struct, Enum
    {
        if (!root.TryGetPropertyValue(property, out var node)) return;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text) && Enum.TryParse<T>(text, true, out var parsed) && Enum.IsDefined(parsed)) return;
            if (value.TryGetValue<int>(out var number) && Enum.IsDefined(typeof(T), number)) return;
        }
        root.Remove(property);
        invalid.Add(property);
    }

    private static void Migrate(AppSettings settings)
    {
        switch (settings.Version)
        {
            case 0:
            case 1:
                settings.KeyBindings ??= [];
                settings.WorkspaceExclusions ??= [];
                settings.RemoteImageTrustedFiles ??= [];
                settings.Version = AppSettings.CurrentVersion;
                break;
            case < 0:
                throw new JsonException("The settings version cannot be negative.");
        }
    }
}
