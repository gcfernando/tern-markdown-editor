using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Tern.PreviewView;

/// <summary>Resource keys the preview looks up. Define them in the host's theme to restyle the preview.</summary>
public static class PreviewKeys
{
    /// <summary>Body text.</summary>
    public const string Foreground = "Tern.Preview.Foreground";

    /// <summary>Secondary text: captions, notes, placeholders.</summary>
    public const string Muted = "Tern.Preview.Muted";

    /// <summary>Headings.</summary>
    public const string Heading = "Tern.Preview.Heading";

    /// <summary>Links.</summary>
    public const string Link = "Tern.Preview.Link";

    /// <summary>Links whose local target does not exist.</summary>
    public const string LinkBroken = "Tern.Preview.LinkBroken";

    /// <summary>Code panel and inline code background.</summary>
    public const string CodeBackground = "Tern.Preview.CodeBackground";

    /// <summary>Code text.</summary>
    public const string CodeForeground = "Tern.Preview.CodeForeground";

    /// <summary>Locally tokenised code keywords, strings, methods, types, constants and comments.</summary>
    public const string CodeKeyword = "Tern.Preview.CodeKeyword";
    public const string CodeString = "Tern.Preview.CodeString";
    public const string CodeFunction = "Tern.Preview.CodeFunction";
    public const string CodeType = "Tern.Preview.CodeType";
    public const string CodeConstant = "Tern.Preview.CodeConstant";
    public const string CodeComment = "Tern.Preview.CodeComment";

    /// <summary>Rules and outlines.</summary>
    public const string Border = "Tern.Preview.Border";

    /// <summary>Background of table header rows.</summary>
    public const string TableHeaderBackground = "Tern.Preview.TableHeaderBackground";

    /// <summary>Bar to the left of block quotes.</summary>
    public const string QuoteBar = "Tern.Preview.QuoteBar";

    /// <summary>Note alert colour.</summary>
    public const string AlertNote = "Tern.Preview.AlertNote";

    /// <summary>Tip alert colour.</summary>
    public const string AlertTip = "Tern.Preview.AlertTip";

    /// <summary>Important alert colour.</summary>
    public const string AlertImportant = "Tern.Preview.AlertImportant";

    /// <summary>Warning alert colour.</summary>
    public const string AlertWarning = "Tern.Preview.AlertWarning";

    /// <summary>Text on a solid warning banner.</summary>
    public const string WarningForeground = "Tern.Preview.WarningForeground";

    /// <summary>Caution alert colour.</summary>
    public const string AlertCaution = "Tern.Preview.AlertCaution";

    /// <summary>Accent: bullets, focus, diagram strokes.</summary>
    public const string Accent = "Tern.Preview.Accent";

    /// <summary>Background of the preview surface.</summary>
    public const string Background = "Tern.Preview.Background";

    /// <summary>Text selection highlight.</summary>
    public const string Selection = "Tern.Preview.Selection";

    /// <summary>Background of highlighted (<c>mark</c>) text.</summary>
    public const string Mark = "Tern.Preview.Mark";

    /// <summary>Links on marked text retain distinct readable normal/broken colours.</summary>
    public const string MarkLink = "Tern.Preview.MarkLink";
    public const string MarkLinkBroken = "Tern.Preview.MarkLinkBroken";

    /// <summary>Opaque keyboard-link focus colours, independent of the underlying inline background.</summary>
    public const string FocusBackground = "Tern.Preview.FocusBackground";
    public const string FocusForeground = "Tern.Preview.FocusForeground";

    /// <summary>Error text, for example a formula that failed to parse.</summary>
    public const string Error = "Tern.Preview.Error";
}

/// <summary>Ready-made colour sets for <see cref="PreviewKeys"/>.</summary>
public static class PreviewTheme
{
    private static readonly Dictionary<string, IBrush> Defaults = Create(
        background: "#0D1117", foreground: "#F0F6FC", muted: "#9198A1", heading: "#F0F6FC", link: "#4493F8", linkBroken: "#F87171",
        codeBackground: "#151B23", codeForeground: "#F0F6FC", border: "#3D444D", tableHeader: "#151B23", quoteBar: "#3D444D",
        note: "#38BDF8", tip: "#34D399", important: "#A78BFA", warning: "#FBBF24", caution: "#F87171",
        accent: "#2DD4BF", selection: "#663880FF", mark: "#6B5A12", error: "#E58A8A",
        keyword: "#FF7B72", codeString: "#A5D6FF", function: "#D2A8FF", type: "#FFA657", constant: "#79C0FF", comment: "#9198A1",
        warningForeground: "#1F2328", focusBackground: "#2DD4BF", focusForeground: "#0D1117", markLink: "#B6DEFF", markLinkBroken: "#FFD1D1");

