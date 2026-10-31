using Tern.Rendering.Preview;

namespace Tern.Export;

/// <summary>Paper size of a PDF export.</summary>
public enum PdfPageSize
{
    /// <summary>210 × 297 mm.</summary>
    A4,

    /// <summary>8.5 × 11 inches.</summary>
    Letter,
}

/// <summary>Page margins in PDF points (1/72 inch).</summary>
/// <param name="Top">Top margin.</param>
/// <param name="Right">Right margin.</param>
/// <param name="Bottom">Bottom margin. Page numbers are printed inside it.</param>
/// <param name="Left">Left margin.</param>
public readonly record struct PdfMargins(double Top, double Right, double Bottom, double Left)
{
    private const double PointsPerMillimetre = 72.0 / 25.4;

    /// <summary>The default: 20 mm on every side.</summary>
    public static PdfMargins Default { get; } = FromMillimetres(20);

    /// <summary>The same margin, in millimetres, on all four sides.</summary>
    public static PdfMargins FromMillimetres(double millimetres) => FromMillimetres(millimetres, millimetres, millimetres, millimetres);

    /// <summary>Margins given in millimetres.</summary>
    public static PdfMargins FromMillimetres(double top, double right, double bottom, double left) =>
        new(top * PointsPerMillimetre, right * PointsPerMillimetre, bottom * PointsPerMillimetre, left * PointsPerMillimetre);
}

/// <summary>Settings for <see cref="PdfExporter"/>.</summary>
public sealed record PdfExportOptions
{
    /// <summary>Optional host retrieval of live remote image bytes; unsupported PDF formats produce a warning.</summary>
    public Func<string, byte[]?>? RemoteImageBytes { get; init; }
    /// <summary>How the Markdown is parsed, and which folders local images may come from.</summary>
    public PreviewOptions Preview { get; init; } = ExportDefaults.PreviewOptions;

    /// <summary>The document title stored in the PDF information dictionary. When null the first heading is used.</summary>
    public string? Title { get; init; }

    /// <summary>The author stored in the PDF information dictionary.</summary>
    public string? Author { get; init; }

    /// <summary>Paper size. Default A4.</summary>
    public PdfPageSize PageSize { get; init; } = PdfPageSize.A4;

    /// <summary>Page margins. Default 20 mm. The bottom margin must leave room for page numbers.</summary>
    public PdfMargins Margins { get; init; } = PdfMargins.Default;

    /// <summary>Body text size in points (6 to 36). Default 11.</summary>
    public double BaseFontSize { get; init; } = 11;

    /// <summary>Whether "n / N" is printed at the bottom of every page.</summary>
    public bool PageNumbers { get; init; } = true;

    /// <summary>Largest single image that is embedded. Default 20 MB.</summary>
    public long MaxImageBytes { get; init; } = 20L * 1024 * 1024;

    /// <summary>Largest total size of all embedded images. Default 200 MB.</summary>
    public long MaxTotalImageBytes { get; init; } = 200L * 1024 * 1024;

    /// <summary>The export stops adding pages after this many and marks the result truncated. Default 2000.</summary>
    public int MaxPages { get; init; } = 2000;

    /// <summary>The creation date stored in the file. Set it to a fixed value to get byte-for-byte repeatable output; null uses the current time.</summary>
    public DateTimeOffset? CreationDate { get; init; }

    /// <summary>Whether front matter is printed as a small properties table.</summary>
    public bool IncludeFrontMatter { get; init; } = true;
}

/// <summary>The outcome of a PDF export.</summary>
/// <param name="Data">The PDF file.</param>
/// <param name="PageCount">The number of pages.</param>
/// <param name="Warnings">Things that were left out or replaced, for example unsupported image formats.</param>
/// <param name="Truncated">True when the preview budget or the page limit cut the document short.</param>
public sealed record PdfExportResult(byte[] Data, int PageCount, IReadOnlyList<string> Warnings, bool Truncated);
