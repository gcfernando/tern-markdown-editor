using System.Diagnostics;
using Markdig.Extensions.Alerts;
using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Tables;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Tern.Markdown;
using Tern.Rendering.Html;
using Tern.Rendering.Resources;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdSpan = Markdig.Syntax.SourceSpan;

namespace Tern.Rendering.Preview;

/// <summary>
/// Turns Markdown into a <see cref="PreviewDocument"/>: a tree of blocks and inlines with source positions,
/// resolved links and images, sanitised HTML, and budgets that make huge or hostile input degrade instead of hang.
/// </summary>
public static class PreviewBuilder
{
    /// <summary>Builds a preview model. Never throws for any input text.</summary>
    public static PreviewDocument Build(string markdown, PreviewOptions options) => Build(markdown, options, null);

    /// <summary>Builds a preview model with a caller-supplied resolver, for callers that render many documents with the same roots.</summary>
    public static PreviewDocument Build(string markdown, PreviewOptions options, SafeResourceResolver? resolver)
        => Build(markdown, options, resolver, CancellationToken.None);

    /// <summary>Builds a model, observing cancellation before and after parsing and throughout model construction.
    /// The underlying Markdig parse itself cannot be interrupted.</summary>
    public static PreviewDocument Build(string markdown, PreviewOptions options, SafeResourceResolver? resolver, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (markdown.Length == 0) return new PreviewDocument([], [], TruncationReason.None, 0, 0, 1);
        return BuildNonEmpty(markdown, options, resolver, cancellationToken);
    }

    private static PreviewDocument BuildNonEmpty(string markdown, PreviewOptions options, SafeResourceResolver? resolver, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        var text = markdown;
        var truncation = TruncationReason.None;
        if (text.Length > options.MaxSourceLength)
        {
            text = text[..CutPoint(text, options.MaxSourceLength)];
            truncation = TruncationReason.SourceTooLarge;
        }

        MarkdownDocument document;
        try
        {
            document = Markdig.Markdown.Parse(text, MarkdownPipelines.For(options.Profile));
        }
        catch (ArgumentException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Markdig refuses pathologically nested input; show the source verbatim rather than nothing.
            return PlainFallback(text);
        }

        resolver ??= CreateResolver(options);
        cancellationToken.ThrowIfCancellationRequested();
        var worker = new Worker(text, options, resolver, clock, cancellationToken);
        var blocks = worker.BuildDocument(document);
        return new PreviewDocument(
            blocks,
            worker.Outline,
            worker.Truncation != TruncationReason.None && (worker.Truncation != TruncationReason.DepthLimit || truncation == TruncationReason.None) ? worker.Truncation : truncation,
            worker.NodeCount,
            text.Length,
            worker.LineCount,
            worker.Anchors,
            worker.DetachedAnchors);
    }

    /// <summary>Creates the resolver a build would use for <paramref name="options"/>.</summary>
    public static SafeResourceResolver CreateResolver(PreviewOptions options)
    {
        var directory = string.IsNullOrEmpty(options.DocumentPath) ? null : Path.GetDirectoryName(Path.GetFullPath(options.DocumentPath));
        return new SafeResourceResolver(options.Resolver with
        {
            DocumentDirectory = directory ?? options.Resolver.DocumentDirectory,
            WorkspaceRoot = options.WorkspaceRoot ?? options.Resolver.WorkspaceRoot,
            AdditionalRoots = [.. options.Resolver.AdditionalRoots, .. options.AllowedRoots],
        });
    }

    private static PreviewDocument PlainFallback(string text)
    {
        var lines = new LineIndex(text);
        var location = new SourceLocation(1, Math.Max(1, lines.LineCount), 0, text.Length);
        return new PreviewDocument([new CodeBlockNode(location, null, text, false)], [], TruncationReason.DepthLimit, 1, text.Length, lines.LineCount);
    }

    private static int CutPoint(string text, int limit)
    {
        var window = text.AsSpan(0, limit);
        var blank = window.LastIndexOf("\n\n", StringComparison.Ordinal);
        if (blank > limit / 2)
        {
            return blank + 2;
        }

        var newline = window.LastIndexOf('\n');
        if (newline > limit / 2)
        {
            return newline + 1;
        }

        return limit > 0 && char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit;
    }

    private sealed class DetailsFrame(SourceLocation start, bool isOpen, List<InlineNode> summary)
    {
        public BlockAlignment Alignment { get; init; }
        public SourceLocation Start { get; } = start;

        public bool IsOpen { get; } = isOpen;

        public List<InlineNode> Summary { get; } = summary;

        public List<BlockNode> Blocks { get; } = [];

        public SourceLocation End { get; set; } = start;
    }

    private sealed class InlineFrame(string tag, SourceLocation start, HtmlTag open)
    {
        public string Tag { get; } = tag;

