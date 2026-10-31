namespace Tern.Domain.Text;

public enum TextEncodingKind
{
    Utf8,
    Utf16LittleEndian,
    Utf16BigEndian,
    Utf32LittleEndian,
    Utf32BigEndian,
    Latin1,
    Windows1252,
}

public enum LineEnding
{
    None,
    Lf,
    CrLf,
    Cr,
}

/// <summary>
/// Everything needed to write a document back in the form it was read:
/// encoding, BOM and the line-ending convention. The text itself keeps its original
/// line endings, so this record only describes them for display and for new input.
/// </summary>
public sealed record TextFileFormat(
    TextEncodingKind Encoding,
    bool HasBom,
    LineEnding DominantLineEnding,
    bool HasMixedLineEndings,
    bool EndsWithNewline)
{
    public static TextFileFormat NewFileDefault { get; } =
        new(TextEncodingKind.Utf8, false, LineEnding.CrLf, false, true);

    public string EncodingDisplayName => (Encoding, HasBom) switch
    {
        (TextEncodingKind.Utf8, true) => "UTF-8 BOM",
        (TextEncodingKind.Utf8, false) => "UTF-8",
        (TextEncodingKind.Utf16LittleEndian, _) => "UTF-16 LE" + (HasBom ? " BOM" : " (no BOM)"),
        (TextEncodingKind.Utf16BigEndian, _) => "UTF-16 BE" + (HasBom ? " BOM" : " (no BOM)"),
        (TextEncodingKind.Utf32LittleEndian, _) => "UTF-32 LE" + (HasBom ? " BOM" : " (no BOM)"),
        (TextEncodingKind.Utf32BigEndian, _) => "UTF-32 BE" + (HasBom ? " BOM" : " (no BOM)"),
        (TextEncodingKind.Latin1, _) => "ISO-8859-1",
        (TextEncodingKind.Windows1252, _) => "Windows-1252",
        _ => TextEncodingIds.DisplayName(Encoding),
    };

    public string LineEndingDisplayName => DominantLineEnding switch
    {
        LineEnding.CrLf => HasMixedLineEndings ? "CRLF (mixed)" : "CRLF",
        LineEnding.Cr => HasMixedLineEndings ? "CR (mixed)" : "CR",
        _ => HasMixedLineEndings ? "LF (mixed)" : "LF",
    };

    /// <summary>The terminator to use for newly typed line breaks.</summary>
    public string NewLineText => DominantLineEnding switch
    {
        LineEnding.CrLf => "\r\n",
        LineEnding.Cr => "\r",
        _ => "\n",
    };
}

/// <summary>Stable encoding identifiers without changing the original persisted enum values.</summary>
public static class TextEncodingIds
{
    private const int CodePageOffset = 100000;

    static TextEncodingIds() => System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

    public static TextEncodingKind FromCodePage(int codePage) => codePage switch
    {
        65001 => TextEncodingKind.Utf8, 1200 => TextEncodingKind.Utf16LittleEndian,
        1201 => TextEncodingKind.Utf16BigEndian, 12000 => TextEncodingKind.Utf32LittleEndian,
        12001 => TextEncodingKind.Utf32BigEndian, 28591 => TextEncodingKind.Latin1,
        1252 => TextEncodingKind.Windows1252,
        > 0 and <= 65535 when codePage != 65000 => (TextEncodingKind)(CodePageOffset + codePage),
        _ => throw new ArgumentOutOfRangeException(nameof(codePage)),
    };

    public static int GetCodePage(TextEncodingKind kind) => kind switch
    {
        TextEncodingKind.Utf8 => 65001, TextEncodingKind.Utf16LittleEndian => 1200,
        TextEncodingKind.Utf16BigEndian => 1201, TextEncodingKind.Utf32LittleEndian => 12000,
        TextEncodingKind.Utf32BigEndian => 12001, TextEncodingKind.Latin1 => 28591,
        TextEncodingKind.Windows1252 => 1252,
        _ when (int)kind > CodePageOffset && (int)kind <= CodePageOffset + 65535 && (int)kind != CodePageOffset + 65000 => (int)kind - CodePageOffset,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static bool SupportsBom(TextEncodingKind kind) => kind is TextEncodingKind.Utf8
        or TextEncodingKind.Utf16LittleEndian or TextEncodingKind.Utf16BigEndian
        or TextEncodingKind.Utf32LittleEndian or TextEncodingKind.Utf32BigEndian;

    public static bool IsSupported(TextEncodingKind kind)
    {
        try
        {
            _ = System.Text.Encoding.GetEncoding(GetCodePage(kind), System.Text.EncoderFallback.ExceptionFallback, System.Text.DecoderFallback.ExceptionFallback);
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    public static string DisplayName(TextEncodingKind kind)
    {
        var codePage = GetCodePage(kind);
        return $"{System.Text.Encoding.GetEncoding(codePage).EncodingName} (CP {codePage})";
    }
}

public static class LineEndingAnalyzer
{
    public static TextFileFormat Describe(TextEncodingKind encoding, bool hasBom, ReadOnlySpan<char> text)
    {
        int lf = 0, crlf = 0, cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    cr++;
                }
            }
            else if (c == '\n')
            {
                lf++;
            }
        }

        var kinds = (lf > 0 ? 1 : 0) + (crlf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        var dominant = LineEnding.Lf;
        var best = 0;
        if (crlf > best) { dominant = LineEnding.CrLf; best = crlf; }
        if (lf > best) { dominant = LineEnding.Lf; best = lf; }
        if (cr > best) { dominant = LineEnding.Cr; }

        var endsWithNewline = text.Length > 0 && (text[^1] == '\n' || text[^1] == '\r');
        return new TextFileFormat(encoding, hasBom, dominant, kinds > 1, endsWithNewline);
    }
}
