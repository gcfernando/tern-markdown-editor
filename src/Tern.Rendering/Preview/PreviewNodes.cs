using Tern.Rendering.Html;
using Tern.Rendering.Resources;

namespace Tern.Rendering.Preview;

/// <summary>Where a node came from in the Markdown source. Lines are 1-based and inclusive; offsets are UTF-16 code units, end exclusive.</summary>
public readonly record struct SourceLocation(int StartLine, int EndLine, int StartOffset, int EndOffset)
{
    /// <summary>Whether <paramref name="line"/> lies within the node's lines.</summary>
    public bool ContainsLine(int line) => line >= StartLine && line <= EndLine;

    /// <summary>Whether <paramref name="offset"/> lies within the node's text.</summary>
    public bool ContainsOffset(int offset) => offset >= StartOffset && offset < EndOffset;
}

/// <summary>Base of everything in a <see cref="PreviewDocument"/>.</summary>
/// <param name="Source">The node's location in the source text.</param>
public abstract record PreviewNode(SourceLocation Source);

/// <summary>A block-level node.</summary>
public abstract record BlockNode(SourceLocation Source) : PreviewNode(Source)
{
    /// <summary>Allow-listed presentation alignment inherited from a surrounding HTML container.</summary>
    public BlockAlignment Alignment { get; init; }
}

/// <summary>Safe block presentation values; arbitrary CSS never enters the preview model.</summary>
public enum BlockAlignment
{
    None,
    Left,
    Center,
    Right,
}

/// <summary>An inline node.</summary>
public abstract record InlineNode(SourceLocation Source) : PreviewNode(Source);

/// <summary>Column alignment of a table.</summary>
public enum TableAlignment
{
    /// <summary>No alignment given.</summary>
    None,

    /// <summary>Left aligned.</summary>
    Left,

    /// <summary>Centred.</summary>
    Center,

    /// <summary>Right aligned.</summary>
    Right,
}

/// <summary>State of a task-list item.</summary>
public enum TaskState
{
    /// <summary>Not a task item.</summary>
    None,

    /// <summary><c>[ ]</c></summary>
    Unchecked,

    /// <summary><c>[x]</c></summary>
    Checked,
}

/// <summary>GitHub-style callout kinds.</summary>
public enum AlertKind
{
    /// <summary>NOTE</summary>
    Note,

    /// <summary>TIP</summary>
    Tip,

    /// <summary>IMPORTANT</summary>
    Important,

    /// <summary>WARNING</summary>
    Warning,

    /// <summary>CAUTION</summary>
    Caution,
}

/// <summary>Formatting that comes from allow-listed inline HTML.</summary>
public enum InlineStyle
{
    /// <summary><c>&lt;kbd&gt;</c></summary>
    Kbd,

    /// <summary><c>&lt;sub&gt;</c></summary>
    Subscript,

    /// <summary><c>&lt;sup&gt;</c></summary>
    Superscript,

    /// <summary><c>&lt;mark&gt;</c></summary>
    Mark,

    /// <summary><c>&lt;u&gt;</c> or <c>&lt;ins&gt;</c></summary>
    Underline,
}

/// <summary>A heading. <paramref name="Anchor"/> follows GitHub's slug rules, with duplicate suffixes.</summary>
public sealed record HeadingNode(SourceLocation Source, int Level, string Anchor, string PlainText, IReadOnlyList<InlineNode> Inlines) : BlockNode(Source);

/// <summary>A paragraph.</summary>
public sealed record ParagraphNode(SourceLocation Source, IReadOnlyList<InlineNode> Inlines) : BlockNode(Source);

/// <summary>One item of a list.</summary>
public sealed record ListItemNode(SourceLocation Source, IReadOnlyList<BlockNode> Blocks, TaskState Task) : PreviewNode(Source);

/// <summary>An ordered or bullet list. A loose list (<paramref name="Tight"/> false) separates its items with spacing.</summary>
public sealed record ListNode(SourceLocation Source, bool Ordered, int StartNumber, bool Tight, IReadOnlyList<ListItemNode> Items) : BlockNode(Source);

/// <summary>A block quote.</summary>
public sealed record BlockQuoteNode(SourceLocation Source, IReadOnlyList<BlockNode> Blocks) : BlockNode(Source);

/// <summary>A GitHub-style alert (<c>&gt; [!NOTE]</c>).</summary>
public sealed record AlertNode(SourceLocation Source, AlertKind Kind, IReadOnlyList<BlockNode> Blocks) : BlockNode(Source);

/// <summary>A fenced or indented code block. <paramref name="Language"/> is the first word of the info string.</summary>
public sealed record CodeBlockNode(SourceLocation Source, string? Language, string Code, bool IsFenced) : BlockNode(Source);

/// <summary>A horizontal rule.</summary>
public sealed record ThematicBreakNode(SourceLocation Source) : BlockNode(Source);

/// <summary>A table cell.</summary>
public sealed record TableCellNode(SourceLocation Source, IReadOnlyList<InlineNode> Inlines, TableAlignment Alignment) : PreviewNode(Source);

/// <summary>A table row.</summary>
public sealed record TableRowNode(SourceLocation Source, IReadOnlyList<TableCellNode> Cells, bool IsHeader) : PreviewNode(Source);

