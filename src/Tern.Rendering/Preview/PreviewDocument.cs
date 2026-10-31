namespace Tern.Rendering.Preview;

/// <summary>Why a <see cref="PreviewDocument"/> is incomplete.</summary>
public enum TruncationReason
{
    /// <summary>The document is complete.</summary>
    None,

    /// <summary>The source exceeded <see cref="PreviewOptions.MaxSourceLength"/>.</summary>
    SourceTooLarge,

    /// <summary>The node budget was exhausted.</summary>
    NodeBudget,

    /// <summary>The build time budget was exhausted.</summary>
    TimeBudget,

    /// <summary>Content nested deeper than <see cref="PreviewOptions.MaxDepth"/> was flattened.</summary>
    DepthLimit,
}

/// <summary>An outline entry.</summary>
public sealed record OutlineEntry(int Level, string Text, string Anchor, int Line);

/// <summary>A UI-framework-free model of a rendered Markdown document, with source positions on every node.</summary>
public sealed class PreviewDocument
{
    // Footnote definitions are gathered at the end of the model, so block start lines are not necessarily ascending.
    private readonly int[] _sortedStartLines;
    private readonly int[] _sortedIndexes;
    private readonly BlockNode[] _navigationBlocks;
    private readonly int[] _navigationStarts;

    internal PreviewDocument(IReadOnlyList<BlockNode> blocks, IReadOnlyList<OutlineEntry> outline, TruncationReason truncation, int nodeCount, int sourceLength, int lineCount,
        IReadOnlyDictionary<string, SourceLocation>? anchors = null, IReadOnlyDictionary<string, SourceLocation>? detachedAnchors = null)
    {
        Blocks = blocks;
        Outline = outline;
        Truncation = truncation;
        NodeCount = nodeCount;
        SourceLength = sourceLength;
        LineCount = lineCount;
        Anchors = anchors ?? new Dictionary<string, SourceLocation>(StringComparer.Ordinal);
        DetachedAnchors = detachedAnchors ?? new Dictionary<string, SourceLocation>(StringComparer.Ordinal);
        _sortedIndexes = [.. Enumerable.Range(0, blocks.Count).OrderBy(i => blocks[i].Source.StartLine).ThenBy(i => i)];
        _sortedStartLines = [.. _sortedIndexes.Select(i => blocks[i].Source.StartLine)];
        _navigationBlocks = [.. Descendants(blocks).OrderBy(b => b.Source.StartOffset).ThenByDescending(b => b.Source.EndOffset)];
        _navigationStarts = [.. _navigationBlocks.Select(b => b.Source.StartOffset)];
    }

    /// <summary>Top-level blocks in source order.</summary>
    public IReadOnlyList<BlockNode> Blocks { get; }

    /// <summary>All headings in document order, including those nested in quotes and lists.</summary>
    public IReadOnlyList<OutlineEntry> Outline { get; }

    /// <summary>First declared safe fragment targets, including HTML aliases and generated headings.</summary>
    public IReadOnlyDictionary<string, SourceLocation> Anchors { get; }

    /// <summary>Inert HTML targets without a visible preview node; exporters re-emit them at their target blocks.</summary>
    public IReadOnlyDictionary<string, SourceLocation> DetachedAnchors { get; }

    /// <summary>Finds a visible navigation target, avoiding empty rows for standalone HTML anchors.</summary>
    public int? FindAnchorSourceLine(string id)
    {
        if (!Anchors.TryGetValue(id, out var source)) return null;
        if (!DetachedAnchors.ContainsKey(id)) return source.StartLine;
        var index = Array.BinarySearch(_navigationStarts, source.StartOffset);
        if (index < 0) index = ~index - 1;
        while (index + 1 < _navigationStarts.Length && _navigationStarts[index + 1] == source.StartOffset) index++;
        if (index >= 0 && _navigationBlocks[index] is { } containing
            && containing.Source.ContainsOffset(source.StartOffset)
            && (!ChildBlocks(containing).Any() || containing.Source.StartOffset == source.StartOffset)) return source.StartLine;
        index = Array.BinarySearch(_navigationStarts, source.EndOffset);
        if (index < 0) index = ~index;
        return index < _navigationBlocks.Length ? _navigationBlocks[index].Source.StartLine : source.StartLine;
    }

    private static IEnumerable<BlockNode> Descendants(IEnumerable<BlockNode> blocks)
    {
        foreach (var block in blocks)
        {
            yield return block;
            foreach (var child in Descendants(ChildBlocks(block))) yield return child;
        }
    }

    /// <summary>Why the document is incomplete, or <see cref="TruncationReason.None"/>.</summary>
    public TruncationReason Truncation { get; }

    /// <summary>True when part of the source was not rendered.</summary>
    public bool Truncated => Truncation != TruncationReason.None;

    /// <summary>Number of nodes built.</summary>
    public int NodeCount { get; }

    /// <summary>Number of source characters that were processed.</summary>
    public int SourceLength { get; }

    /// <summary>Number of lines in the processed source.</summary>
    public int LineCount { get; }

    /// <summary>
    /// Index of the top-level block that covers <paramref name="line"/>. A line between blocks (a blank line) maps to the
    /// block before it. Returns -1 when the line precedes every block or there are no blocks.
    /// </summary>
    public int FindBlockIndexAtLine(int line)
    {
        var position = Array.BinarySearch(_sortedStartLines, line);
        if (position < 0)
        {
            position = ~position - 1;
        }
        else
        {
            // Several blocks can start on one line (an HTML block and what follows); the first is the outermost.
            while (position > 0 && _sortedStartLines[position - 1] == line)
            {
                position--;
            }
        }

        return position < 0 ? -1 : _sortedIndexes[position];
    }

    /// <summary>The top-level block that covers <paramref name="line"/>, or null.</summary>
    public BlockNode? FindBlockAtLine(int line)
    {
        var index = FindBlockIndexAtLine(line);
        return index < 0 ? null : Blocks[index];
    }

    /// <summary>First source line of the top-level block at <paramref name="index"/>, for scroll synchronisation.</summary>
    public int LineForBlockIndex(int index) => Blocks[index].Source.StartLine;

    /// <summary>The innermost block (descending into quotes, lists, alerts, footnotes, details) that contains <paramref name="line"/>.</summary>
    public BlockNode? FindDeepestBlockAtLine(int line)
    {
        var current = FindBlockAtLine(line);
        if (current is null || !current.Source.ContainsLine(line))
        {
            return current;
        }

        while (true)
        {
            var child = ChildBlocks(current).FirstOrDefault(b => b.Source.ContainsLine(line));
            if (child is null)
            {
                return current;
            }

            current = child;
        }
    }

    private static IEnumerable<BlockNode> ChildBlocks(BlockNode block) => block switch
    {
        BlockQuoteNode quote => quote.Blocks,
        AlertNode alert => alert.Blocks,
        DetailsNode details => details.Blocks,
        ListNode list => list.Items.SelectMany(i => i.Blocks),
        FootnoteSectionNode section => section.Items.SelectMany(i => i.Blocks),
        _ => [],
    };
}