        public SourceLocation Start { get; } = start;

        public HtmlTag Open { get; } = open;

        public List<InlineNode> Children { get; } = [];
    }

    private sealed class Worker
    {
        private readonly string _text;
        private readonly LineIndex _lines;
        private readonly PreviewOptions _options;
        private readonly SafeResourceResolver _resolver;
        private readonly HtmlSanitizer _sanitizer;
        private readonly HeadingAnchors _anchors = new();
        private readonly Stopwatch _clock;
        private readonly CancellationToken _cancellationToken;
        private readonly Dictionary<(bool, string), ResourceDecision> _decisions = [];
        private BlockAlignment _alignment;

        public Worker(string text, PreviewOptions options, SafeResourceResolver resolver, Stopwatch clock, CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
            _clock = clock;
            _text = text;
            _options = options;
            _resolver = resolver;
            _lines = new LineIndex(text);
            var userPolicy = options.Sanitizer.UrlPolicy;
            _sanitizer = new HtmlSanitizer(options.Sanitizer with
            {
                UrlPolicy = (kind, url) =>
                {
                    var decision = kind == HtmlUrlKind.Image ? Decide(true, url) : Decide(false, url);
                    return decision is Blocked ? null : userPolicy is null ? url : userPolicy(kind, url);
                },
            });
        }

        public List<OutlineEntry> Outline { get; } = [];

        public Dictionary<string, SourceLocation> Anchors { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, SourceLocation> DetachedAnchors { get; } = new(StringComparer.Ordinal);

        private void RegisterAnchor(string? id, SourceLocation source, bool detached)
        {
            if (Anchors.Count >= 10_000 || !HtmlSanitizer.IsValidAnchorName(id) || !Anchors.TryAdd(id!, source)) return;
            if (detached) DetachedAnchors.Add(id!, source);
        }

        private void RegisterHtmlAnchors(string tag, IReadOnlyList<HtmlAttr> attributes, SourceLocation source, bool detached)
        {
            RegisterAnchor(attributes.FirstOrDefault(a => a.Name == "id").Value, source, detached);
            if (tag == "a") RegisterAnchor(attributes.FirstOrDefault(a => a.Name == "name").Value, source, detached);
        }

        public TruncationReason Truncation { get; private set; }

        public int NodeCount { get; private set; }

        public int LineCount => _lines.LineCount;

        public IReadOnlyList<BlockNode> BuildDocument(MarkdownDocument document) => BuildBlockList(document, 0);

        private void SetTruncation(TruncationReason reason)
        {
            if (Truncation == TruncationReason.None || (Truncation == TruncationReason.DepthLimit && reason != TruncationReason.DepthLimit))
            {
                Truncation = reason;
            }
        }

        private bool Spend()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (Truncation is TruncationReason.NodeBudget or TruncationReason.TimeBudget)
            {
                return false;
            }

            if (++NodeCount > _options.MaxNodes)
            {
                SetTruncation(TruncationReason.NodeBudget);
                return false;
            }

            if ((NodeCount == 1 || (NodeCount & 127) == 0) && _clock.Elapsed > _options.MaxBuildTime)
            {
                SetTruncation(TruncationReason.TimeBudget);
                return false;
            }

            return true;
        }

        private ResourceDecision Decide(bool image, string destination)
        {
            if (!_decisions.TryGetValue((image, destination), out var decision))
            {
                decision = image ? _resolver.ResolveImage(destination) : _resolver.ResolveLink(destination);
                if (_decisions.Count < 10_000)
                {
                    _decisions[(image, destination)] = decision;
                }
            }

            return decision;
        }

        private SourceLocation Locate(int start, int endExclusive)
        {
            start = Math.Clamp(start, 0, _text.Length);
            endExclusive = Math.Clamp(endExclusive, start, _text.Length);
            while (endExclusive > start && _text[endExclusive - 1] is '\n' or '\r')
            {
                endExclusive--;
            }

            var endLine = _lines.LineOf(endExclusive > start ? endExclusive - 1 : start);
            return new SourceLocation(_lines.LineOf(start), endLine, start, endExclusive);
        }

        private SourceLocation Locate(MdSpan span) => span.IsEmpty || span.End < span.Start ? Locate(0, 0) : Locate(span.Start, span.End + 1);

        private SourceLocation Locate(MdSpan span, SourceLocation fallback) => span.IsEmpty || span.End < span.Start || span.Start < 0 ? fallback : Locate(span.Start, span.End + 1);

