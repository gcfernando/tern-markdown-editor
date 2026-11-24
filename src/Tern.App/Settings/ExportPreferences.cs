namespace Tern.App.Settings;

public sealed record ExportPreset(string Paper = "A4", decimal Margin = 20, bool PageNumbers = true,
    string Theme = "Light", string Language = "en", bool IncludeFrontMatter = false);

public sealed class ExportPreferences
{
    public Dictionary<string, ExportPreset> Formats { get; set; } = [];
    public Dictionary<string, string> WorkspaceFolders { get; set; } = [];

    public ExportPreferences Clone() => new()
    {
        Formats = new(Formats),
        WorkspaceFolders = new(WorkspaceFolders)
    };

    public void Normalize()
    {
        Formats ??= [];
        WorkspaceFolders ??= [];
        foreach (var key in Formats.Keys.ToArray())
        {
            if (Formats[key] is not { } preset) { Formats.Remove(key); continue; }
            Formats[key] = preset with { Margin = Math.Clamp(preset.Margin, 10, 40), Language = preset.Language ?? "en" };
        }
    }
}
