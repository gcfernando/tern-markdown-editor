namespace Tern.Markdown;

/// <summary>Half-open range of UTF-16 offsets in the source text.</summary>
public readonly record struct SourceRange(int Start, int End)
{
    public int Length => End - Start;

    public bool Contains(int offset) => offset >= Start && offset < End;

    public bool Intersects(SourceRange other) => Start < other.End && other.Start < End;
}

public sealed record HeadingInfo(int Level, string Text, string Anchor, int Line, SourceRange Range);

public enum LinkKind
{
    Inline,
    Image,
    Autolink,
    ReferenceDefinition,
    WikiLink,
}

/// <param name="Destination">The destination exactly as written between the delimiters.</param>
/// <param name="Range">Where <paramref name="Destination"/> sits in the source; rewriting replaces this range only.</param>
public sealed record LinkInfo(LinkKind Kind, string Destination, SourceRange Range, int Line, string Label);

public sealed record TaskItemInfo(int Line, bool Checked, SourceRange Marker, string Text);

public sealed record CodeBlockInfo(string? Language, SourceRange Range, int Line, bool IsFenced);

public sealed record TableInfo(SourceRange Range, int Line, int Columns, int Rows);

public sealed record FootnoteInfo(string Label, SourceRange Range, int Line);

public sealed record FrontMatterInfo(SourceRange Range, string Yaml);

/// <summary>Everything the application needs to know about a document's structure, computed from one parse.</summary>
public sealed class MarkdownAnalysis
{
    public required MarkdownProfile Profile { get; init; }

    public required LineIndex Lines { get; init; }

    public FrontMatterInfo? FrontMatter { get; init; }

    public IReadOnlyList<HeadingInfo> Headings { get; init; } = [];

    /// <summary>Safe explicit HTML id/name targets, in original source coordinates.</summary>
    public IReadOnlyDictionary<string, SourceRange> ExplicitAnchors { get; init; } = new Dictionary<string, SourceRange>(StringComparer.Ordinal);

    public IReadOnlyList<LinkInfo> Links { get; init; } = [];

    public IReadOnlyList<TaskItemInfo> Tasks { get; init; } = [];

    public IReadOnlyList<CodeBlockInfo> CodeBlocks { get; init; } = [];

    public IReadOnlyList<TableInfo> Tables { get; init; } = [];

    public IReadOnlyList<FootnoteInfo> Footnotes { get; init; } = [];

    public int WordCount { get; init; }

    public int CharacterCount { get; init; }

    public IEnumerable<string> Anchors => Headings.Select(h => h.Anchor).Concat(ExplicitAnchors.Keys);
}