        private static SourceLocation Cover(SourceLocation own, IEnumerable<PreviewNode> children)
        {
            var start = own.StartOffset;
            var end = own.EndOffset;
            var startLine = own.StartLine;
            var endLine = own.EndLine;
                foreach (var child in children)
                {
                    if (child.Source.EndOffset > end)
                    {
                        end = child.Source.EndOffset;
                        endLine = child.Source.EndLine;
                    }

                    if (child.Source.StartOffset < start)
                    {
                        start = child.Source.StartOffset;
                        startLine = child.Source.StartLine;
                    }
                }

                return new SourceLocation(startLine, endLine, start, end);
            }

            private List<BlockNode> BuildBlockList(IEnumerable<MdBlock> children, int depth)
            {
                var result = new List<BlockNode>();
                var open = new Stack<DetailsFrame>();
                var suppressedDetails = 0;
                var inheritedAlignment = _alignment;
                var alignmentFrames = new List<(string Tag, BlockAlignment Alignment)>();
                var suppressedAlignmentDepth = 0;
                try
                {
                foreach (var child in children)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    if (Truncation is TruncationReason.NodeBudget or TruncationReason.TimeBudget)
                    {
                        break;
                    }

                    _alignment = alignmentFrames.Count > 0 ? alignmentFrames[^1].Alignment : inheritedAlignment;
                    var target = open.Count > 0 ? open.Peek().Blocks : result;
                    var previousCount = target.Count;
                    if (child is HtmlBlock html)
                    {
                        if (TryHandleAlignmentContainers(html, alignmentFrames, depth + open.Count,
                            inheritedAlignment, ref suppressedAlignmentDepth)) continue;
                        AddHtmlBlock(html, target, open, result, depth + alignmentFrames.Count, ref suppressedDetails);
                        if (open.Count > 0)
                        {
                            var frame = open.Peek();
                            frame.End = Cover(frame.End, [new TextNode(Locate(html.Span), string.Empty)]);
                        }
                    }
                    else
                    {
                        AddBlock(child, target, depth + open.Count + alignmentFrames.Count);
                        if (open.Count > 0 && target.Count > 0)
                        {
                            open.Peek().End = Cover(open.Peek().End, [target[^1]]);
                        }
                    }

                    for (var i = previousCount; i < target.Count; i++)
                    {
                        if (target[i].Alignment == BlockAlignment.None)
                            target[i] = target[i] with { Alignment = _alignment };
                    }
                }

                while (open.Count > 0)
                {
                    var frame = open.Pop();
                    (open.Count > 0 ? open.Peek().Blocks : result).Add(CompleteDetails(frame));
                }

                return result;
            }
            finally
            {
                _alignment = inheritedAlignment;
            }
        }

        // CommonMark emits container tags as separate HTML blocks when Markdown is
        // separated by blank lines. Retain only their validated alignment context.
        private bool TryHandleAlignmentContainers(HtmlBlock html, List<(string Tag, BlockAlignment Alignment)> frames,
            int depth, BlockAlignment inherited, ref int suppressedDepth)
        {
            var raw = html.Lines.ToString();
            if (raw.Length > _options.Sanitizer.MaxInputLength) return false;
            var tokens = HtmlTokenizer.Tokenize(raw);
            if (tokens.Count == 0 || tokens.Any(t => t.Kind == HtmlTokenKind.Text
                ? !string.IsNullOrWhiteSpace(t.Text) : t.Name is not ("div" or "p"))) return false;
            foreach (var token in tokens)
            {
                if (!Spend()) break;
                if (token.Kind == HtmlTokenKind.Text) continue;
                if (token.Kind == HtmlTokenKind.StartTag)
                {
                    RegisterHtmlAnchors(token.Name, token.Attributes, Locate(html.Span), detached: true);
                    if (suppressedDepth > 0 || depth + frames.Count >= _options.MaxDepth)
                    {
                        suppressedDepth++;
                        SetTruncation(TruncationReason.DepthLimit);
                        continue;
                    }
                    var alignment = ReadAlignment(token.Attributes);
                    frames.Add((token.Name, alignment == BlockAlignment.None
                        ? frames.Count > 0 ? frames[^1].Alignment : inherited : alignment));
                }
                else if (suppressedDepth > 0) suppressedDepth--;
                else
                {
                    var index = frames.FindLastIndex(f => f.Tag == token.Name);
                    if (index >= 0) frames.RemoveRange(index, frames.Count - index);
                }
            }
            return true;
        }

        private static BlockAlignment ReadAlignment(IReadOnlyList<HtmlAttr> attributes) =>
            attributes.FirstOrDefault(a => a.Name == "align").Value?.Trim().ToLowerInvariant() switch
            {
                "left" => BlockAlignment.Left,
                "center" => BlockAlignment.Center,
                "right" => BlockAlignment.Right,
                _ => BlockAlignment.None,
            };

