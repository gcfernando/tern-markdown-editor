using System.Globalization;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Tern.Export;

/// <summary>Validated one-based original pages. A subset never repaginates the document.</summary>
public static class PrintPageSelection
{
    public static bool TryParse(string? text, int pageCount, out int[] pages, out string? error)
    {
        pages = []; error = null;
        if (pageCount < 1) { error = "The document has no printable pages."; return false; }
        if (text?.Length > 32000) { error = "The page selection is too long. Use ranges to shorten it."; return false; }
        var result = new SortedSet<int>();
        foreach (var part in (text ?? "").Replace('–', '-').Replace('—', '-').Replace('‑', '-').Replace('−', '-').Split(','))
        {
            var range = part.Trim().Split('-');
            if (range.Length is < 1 or > 2 || !int.TryParse(range[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var first)
                || !int.TryParse(range[^1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var last)
                || first < 1 || last < first || last > pageCount)
            { error = $"Use pages 1–{pageCount}, for example 1-3, 5. Ranges must run from a lower page to a higher page."; return false; }
            if ((long)last - first + 1 > 100000) { error = "The page selection is too large. Use a smaller range."; return false; }
            for (var p = first; ; p++)
            {
                result.Add(p);
                if (result.Count > 100000) { error = "The page selection is too large. Use a smaller range."; return false; }
                if (p == last) break; // Do not overflow when last == int.MaxValue.
            }
        }
        pages = result.ToArray();
        return pages.Length > 0;
    }

    public static byte[] Extract(byte[] pdfBytes, IReadOnlyList<int> pages)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        ArgumentNullException.ThrowIfNull(pages);
        using var source = PdfReader.Open(new MemoryStream(pdfBytes, writable: false), PdfDocumentOpenMode.Import);
        if (pages.Count == 0 || pages.Any(p => p < 1 || p > source.PageCount)) throw new ArgumentOutOfRangeException(nameof(pages));
        using var selected = new PdfDocument();
        selected.Info.Title = source.Info.Title;
        selected.Info.Creator = "Tern";
        foreach (var p in pages.Distinct().Order()) selected.AddPage(source.Pages[p - 1]);
        using var output = new MemoryStream();
        selected.Save(output, closeStream: false);
        return output.ToArray();
    }
}
