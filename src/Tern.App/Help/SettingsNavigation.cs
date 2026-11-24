namespace Tern.App.Help;

/// <summary>A deliberate settings destination. Keys remain stable for existing entry points.</summary>
public sealed record SettingsCategory(string Key, string Title);

public static class SettingsNavigation
{
    public static IReadOnlyList<SettingsCategory> Categories { get; } =
    [
        new("Appearance", "Theme and accessibility"),
        new("Editor", "Writing and editing"),
        new("Reading", "Reading and views"),
        new("Layout", "Panels and guidance"),
        new("Files", "Files and saving"),
        new("Workspace", "Workspaces and search"),
        new("Privacy", "Privacy and network"),
        new("Knowledge assistant", "Knowledge assistant"),
        new("Keyboard", "Keyboard shortcuts"),
        new("AI connections", "AI connections"),
        new("Windows integration", "Windows integration"),
    ];

    public static string Title(string key) => Categories.FirstOrDefault(category => category.Key == key)?.Title ?? key;

    public static string ResolveKey(string category) => Categories.FirstOrDefault(item =>
        item.Key.Equals(category, StringComparison.OrdinalIgnoreCase) || item.Title.Equals(category, StringComparison.OrdinalIgnoreCase))?.Key
        ?? Categories[0].Key;
}
