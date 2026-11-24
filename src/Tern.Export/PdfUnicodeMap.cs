using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Tern.Export;

/// <summary>Corrects PDFsharp 6.2's supplementary character maps to UTF-16BE so copy/search retains the original Unicode.</summary>
internal static partial class PdfUnicodeMap
{
    [GeneratedRegex(@"(?<range><[0-9A-Fa-f]{4}>\s*<[0-9A-Fa-f]{4}>\s*)<(?<scalar>[0-9A-Fa-f]{5,6})>", RegexOptions.CultureInvariant)]
    private static partial Regex SupplementaryRange();

    public static byte[] Correct(byte[] data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var input = new MemoryStream(data);
        using var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        var seen = new HashSet<PdfDictionary>();
        var changed = false;
        foreach (var page in document.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fonts = page.Resources.Elements.GetDictionary("/Font");
            if (fonts is null) continue;
            foreach (var key in fonts.Elements.Keys)
            {
                var map = fonts.Elements.GetDictionary(key)?.Elements.GetDictionary("/ToUnicode");
                if (map?.Stream is null || !seen.Add(map)) continue;
                var original = Encoding.ASCII.GetString(map.Stream.UnfilteredValue);
                var corrected = SupplementaryRange().Replace(original, match =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var scalar = int.Parse(match.Groups["scalar"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    if (!Rune.IsValid(scalar)) return match.Value;
                    var utf16 = Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(char.ConvertFromUtf32(scalar)));
                    return match.Groups["range"].Value + "<" + utf16 + ">";
                });
                if (corrected == original) continue;
                map.Elements.Remove("/Filter");
                map.Elements.Remove("/DecodeParms");
                map.Stream.Value = Encoding.ASCII.GetBytes(corrected);
                changed = true;
            }
        }
        if (!changed) return data;
        using var output = new CancellableMemoryStream(cancellationToken);
        document.Save(output, closeStream: false);
        return output.ToArray();
    }
}
