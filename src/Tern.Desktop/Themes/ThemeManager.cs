using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Platform;
using Avalonia.Threading;
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
    public const string ControlBorder = "Tern.ControlBorder";
    public const string Success = "Tern.Success";
    public const string Info = "Tern.Info";
    public const string InfoBackground = "Tern.InfoBackground";
    public const string ErrorBackground = "Tern.ErrorBackground";
    public const string WarningBackground = "Tern.WarningBackground";
    public const string SuccessBackground = "Tern.SuccessBackground";
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
    public const string Scrim = "Tern.Scrim";
    public const string Shadow = "Tern.Shadow";
    public const string CanvasBackground = "Tern.CanvasBackground";
    public const string FindMatch = PreviewKeys.FindMatch;
    public const string FindMatchOther = PreviewKeys.FindMatchOther;
    public const string FindMatchOutline = PreviewKeys.FindMatchOutline;
    public const string SyntaxHeading = "Tern.Syntax.Heading";
    public const string SyntaxEmphasis = "Tern.Syntax.Emphasis";
    public const string SyntaxCode = "Tern.Syntax.Code";
    public const string SyntaxLink = "Tern.Syntax.Link";
    public const string SyntaxQuote = "Tern.Syntax.Quote";
    public const string SyntaxMarker = "Tern.Syntax.Marker";
    public const string SyntaxMeta = "Tern.Syntax.Meta";

    private static ResourceDictionary? _applied;
    private static Application? _application;
    private static IPlatformSettings? _platform;
    private static ThemeChoice _choice;
    internal static Func<Application, SystemContrastPalette?> ContrastPaletteProvider { get; set; } = application =>
        OperatingSystem.IsWindows() && application.PlatformSettings?.GetColorValues().ContrastPreference == ColorContrastPreference.High
            ? WindowsContrastColors.Read() : null;

    /// <summary>The variant a theme setting resolves to; System defers to the operating system.</summary>
    public static ThemeVariant VariantFor(ThemeChoice theme) => theme switch
    {
        ThemeChoice.Midnight or ThemeChoice.Graphite or ThemeChoice.Harbor => ThemeVariant.Dark,
        ThemeChoice.Paper or ThemeChoice.Linen or ThemeChoice.Mist => ThemeVariant.Light,
        _ => ThemeVariant.Default,
    };

    private static ResourceDictionary LightPalette(ThemeChoice theme) => theme switch
    {
        ThemeChoice.Linen => Linen(),
        ThemeChoice.Mist => Mist(),
        _ => Paper(),
    };

    private static ResourceDictionary DarkPalette(ThemeChoice theme) => theme switch
    {
        ThemeChoice.Graphite => Graphite(),
        ThemeChoice.Harbor => Harbor(),
        _ => Midnight(),
    };

    public static void Apply(Application application, ThemeChoice theme)
    {
        _application = application;
        _choice = theme;
        if (!ReferenceEquals(_platform, application.PlatformSettings))
        {
            if (_platform is not null) _platform.ColorValuesChanged -= OnSystemColorsChanged;
            _platform = application.PlatformSettings;
            if (_platform is not null) _platform.ColorValuesChanged += OnSystemColorsChanged;
        }
        var contrast = ContrastPaletteProvider(application);
        application.RequestedThemeVariant = contrast is null ? VariantFor(theme)
            : contrast.Background.R * .2126 + contrast.Background.G * .7152 + contrast.Background.B * .0722 < 128
                ? ThemeVariant.Dark : ThemeVariant.Light;
        var dictionary = new ResourceDictionary();
        dictionary.ThemeDictionaries[ThemeVariant.Light] = contrast is null ? LightPalette(theme) : HighContrast(contrast);
        dictionary.ThemeDictionaries[ThemeVariant.Dark] = contrast is null ? DarkPalette(theme) : HighContrast(contrast);

        if (_applied is not null)
        {
            application.Resources.MergedDictionaries.Remove(_applied);
        }

        application.Resources.MergedDictionaries.Add(dictionary);
        _applied = dictionary;
    }

    private static void OnSystemColorsChanged(object? sender, PlatformColorValues values) =>
        Dispatcher.UIThread.Post(() => { if (_application is { } application) Apply(application, _choice); });

    internal static ResourceDictionary HighContrast(SystemContrastPalette palette)
    {
        var resources = Paper();
        foreach (var key in resources.Keys.ToArray())
            if (resources[key] is IBrush) resources[key] = new SolidColorBrush(palette.Text);
        void Set(Color color, params string[] keys)
        {
            foreach (var key in keys) resources[key] = new SolidColorBrush(color);
        }
        Set(palette.Background, Bg, Panel, Surface, Hover, CanvasBackground, AccentSoft, LineHighlight, "ComboBoxBackgroundUnfocused",
            ErrorBackground, WarningBackground, SuccessBackground, InfoBackground, PreviewKeys.Background, PreviewKeys.CodeBackground,
            PreviewKeys.TableHeaderBackground, PreviewKeys.Mark);
        Set(palette.Border, Border, ControlBorder, PreviewKeys.Border, PreviewKeys.QuoteBar);
        Set(palette.Highlight, Accent, PreviewKeys.Accent, PreviewKeys.FocusBackground, PreviewKeys.Selection,
            FindMatch, FindMatchOther, FindMatchOutline);
        Set(palette.HighlightText, OnAccent, PreviewKeys.FocusForeground);
        Set(palette.Background, OnDanger, PreviewKeys.WarningForeground, Scrim);
        Set(palette.Link, SyntaxLink, PreviewKeys.Link, PreviewKeys.MarkLink);
        Set(palette.Background, "SystemControlFocusVisualSecondaryBrush");
        Set(palette.Highlight, "SystemControlFocusVisualPrimaryBrush");
        resources[Shadow] = new BoxShadows();
        resources["SystemAccentColor"] = palette.Highlight;
        for (var step = 1; step <= 3; step++)
        {
            resources["SystemAccentColorDark" + step] = palette.Highlight;
            resources["SystemAccentColorLight" + step] = palette.Highlight;
        }
        return resources;
    }

    public static ResourceDictionary Midnight() => Build(
        PreviewTheme.Midnight(), dark: true,
        tones: new("#8CC4F5", "#1B2E40", "#7DD99A", "#1D3527", "#382C1B", "#3A2024"),
        bg: "#131719", panel: "#191F23", surface: "#1C2226", border: "#30393F", text: "#E9EDF0", muted: "#A3ADB5",
        accent: "#6BC8B8", accentSoft: "#243C3A", onAccent: "#102A28", hover: "#283137", danger: "#FF9C94", warning: "#FBBF24",
        line: "#1A2B35", heading: "#5EEAD4", emphasis: "#F0ABFC", code: "#FCA5A5", link: "#60A5FA", quote: "#94A3B8", marker: "#94A3B8", meta: "#A78BFA");

    public static ResourceDictionary Paper() => Build(
        PreviewTheme.Paper(), dark: false,
        tones: new("#175A9E", "#E3EFFB", "#1B6B37", "#DDF1E3", "#FEF3C7", "#FEE2E2"),
        bg: "#F4F5F7", panel: "#EDEFF2", surface: "#FFFFFF", border: "#DCE0E6", text: "#21252B", muted: "#5D6670",
        accent: "#156E66", accentSoft: "#DCEBE7", onAccent: "#FFFFFF", hover: "#E2E6EB", danger: "#B91C1C", warning: "#7F4F00",
        line: "#F1F5F9", heading: "#0F6E66", emphasis: "#862CD6", code: "#B91C1C", link: "#1D4ED8", quote: "#475569", marker: "#475569", meta: "#6D28D9");

    public static ResourceDictionary Graphite() => Build(
        Reading(PreviewTheme.Midnight(), "#1A1B1E", "#ECECEE", "#B0B3B8", "#9CC2FA", "#8AB4F8", "#232427", "#3A3C41", "#8AB4F8"), dark: true,
        tones: new("#9CC8F7", "#232F3D", "#8BD9A5", "#22332A", "#362E1D", "#3B2628"),
        bg: "#18191B", panel: "#1E1F22", surface: "#232427", border: "#3A3C41", text: "#ECECEE", muted: "#B0B3B8",
        accent: "#8AB4F8", accentSoft: "#2B3648", onAccent: "#10203A", hover: "#303236", danger: "#FF9B94", warning: "#F5C451",
        line: "#26282C", heading: "#9CC2FA", emphasis: "#E5B3F5", code: "#F5A9A0", link: "#8AB4F8", quote: "#B0B3B8", marker: "#B0B3B8", meta: "#C4B5FD");

    public static ResourceDictionary Harbor() => Build(
        Reading(PreviewTheme.Midnight(), "#101D31", "#E8EEF7", "#B3C0D2", "#7FDCEF", "#93C5FD", "#182A44", "#2F4667", "#6FD3E8"), dark: true,
        tones: new("#9CCFFD", "#1A3352", "#86E3B4", "#163A38", "#3A3426", "#432A36"),
        bg: "#0F1B2D", panel: "#14233A", surface: "#182A44", border: "#2F4667", text: "#E8EEF7", muted: "#B3C0D2",
        accent: "#6FD3E8", accentSoft: "#1F4254", onAccent: "#0B2530", hover: "#233A5A", danger: "#FFA7A0", warning: "#FBCB5C",
        line: "#1A3050", heading: "#7FDCEF", emphasis: "#F2B8E6", code: "#FFB4A2", link: "#93C5FD", quote: "#B3C0D2", marker: "#B3C0D2", meta: "#C7B8FF");

    public static ResourceDictionary Linen() => Build(
        Reading(PreviewTheme.Paper(), "#FFFCF6", "#2B2620", "#5F5648", "#7A3F0E", "#1F4F9C", "#FBF7EE", "#DDD4C3", "#8A4B14"), dark: false,
        tones: new("#1F4F8F", "#E6EBF2", "#35652A", "#E5EED8", "#F7E7C6", "#F8E1DA"),
        bg: "#F8F4EC", panel: "#F3EEE4", surface: "#FFFCF6", border: "#DDD4C3", text: "#2B2620", muted: "#5F5648",
        accent: "#8A4B14", accentSoft: "#F0E2D0", onAccent: "#FFFFFF", hover: "#E9E2D5", danger: "#A61B1B", warning: "#7A4A00",
        line: "#F6F0E4", heading: "#7A3F0E", emphasis: "#7B2D8B", code: "#A32A1E", link: "#1F4F9C", quote: "#5A5245", marker: "#5A5245", meta: "#5E3A9E");

    public static ResourceDictionary Mist() => Build(
        Reading(PreviewTheme.Paper(), "#FFFFFF", "#1C2733", "#4C5B6B", "#1A5496", "#1A4FBF", "#F5F8FB", "#CFD9E3", "#1F5FA8"), dark: false,
        tones: new("#1A5496", "#DCE9F7", "#14693F", "#DAF0E4", "#FBEFD2", "#FBE3E5"),
        bg: "#F2F6FA", panel: "#E9EFF5", surface: "#FFFFFF", border: "#CFD9E3", text: "#1C2733", muted: "#4C5B6B",
        accent: "#1F5FA8", accentSoft: "#D9E7F5", onAccent: "#FFFFFF", hover: "#DCE5EE", danger: "#A81C1C", warning: "#7C4700",
        line: "#EEF3F8", heading: "#1A5496", emphasis: "#7A2BBF", code: "#A8261C", link: "#1A4FBF", quote: "#475666", marker: "#475666", meta: "#5B34B5");

    /// <summary>Applies a theme's reading surface over a base palette whose code colors suit the same light or dark range.</summary>
    private static ResourceDictionary Reading(ResourceDictionary preview, string background, string foreground, string muted,
        string heading, string link, string codeBackground, string border, string accent)
    {
        void Set(string hex, params string[] keys)
        {
            foreach (var key in keys) preview[key] = new SolidColorBrush(Color.Parse(hex));
        }
        Set(background, PreviewKeys.Background);
        Set(foreground, PreviewKeys.Foreground);
        Set(muted, PreviewKeys.Muted);
        Set(heading, PreviewKeys.Heading);
        Set(link, PreviewKeys.Link);
        Set(codeBackground, PreviewKeys.CodeBackground, PreviewKeys.TableHeaderBackground);
        Set(border, PreviewKeys.Border, PreviewKeys.QuoteBar);
        Set(accent, PreviewKeys.Accent, PreviewKeys.FocusBackground);
        return preview;
    }

    /// <summary>Message colors chosen for one theme. Warning and error text colors are the theme's warning and danger values.</summary>
    private sealed record ToneColors(string Info, string InfoBackground, string Success, string SuccessBackground, string WarningBackground, string ErrorBackground);

    private static ResourceDictionary Build(ResourceDictionary preview, bool dark, ToneColors tones, string bg, string panel, string surface, string border, string text,
        string muted, string accent, string accentSoft, string onAccent, string hover, string danger, string warning, string line,
        string heading, string emphasis, string code, string link, string quote, string marker, string meta)
    {
        var dictionary = new ResourceDictionary();
        foreach (var pair in preview)
        {
            dictionary[pair.Key] = pair.Value;
        }
        // Fluent's native thumb keeps its hover/pressed routing and reads this shared palette.
        dictionary["ScrollBarThumbBackgroundColor"] = preview[PreviewKeys.Muted];
        dictionary["ScrollBarPanningThumbBackground"] = preview[PreviewKeys.Muted];
        dictionary["ScrollBarThumbFillPointerOver"] = preview[PreviewKeys.Foreground];
        dictionary["ScrollBarThumbFillPressed"] = preview[PreviewKeys.Foreground];

        void Brush(string key, string hex) => dictionary[key] = new SolidColorBrush(Color.Parse(hex));
        Brush(Bg, bg);
        Brush(Panel, panel);
        Brush(Surface, surface);
        Brush(Border, border);
        Brush(ControlBorder, dark ? "#7C8997" : "#707B87");
        Brush(Scrim, dark ? "#99000000" : "#66000000");
        Brush(CanvasBackground, dark ? "#131719" : "#E2E6EB");
        dictionary[Shadow] = new BoxShadows(new BoxShadow
        {
            Blur = 38, OffsetY = 12, Color = Color.Parse(dark ? "#80000000" : "#40000000")
        });
        Brush(Success, tones.Success);
        Brush(ErrorBackground, tones.ErrorBackground);
        Brush(WarningBackground, tones.WarningBackground);
        Brush(SuccessBackground, tones.SuccessBackground);
        Brush(Info, tones.Info);
        Brush(InfoBackground, tones.InfoBackground);
        Brush(Text, text);
        Brush(Muted, muted);
        Brush(Accent, accent);
        Brush(AccentSoft, accentSoft);
        // Fluent fills a keyboard-focused dropdown with Windows blue in light themes. Each theme supplies its own color.
        Brush("ComboBoxBackgroundUnfocused", accentSoft);
        Brush("ComboBoxBackgroundBorderBrushUnfocused", accent);
        Brush(OnAccent, onAccent);
        Brush(Hover, hover);
        Brush(Danger, danger);
        Brush(OnDanger, dark ? "#191F23" : "#FFFFFF");
        Brush(Warning, warning);
        Brush(LineHighlight, line);
        Brush(SyntaxHeading, heading);
        Brush(SyntaxEmphasis, emphasis);
        Brush(SyntaxCode, code);
        Brush(SyntaxLink, link);
        Brush(SyntaxQuote, quote);
        Brush(SyntaxMarker, marker);
        Brush(SyntaxMeta, meta);
        var accentColor = Color.Parse(accent);
        dictionary["SystemAccentColor"] = accentColor;
        for (var step = 1; step <= 3; step++)
        {
            dictionary["SystemAccentColorDark" + step] = Mix(accentColor, Colors.Black, step * .12);
            dictionary["SystemAccentColorLight" + step] = Mix(accentColor, Colors.White, step * .12);
        }
        return dictionary;
    }

    private static Color Mix(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));
}
