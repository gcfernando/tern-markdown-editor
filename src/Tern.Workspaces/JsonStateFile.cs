using System.Text.Json;

namespace Tern.Workspaces;

public enum StoreLoadStatus
{
    /// <summary>Loaded normally.</summary>
    Loaded,

    /// <summary>There was no file yet.</summary>
    Missing,

    /// <summary>The file could not be parsed; it was set aside and the store started empty.</summary>
    Corrupt,

    /// <summary>The file was written by a newer version of Tern. Readable fields were used and a backup copy was kept.</summary>
    NewerVersion,
}

/// <summary>Shared plumbing for the small JSON state files (recent items, session): tolerant reads, atomic writes.</summary>
internal static class JsonStateFile
{
    public static readonly JsonWriterOptions WriterOptions = new() { Indented = true };

    /// <summary>Reads and parses the file. On failure the file is quarantined and <paramref name="document"/> is null.</summary>
    public static StoreLoadStatus Read(string path, int currentVersion, out JsonDocument? document)
    {
        document = null;
        byte[] bytes;
        try
        {
            if (!File.Exists(path))
            {
                return StoreLoadStatus.Missing;
            }

            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StoreLoadStatus.Corrupt;
        }

        try
        {
            document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException)
        {
            AtomicFile.Quarantine(path, "corrupt");
            return StoreLoadStatus.Corrupt;
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            document = null;
            AtomicFile.Quarantine(path, "corrupt");
            return StoreLoadStatus.Corrupt;
        }

        var version = GetInt(document.RootElement, "version", 1);
        if (version > currentVersion)
        {
            TryCopy(path, $"{path}.v{version}.bak");
            return StoreLoadStatus.NewerVersion;
        }

        return StoreLoadStatus.Loaded;
    }

    public static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    public static int GetInt(JsonElement element, string name, int fallback) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : fallback;

    public static double? GetDouble(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var v) && double.IsFinite(v) ? v : null;

    public static bool GetBool(JsonElement element, string name, bool fallback) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False ? p.GetBoolean() : fallback;

    public static DateTime? GetDate(JsonElement element, string name) =>
        GetString(element, name) is { } text && DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;

    public static IEnumerable<JsonElement> GetArray(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray()
            : [];

    private static void TryCopy(string source, string target)
    {
        try
        {
            if (!File.Exists(target))
            {
                File.Copy(source, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the backup is a courtesy for downgrades.
        }
    }
}
