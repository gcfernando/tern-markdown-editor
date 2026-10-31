using System.Collections.Frozen;

namespace Tern.Rendering.Resources;

/// <summary>Configuration for <see cref="SafeResourceResolver"/>.</summary>
public sealed record ResourceResolverOptions
{
    /// <summary>Image extensions that may be returned, lower-case with the leading dot.</summary>
    public static readonly IReadOnlySet<string> DefaultImageExtensions =
        new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".svg" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Directory of the document; relative destinations resolve against it and it is always an allowed root.</summary>
    public string? DocumentDirectory { get; init; }

    /// <summary>Workspace root; destinations starting with <c>/</c> resolve against it and it is an allowed root.</summary>
    public string? WorkspaceRoot { get; init; }

    /// <summary>Further directories that may be referenced.</summary>
    public IReadOnlyList<string> AdditionalRoots { get; init; } = [];

    /// <summary>Exact image files explicitly selected for this document; does not grant access to their folders.</summary>
    public IReadOnlyList<string> AdditionalImageFiles { get; init; } = [];

    /// <summary>Largest image file that will be accepted.</summary>
    public long MaxImageBytes { get; init; } = 20L * 1024 * 1024;

    /// <summary>Whether <c>data:image/png|jpeg|gif|webp;base64</c> images are accepted.</summary>
    public bool AllowDataImages { get; init; } = true;

    /// <summary>Largest decoded data-URI image.</summary>
    public int MaxDataImageBytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>Longest accepted path, measured after resolution.</summary>
    public int MaxPathLength { get; init; } = 1024;

    /// <summary>Longest accepted non-data link destination.</summary>
    public int MaxDestinationLength { get; init; } = 8192;

    /// <summary>Extensions accepted for images.</summary>
    public IReadOnlySet<string> ImageExtensions { get; init; } = DefaultImageExtensions;
}
