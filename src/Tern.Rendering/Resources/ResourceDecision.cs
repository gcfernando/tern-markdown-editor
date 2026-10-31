namespace Tern.Rendering.Resources;

/// <summary>Why a link or image destination was refused.</summary>
public enum BlockReason
{
    /// <summary>The destination is empty or whitespace.</summary>
    Empty,

    /// <summary>The destination contains control characters or NUL.</summary>
    ControlCharacters,

    /// <summary>The scheme is not on the allow-list.</summary>
    DisallowedScheme,

    /// <summary>A scripting scheme such as <c>javascript:</c> or <c>vbscript:</c>.</summary>
    ScriptScheme,

    /// <summary>A <c>file:</c> URL. Local files must be referenced by relative path.</summary>
    FileUrl,

    /// <summary>A <c>data:</c> URL that is not an allowed image, or an image that exceeds the size cap.</summary>
    DataUrl,

    /// <summary>A malformed data URI whose payload does not match its declared image type.</summary>
    InvalidDataUri,

    /// <summary>A UNC or network-path reference such as <c>\\server\share</c> or <c>//server</c>.</summary>
    UncPath,

    /// <summary>A Win32 device path (<c>\\.\</c>, <c>\\?\</c>) or a reserved device name such as <c>CON</c>.</summary>
    DevicePath,

    /// <summary>An NTFS alternate data stream or any other stray colon in a path.</summary>
    AlternateDataStream,

    /// <summary>The path climbs out of every allowed root using <c>..</c>.</summary>
    Traversal,

    /// <summary>An absolute path that is not inside an allowed root.</summary>
    OutsideAllowedRoots,

    /// <summary>A symbolic link or junction whose target lies outside every allowed root.</summary>
    SymlinkEscape,

    /// <summary>The path or destination exceeds the configured length limit.</summary>
    PathTooLong,

    /// <summary>The path is syntactically invalid, or is encoded more than once in a way that hides its meaning.</summary>
    InvalidPath,

    /// <summary>The URL could not be parsed.</summary>
    InvalidUri,

    /// <summary>A relative path was given but there is no document directory or workspace root to resolve it against.</summary>
    NoBaseDirectory,

    /// <summary>The referenced file does not exist.</summary>
    NotFound,

    /// <summary>The image file exceeds the size limit.</summary>
    FileTooLarge,

    /// <summary>The file extension is not an allowed image type.</summary>
    UnsupportedImageType,

    /// <summary>The image lives on the network. Tern never fetches remote resources.</summary>
    RemoteResourceBlocked,
}

/// <summary>The outcome of classifying a link or image destination. Always one of the sealed subtypes in this namespace.</summary>
public abstract record ResourceDecision
{
    private protected ResourceDecision()
    {
    }
}

/// <summary>A file inside the allowed roots.</summary>
/// <param name="CanonicalPath">The fully resolved path with symbolic links followed.</param>
/// <param name="Fragment">The decoded <c>#fragment</c>, if any.</param>
/// <param name="IsActiveContentRisk">True for formats that can carry active content (SVG, HTML) or run code (executables, scripts). Rasterise or confirm before use.</param>
/// <param name="Exists">Whether the file or directory exists. Always true for images.</param>
public sealed record LocalFile(string CanonicalPath, string? Fragment, bool IsActiveContentRisk, bool Exists) : ResourceDecision;

/// <summary>An http, https or mailto link. It may be opened by the operating system after the user confirms; the application never fetches it.</summary>
public sealed record ExternalLink(Uri Uri, string Original) : ResourceDecision;

/// <summary>A same-document fragment reference.</summary>
public sealed record Anchor(string Fragment) : ResourceDecision;

/// <summary>A validated, size-capped PNG/JPEG/GIF/WebP image embedded in the document as a data URI.</summary>
public sealed record InlineImage(string MediaType, byte[] Data) : ResourceDecision;

/// <summary>A destination the application refuses to act on.</summary>
/// <param name="Reason">The category of refusal.</param>
/// <param name="Original">The destination exactly as written, so the UI can show a placeholder.</param>
/// <param name="Detail">A short human-readable explanation.</param>
public sealed record Blocked(BlockReason Reason, string Original, string Detail) : ResourceDecision;
