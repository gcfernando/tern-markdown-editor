using System.Text;
using Tern.Domain.Text;

namespace Tern.Filesystem;

public enum DecodeFailure
{
    None,
    InvalidSequence,
    NotRoundTrippable,
}

/// <summary>Outcome of decoding file bytes. When <see cref="Failure"/> is set the text is a lossy preview and must not be saved over the file.</summary>
public sealed record DecodedText(string Text, TextFileFormat Format, DecodeFailure Failure)
{
    public bool IsLossless => Failure == DecodeFailure.None;
}

public sealed record EncodingOption(string Name, TextEncodingKind Kind, bool HasBom, int CodePage);

/// <summary>
/// Converts between file bytes and text without ever discarding information.
/// Every decode is verified by re-encoding: if the bytes would not come back identical,
/// the document is reported as not safely editable rather than silently rewritten.
/// </summary>
public static class TextFileCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    static TextFileCodec()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static IReadOnlyList<EncodingOption> AvailableEncodings { get; } = CreateEncodingOptions();

    private static IReadOnlyList<EncodingOption> CreateEncodingOptions()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var choices = new List<EncodingOption>();
        foreach (var info in Encoding.GetEncodings().Where(e => e.CodePage != 65000).OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var kind = TextEncodingIds.FromCodePage(info.CodePage);
            // Resolve strict encoder/decoder support before offering an encoding.
            _ = GetEncoding(kind);
            var name = $"{info.DisplayName} · {info.Name} · CP {info.CodePage}";
            choices.Add(new EncodingOption(name + (TextEncodingIds.SupportsBom(kind) ? " · no BOM" : ""), kind, false, info.CodePage));
            if (TextEncodingIds.SupportsBom(kind))
                choices.Add(new EncodingOption(name + " · BOM", kind, true, info.CodePage));
        }
        return choices.AsReadOnly();
    }

    public static DecodedText Decode(ReadOnlySpan<byte> bytes, TextEncodingKind? forcedEncoding = null)
    {
        var (kind, bomLength) = forcedEncoding is { } forced
            ? (forced, BomLength(bytes, forced))
            : DetectEncoding(bytes);

        var hasBom = bomLength > 0;
        var encoding = GetEncoding(kind);
        var payload = bytes[bomLength..];

        string text;
        try
        {
            text = encoding.GetString(payload);
        }
        catch (DecoderFallbackException)
        {
            return DecodeLossy(payload, kind, hasBom);
        }

        var format = LineEndingAnalyzer.Describe(kind, hasBom, text);
        byte[] roundTrip;
        try
        {
            roundTrip = Encode(text, format);
        }
        catch (EncoderFallbackException)
        {
            return new DecodedText(text, format, DecodeFailure.NotRoundTrippable);
        }
        if (!roundTrip.AsSpan().SequenceEqual(bytes))
        {
            return new DecodedText(text, format, DecodeFailure.NotRoundTrippable);
        }

        return new DecodedText(text, format, DecodeFailure.None);
    }

    public static byte[] Encode(string text, TextFileFormat format)
    {
        var encoding = GetEncoding(format.Encoding);
        var preamble = format.HasBom ? Preamble(format.Encoding) : [];
        var body = encoding.GetBytes(text);
        if (!TextEncodingIds.SupportsBom(format.Encoding)
            && !string.Equals(text, encoding.GetString(body), StringComparison.Ordinal))
            throw new EncoderFallbackException("The selected encoding cannot round-trip every character exactly.");
        if (preamble.Length == 0)
        {
            return body;
        }

        var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        return result;
    }

    /// <summary>True when the text can be written in the target encoding without losing characters.</summary>
    public static bool CanRepresent(string text, TextEncodingKind kind)
    {
        var encoding = GetEncoding(kind, strict: true);
        try
        {
            var bytes = encoding.GetBytes(text);
            return string.Equals(text, encoding.GetString(bytes), StringComparison.Ordinal);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static DecodedText DecodeLossy(ReadOnlySpan<byte> payload, TextEncodingKind kind, bool hasBom)
    {
        var text = GetEncoding(kind, strict: false).GetString(payload);
        var format = LineEndingAnalyzer.Describe(kind, hasBom, text);
        return new DecodedText(text, format, DecodeFailure.InvalidSequence);
    }

    private static (TextEncodingKind Kind, int BomLength) DetectEncoding(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 4 && (bytes.Length - 4) % 4 == 0 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0)
        {
            return (TextEncodingKind.Utf32LittleEndian, 4);
        }

        if (bytes.Length >= 4 && (bytes.Length - 4) % 4 == 0 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return (TextEncodingKind.Utf32BigEndian, 4);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (TextEncodingKind.Utf8, 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (TextEncodingKind.Utf16LittleEndian, 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (TextEncodingKind.Utf16BigEndian, 2);
        }

        // Without a BOM, valid UTF-8 is by far the most likely encoding. Anything else is flagged
        // by the caller so the user can pick an encoding explicitly rather than having us guess.
        return (TextEncodingKind.Utf8, 0);
    }

    private static byte[] Preamble(TextEncodingKind kind) => kind switch
    {
        TextEncodingKind.Utf8 => [0xEF, 0xBB, 0xBF],
        TextEncodingKind.Utf16LittleEndian => [0xFF, 0xFE],
        TextEncodingKind.Utf16BigEndian => [0xFE, 0xFF],
        TextEncodingKind.Utf32LittleEndian => [0xFF, 0xFE, 0, 0],
        TextEncodingKind.Utf32BigEndian => [0, 0, 0xFE, 0xFF],
        _ => [],
    };

    private static int BomLength(ReadOnlySpan<byte> bytes, TextEncodingKind kind) => kind switch
    {
        TextEncodingKind.Utf8 when bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF => 3,
        TextEncodingKind.Utf16LittleEndian when bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE => 2,
        TextEncodingKind.Utf16BigEndian when bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF => 2,
        TextEncodingKind.Utf32LittleEndian when bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0 => 4,
        TextEncodingKind.Utf32BigEndian when bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF => 4,
        _ => 0,
    };

    private static Encoding GetEncoding(TextEncodingKind kind, bool strict = true)
    {
        var decoderFallback = strict ? DecoderFallback.ExceptionFallback : DecoderFallback.ReplacementFallback;
        var encoderFallback = strict ? EncoderFallback.ExceptionFallback : EncoderFallback.ReplacementFallback;
        return kind switch
        {
            TextEncodingKind.Utf8 => strict ? StrictUtf8 : new UTF8Encoding(true, false),
            TextEncodingKind.Utf16LittleEndian => new UnicodeEncoding(false, true, strict),
            TextEncodingKind.Utf16BigEndian => new UnicodeEncoding(true, true, strict),
            TextEncodingKind.Utf32LittleEndian => new UTF32Encoding(false, true, strict),
            TextEncodingKind.Utf32BigEndian => new UTF32Encoding(true, true, strict),
            TextEncodingKind.Latin1 => Encoding.GetEncoding("iso-8859-1", encoderFallback, decoderFallback),
            TextEncodingKind.Windows1252 => Encoding.GetEncoding(1252, encoderFallback, decoderFallback),
            _ => Encoding.GetEncoding(TextEncodingIds.GetCodePage(kind), encoderFallback, decoderFallback),
        };
    }
}
