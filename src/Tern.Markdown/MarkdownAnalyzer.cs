using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Tables;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Helpers;
using MdBlock = Markdig.Syntax.Block;

namespace Tern.Markdown;

public static partial class MarkdownAnalyzer
{
    [GeneratedRegex(@"\[\[(?<target>[^\[\]\r\n|]+?)(?:\|(?<alias>[^\[\]\r\n]+?))?\]\]")]
    private static partial Regex WikiLinkPattern();

    [GeneratedRegex(@"^</?(?<name>[A-Za-z][A-Za-z0-9-]*)", RegexOptions.CultureInvariant, 200)]
    private static partial Regex HtmlTagName();

    [GeneratedRegex("(?<name>[A-Za-z_:][-A-Za-z0-9_:.]*)(?:\\s*=\\s*(?:\"(?<double>[^\"]*)\"|'(?<single>[^']*)'|(?<bare>[^\\s\"'=<>`]+)))?", RegexOptions.CultureInvariant, 200)]
    private static partial Regex HtmlAttribute();

    public static MarkdownAnalysis Analyze(string text, MarkdownProfile profile)
    {
        var pipeline = MarkdownPipelines.For(profile);
        var document = Markdig.Markdown.Parse(text, pipeline);
        return Analyze(text, document, profile);
    }

    public static MarkdownAnalysis Analyze(string text, MarkdownDocument document, MarkdownProfile profile)
    {
        var lines = new LineIndex(text);
        var headings = new List<HeadingInfo>();
        var links = new List<LinkInfo>();
        var tasks = new List<TaskItemInfo>();
        var codeBlocks = new List<CodeBlockInfo>();
        var tables = new List<TableInfo>();
        var footnotes = new List<FootnoteInfo>();
        var anchors = new HeadingAnchors();
        var htmlMetadata = new HtmlMetadata(text, lines, headings, anchors, links);
        FrontMatterInfo? frontMatter = null;
        var excluded = new List<SourceRange>();

        foreach (var node in document.Descendants())
        {
            switch (node)
            {
                case YamlFrontMatterBlock fm when frontMatter is null && fm.Span.Start == 0:
                    frontMatter = BuildFrontMatter(text, fm.Span);
                    excluded.Add(Range(fm.Span));
                    break;
                case HeadingBlock heading:
                    var headingText = PlainText(heading.Inline);
                    headings.Add(new HeadingInfo(heading.Level, headingText, anchors.Next(headingText), lines.LineOf(heading.Span.Start), Range(heading.Span)));
                    break;
                case HtmlBlock html:
                    htmlMetadata.Read(Range(html.Span));
                    break;
                case HtmlInline html:
                    htmlMetadata.Read(Range(html.Span));
                    break;
                case FencedCodeBlock fenced:
                    codeBlocks.Add(new CodeBlockInfo(string.IsNullOrWhiteSpace(fenced.Info) ? null : fenced.Info, Range(fenced.Span), lines.LineOf(fenced.Span.Start), true));
                    excluded.Add(Range(fenced.Span));
                    break;
                case CodeBlock code when code is not FencedCodeBlock:
                    codeBlocks.Add(new CodeBlockInfo(null, Range(code.Span), lines.LineOf(code.Span.Start), false));
                    excluded.Add(Range(code.Span));
                    break;
                case Table table:
                    tables.Add(new TableInfo(Range(table.Span), lines.LineOf(table.Span.Start), table.ColumnDefinitions.Count, table.Count));
                    break;
                case Footnote footnote:
                    footnotes.Add(new FootnoteInfo((footnote.Label ?? string.Empty).TrimStart('^'), Range(footnote.Span), lines.LineOf(footnote.Span.Start)));
                    break;
                case LinkReferenceDefinition definition when !definition.UrlSpan.IsEmpty:
                    links.Add(new LinkInfo(LinkKind.ReferenceDefinition, definition.Url ?? string.Empty, Range(definition.UrlSpan), lines.LineOf(definition.UrlSpan.Start), definition.Label ?? string.Empty));
                    break;
                case CodeInline codeInline:
                    excluded.Add(Range(codeInline.Span));
                    break;
                case AutolinkInline auto:
                    links.Add(new LinkInfo(LinkKind.Autolink, auto.Url ?? string.Empty, Range(auto.Span), lines.LineOf(auto.Span.Start), auto.Url ?? string.Empty));
                    break;
                case LinkInline link:
                    CollectLink(text, link, lines, links);
                    break;
                case TaskList task:
                    tasks.Add(BuildTask(text, task, lines));
                    break;
            }
        }

        CollectWikiLinks(text, lines, excluded, links);
        links.Sort((a, b) => a.Range.Start.CompareTo(b.Range.Start));

        var body = frontMatter is null ? text : text[frontMatter.Range.End..];
        return new MarkdownAnalysis
        {
            Profile = profile,
            Lines = lines,
            FrontMatter = frontMatter,
            Headings = headings,
            ExplicitAnchors = htmlMetadata.ExplicitAnchors,
            Links = links,
            Tasks = tasks,
            CodeBlocks = codeBlocks,
            Tables = tables,
            Footnotes = footnotes,
            WordCount = CountWords(body),
            CharacterCount = text.Length,
        };
    }