        private static DetailsNode CompleteDetails(DetailsFrame frame) =>
            new(Cover(frame.Start, frame.Blocks.Cast<PreviewNode>().Append(new TextNode(frame.End, string.Empty))), frame.Summary, frame.IsOpen, frame.Blocks)
            { Alignment = frame.Alignment };

        private void AddHtmlBlock(HtmlBlock html, List<BlockNode> target, Stack<DetailsFrame> open, List<BlockNode> root, int depth, ref int suppressedDetails)
        {
            if (html.Type == HtmlBlockType.Comment || !Spend())
            {
                return;
            }

            var location = Locate(html.Span);
            var raw = html.Lines.ToString();
            var svg = raw.Trim();
            // General HTML still drops foreign content. Only an entire, independently
            // validated SVG image enters the image path; no document DOM is executed.
            if (svg.StartsWith("<svg", StringComparison.Ordinal)
                && System.Text.Encoding.UTF8.GetByteCount(svg) <= SafeSvgPolicy.MaxBytes)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(svg);
                if (SafeSvgPolicy.TryNormalize(bytes, out var normalized, out var name) && Spend())
                {
                    target.Add(new ParagraphNode(location, [new ImageNode(location, string.Empty, null, name, new InlineImage("image/svg+xml", normalized))]));
                    return;
                }
            }
            var tokens = HtmlTokenizer.Tokenize(raw);
            var structural = tokens.Any(t => t.Name == "details" && t.Kind != HtmlTokenKind.Text);
            if (!structural)
            {
                var pieces = _sanitizer.Parse(raw).ToList();
                var anchorOnly = pieces.All(p => p.Kind == HtmlPieceKind.Text ? string.IsNullOrWhiteSpace(p.Text)
                    : p.Tag == "a" && p.GetAttribute("href") is null);
                for (var i = 0; i < pieces.Count; i++)
                {
                    var piece = pieces[i];
                    if (piece.Kind != HtmlPieceKind.Open && piece.Kind != HtmlPieceKind.Void) continue;
                    RegisterHtmlAnchors(piece.Tag, piece.Attributes, location, anchorOnly);
                    if (piece.Tag.Length == 2 && piece.Tag[0] == 'h' && piece.Tag[1] is >= '1' and <= '6')
                    {
                        var end = pieces.FindIndex(i + 1, p => p.Kind == HtmlPieceKind.Close && p.Tag == piece.Tag);
                        if (end < 0) continue;
                        var plain = HtmlSanitizer.ToPlainText(pieces.Skip(i + 1).Take(end - i - 1).ToList()).Trim();
                        var generated = _anchors.Next(plain);
                        var id = piece.GetAttribute("id") ?? generated;
                        RegisterAnchor(id, location, detached: false);
                        Outline.Add(new OutlineEntry(piece.Tag[1] - '0', plain, id, location.StartLine));
                        if (piece.GetAttribute("id") is null && HtmlSanitizer.IsValidAnchorName(id))
                            pieces[i] = piece with { Attributes = [.. piece.Attributes, new HtmlAttr("id", id)] };
                    }
                }
                if (anchorOnly) return;
                if (pieces.Count > 0)
                {
                    target.Add(new HtmlSafeBlockNode(location, pieces, HtmlSanitizer.Serialize(pieces))
                    {
                        Alignment = pieces.FirstOrDefault(p => p.Kind == HtmlPieceKind.Open) is { } first
                            ? ReadAlignment(first.Attributes) : BlockAlignment.None,
                        Links = ResolveAttributes(pieces, "href", false),
                        Images = ResolveAttributes(pieces, "src", true),
                    });
                }

                return;
            }

            // details/summary arrive as separate HTML blocks with Markdown in between, so they are stitched back together here.
            var text = new System.Text.StringBuilder();
            List<InlineNode>? summary = null;
            var summaryText = new System.Text.StringBuilder();
            var inSummary = false;
            DetailsFrame? pendingFrame = null;

            void FlushText()
            {
                var content = System.Text.RegularExpressions.Regex.Replace(text.ToString(), @"\s+", " ").Trim();
                text.Clear();
                if (content.Length > 0 && open.Count > 0)
                {
                    open.Peek().Blocks.Add(new ParagraphNode(location, [new TextNode(location, content)]));
                }
            }

