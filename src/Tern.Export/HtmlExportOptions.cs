using Tern.Rendering.Preview;

namespace Tern.Export;

/// <summary>Colour scheme of an exported HTML page.</summary>
public enum ExportTheme
{
    /// <summary>Follows the reader's system setting (<c>prefers-color-scheme</c>).</summary>
    Auto,

    /// <summary>Always light.</summary>
    Light,

    /// <summary>Always dark.</summary>
    Dark,
}

/// <summary>Settings for <see cref="HtmlExporter"/>.</summary>
public sealed record HtmlExportOptions
{
    /// <summary>Optional host retrieval of remote image bytes. The desktop host fetches live bytes; the exporter has no network transport.</summary>
    public Func<string, byte[]?>? RemoteImageBytes { get; init; }
    /// <summary>How the Markdown is parsed, and which folders local images may come from.</summary>
    public PreviewOptions Preview { get; init; } = ExportDefaults.PreviewOptions;

    /// <summary>The page title. When null the first heading, the front matter <c>title</c> or "Document" is used.</summary>
    public string? Title { get; init; }

    /// <summary>The <c>lang</c> attribute of the page. Must be a simple BCP 47 tag; anything else falls back to "en".</summary>
    public string Language { get; init; } = "en";

    /// <summary>The colour scheme.</summary>
    public ExportTheme Theme { get; init; } = ExportTheme.Auto;

    /// <summary>Largest single image that is embedded. Larger images are replaced by a placeholder. Default 5 MB.</summary>
    public long MaxInlineImageBytes { get; init; } = 5L * 1024 * 1024;

    /// <summary>Largest total size of all embedded images. Default 50 MB.</summary>
    public long MaxTotalImageBytes { get; init; } = 50L * 1024 * 1024;

    /// <summary>Whether front matter is shown as a collapsible properties block.</summary>
    public bool IncludeFrontMatter { get; init; } = true;
}

/// <summary>The outcome of an HTML export.</summary>
/// <param name="Html">The complete, self-contained HTML document.</param>
/// <param name="Title">The title that was used.</param>
/// <param name="Warnings">Things that were left out or replaced, for example images that could not be embedded.</param>
/// <param name="Truncated">True when the preview budget cut the document short.</param>
/// <param name="EmbeddedImageCount">How many images were embedded.</param>
public sealed record HtmlExportResult(string Html, string Title, IReadOnlyList<string> Warnings, bool Truncated, int EmbeddedImageCount);
