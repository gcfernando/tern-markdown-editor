using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Tern.App.Settings;
using Tern.PreviewView;

namespace Tern.Desktop.Themes;

/// <summary>Builds the Midnight and Paper resource dictionaries (app chrome, syntax colours and the preview keys) and applies the chosen one.</summary>
public static class ThemeManager
{
    public const string Bg = "Tern.Bg";
    public const string Panel = "Tern.Panel";
    public const string Surface = "Tern.Surface";
    public const string Border = "Tern.Border";
    public const string Text = "Tern.Text";
    public const string Muted = "Tern.Muted";
    public const string Accent = "Tern.Accent";
    public const string AccentSoft = "Tern.AccentSoft";
    public const string OnAccent = "Tern.OnAccent";
    public const string Hover = "Tern.Hover";
    public const string Danger = "Tern.Danger";
    public const string OnDanger = "Tern.OnDanger";
    public const string Warning = "Tern.Warning";
    public const string LineHighlight = "Tern.LineHighlight";
    public const string SyntaxHeading = "Tern.Syntax.Heading";
    public const string SyntaxEmphasis = "Tern.Syntax.Emphasis";
    public const string SyntaxCode = "Tern.Syntax.Code";
    public const string SyntaxLink = "Tern.Syntax.Link";
    public const string SyntaxQuote = "Tern.Syntax.Quote";
    public const string SyntaxMarker = "Tern.Syntax.Marker";
    public const string SyntaxMeta = "Tern.Syntax.Meta";

    private static ResourceDictionary? _applied;

    /// <summary>The variant a theme setting resolves to; System defers to the operating system.</summary>
    public static ThemeVariant VariantFor(ThemeChoice theme) => theme switch
    {
        ThemeChoice.Midnight => ThemeVariant.Dark,
        ThemeChoice.Paper => ThemeVariant.Light,
        _ => ThemeVariant.Default,
    };

    public static void Apply(Application application, ThemeChoice theme)
    {
        application.RequestedThemeVariant = VariantFor(theme);
        var dark = theme == ThemeChoice.Midnight ||
            (theme == ThemeChoice.System && application.ActualThemeVariant == ThemeVariant.Dark);
        var dictionary = dark ? Midnight() : Paper();

        if (_applied is not null)
        {
            application.Resources.MergedDictionaries.Remove(_applied);
        }

        application.Resources.MergedDictionaries.Add(dictionary);
        _applied = dictionary;
    }

    public static ResourceDictionary Midnight() => Build(
        PreviewTheme.Midnight(),
        bg: "#131719", panel: "#191F23", surface: "#1C2226", border: "#30393F", text: "#E9EDF0", muted: "#A3ADB5",
        accent: "#6BC8B8", accentSoft: "#243C3A", onAccent: "#102A28", hover: "#283137", danger: "#F87171", warning: "#FBBF24",
        line: "#1A2B35", heading: "#5EEAD4", emphasis: "#F0ABFC", code: "#FCA5A5", link: "#60A5FA", quote: "#94A3B8", marker: "#94A3B8", meta: "#A78BFA");

    public static ResourceDictionary Paper() => Build(
        PreviewTheme.Paper(),
        bg: "#F4F5F7", panel: "#EDEFF2", surface: "#FFFFFF", border: "#DCE0E6", text: "#21252B", muted: "#5D6670",
        accent: "#156E66", accentSoft: "#DCEBE7", onAccent: "#FFFFFF", hover: "#E2E6EB", danger: "#B91C1C", warning: "#B45309",
        line: "#F1F5F9", heading: "#0F766E", emphasis: "#9333EA", code: "#B91C1C", link: "#1D4ED8", quote: "#475569", marker: "#475569", meta: "#6D28D9");

    private static ResourceDictionary Build(ResourceDictionary preview, string bg, string panel, string surface, string border, string text,
        string muted, string accent, string accentSoft, string onAccent, string hover, string danger, string warning, string line,
        string heading, string emphasis, string code, string link, string quote, string marker, string meta)
    {
        var dictionary = new ResourceDictionary();
        foreach (var pair in preview)
        {
            dictionary[pair.Key] = pair.Value;
        }

        void Brush(string key, string hex) => dictionary[key] = new SolidColorBrush(Color.Parse(hex));
        Brush(Bg, bg);
        Brush(Panel, panel);
        Brush(Surface, surface);
        Brush(Border, border);
        Brush(Text, text);
        Brush(Muted, muted);
        Brush(Accent, accent);
        Brush(AccentSoft, accentSoft);
        Brush(OnAccent, onAccent);
        Brush(Hover, hover);
        Brush(Danger, danger);
        Brush(OnDanger, danger == "#F87171" ? "#191F23" : "#FFFFFF");
        Brush(Warning, warning);
        Brush(LineHighlight, line);
        Brush(SyntaxHeading, heading);
        Brush(SyntaxEmphasis, emphasis);
        Brush(SyntaxCode, code);
        Brush(SyntaxLink, link);
        Brush(SyntaxQuote, quote);
        Brush(SyntaxMarker, marker);
        Brush(SyntaxMeta, meta);
        return dictionary;
    }
}