            foreach (var token in tokens)
            {
                if (!Spend())
                {
                    break;
                }

                switch (token.Kind)
                {
                    case HtmlTokenKind.Text when inSummary:
                        summaryText.Append(token.Text);
                        break;
                    case HtmlTokenKind.Text:
                        text.Append(token.Text);
                        break;
                    case HtmlTokenKind.StartTag when token.Name == "details":
                        FlushText();
                        RegisterHtmlAnchors(token.Name, token.Attributes, location, detached: true);
                        if (depth + open.Count >= _options.MaxDepth || suppressedDetails > 0)
                        {
                            suppressedDetails++;
                            SetTruncation(TruncationReason.DepthLimit);
                            break;
                        }

                        pendingFrame = new DetailsFrame(location, token.Attributes.Any(a => a.Name == "open"), []) { Alignment = _alignment };
                        open.Push(pendingFrame);
                        summary = null;
                        break;
                    case HtmlTokenKind.StartTag when token.Name == "summary" && open.Count > 0:
                        RegisterHtmlAnchors(token.Name, token.Attributes, location, detached: true);
                        inSummary = true;
                        summaryText.Clear();
                        break;
                    case HtmlTokenKind.EndTag when token.Name == "summary" && inSummary:
                        inSummary = false;
                        summary = [new TextNode(location, System.Text.RegularExpressions.Regex.Replace(summaryText.ToString(), @"\s+", " ").Trim())];
                        open.Peek().Summary.Clear();
                        open.Peek().Summary.AddRange(summary);
                        break;
                    case HtmlTokenKind.EndTag when token.Name == "details" && open.Count > 0:
                        if (suppressedDetails > 0)
                        {
                            suppressedDetails--;
                            break;
                        }

                        FlushText();
                        var frame = open.Pop();
                        frame.End = Cover(frame.End, [new TextNode(location, string.Empty)]);
                        (open.Count > 0 ? open.Peek().Blocks : root).Add(CompleteDetails(frame));
                        break;
                }
            }

