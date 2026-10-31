using Tern.Markdown;
using Tern.Rendering.Html;
using Tern.Rendering.Resources;

namespace Tern.Rendering.Preview;

/// <summary>Settings for <see cref="PreviewBuilder"/>.</summary>
public sealed record PreviewOptions
{
    /// <summary>Which Markdown syntax is recognised.</summary>
    public MarkdownProfile Profile { get; init; } = MarkdownProfile.Extended;

    /// <summary>Path of the document being previewed; its directory is the base for relative links and images.</summary>
    public string? DocumentPath { get; init; }

    /// <summary>Workspace root: an allowed root, and the base for destinations starting with <c>/</c>.</summary>
    public string? WorkspaceRoot { get; init; }

    /// <summary>Additional directories that links and images may refer to.</summary>
    public IReadOnlyList<string> AllowedRoots { get; init; } = [];

    /// <summary>Limits for image size, data URIs and paths. Its roots are merged with <see cref="DocumentPath"/>, <see cref="WorkspaceRoot"/> and <see cref="AllowedRoots"/>.</summary>
    public ResourceResolverOptions Resolver { get; init; } = new();

    /// <summary>Options for sanitising raw HTML. The URL policy is always combined with the resource resolver.</summary>
    public HtmlSanitizerOptions Sanitizer { get; init; } = new();

    /// <summary>Stop building after this many nodes and mark the document truncated.</summary>
    public int MaxNodes { get; init; } = 250_000;

    /// <summary>Source beyond this many characters is not parsed; the cut happens at a block boundary where possible.</summary>
    public int MaxSourceLength { get; init; } = 4_000_000;

    /// <summary>Nesting beyond this depth is flattened to plain text.</summary>
    public int MaxDepth { get; init; } = 48;

    /// <summary>Wall-clock budget including parsing. Checked cooperatively after the non-interruptible Markdig parse.</summary>
    public TimeSpan MaxBuildTime { get; init; } = TimeSpan.FromSeconds(5);
}
