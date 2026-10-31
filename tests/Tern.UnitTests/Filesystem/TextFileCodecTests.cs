using System.Text;
using Tern.Domain.Text;
using Tern.Filesystem;

namespace Tern.UnitTests.Filesystem;

public class TextFileCodecTests
{
    public static TheoryData<string, byte[]> Fixtures => new()
    {
        { "utf8 lf", "# Title\n\nBody\n"u8.ToArray() },
        { "utf8 crlf", "# Title\r\n\r\nBody\r\n"u8.ToArray() },
        { "utf8 cr only", "a\rb\rc"u8.ToArray() },
        { "mixed endings", "a\r\nb\nc\rd\r\n"u8.ToArray() },
        { "no final newline", "last line"u8.ToArray() },
        { "empty", [] },
        { "utf8 bom", [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. "bom\n"u8.ToArray()] },
        { "emoji and combining", Encoding.UTF8.GetBytes("caf\u0065\u0301 \u00e9 \U0001F600 \u0645\u0631\u062D\u0628\u0627\n") },
        { "nfd vs nfc stay distinct", Encoding.UTF8.GetBytes("\u00e9\u0065\u0301\n") },
        { "utf16 le bom", [.. new byte[] { 0xFF, 0xFE }, .. Encoding.Unicode.GetBytes("hello\r\nworld")] },
        { "utf16 be bom", [.. new byte[] { 0xFE, 0xFF }, .. Encoding.BigEndianUnicode.GetBytes("hello\nworld")] },
        { "embedded nul", "a\0b\n"u8.ToArray() },
        { "bom char mid-file", Encoding.UTF8.GetBytes("a\uFEFFb\n") },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Decode_then_encode_returns_identical_bytes(string name, byte[] bytes)
    {
        var decoded = TextFileCodec.Decode(bytes);

        Assert.True(decoded.IsLossless, name);
        Assert.Equal(bytes, TextFileCodec.Encode(decoded.Text, decoded.Format));
    }

    [Fact]
    public void Utf8_bom_is_detected_and_not_part_of_the_text()
    {
        var decoded = TextFileCodec.Decode([0xEF, 0xBB, 0xBF, (byte)'a']);

        Assert.True(decoded.Format.HasBom);
        Assert.Equal(TextEncodingKind.Utf8, decoded.Format.Encoding);
        Assert.Equal("a", decoded.Text);
    }

    [Fact]
    public void Invalid_utf8_is_reported_and_never_considered_lossless()
    {
        var decoded = TextFileCodec.Decode([(byte)'a', 0xFF, 0xFE, (byte)'b']);

        Assert.Equal(DecodeFailure.InvalidSequence, decoded.Failure);
    }

    [Fact]
    public void Latin1_can_be_chosen_explicitly_and_round_trips_every_byte()
    {
        var all = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();

        var decoded = TextFileCodec.Decode(all, TextEncodingKind.Latin1);

        Assert.True(decoded.IsLossless);
        Assert.Equal(all, TextFileCodec.Encode(decoded.Text, decoded.Format));
    }

    [Fact]
    public void Windows1252_with_undefined_bytes_round_trips_or_is_flagged()
    {
        byte[] bytes = [0x81, 0x8D, 0x93, 0x94, 0x41];

        var decoded = TextFileCodec.Decode(bytes, TextEncodingKind.Windows1252);

        if (decoded.IsLossless)
        {
            Assert.Equal(bytes, TextFileCodec.Encode(decoded.Text, decoded.Format));
        }
        else
        {
            Assert.Equal(DecodeFailure.NotRoundTrippable, decoded.Failure);
        }
    }

    [Theory]
    [InlineData("a\nb\nc\n", LineEnding.Lf, false)]
    [InlineData("a\r\nb\r\n", LineEnding.CrLf, false)]
    [InlineData("a\rb\r", LineEnding.Cr, false)]
    [InlineData("a\r\nb\r\nc\n", LineEnding.CrLf, true)]
    public void Line_ending_style_is_described(string text, LineEnding expected, bool mixed)
    {
        var format = LineEndingAnalyzer.Describe(TextEncodingKind.Utf8, false, text);

        Assert.Equal(expected, format.DominantLineEnding);
        Assert.Equal(mixed, format.HasMixedLineEndings);
    }

    [Fact]
    public void Characters_outside_the_target_encoding_are_detected_before_saving()
    {
        Assert.False(TextFileCodec.CanRepresent("emoji \U0001F600", TextEncodingKind.Latin1));
        Assert.True(TextFileCodec.CanRepresent("caf\u00e9", TextEncodingKind.Latin1));
    }

    [Fact]
    public void Random_unicode_documents_round_trip_in_every_unicode_encoding()
    {
        var random = new Random(20261026);
        var alphabet = new[] { "a", "Z", " ", "\n", "\r\n", "\r", "\t", "#", "*", "\u00e9", "e\u0301", "\U0001F600", "\u4E2D", "\u0645", "\uFEFF" };
        var formats = new (TextEncodingKind Kind, bool Bom)[]
        {
            (TextEncodingKind.Utf8, false), (TextEncodingKind.Utf8, true),
            (TextEncodingKind.Utf16LittleEndian, true), (TextEncodingKind.Utf16BigEndian, true),
            (TextEncodingKind.Utf32LittleEndian, true), (TextEncodingKind.Utf32BigEndian, true),
        };

        for (var i = 0; i < 300; i++)
        {
            var builder = new StringBuilder();
            var length = random.Next(0, 60);
            for (var j = 0; j < length; j++)
            {
                builder.Append(alphabet[random.Next(alphabet.Length)]);
            }

            var text = builder.ToString();
            foreach (var (kind, bom) in formats)
            {
                // Without a BOM, a leading U+FEFF is bytewise identical to a BOM, so it is inherently ambiguous.
                if (!bom && text.StartsWith('\uFEFF'))
                {
                    continue;
                }

                var format = LineEndingAnalyzer.Describe(kind, bom, text);
                var bytes = TextFileCodec.Encode(text, format);
                var decoded = TextFileCodec.Decode(bytes);

                Assert.True(decoded.IsLossless, $"{kind} bom={bom} text={text.Length}");
                Assert.Equal(text, decoded.Text);
                Assert.Equal(bytes, TextFileCodec.Encode(decoded.Text, decoded.Format));
            }
        }
    }
}