            FlushText();
        }

        private Dictionary<string, ResourceDecision> ResolveAttributes(IReadOnlyList<HtmlPiece> pieces, string attribute, bool image)
        {
            var result = new Dictionary<string, ResourceDecision>(StringComparer.Ordinal);
            foreach (var piece in pieces)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (piece.GetAttribute(attribute) is { } value && !result.ContainsKey(value))
                {
                    result[value] = Decide(image, value);
                }
            }

            return result;
        }

        private void AddBlock(MdBlock block, List<BlockNode> target, int depth)
        {
            if (!Spend())
            {
                return;
            }

            var location = Locate(block.Span);
            if (depth > _options.MaxDepth)
            {
                SetTruncation(TruncationReason.DepthLimit);
                var slice = _text[location.StartOffset..location.EndOffset];
                target.Add(new ParagraphNode(location, [new TextNode(location, slice)]));
                return;
            }

            switch (block)
            {
                case YamlFrontMatterBlock front:
                    target.Add(BuildFrontMatter(front, location));
                    break;
                case HeadingBlock heading:
                    var plain = MarkdownAnalyzer.PlainText(heading.Inline);
                    var anchor = _anchors.Next(plain);
                    RegisterAnchor(anchor, location, detached: false);
                    Outline.Add(new OutlineEntry(heading.Level, plain, anchor, location.StartLine));
                    target.Add(new HeadingNode(location, heading.Level, anchor, plain, BuildInlines(heading.Inline, location, depth)));
                    break;
                case MathBlock math:
                    target.Add(new MathBlockNode(location, math.Lines.ToString()));
                    break;
                case FencedCodeBlock fenced:
                    target.Add(BuildFence(fenced, location));
                    break;
                case CodeBlock code:
                    target.Add(new CodeBlockNode(location, null, code.Lines.ToString(), false));
                    break;
                case ParagraphBlock paragraph:
                    var paragraphInlines = BuildInlines(paragraph.Inline, location, depth);
                    if (paragraphInlines.Count > 0) target.Add(new ParagraphNode(location, paragraphInlines));
                    break;
                case ThematicBreakBlock:
                    target.Add(new ThematicBreakNode(location));
                    break;
                case AlertBlock alert:
                    var alertBlocks = BuildBlockList(alert, depth + 1);
                    if (Enum.TryParse<AlertKind>(alert.Kind.ToString(), ignoreCase: true, out var parsed))
                    {
                        target.Add(new AlertNode(Cover(location, alertBlocks), parsed, alertBlocks));
                    }
                    else
                    {
                        // Markdig accepts any [!NAME]; only the five GitHub kinds are callouts, the rest stay a visible plain quote.
                        var markerLine = new SourceLocation(location.StartLine, location.StartLine, location.StartOffset, location.StartOffset);
                        var marker = new ParagraphNode(markerLine, [new TextNode(markerLine, $"[!{alert.Kind}]")]);
                        target.Add(new BlockQuoteNode(Cover(location, alertBlocks), [marker, .. alertBlocks]));
                    }

                    break;
                case QuoteBlock quote:
                    var quoteBlocks = BuildBlockList(quote, depth + 1);
                    target.Add(new BlockQuoteNode(Cover(location, quoteBlocks), quoteBlocks));
                    break;
                case ListBlock list:
                    target.Add(BuildList(list, location, depth));
                    break;
                case Table table:
                    target.Add(BuildTable(table, location, depth));
                    break;
                case FootnoteGroup group:
                    target.Add(BuildFootnotes(group, location, depth));
                    break;
                case DefinitionList definitions:
                    target.Add(BuildDefinitionList(definitions, location, depth));
                    break;
                case LinkReferenceDefinitionGroup or LinkReferenceDefinition:
                    break;
                case LeafBlock leaf when leaf.Lines.Count > 0:
                    target.Add(new ParagraphNode(location, [new TextNode(location, leaf.Lines.ToString())]));
                    break;
                case ContainerBlock container:
                    target.AddRange(BuildBlockList(container, depth + 1));
                    break;
            }
        }

        private FrontMatterNode BuildFrontMatter(YamlFrontMatterBlock front, SourceLocation location)
        {
            var body = _text[location.StartOffset..location.EndOffset];
            var lines = body.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (lines.Count > 0)
            {
                lines.RemoveAt(0);
            }

            if (lines.Count > 0 && lines[^1].TrimEnd() is "---" or "...")
            {
                lines.RemoveAt(lines.Count - 1);
            }

            var yaml = string.Join('\n', lines);
            return new FrontMatterNode(location, FrontMatterReader.Read(yaml), yaml);
        }

        private BlockNode BuildFence(FencedCodeBlock fenced, SourceLocation location)
        {
            var info = fenced.Info?.Trim() ?? string.Empty;
            var language = info.Split([' ', '\t', '{'], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            var code = fenced.Lines.ToString();
            if (string.Equals(language, "mermaid", StringComparison.OrdinalIgnoreCase))
            {
                return new DiagramNode(location, "mermaid", code);
            }

            if (string.Equals(language, "math", StringComparison.OrdinalIgnoreCase))
            {
                return new MathBlockNode(location, code);
            }

            return new CodeBlockNode(location, language, code, true);
        }

        private ListNode BuildList(ListBlock list, SourceLocation location, int depth)
        {
            var items = new List<ListItemNode>();
            foreach (var child in list)
            {
                if (child is not ListItemBlock item || !Spend())
                {
                    continue;
                }

                var state = TaskState.None;
                if (item.FirstOrDefault() is ParagraphBlock { Inline.FirstChild: TaskList task })
                {
                    state = task.Checked ? TaskState.Checked : TaskState.Unchecked;
                }

                var blocks = BuildBlockList(item, depth + 1);
                items.Add(new ListItemNode(Cover(Locate(item.Span), blocks), blocks, state));
            }

            var start = int.TryParse(list.OrderedStart, out var number) ? number : 1;
            return new ListNode(Cover(location, items), list.IsOrdered, start, !list.IsLoose, items);
        }

        private TableNode BuildTable(Table table, SourceLocation location, int depth)
        {
            var alignments = table.ColumnDefinitions
                .Select(d => d.Alignment switch
                {
                    TableColumnAlign.Left => TableAlignment.Left,
                    TableColumnAlign.Center => TableAlignment.Center,
                    TableColumnAlign.Right => TableAlignment.Right,
                    _ => TableAlignment.None,
                })
                .ToList();
            TableRowNode? header = null;
            var body = new List<TableRowNode>();
            foreach (var child in table)
            {
                if (child is not TableRow row || !Spend())
                {
                    continue;
                }

                var cells = new List<TableCellNode>();
                var column = 0;
                foreach (var cellBlock in row)
                {
                    if (cellBlock is not TableCell cell || !Spend())
                    {
                        continue;
                    }

                    var cellLocation = Locate(cell.Span, Locate(row.Span));
                    var inlines = new List<InlineNode>();
                    foreach (var content in cell)
                    {
                        if (content is ParagraphBlock paragraph)
                        {
                            inlines.AddRange(BuildInlines(paragraph.Inline, cellLocation, depth + 1));
                        }
                    }

                    var index = cell.ColumnIndex >= 0 ? cell.ColumnIndex : column;
                    cells.Add(new TableCellNode(cellLocation, inlines, index < alignments.Count ? alignments[index] : TableAlignment.None));
                    column++;
                }

                var rowNode = new TableRowNode(Cover(Locate(row.Span), cells), cells, row.IsHeader);
                if (row.IsHeader && header is null)
                {
                    header = rowNode;
                }
                else
                {
                    body.Add(rowNode);
                }
            }

            var rows = new List<PreviewNode>(body);
            if (header is not null)
            {
                rows.Add(header);
            }

            return new TableNode(Cover(location, rows), alignments, header, body);
        }

        private FootnoteSectionNode BuildFootnotes(FootnoteGroup group, SourceLocation location, int depth)
        {
            var items = new List<FootnoteNode>();
            foreach (var child in group)
            {
                if (child is not Footnote footnote || !Spend())
                {
                    continue;
                }

                var blocks = BuildBlockList(footnote, depth + 1);
                items.Add(new FootnoteNode(Cover(Locate(footnote.Span), blocks), (footnote.Label ?? string.Empty).TrimStart('^'), footnote.Order, blocks));
            }

            var covered = items.Count == 0 ? location : Cover(items[0].Source, items.Cast<PreviewNode>());
            return new FootnoteSectionNode(covered, items);
        }

        private DefinitionListNode BuildDefinitionList(DefinitionList list, SourceLocation location, int depth)
        {
            var items = new List<DefinitionItemNode>();
            foreach (var child in list)
            {
                if (child is not DefinitionItem item || !Spend())
                {
                    continue;
                }

                var itemLocation = Locate(item.Span);
                var terms = item.OfType<DefinitionTerm>().Select(t => (IReadOnlyList<InlineNode>)BuildInlines(t.Inline, itemLocation, depth + 1)).ToList();
                var definitions = BuildBlockList(item.Where(b => b is not DefinitionTerm), depth + 1);
                items.Add(new DefinitionItemNode(Cover(itemLocation, definitions), terms, definitions));
            }

            return new DefinitionListNode(Cover(location, items), items);
        }

        private List<InlineNode> BuildInlines(ContainerInline? container, SourceLocation fallback, int depth)
        {
            var result = new List<InlineNode>();
            if (container is null)
            {
                return result;
            }

            var startsWithTask = container.FirstChild is TaskList;
            var frames = new List<InlineFrame>();
            AddChildren(container, result, frames, fallback, depth);
            while (frames.Count > 0)
            {
                // An unclosed tag has no effect other than its text surviving.
                var frame = frames[^1];
                frames.RemoveAt(frames.Count - 1);
                (frames.Count > 0 ? frames[^1].Children : result).AddRange(frame.Children);
            }

            if (startsWithTask && result.Count > 0 && result[0] is TextNode first)
            {
                var trimmed = first.Text.TrimStart();
                var removed = first.Text.Length - trimmed.Length;
                if (trimmed.Length == 0)
                {
                    result.RemoveAt(0);
                }
                else if (removed > 0)
                {
                    result[0] = first with { Text = trimmed, Source = first.Source with { StartOffset = first.Source.StartOffset + removed } };
                }
            }

            return result;
        }

        private void AddChildren(ContainerInline container, List<InlineNode> result, List<InlineFrame> frames, SourceLocation fallback, int depth)
        {
            for (var child = container.FirstChild; child is not null; child = child.NextSibling)
            {
                if (!Spend())
                {
                    return;
                }

                var current = frames.Count > 0 ? frames[^1].Children : result;
                if (child is HtmlInline html)
                {
                    HandleHtmlInline(html, current, result, frames, fallback, depth);
                }
                else
                {
                    AddInline(child, current, frames, fallback, depth);
                }
            }
        }

        private void AddInline(MdInline inline, List<InlineNode> into, List<InlineFrame> frames, SourceLocation fallback, int depth)
        {
            var location = Locate(inline.Span, fallback);
            if (depth > _options.MaxDepth)
            {
                SetTruncation(TruncationReason.DepthLimit);
                return;
            }

            switch (inline)
            {
                case LiteralInline literal:
                    into.Add(new TextNode(location, literal.Content.ToString()));
                    break;
                case HtmlEntityInline entity:
                    into.Add(new TextNode(location, entity.Transcoded.ToString()));
                    break;
                case CodeInline code:
                    into.Add(new CodeSpanNode(location, code.Content));
                    break;
                case LineBreakInline lineBreak:
                    into.Add(new LineBreakNode(location, lineBreak.IsHard));
                    break;
                case TaskList:
                    break;
                case MathInline math:
                    into.Add(new MathSpanNode(location, math.Content.ToString()));
                    break;
                case FootnoteLink link:
                    if (!link.IsBackLink)
                    {
                        into.Add(new FootnoteRefNode(location, (link.Footnote?.Label ?? string.Empty).TrimStart('^'), link.Footnote?.Order ?? link.Index));
                    }

                    break;
                case AutolinkInline auto:
                    var url = auto.IsEmail ? "mailto:" + auto.Url : auto.Url ?? string.Empty;
                    into.Add(new LinkNode(location, url, null, Decide(false, url), [new TextNode(location, auto.Url ?? string.Empty)]));
                    break;
                case LinkInline link:
                    into.Add(BuildLink(link, location, depth));
                    break;
                case EmphasisInline emphasis:
                    AddEmphasis(emphasis, into, location, depth);
                    break;
                case ContainerInline container:
                    AddChildren(container, into, frames, location, depth + 1);
                    break;
            }
        }

        private InlineNode BuildLink(LinkInline link, SourceLocation location, int depth)
        {
            var destination = link.Url ?? string.Empty;
            var title = string.IsNullOrEmpty(link.Title) ? null : link.Title;
            if (link.IsImage)
            {
                return new ImageNode(location, destination, title, MarkdownAnalyzer.PlainText(link), Decide(true, destination));
            }

            var children = BuildInlines(link, location, depth + 1);
            return new LinkNode(location, destination, title, Decide(false, destination), children);
        }

        private void AddEmphasis(EmphasisInline emphasis, List<InlineNode> into, SourceLocation location, int depth)
        {
            var children = BuildInlines(emphasis, location, depth + 1);
            into.Add(emphasis.DelimiterChar switch
            {
                '~' => new StrikethroughNode(location, children),
                '*' or '_' when emphasis.DelimiterCount >= 2 => new StrongNode(location, children),
                _ => new EmphasisNode(location, children),
            });
        }

        private void HandleHtmlInline(HtmlInline html, List<InlineNode> current, List<InlineNode> root, List<InlineFrame> frames, SourceLocation fallback, int depth)
        {
            var location = Locate(html.Span, fallback);
            if (!_sanitizer.TryParseTag(html.Tag, out var tag))
            {
                return;
            }

            if (!tag.IsClosing) RegisterHtmlAnchors(tag.Name, tag.Attributes, location, detached: true);

            if (!tag.IsClosing)
            {
                switch (tag.Name)
                {
                    case "br":
                        current.Add(new LineBreakNode(location, true));
                        break;
                    case "img":
                        var source = tag.GetAttribute("src");
                        if (source is not null)
                        {
                            current.Add(new ImageNode(location, source, tag.GetAttribute("title"), tag.GetAttribute("alt") ?? string.Empty, Decide(true, source)));
                        }

                        break;
                    case "kbd" or "sub" or "sup" or "mark" or "u" or "ins" or "b" or "strong" or "i" or "em" or "s" or "del" or "code":
                    case "a" when tag.GetAttribute("href") is not null:
                        if (depth + frames.Count >= _options.MaxDepth)
                        {
                            SetTruncation(TruncationReason.DepthLimit);
                            break;
                        }

                        frames.Add(new InlineFrame(tag.Name, location, tag));
                        break;
                }

                return;
            }

            var index = frames.FindLastIndex(f => f.Tag == tag.Name);
            if (index < 0)
            {
                return;
            }

            // Frames opened after the matching one never closed; they only contribute their children.
            for (var i = frames.Count - 1; i > index; i--)
            {
                var stray = frames[i];
                frames.RemoveAt(i);
                frames[i - 1].Children.AddRange(stray.Children);
            }

            var frame = frames[index];
            frames.RemoveAt(index);
            var parent = index > 0 ? frames[index - 1].Children : root;
            var span = new SourceLocation(frame.Start.StartLine, location.EndLine, frame.Start.StartOffset, location.EndOffset);
            parent.Add(WrapFrame(frame, span));
        }

        private InlineNode WrapFrame(InlineFrame frame, SourceLocation span) => frame.Tag switch
        {
            "kbd" => new StyledNode(span, InlineStyle.Kbd, frame.Children),
            "sub" => new StyledNode(span, InlineStyle.Subscript, frame.Children),
            "sup" => new StyledNode(span, InlineStyle.Superscript, frame.Children),
            "mark" => new StyledNode(span, InlineStyle.Mark, frame.Children),
            "u" or "ins" => new StyledNode(span, InlineStyle.Underline, frame.Children),
            "b" or "strong" => new StrongNode(span, frame.Children),
            "i" or "em" => new EmphasisNode(span, frame.Children),
            "s" or "del" => new StrikethroughNode(span, frame.Children),
            "code" => new CodeSpanNode(span, string.Concat(frame.Children.Select(PlainOf))),
            _ => BuildHtmlLink(frame, span),
        };

        private LinkNode BuildHtmlLink(InlineFrame frame, SourceLocation span)
        {
            var destination = frame.Open.GetAttribute("href") ?? string.Empty;
            return new LinkNode(span, destination, frame.Open.GetAttribute("title"), Decide(false, destination), frame.Children);
        }

        private static string PlainOf(InlineNode node) => node switch
        {
            TextNode text => text.Text,
            CodeSpanNode code => code.Code,
            EmphasisNode e => string.Concat(e.Children.Select(PlainOf)),
            StrongNode s => string.Concat(s.Children.Select(PlainOf)),
            StrikethroughNode s => string.Concat(s.Children.Select(PlainOf)),
            StyledNode s => string.Concat(s.Children.Select(PlainOf)),
            LinkNode l => string.Concat(l.Children.Select(PlainOf)),
            ImageNode i => i.Alt,
            LineBreakNode => " ",
            _ => string.Empty,
        };
    }
}