/// <summary>A GFM table.</summary>
public sealed record TableNode(SourceLocation Source, IReadOnlyList<TableAlignment> Alignments, TableRowNode? Header, IReadOnlyList<TableRowNode> Body) : BlockNode(Source);

/// <summary>One footnote definition. <paramref name="Number"/> matches <see cref="FootnoteRefNode.Number"/>.</summary>
public sealed record FootnoteNode(SourceLocation Source, string Label, int Number, IReadOnlyList<BlockNode> Blocks) : PreviewNode(Source);

/// <summary>All footnote definitions, shown together at the end of the document.</summary>
public sealed record FootnoteSectionNode(SourceLocation Source, IReadOnlyList<FootnoteNode> Items) : BlockNode(Source);

/// <summary>A display math block. Render it with <see cref="Tern.Rendering.Mathematics.MathText.Render"/>.</summary>
public sealed record MathBlockNode(SourceLocation Source, string Latex) : BlockNode(Source);

/// <summary>A diagram fence such as <c>mermaid</c>. Parse it with <see cref="Tern.Rendering.Mermaid.MermaidDiagram.TryParse"/>.</summary>
public sealed record DiagramNode(SourceLocation Source, string DiagramType, string DiagramSource) : BlockNode(Source);

/// <summary>A raw HTML block after sanitisation. <paramref name="Html"/> is the safe serialisation of <paramref name="Pieces"/>.</summary>
public sealed record HtmlSafeBlockNode(SourceLocation Source, IReadOnlyList<HtmlPiece> Pieces, string Html) : BlockNode(Source)
{
    /// <summary>Link decisions captured by the same policy as Markdown links.</summary>
    public IReadOnlyDictionary<string, ResourceDecision> Links { get; init; } = new Dictionary<string, ResourceDecision>();

    /// <summary>Image decisions captured by the same policy as Markdown images.</summary>
    public IReadOnlyDictionary<string, ResourceDecision> Images { get; init; } = new Dictionary<string, ResourceDecision>();
}

/// <summary>A collapsible <c>&lt;details&gt;</c> section assembled from HTML blocks and the Markdown between them.</summary>
public sealed record DetailsNode(SourceLocation Source, IReadOnlyList<InlineNode> Summary, bool IsOpen, IReadOnlyList<BlockNode> Blocks) : BlockNode(Source);

/// <summary>A key/value pair from front matter.</summary>
public sealed record FrontMatterProperty(string Key, string Value);

/// <summary>YAML front matter summarised as top-level properties, for display as a collapsible properties block.</summary>
public sealed record FrontMatterNode(SourceLocation Source, IReadOnlyList<FrontMatterProperty> Properties, string Raw) : BlockNode(Source);

/// <summary>A term with its definitions.</summary>
public sealed record DefinitionItemNode(SourceLocation Source, IReadOnlyList<IReadOnlyList<InlineNode>> Terms, IReadOnlyList<BlockNode> Definitions) : PreviewNode(Source);

/// <summary>A definition list.</summary>
public sealed record DefinitionListNode(SourceLocation Source, IReadOnlyList<DefinitionItemNode> Items) : BlockNode(Source);

/// <summary>Literal text.</summary>
public sealed record TextNode(SourceLocation Source, string Text) : InlineNode(Source);

/// <summary>Emphasis (italic).</summary>
public sealed record EmphasisNode(SourceLocation Source, IReadOnlyList<InlineNode> Children) : InlineNode(Source);

/// <summary>Strong emphasis (bold).</summary>
public sealed record StrongNode(SourceLocation Source, IReadOnlyList<InlineNode> Children) : InlineNode(Source);

/// <summary>Strikethrough.</summary>
public sealed record StrikethroughNode(SourceLocation Source, IReadOnlyList<InlineNode> Children) : InlineNode(Source);

/// <summary>Inline code.</summary>
public sealed record CodeSpanNode(SourceLocation Source, string Code) : InlineNode(Source);

/// <summary>A link with the resolver's decision about its destination.</summary>
public sealed record LinkNode(SourceLocation Source, string Destination, string? Title, ResourceDecision Resolution, IReadOnlyList<InlineNode> Children) : InlineNode(Source);

/// <summary>An image with the resolver's decision about where it may be loaded from.</summary>
public sealed record ImageNode(SourceLocation Source, string Destination, string? Title, string Alt, ResourceDecision Resolution) : InlineNode(Source);

/// <summary>A soft (newline in source) or hard (explicit) line break.</summary>
public sealed record LineBreakNode(SourceLocation Source, bool IsHard) : InlineNode(Source);

/// <summary>A reference to a footnote.</summary>
public sealed record FootnoteRefNode(SourceLocation Source, string Label, int Number) : InlineNode(Source);

/// <summary>Inline math.</summary>
public sealed record MathSpanNode(SourceLocation Source, string Latex) : InlineNode(Source);

/// <summary>Formatting from allow-listed inline HTML such as <c>&lt;kbd&gt;</c>.</summary>
public sealed record StyledNode(SourceLocation Source, InlineStyle Style, IReadOnlyList<InlineNode> Children) : InlineNode(Source);
