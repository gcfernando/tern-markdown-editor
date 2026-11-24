using Tern.App.Settings;
using Tern.Export;
using Tern.Markdown;

namespace Tern.App.Presentation;

public static class EnumDisplayNames
{
    public static string DisplayName(this MarkdownProfile value) => value switch
    {
        MarkdownProfile.CommonMark => "CommonMark Markdown",
        MarkdownProfile.Gfm => "GitHub Flavored Markdown (GFM)",
        MarkdownProfile.Extended => "Extended Markdown",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    public static string DisplayName(this ThemeChoice value) => value switch
    {
        ThemeChoice.System => "Follow system",
        ThemeChoice.Paper => "Paper (light)",
        ThemeChoice.Midnight => "Midnight (dark)",
        ThemeChoice.Graphite => "Graphite (dark)",
        ThemeChoice.Harbor => "Harbor (dark blue)",
        ThemeChoice.Linen => "Linen (warm light)",
        ThemeChoice.Mist => "Mist (cool light)",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    public static string DisplayName(this ViewMode value) => value switch
    {
        ViewMode.Source => "Source editor",
        ViewMode.Split => "Split editor and preview",
        ViewMode.Reading => "Reading preview",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    public static string DisplayName(this RemoteImagePolicy value) => value switch
    {
        RemoteImagePolicy.Ask => "Ask before loading",
        RemoteImagePolicy.Always => "Always load remote images",
        RemoteImagePolicy.Never => "Never load remote images",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    public static string DisplayName(this ExportTheme value) => value switch
    {
        ExportTheme.Auto => "Follow system",
        ExportTheme.Light => "Light appearance",
        ExportTheme.Dark => "Dark appearance",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    public static string DisplayName(this PdfPageSize value) => value switch
    {
        PdfPageSize.A4 => "A4 (210 × 297 mm)",
        PdfPageSize.Letter => "US Letter (8.5 × 11 in)",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    public static string For<T>(T value) where T : struct, Enum => value switch
    {
        MarkdownProfile profile => profile.DisplayName(),
        ThemeChoice theme => theme.DisplayName(),
        ViewMode mode => mode.DisplayName(),
        RemoteImagePolicy policy => policy.DisplayName(),
        ExportTheme theme => theme.DisplayName(),
        PdfPageSize page => page.DisplayName(),
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "No display names are defined for this choice.")
    };
}
