using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Tern.Rendering.Preview;
using Tern.Rendering.Resources;

namespace Tern.Export;

/// <summary>
/// Exports Markdown as one self-contained HTML file: styles are embedded, local images become <c>data:</c> URIs, and the page
/// contains no scripts, external fonts or remote references. A Content-Security-Policy meta tag enforces this in the reader's browser.
/// </summary>
public static partial class HtmlExporter
{
    [GeneratedRegex(@"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8}){0,3}$")]
    private static partial Regex LanguageTag();

    /// <summary>Parses <paramref name="markdown"/> with the preview pipeline and renders it as HTML.</summary>
    public static HtmlExportResult Export(string markdown, HtmlExportOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        options ??= new HtmlExportOptions();
        var resolver = PreviewBuilder.CreateResolver(options.Preview);
        var document = PreviewBuilder.Build(markdown, options.Preview, resolver, cancellationToken);
        return Export(document, options, resolver, cancellationToken);
    }

    /// <summary>Renders an already built preview document. <paramref name="resolver"/> must be the one the document was built with.</summary>
    public static HtmlExportResult Export(PreviewDocument document, HtmlExportOptions options, SafeResourceResolver resolver, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(resolver);
        cancellationToken.ThrowIfCancellationRequested();

        var warnings = new List<string>();
        if (document.Truncated) warnings.Add("The export is incomplete: the document exceeded the rendering budget. Only the first part was exported.");
        var images = new ImageEmbedder(options.MaxInlineImageBytes, options.MaxTotalImageBytes, warnings, options.RemoteImageBytes, cancellationToken);
        var body = new HtmlRenderer(resolver, images, options.IncludeFrontMatter, warnings, cancellationToken).Render(document);
        var title = ResolveTitle(document, options);
        // HTML parsers normalize CRLF/CR to LF before applying CSP hashes. Hash and emit
        // that same canonical text, regardless of this source file's checkout line endings.
        var css = HtmlTheme.Css.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var cssHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(css)));
        var theme = options.Theme.ToString().ToLowerInvariant();
        var language = LanguageTag().IsMatch(options.Language) ? options.Language : "en";

        var html = new StringBuilder(body.Length + css.Length + 1024);
        html.Append("<!DOCTYPE html>\n<html lang=\"").Append(language).Append("\" data-theme=\"").Append(theme).Append("\">\n<head>\n")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'sha256-").Append(cssHash).Append("'; base-uri 'none'; form-action 'none'\">\n")
            .Append("<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<meta name=\"color-scheme\" content=\"light dark\">\n")
            .Append("<meta name=\"generator\" content=\"Tern\">\n<title>");
        HtmlText.Escape(html, title);
        html.Append("</title>\n<style>").Append(css).Append("</style>\n</head>\n<body>\n<main class=\"markdown-body\">\n")
            .Append(body).Append("</main>\n</body>\n</html>\n");

        cancellationToken.ThrowIfCancellationRequested();
        return new HtmlExportResult(html.ToString(), title, warnings.Distinct(StringComparer.Ordinal).ToList(), document.Truncated, images.Count);
    }

    /// <summary>Exports to a file (written atomically, UTF-8 without BOM) and returns the result.</summary>
    public static HtmlExportResult ExportToFile(string markdown, string outputPath, HtmlExportOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var result = Export(markdown, options, cancellationToken);
        AtomicFile.Write(outputPath, new UTF8Encoding(false).GetBytes(result.Html), options?.Preview.DocumentPath, cancellationToken);
        return result;
    }

    private static string ResolveTitle(PreviewDocument document, HtmlExportOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Title))
        {
            return ExportText.Clean(options.Title).Trim();
        }

        var front = document.Blocks.OfType<FrontMatterNode>().FirstOrDefault()?.Properties
            .FirstOrDefault(p => string.Equals(p.Key, "title", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!string.IsNullOrWhiteSpace(front))
        {
            return front.Trim();
        }

        var heading = document.Blocks.OfType<HeadingNode>().FirstOrDefault(h => h.PlainText.Trim().Length > 0);
        return heading?.PlainText.Trim() ?? "Document";
    }
}
