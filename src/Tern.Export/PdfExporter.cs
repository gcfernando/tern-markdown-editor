using PdfSharp.Pdf;
using Tern.Rendering.Preview;
using Tern.Rendering.Resources;

namespace Tern.Export;

/// <summary>
/// Exports Markdown as a PDF with headings (also as bookmarks), paragraphs, lists, task lists, tables, code blocks, quotes,
/// alerts, footnotes, images, native Mermaid diagrams, links and page numbers. Text is set in fonts already installed on the machine
/// (Segoe UI/Consolas, Arial/Courier New, DejaVu, Liberation or Noto); no font is bundled or downloaded.
/// </summary>
public static class PdfExporter
{
    /// <summary>Parses <paramref name="markdown"/> with the preview pipeline and lays it out as PDF.</summary>
    /// <exception cref="ExportException">No usable system font was found.</exception>
    public static PdfExportResult Export(string markdown, PdfExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        options ??= new PdfExportOptions();
        var resolver = PreviewBuilder.CreateResolver(options.Preview);
        var document = PreviewBuilder.Build(markdown, options.Preview, resolver);
        return Export(document, options, resolver);
    }

    /// <summary>Lays out an already built preview document. <paramref name="resolver"/> must be the one the document was built with.</summary>
    /// <exception cref="ExportException">No usable system font was found.</exception>
    public static PdfExportResult Export(PreviewDocument document, PdfExportOptions options, SafeResourceResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(resolver);
        Validate(options);
        SystemFontResolver.Install();

        var warnings = new List<string>();
        if (document.Truncated) warnings.Add("The export is incomplete: the document exceeded the rendering budget. Only the first part was exported.");
        var images = new ImageEmbedder(options.MaxImageBytes, options.MaxTotalImageBytes, warnings, options.RemoteImageBytes);
        var created = (options.CreationDate ?? DateTimeOffset.UtcNow).UtcDateTime;
        using var pdf = new PdfDocument();
        pdf.Info.Title = ExportText.Clean(options.Title ?? FirstHeading(document) ?? "Document");
        pdf.Info.Author = ExportText.Clean(options.Author);
        pdf.Info.Creator = "Tern";
        pdf.Info.CreationDate = created;
        pdf.Options.CompressContentStreams = true;

        using var renderer = new PdfRenderer(pdf, options, resolver, images, warnings);
        var complete = renderer.Render(document);
        if (options.PageNumbers)
        {
            renderer.AddPageNumbers();
        }

        renderer.AddLinkAnnotations();
        var pageCount = renderer.PageCount;
        using var stream = new MemoryStream();
        pdf.Save(stream, closeStream: false);
        return new PdfExportResult(stream.ToArray(), pageCount, warnings.Distinct(StringComparer.Ordinal).ToList(), document.Truncated || !complete);
    }

    /// <summary>Exports to a file (written atomically) and returns the result.</summary>
    public static PdfExportResult ExportToFile(string markdown, string outputPath, PdfExportOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var result = Export(markdown, options);
        AtomicFile.Write(outputPath, result.Data, options?.Preview.DocumentPath);
        return result;
    }

    private static string? FirstHeading(PreviewDocument document) =>
        document.Blocks.OfType<HeadingNode>().Select(h => h.PlainText.Trim()).FirstOrDefault(t => t.Length > 0);

    private static void Validate(PdfExportOptions options)
    {
        var margins = options.Margins;
        if (margins.Top < 0 || margins.Left < 0 || margins.Right < 0 || margins.Bottom < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Margins must not be negative.");
        }

        if (margins.Bottom < 28 && options.PageNumbers)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The bottom margin must be at least 28 points to leave room for page numbers.");
        }

        var width = PdfRenderer.PageWidth(options.PageSize) - margins.Left - margins.Right;
        var height = PdfRenderer.PageHeight(options.PageSize) - margins.Top - margins.Bottom;
        if (width < 150 || height < 150)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The margins leave less than 150 points of usable page area.");
        }

        if (options.BaseFontSize is < 6 or > 36)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "BaseFontSize must be between 6 and 36 points.");
        }

        if (options.MaxPages < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxPages must be at least 1.");
        }
    }
}
