using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Tern.Rendering.Html;
using Tern.Rendering.Mathematics;
using Tern.Rendering.Mermaid;
using Tern.Rendering.Preview;
using Tern.Rendering.Resources;

namespace Tern.Export;

/// <summary>Turns a <see cref="PreviewDocument"/> into the body markup of the exported page.</summary>
internal sealed partial class HtmlRenderer(SafeResourceResolver resolver, ImageEmbedder images, bool includeFrontMatter, List<string> warnings)
{
    private static readonly HashSet<string> LinkSchemes = new(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto" };

    private readonly StringBuilder _html = new();
    private readonly HashSet<int> _referencedFootnotes = [];
    private Dictionary<int, List<string>> _detachedAnchors = [];

    [GeneratedRegex(@"^[A-Za-z0-9_+#.\-]{1,32}$")]
    private static partial Regex LanguageName();

    [GeneratedRegex(@"^\d{1,4}%?$")]
    private static partial Regex Dimension();

    [GeneratedRegex(@"^\d{1,3}$")]
    private static partial Regex SpanCount();

    public string Render(PreviewDocument document)
    {
        _html.Clear();
        _referencedFootnotes.Clear();
        _detachedAnchors = document.DetachedAnchors.Keys.GroupBy(id =>
        {
            var line = document.FindAnchorSourceLine(id) ?? document.DetachedAnchors[id].StartLine;
            return document.FindDeepestBlockAtLine(line)?.Source.StartLine ?? line;
        }).ToDictionary(g => g.Key, g => g.ToList());
        Blocks(document.Blocks, tight: false);
        foreach (var ids in _detachedAnchors.OrderBy(pair => pair.Key).Select(pair => pair.Value)) EmitAnchors(ids);
        _detachedAnchors.Clear();
        if (document.Truncated)
        {
            _html.Append("<p class=\"truncated\">This document is very large. Only the first part is shown (")
                .Append(HtmlText.Escape(document.Truncation.ToString())).Append(").</p>\n");
        }

        return _html.ToString();
    }

    private void Blocks(IEnumerable<BlockNode> blocks, bool tight)
    {
        foreach (var block in blocks)
        {
            Block(block, tight);
        }
    }

    private void Block(BlockNode block, bool tight)
    {
        if (_detachedAnchors.Remove(block.Source.StartLine, out var ids)) EmitAnchors(ids);
        switch (block)
        {
            case HeadingNode heading:
                var level = Math.Clamp(heading.Level, 1, 6);
                _html.Append("<h").Append(level.ToString(CultureInfo.InvariantCulture));
                if (heading.Anchor.Length > 0)
                {
                    _html.Append(" id=\"").Append(HtmlText.Escape(heading.Anchor)).Append('"');
                }

                _html.Append('>');
                Inlines(heading.Inlines);
                _html.Append("</h").Append(level.ToString(CultureInfo.InvariantCulture)).Append(">\n");
                break;
            case ParagraphNode paragraph:
                if (tight)
                {
                    Inlines(paragraph.Inlines);
                    _html.Append('\n');
                }
                else
                {
                    _html.Append("<p>");
                    Inlines(paragraph.Inlines);
                    _html.Append("</p>\n");
                }

                break;
            case ListNode list:
                List(list);
                break;
            case BlockQuoteNode quote:
                _html.Append("<blockquote>\n");
                Blocks(quote.Blocks, tight: false);
                _html.Append("</blockquote>\n");
                break;
            case AlertNode alert:
                var kind = alert.Kind.ToString();
                _html.Append("<div class=\"alert alert-").Append(kind.ToLowerInvariant()).Append("\"><p class=\"alert-title\">").Append(kind).Append("</p>\n");
                Blocks(alert.Blocks, tight: false);
                _html.Append("</div>\n");
                break;
            case CodeBlockNode code:
                _html.Append("<pre><code");
                if (code.Language is { } language && LanguageName().IsMatch(language))
                {
                    _html.Append(" class=\"language-").Append(language).Append('"');
                }

                _html.Append('>');
                HtmlText.Escape(_html, code.Code.TrimEnd('\r', '\n'));
                _html.Append("</code></pre>\n");
                break;
            case ThematicBreakNode:
                _html.Append("<hr>\n");
                break;
            case TableNode table:
                Table(table);
                break;
            case FootnoteSectionNode footnotes:
                Footnotes(footnotes);
                break;
            case MathBlockNode math:
                MathBlock(math);
                break;
            case DiagramNode diagram:
                Diagram(diagram);
                break;
            case HtmlSafeBlockNode safe:
                SafeHtml(safe.Pieces);
                _html.Append('\n');
                break;
            case DetailsNode details:
                _html.Append(details.IsOpen ? "<details open>" : "<details>").Append("<summary>");
                Inlines(details.Summary);
                _html.Append("</summary>\n");
                Blocks(details.Blocks, tight: false);
                _html.Append("</details>\n");
                break;
            case FrontMatterNode front:
                FrontMatter(front);
                break;
            case DefinitionListNode definitions:
                _html.Append("<dl>\n");
                foreach (var item in definitions.Items)
                {
                    foreach (var term in item.Terms)
                    {
                        _html.Append("<dt>");
                        Inlines(term);
                        _html.Append("</dt>\n");
                    }

                    _html.Append("<dd>");
                    Blocks(item.Definitions, tight: true);
                    _html.Append("</dd>\n");
                }

                _html.Append("</dl>\n");
                break;
        }
    }

    private void EmitAnchors(IEnumerable<string> ids)
    {
        foreach (var id in ids)
            _html.Append("<a id=\"").Append(HtmlText.Escape(id)).Append("\"></a>");
    }

    private void List(ListNode list)
    {
        var tag = list.Ordered ? "ol" : "ul";
        _html.Append('<').Append(tag);
        if (list.Ordered && list.StartNumber != 1)
        {
            _html.Append(" start=\"").Append(list.StartNumber.ToString(CultureInfo.InvariantCulture)).Append('"');
        }

        _html.Append(">\n");
        foreach (var item in list.Items)
        {
            _html.Append("<li");
            if (item.Task != TaskState.None)
            {
                _html.Append(item.Task == TaskState.Checked ? " class=\"task done\"" : " class=\"task\"");
            }

            _html.Append('>');
            if (item.Task != TaskState.None)
            {
                var done = item.Task == TaskState.Checked;
                _html.Append("<span class=\"box\" role=\"img\" aria-label=\"").Append(done ? "done" : "not done").Append("\">").Append(done ? '☑' : '☐').Append("</span> ");
            }

            Blocks(item.Blocks, list.Tight);
            _html.Append("</li>\n");
        }

        _html.Append("</").Append(tag).Append(">\n");
    }

    private void Table(TableNode table)
    {
        _html.Append("<table>\n");
        if (table.Header is { } header)
        {
            _html.Append("<thead>\n");
            Row(header, "th");
            _html.Append("</thead>\n");
        }

        _html.Append("<tbody>\n");
        foreach (var row in table.Body)
        {
            Row(row, "td");
        }

        _html.Append("</tbody>\n</table>\n");
    }

    private void Row(TableRowNode row, string cellTag)
    {
        _html.Append("<tr>");
        foreach (var cell in row.Cells)
        {
            _html.Append('<').Append(cellTag);
            var alignment = cell.Alignment switch
            {
                TableAlignment.Left => "a-left",
                TableAlignment.Center => "a-center",
                TableAlignment.Right => "a-right",
                _ => null,
            };
            if (alignment is not null)
            {
                _html.Append(" class=\"").Append(alignment).Append('"');
            }

            _html.Append('>');
            Inlines(cell.Inlines);
            _html.Append("</").Append(cellTag).Append('>');
        }

        _html.Append("</tr>\n");
    }

    private void Footnotes(FootnoteSectionNode section)
    {
        _html.Append("<section class=\"footnotes\" role=\"doc-endnotes\"><ol>\n");
        foreach (var note in section.Items)
        {
            var number = note.Number.ToString(CultureInfo.InvariantCulture);
            _html.Append("<li id=\"fn-").Append(number).Append("\">");
            Blocks(note.Blocks, tight: true);
            if (_referencedFootnotes.Contains(note.Number))
            {
                _html.Append(" <a href=\"#fnref-").Append(number).Append("\" role=\"doc-backlink\">↩</a>");
            }

            _html.Append("</li>\n");
        }

        _html.Append("</ol></section>\n");
    }

    private void MathBlock(MathBlockNode node)
    {
        var result = MathText.Render(node.Latex);
        if (result.IsError)
        {
            _html.Append("<pre class=\"math-block math-error\" title=\"").Append(HtmlText.Escape(result.ErrorMessage)).Append("\">");
            HtmlText.Escape(_html, result.Source);
            _html.Append("</pre>\n");
            return;
        }

        _html.Append("<div class=\"math-block\">");
        HtmlText.Escape(_html, result.ToPlainUnicode());
        _html.Append("</div>\n");
    }

    private void Diagram(DiagramNode node)
    {
        _ = MermaidDiagram.TryParse(node.DiagramSource, out var parsed);
        string? svg = null;
        string? notice = null;
        switch (parsed.Kind)
        {
            case MermaidResultKind.Flowchart:
                svg = MermaidSvg.Render(parsed.Flowchart!.Layout(), parsed.Flowchart.Description);
                break;
            case MermaidResultKind.Chart:
                svg = MermaidSvg.Render(parsed.Chart!.Layout(), parsed.Chart.Description);
                break;
            case MermaidResultKind.Sequence:
                svg = MermaidSvg.Render(parsed.Sequence!.Layout(), "Sequence diagram");
                break;
            case MermaidResultKind.Unsupported:
                notice = $"Mermaid '{parsed.UnsupportedType}' diagrams cannot be drawn offline; the source is shown instead.";
                break;
            default:
                notice = $"This Mermaid diagram could not be drawn: {parsed.ErrorMessage}" + (parsed.ErrorLine > 0 ? $" (line {parsed.ErrorLine})" : string.Empty);
                break;
        }

        if (svg is not null)
        {
            _html.Append("<div class=\"diagram-block\">").Append(svg).Append("</div>\n");
            return;
        }

        warnings.Add(notice!);
        _html.Append("<div class=\"diagram-block\"><pre><code class=\"language-mermaid\">");
        HtmlText.Escape(_html, node.DiagramSource.TrimEnd('\r', '\n'));
        _html.Append("</code></pre><p class=\"notice\">").Append(HtmlText.Escape(notice)).Append("</p></div>\n");
    }

    private void FrontMatter(FrontMatterNode node)
    {
        if (!includeFrontMatter || node.Properties.Count == 0)
        {
            return;
        }

        _html.Append("<details class=\"front-matter\"><summary>Properties</summary><dl>");
        foreach (var property in node.Properties)
        {
            _html.Append("<dt>").Append(HtmlText.Escape(property.Key)).Append("</dt><dd>").Append(HtmlText.Escape(property.Value)).Append("</dd>");
        }

        _html.Append("</dl></details>\n");
    }

    private void Inlines(IEnumerable<InlineNode> nodes)
    {
        foreach (var node in nodes)
        {
            Inline(node);
        }
    }

    private void Wrap(string tag, IEnumerable<InlineNode> children)
    {
        _html.Append('<').Append(tag).Append('>');
        Inlines(children);
        _html.Append("</").Append(tag).Append('>');
    }

    private void Inline(InlineNode node)
    {
        switch (node)
        {
            case TextNode text:
                HtmlText.Escape(_html, text.Text);
                break;
            case EmphasisNode emphasis:
                Wrap("em", emphasis.Children);
                break;
            case StrongNode strong:
                Wrap("strong", strong.Children);
                break;
            case StrikethroughNode strike:
                Wrap("del", strike.Children);
                break;
            case CodeSpanNode code:
                _html.Append("<code>");
                HtmlText.Escape(_html, code.Code);
                _html.Append("</code>");
                break;
            case LinkNode link:
                Link(link);
                break;
            case ImageNode image:
                Image(image);
                break;
            case LineBreakNode lineBreak:
                _html.Append(lineBreak.IsHard ? "<br>\n" : "\n");
                break;
            case FootnoteRefNode reference:
                var number = reference.Number.ToString(CultureInfo.InvariantCulture);
                _html.Append("<sup class=\"footnote-ref\"><a href=\"#fn-").Append(number).Append('"');
                if (_referencedFootnotes.Add(reference.Number))
                {
                    _html.Append(" id=\"fnref-").Append(number).Append('"');
                }

                _html.Append(" role=\"doc-noteref\">[").Append(number).Append("]</a></sup>");
                break;
            case MathSpanNode math:
                var rendered = MathText.Render(math.Latex);
                _html.Append(rendered.IsError ? "<span class=\"math math-error\" title=\"" + HtmlText.Escape(rendered.ErrorMessage) + "\">" : "<span class=\"math\">");
                HtmlText.Escape(_html, rendered.IsError ? rendered.Source : rendered.ToPlainUnicode());
                _html.Append("</span>");
                break;
            case StyledNode styled:
                var tag = styled.Style switch
                {
                    InlineStyle.Kbd => "kbd",
                    InlineStyle.Subscript => "sub",
                    InlineStyle.Superscript => "sup",
                    InlineStyle.Mark => "mark",
                    _ => "u",
                };
                Wrap(tag, styled.Children);
                break;
        }
    }

    private void Link(LinkNode link)
    {
        var title = link.Title is { Length: > 0 } t ? " title=\"" + HtmlText.Escape(t) + "\"" : string.Empty;
        switch (link.Resolution)
        {
            case ExternalLink external when LinkSchemes.Contains(external.Uri.Scheme):
                _html.Append("<a href=\"").Append(HtmlText.Escape(external.Uri.AbsoluteUri)).Append("\" rel=\"noopener noreferrer\"").Append(title).Append('>');
                Inlines(link.Children);
                _html.Append("</a>");
                break;
            case Anchor anchor when AnchorId(anchor.Fragment) is { Length: > 0 } id:
                _html.Append("<a href=\"#").Append(HtmlText.Escape(id)).Append('"').Append(title).Append('>');
                Inlines(link.Children);
                _html.Append("</a>");
                break;
            default:
                // Local files cannot be opened from a standalone export, and naming their absolute path would leak the author's folder layout.
                var cls = link.Resolution is Blocked ? "link-blocked" : "link-local";
                _html.Append("<span class=\"").Append(cls).Append("\" title=\"").Append(HtmlText.Escape(link.Destination)).Append("\">");
                Inlines(link.Children);
                _html.Append("</span>");
                break;
        }
    }

    private void Image(ImageNode image) => ImageMarkup(image.Resolution, image.Destination, image.Alt, image.Title, null, null);

    private void ImageMarkup(ResourceDecision decision, string destination, string alt, string? title, string? width, string? height)
    {
        var label = string.IsNullOrWhiteSpace(alt) ? destination : alt;
        var embedded = images.TryEmbed(decision, label);
        if (embedded is null)
        {
            _html.Append("<span class=\"image-placeholder\" role=\"img\" aria-label=\"").Append(HtmlText.Escape(alt)).Append("\">🖼 ").Append(HtmlText.Escape(label)).Append("</span>");
            return;
        }

        _html.Append("<img src=\"").Append(embedded.ToDataUri()).Append("\" alt=\"").Append(HtmlText.Escape(alt)).Append('"');
        if (!string.IsNullOrEmpty(title))
        {
            _html.Append(" title=\"").Append(HtmlText.Escape(title)).Append('"');
        }

        if (width is not null)
        {
            _html.Append(" width=\"").Append(width).Append('"');
        }

        if (height is not null)
        {
            _html.Append(" height=\"").Append(height).Append('"');
        }

        _html.Append('>');
    }

    private static string AnchorId(string fragment) => fragment.TrimStart('#');

    /// <summary>
    /// Writes sanitized raw HTML again. The sanitizer keeps the author's original <c>href</c>/<c>src</c> values, so every one is
    /// resolved here a second time and only the outcome of that decision is written.
    /// </summary>
    private void SafeHtml(IReadOnlyList<HtmlPiece> pieces)
    {
        var emitted = new Stack<string?>();
        foreach (var piece in pieces)
        {
            switch (piece.Kind)
            {
                case HtmlPieceKind.Text:
                    HtmlText.Escape(_html, piece.Text);
                    break;
                case HtmlPieceKind.Close:
                    if (emitted.Count > 0 && emitted.Pop() is { } closing)
                    {
                        _html.Append("</").Append(closing).Append('>');
                    }

                    break;
                default:
                    var tag = OpenTag(piece);
                    if (piece.Kind == HtmlPieceKind.Open)
                    {
                        emitted.Push(tag);
                    }

                    break;
            }
        }

        while (emitted.Count > 0)
        {
            if (emitted.Pop() is { } open)
            {
                _html.Append("</").Append(open).Append('>');
            }
        }
    }

    /// <summary>Writes an opening or void tag and returns the tag name that must be closed later, or null when nothing needs closing.</summary>
    private string? OpenTag(HtmlPiece piece)
    {
        var name = piece.Tag.ToLowerInvariant();
        if (!HtmlSanitizer.AllowedTagNames.Contains(name))
        {
            return null;
        }

        if (name == "img")
        {
            var width = Dimension().IsMatch(Attribute(piece, "width") ?? string.Empty) ? Attribute(piece, "width") : null;
            var height = Dimension().IsMatch(Attribute(piece, "height") ?? string.Empty) ? Attribute(piece, "height") : null;
            var source = Attribute(piece, "src") ?? string.Empty;
            ImageMarkup(resolver.ResolveImage(source), source, Attribute(piece, "alt") ?? string.Empty, Attribute(piece, "title"), width, height);
            return null;
        }

        if (name == "a")
        {
            var href = Attribute(piece, "href");
            if (href is null && (Attribute(piece, "id") is not null || Attribute(piece, "name") is not null))
            {
                _html.Append("<a").Append(CommonAttributes(piece)).Append('>');
                return "a";
            }
            var decision = href is null ? null : resolver.ResolveLink(href);
            if (decision is ExternalLink external && LinkSchemes.Contains(external.Uri.Scheme))
            {
                _html.Append("<a href=\"").Append(HtmlText.Escape(external.Uri.AbsoluteUri)).Append("\" rel=\"noopener noreferrer\"").Append(CommonAttributes(piece)).Append('>');
                return "a";
            }

            if (decision is Anchor anchor && AnchorId(anchor.Fragment) is { Length: > 0 } id)
            {
                _html.Append("<a href=\"#").Append(HtmlText.Escape(id)).Append('"').Append(CommonAttributes(piece)).Append('>');
                return "a";
            }

            _html.Append("<span class=\"link-local\">");
            return "span";
        }

        _html.Append('<').Append(name).Append(CommonAttributes(piece)).Append('>');
        return piece.Kind == HtmlPieceKind.Void ? null : name;
    }

    private static string? Attribute(HtmlPiece piece, string name)
    {
        foreach (var attribute in piece.Attributes)
        {
            if (string.Equals(attribute.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return attribute.Value;
            }
        }

        return null;
    }

    private static string CommonAttributes(HtmlPiece piece)
    {
        var allowed = HtmlSanitizer.AllowedAttributeNames(piece.Tag.ToLowerInvariant());
        var builder = new StringBuilder();
        foreach (var attribute in piece.Attributes)
        {
            var name = attribute.Name.ToLowerInvariant();
            if (!allowed.Contains(name))
            {
                continue;
            }

            switch (name)
            {
                case "id" or "name" when HtmlSanitizer.IsValidAnchorName(attribute.Value):
                    builder.Append(' ').Append(name).Append("=\"").Append(HtmlText.Escape(attribute.Value)).Append('"');
                    break;
                case "title":
                    builder.Append(" title=\"").Append(HtmlText.Escape(attribute.Value)).Append('"');
                    break;
                case "open":
                    builder.Append(" open");
                    break;
                case "colspan" or "rowspan" when SpanCount().IsMatch(attribute.Value):
                    builder.Append(' ').Append(name).Append("=\"").Append(attribute.Value).Append('"');
                    break;
                case "width" or "height" when Dimension().IsMatch(attribute.Value):
                    builder.Append(' ').Append(name).Append("=\"").Append(attribute.Value).Append('"');
                    break;
                case "align" when attribute.Value.ToLowerInvariant() is "left" or "right" or "center" or "justify":
                    builder.Append(" align=\"").Append(attribute.Value.ToLowerInvariant()).Append('"');
                    break;
            }
        }

        return builder.ToString();
    }
}