    /// <summary>Reads inert metadata from Markdig's already-classified HTML spans. Markdig's
    /// HTML scanner supplies tag boundaries; this does not parse or execute document HTML.</summary>
    private sealed class HtmlMetadata(string text, LineIndex lines, List<HeadingInfo> headings, HeadingAnchors anchors, List<LinkInfo> links)
    {
        private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
        {
            "b", "i", "em", "strong", "u", "s", "del", "ins", "mark", "sub", "sup", "kbd", "code", "pre", "br", "hr", "p", "div", "span",
            "details", "summary", "blockquote", "ul", "ol", "li", "table", "thead", "tbody", "tfoot", "tr", "th", "td", "caption",
            "colgroup", "col", "h1", "h2", "h3", "h4", "h5", "h6", "a", "img", "figure", "figcaption", "abbr", "small", "cite", "q", "samp", "var", "dl", "dt", "dd",
        };
        private static readonly HashSet<string> DropContent = new(StringComparer.Ordinal)
        {
            "script", "style", "iframe", "object", "embed", "applet", "svg", "math", "template", "noembed", "noframes", "textarea", "title",
            "plaintext", "xmp", "frameset", "frame", "head", "select", "option", "datalist",
        };
        private string? _droppedTag;
        private int _dropDepth;
        private string? _headingTag;
        private string? _headingId;
        private int _headingStart;
        private readonly StringBuilder _headingText = new();

        public Dictionary<string, SourceRange> ExplicitAnchors { get; } = new(StringComparer.Ordinal);

        public void Read(SourceRange range)
        {
            if (range.Start < 0 || range.End < range.Start) return;
            var end = Math.Min(text.Length, range.End);
            var offset = Math.Clamp(range.Start, 0, end);
            while (offset < end)
            {
                var next = text.IndexOf('<', offset, end - offset);
                if (next < 0) { AddText(text[offset..end]); break; }
                AddText(text[offset..next]);
                var slice = new StringSlice(text, next, end - 1);
                if (!HtmlHelper.TryParseHtmlTag(ref slice, out var tag)) { AddText("<"); offset = next + 1; continue; }
                offset = slice.Start;
                if (tag.Length > 4096) continue;
                var tagName = HtmlTagName().Match(tag);
                if (!tagName.Success) continue; // comments and declarations are inert metadata-free spans.
                var name = tagName.Groups["name"].Value.ToLowerInvariant();
                var closing = tag.StartsWith("</", StringComparison.Ordinal);
                if (_droppedTag is not null)
                {
                    if (name == _droppedTag) _dropDepth += closing ? -1 : 1;
                    if (_dropDepth <= 0) _droppedTag = null;
                    continue;
                }
                if (!closing && DropContent.Contains(name)) { _droppedTag = name; _dropDepth = 1; continue; }
                if (!Allowed.Contains(name)) continue;
                if (closing)
                {
                    if (name == _headingTag)
                    {
                        var plain = _headingText.ToString().Trim();
                        var generated = anchors.Next(plain);
                        headings.Add(new HeadingInfo(name[1] - '0', plain, _headingId ?? generated,
                            lines.LineOf(_headingStart), new SourceRange(_headingStart, offset)));
                        _headingTag = null;
                        _headingText.Clear();
                    }
                    continue;
                }
                string? id = null;
                var seenAttributes = new HashSet<string>(StringComparer.Ordinal);
                foreach (Match attribute in HtmlAttribute().Matches(tag[tagName.Length..]))
                {
                    var attributeName = attribute.Groups["name"].Value.ToLowerInvariant();
                    if ((name == "img" && attributeName == "src") || (name == "a" && attributeName == "href"))
                    {
                        var valueGroup = attribute.Groups["double"].Success ? attribute.Groups["double"]
                            : attribute.Groups["single"].Success ? attribute.Groups["single"] : attribute.Groups["bare"];
                        if (valueGroup.Success && seenAttributes.Add(attributeName))
                        {
                            var start = next + tagName.Length + valueGroup.Index;
                            links.Add(new LinkInfo(name == "img" ? LinkKind.Image : LinkKind.Inline,
                                System.Net.WebUtility.HtmlDecode(valueGroup.Value), new SourceRange(start, start + valueGroup.Length), lines.LineOf(start), valueGroup.Value));
                        }
                    }
                    if (attributeName != "id" && !(name == "a" && attributeName == "name")) continue;
                    var value = System.Net.WebUtility.HtmlDecode(attribute.Groups["double"].Success ? attribute.Groups["double"].Value
                        : attribute.Groups["single"].Success ? attribute.Groups["single"].Value : attribute.Groups["bare"].Value);
                    if (!ValidName(value)) continue;
                    if (!seenAttributes.Add(attributeName)) continue;
                    if (attributeName == "id") id ??= value;
                    if (ExplicitAnchors.Count < 10_000) ExplicitAnchors.TryAdd(value, new SourceRange(next, offset));
                }
                if (name.Length == 2 && name[0] == 'h' && name[1] is >= '1' and <= '6')
                {
                    _headingTag = name;
                    _headingId = id;
                    _headingStart = next;
                    _headingText.Clear();
                }
                else if (name == "br") AddText("\n");
            }
        }