    /// <summary>A dark navy theme with teal accents.</summary>
    public static ResourceDictionary Midnight() => ToDictionary(Defaults);

    /// <summary>A warm light theme for reading on paper-like backgrounds.</summary>
    public static ResourceDictionary Paper() => ToDictionary(Create(
        background: "#FFFFFF", foreground: "#1F2328", muted: "#59636E", heading: "#1F2328", link: "#0969DA", linkBroken: "#C0392B",
        codeBackground: "#F6F8FA", codeForeground: "#1F2328", border: "#D1D9E0", tableHeader: "#F6F8FA", quoteBar: "#D1D9E0",
        note: "#0969DA", tip: "#1A7F37", important: "#8250DF", warning: "#9A6700", caution: "#CF222E",
        accent: "#0F766E", selection: "#553880FF", mark: "#FFE58A", error: "#B42318",
        keyword: "#CF222E", codeString: "#0A3069", function: "#8250DF", type: "#953800", constant: "#0550AE", comment: "#59636E",
        warningForeground: "#FFFFFF", focusBackground: "#0F766E", focusForeground: "#FFFFFF", markLink: "#0550AE", markLinkBroken: "#9C2118"));

    /// <summary>The brush for <paramref name="key"/> in the Midnight set, used until the host defines the key.</summary>
    internal static IBrush Fallback(string key) => Defaults.TryGetValue(key, out var brush) ? brush : Brushes.Gray;

    /// <summary>Looks a theme brush up from <paramref name="host"/>'s resource scope, falling back to Midnight.</summary>
    internal static IBrush Resolve(Control host, string key) =>
        host.TryFindResource(key, host.ActualThemeVariant, out var value) && value is IBrush brush ? brush : Fallback(key);

    /// <summary>Keeps <paramref name="property"/> of <paramref name="host"/> in sync with a theme brush, including theme swaps.</summary>
    internal static void Bind(Control host, AvaloniaProperty property, string key) =>
        host.Bind(property, host.GetResourceObservable(key, value => value as IBrush ?? Fallback(key)));

    private static ResourceDictionary ToDictionary(Dictionary<string, IBrush> brushes)
    {
        var dictionary = new ResourceDictionary();
        foreach (var (key, brush) in brushes)
        {
            dictionary[key] = brush;
        }

        return dictionary;
    }

    private static Dictionary<string, IBrush> Create(
        string background, string foreground, string muted, string heading, string link, string linkBroken, string codeBackground,
        string codeForeground, string border, string tableHeader, string quoteBar, string note, string tip, string important,
        string warning, string caution, string accent, string selection, string mark, string error,
        string keyword, string codeString, string function, string type, string constant, string comment,
        string warningForeground, string focusBackground, string focusForeground, string markLink, string markLinkBroken) => new()
        {
            [PreviewKeys.Background] = Solid(background),
            [PreviewKeys.Foreground] = Solid(foreground),
            [PreviewKeys.Muted] = Solid(muted),
            [PreviewKeys.Heading] = Solid(heading),
            [PreviewKeys.Link] = Solid(link),
            [PreviewKeys.LinkBroken] = Solid(linkBroken),
            [PreviewKeys.CodeBackground] = Solid(codeBackground),
            [PreviewKeys.CodeForeground] = Solid(codeForeground),
            [PreviewKeys.CodeKeyword] = Solid(keyword),
            [PreviewKeys.CodeString] = Solid(codeString),
            [PreviewKeys.CodeFunction] = Solid(function),
            [PreviewKeys.CodeType] = Solid(type),
            [PreviewKeys.CodeConstant] = Solid(constant),
            [PreviewKeys.CodeComment] = Solid(comment),
            [PreviewKeys.Border] = Solid(border),
            [PreviewKeys.TableHeaderBackground] = Solid(tableHeader),
            [PreviewKeys.QuoteBar] = Solid(quoteBar),
            [PreviewKeys.AlertNote] = Solid(note),
            [PreviewKeys.AlertTip] = Solid(tip),
            [PreviewKeys.AlertImportant] = Solid(important),
            [PreviewKeys.AlertWarning] = Solid(warning),
            [PreviewKeys.WarningForeground] = Solid(warningForeground),
            [PreviewKeys.AlertCaution] = Solid(caution),
            [PreviewKeys.Accent] = Solid(accent),
            [PreviewKeys.Selection] = Solid(selection),
            [PreviewKeys.Mark] = Solid(mark),
            [PreviewKeys.MarkLink] = Solid(markLink),
            [PreviewKeys.MarkLinkBroken] = Solid(markLinkBroken),
            [PreviewKeys.FocusBackground] = Solid(focusBackground),
            [PreviewKeys.FocusForeground] = Solid(focusForeground),
            [PreviewKeys.Error] = Solid(error),
        };

    private static ImmutableSolidColorBrush Solid(string hex) => new(Color.Parse(hex));
}
