using System.Text.Json;
using System.Text.Json.Serialization;
using Tern.Markdown;

namespace Tern.App.Settings;

public enum ThemeChoice
{
    Midnight,
    Paper,
    System,
}

public enum ViewMode
{
    Source,
    Split,
    Reading,
}

/// <summary>User preferences. Every property has a safe default so a missing or partial file never breaks startup.</summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;

    public ThemeChoice Theme { get; set; } = ThemeChoice.System;

    public string EditorFontFamily { get; set; } = "Cascadia Code, Consolas, Menlo, monospace";

    public double EditorFontSize { get; set; } = 14;

    public double PreviewFontSize { get; set; } = 16;

    public double ReadingWidth { get; set; } = 760;

    public bool PreviewFullWidth { get; set; } = true;

    public bool WordWrap { get; set; } = true;

    public bool ShowLineNumbers { get; set; } = true;

    public bool HighlightCurrentLine { get; set; } = true;

    public int TabSize { get; set; } = 4;

    /// <summary>Whether the Tab key inserts spaces (true) or a tab character.</summary>
    public bool InsertSpacesForTab { get; set; } = true;

    public MarkdownProfile DefaultProfile { get; set; } = MarkdownProfile.Extended;

    public ViewMode DefaultViewMode { get; set; } = ViewMode.Split;

    public bool RestoreSession { get; set; } = true;

    /// <summary>Reload an unmodified document automatically when it changes on disk; modified ones always ask.</summary>
    public bool AutoReloadCleanDocuments { get; set; } = true;

    public bool EnsureFinalNewlineOnSave { get; set; }

    public bool TrimTrailingWhitespaceOnSave { get; set; }

    public bool SyncScroll { get; set; } = true;

    public bool ReduceMotion { get; set; }

    public double UiScale { get; set; } = 1.0;

    public double SidebarWidth { get; set; } = 280;

    public bool SidebarVisible { get; set; } = true;

    /// <summary>Overrides of the default key bindings: command id to gesture text such as "Ctrl+Shift+P".</summary>
    public Dictionary<string, string> KeyBindings { get; set; } = [];

    public List<string> WorkspaceExclusions { get; set; } = [];

    public bool AllowExternalLinkOpening { get; set; } = true;

    /// <summary>Clamps values that a hand-edited file could push to unusable extremes.</summary>
    public void Normalize()
    {
        EditorFontSize = Bounded(EditorFontSize, 8, 48, 14);
        PreviewFontSize = Bounded(PreviewFontSize, 8, 48, 16);
        ReadingWidth = Bounded(ReadingWidth, 360, 1600, 760);
        TabSize = Math.Clamp(TabSize, 1, 16);
        UiScale = Bounded(UiScale, 0.75, 2.5, 1.0);
        SidebarWidth = Bounded(SidebarWidth, 180, 800, 280);
        if (string.IsNullOrWhiteSpace(EditorFontFamily))
        {
            EditorFontFamily = new AppSettings().EditorFontFamily;
        }

        KeyBindings ??= [];
        WorkspaceExclusions ??= [];
    }

    // Math.Clamp passes NaN straight through, so a non-finite value falls back to the default instead.
    private static double Bounded(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON, atomically, tolerating corrupt or missing files.</summary>
public sealed class SettingsStore(string directory)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string FilePath { get; } = Path.Combine(directory, "settings.json");

    /// <summary>Set when the last load found an unreadable file; the original is preserved next to it as .corrupt.</summary>
    public string? LastLoadProblem { get; private set; }

    public AppSettings Load()
    {
        LastLoadProblem = null;
        if (!File.Exists(FilePath))
        {
            return new AppSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
            settings.Normalize();
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            LastLoadProblem = $"Settings could not be read ({ex.Message}); defaults are in use.";
            TryPreserveCorrupt();
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        settings.Normalize();
        Directory.CreateDirectory(directory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        File.Move(temp, FilePath, overwrite: true);
    }

    private void TryPreserveCorrupt()
    {
        try
        {
            File.Move(FilePath, FilePath + ".corrupt", overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