        private void AddText(string value)
        {
            if (_headingTag is not null && _droppedTag is null) _headingText.Append(System.Net.WebUtility.HtmlDecode(value));
        }

        private static bool ValidName(string value) => value.Length is > 0 and <= 256
            && !value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '<' or '>' or '"' or '\'');
    }

    private static SourceRange Range(Markdig.Syntax.SourceSpan span) => new(span.Start, span.End + 1);

    private static FrontMatterInfo BuildFrontMatter(string text, Markdig.Syntax.SourceSpan span)
    {
        var range = Range(span);
        var end = Math.Min(range.End, text.Length);
        var body = text[range.Start..end];
        var firstBreak = body.IndexOfAny(['\r', '\n']);
        var yaml = firstBreak < 0 ? string.Empty : body[firstBreak..].Trim();
        if (yaml.EndsWith("---", StringComparison.Ordinal) || yaml.EndsWith("...", StringComparison.Ordinal))
        {
            yaml = yaml[..^3].TrimEnd();
        }

        return new FrontMatterInfo(new SourceRange(range.Start, end), yaml);
    }

    private static void CollectLink(string text, LinkInline link, LineIndex lines, List<LinkInfo> links)
    {
        var label = PlainText(link);
        if (link.IsAutoLink)
        {
            links.Add(new LinkInfo(LinkKind.Autolink, link.Url ?? string.Empty, Range(link.Span), lines.LineOf(link.Span.Start), label));
            return;
        }

        // Reference-style links carry no destination of their own; the definition is collected separately.
        if (link.Reference is not null || link.UrlSpan.IsEmpty || link.UrlSpan.End >= text.Length + 1 || link.UrlSpan.Start < 0)
        {
            return;
        }

        var range = Range(link.UrlSpan);
        var raw = text[range.Start..Math.Min(range.End, text.Length)];
        if (raw.Length >= 2 && raw[0] == '<' && raw[^1] == '>')
        {
            range = new SourceRange(range.Start + 1, range.End - 1);
            raw = raw[1..^1];
        }

        links.Add(new LinkInfo(link.IsImage ? LinkKind.Image : LinkKind.Inline, raw, range, lines.LineOf(range.Start), label));
    }

    private static void CollectWikiLinks(string text, LineIndex lines, List<SourceRange> excluded, List<LinkInfo> links)
    {
        if (!text.Contains("[[", StringComparison.Ordinal))
        {
            return;
        }

        foreach (Match match in WikiLinkPattern().Matches(text))
        {
            var whole = new SourceRange(match.Index, match.Index + match.Length);
            if (excluded.Any(r => r.Intersects(whole)))
            {
                continue;
            }

            var target = match.Groups["target"];
            var alias = match.Groups["alias"];
            links.Add(new LinkInfo(LinkKind.WikiLink, target.Value, new SourceRange(target.Index, target.Index + target.Length), lines.LineOf(match.Index), alias.Success ? alias.Value : target.Value));
        }
    }

    private static TaskItemInfo BuildTask(string text, TaskList task, LineIndex lines)
    {
        var marker = Range(task.Span);
        var line = lines.LineOf(marker.Start);
        var end = lines.LineEnd(text, line);
        var itemText = marker.End <= end ? text[marker.End..end].Trim() : string.Empty;
        return new TaskItemInfo(line, task.Checked, marker, itemText);
    }

    public static string PlainText(ContainerInline? container)
    {
        if (container is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        Append(container, builder);
        return builder.ToString().Trim();

        static void Append(ContainerInline parent, StringBuilder sb)
        {
            for (var child = parent.FirstChild; child is not null; child = child.NextSibling)
            {
                switch (child)
                {
                    case LiteralInline literal:
                        sb.Append(literal.Content.ToString());
                        break;
                    case CodeInline code:
                        sb.Append(code.Content);
                        break;
                    case HtmlEntityInline entity:
                        sb.Append(entity.Transcoded.ToString());
                        break;
                    case LineBreakInline:
                        sb.Append(' ');
                        break;
                    case ContainerInline nested:
                        Append(nested, sb);
                        break;
                }
            }
        }
    }

    private static int CountWords(string text)
    {
        var count = 0;
        var inWord = false;
        foreach (var rune in text.EnumerateRunes())
        {
            var isWordChar = Rune.IsLetterOrDigit(rune) || rune.Value == '\'';
            if (isWordChar && !inWord)
            {
                count++;
            }

            inWord = isWordChar;
        }

        return count;
    }
}
