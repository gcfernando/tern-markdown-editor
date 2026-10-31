using System.Globalization;
using System.Text;
using Tern.Rendering.Html;
using Tern.Rendering.Mathematics;
using Tern.Rendering.Preview;
using Tern.Rendering.Resources;

namespace Tern.Export;

/// <summary>Settings for <see cref="PlainTextExporter"/>.</summary>
public sealed record PlainTextExportOptions
{
    /// <summary>How the Markdown is parsed.</summary>
    public PreviewOptions Preview { get; init; } = ExportDefaults.PreviewOptions;

    /// <summary>Line width used to pad tables and underline headings. Text itself is not re-wrapped. Default 80.</summary>
    public int Width { get; init; } = 80;

    /// <summary>Whether front matter is written as <c>key: value</c> lines.</summary>
    public bool IncludeFrontMatter { get; init; } = true;
}

/// <summary>Exports Markdown as readable plain text with no markup characters other than simple list, quote and table layout.</summary>
public static class PlainTextExporter
{
    /// <summary>Renders <paramref name="markdown"/> as plain text with <c>\n</c> line endings.</summary>
    public static string Export(string markdown, PlainTextExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        options ??= new PlainTextExportOptions();
        var document = PreviewBuilder.Build(markdown, options.Preview);
        return Export(document, options);
    }

    /// <summary>Renders an already built preview document.</summary>
    public static string Export(PreviewDocument document, PlainTextExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var writer = new TextBlockWriter(options ?? new PlainTextExportOptions());
        var lines = writer.Blocks(document.Blocks);
        if (document.Truncated)
        {
            lines.Add(string.Empty);
            lines.Add("[The document is very large and was cut short.]");
        }

        return string.Join('\n', lines).TrimEnd('\n', ' ') + "\n";
    }

    /// <summary>Exports to a UTF-8 file without BOM.</summary>
    public static void ExportToFile(string markdown, string outputPath, PlainTextExportOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        AtomicFile.Write(outputPath, new UTF8Encoding(false).GetBytes(Export(markdown, options)), options?.Preview.DocumentPath);
    }

    private sealed class TextBlockWriter(PlainTextExportOptions options)
    {
        public List<string> Blocks(IEnumerable<BlockNode> blocks)
        {
            var lines = new List<string>();
            foreach (var block in blocks)
            {
                var rendered = Block(block);
                if (rendered.Count == 0)
                {
                    continue;
                }

                if (lines.Count > 0)
                {
                    lines.Add(string.Empty);
                }

                lines.AddRange(rendered);
            }

            return lines;
        }

        private List<string> Block(BlockNode block)
        {
            switch (block)
            {
                case HeadingNode heading:
                    var text = Flatten(heading.Inlines);
                    var result = new List<string> { text };
                    if (heading.Level <= 2 && text.Length > 0)
                    {
                        result.Add(new string(heading.Level == 1 ? '=' : '-', Math.Clamp(text.Length, 3, options.Width)));
                    }

                    return result;
                case ParagraphNode paragraph:
                    return Split(Flatten(paragraph.Inlines));
                case ListNode list:
                    return List(list);
                case BlockQuoteNode quote:
                    return Prefix(Blocks(quote.Blocks), "> ", "> ");
                case AlertNode alert:
                    var body = Blocks(alert.Blocks);
                    body.Insert(0, alert.Kind.ToString().ToUpperInvariant() + ":");
                    return Prefix(body, "> ", "> ");
                case CodeBlockNode code:
                    return Split(code.Code.TrimEnd('\r', '\n')).Select(l => "    " + l).ToList();
                case ThematicBreakNode:
                    return [new string('-', Math.Min(options.Width, 40))];
                case TableNode table:
                    return Table(table);
                case FootnoteSectionNode notes:
                    var footnotes = new List<string>();
                    foreach (var note in notes.Items)
                    {
                        footnotes.AddRange(Prefix(Blocks(note.Blocks), $"[{note.Number.ToString(CultureInfo.InvariantCulture)}] ", "    "));
                    }

                    return footnotes;
                case MathBlockNode math:
                    var rendered = MathText.Render(math.Latex);
                    return Split(rendered.IsError ? rendered.Source : rendered.ToPlainUnicode()).Select(l => "    " + l).ToList();
                case DiagramNode diagram:
                    var lines = new List<string> { $"[{diagram.DiagramType} diagram source]" };
                    lines.AddRange(Split(diagram.DiagramSource.TrimEnd('\r', '\n')).Select(l => "    " + l));
                    return lines;
                case HtmlSafeBlockNode safe:
                    return Split(HtmlSanitizer.ToPlainText(safe.Pieces).Trim());
                case DetailsNode details:
                    var detailLines = new List<string>(Split(Flatten(details.Summary)));
                    var inner = Blocks(details.Blocks);
                    if (inner.Count > 0)
                    {
                        detailLines.AddRange(Prefix(inner, "  ", "  "));
                    }

                    return detailLines;
                case FrontMatterNode front when options.IncludeFrontMatter:
                    return front.Properties.Select(p => $"{p.Key}: {p.Value}").SelectMany(Split).ToList();
                case DefinitionListNode definitions:
                    var definitionLines = new List<string>();
                    foreach (var item in definitions.Items)
                    {
                        definitionLines.AddRange(item.Terms.Select(Flatten));
                        definitionLines.AddRange(Prefix(Blocks(item.Definitions), "    ", "    "));
                    }

                    return definitionLines;
                default:
                    return [];
            }
        }

        private List<string> List(ListNode list)
        {
            var lines = new List<string>();
            var number = list.StartNumber;
            foreach (var item in list.Items)
            {
                var marker = list.Ordered ? number.ToString(CultureInfo.InvariantCulture) + ". " : "- ";
                number++;
                marker += item.Task switch
                {
                    TaskState.Checked => "[x] ",
                    TaskState.Unchecked => "[ ] ",
                    _ => string.Empty,
                };
                var content = Blocks(item.Blocks);
                if (!list.Tight && lines.Count > 0)
                {
                    lines.Add(string.Empty);
                }

                lines.AddRange(Prefix(content, marker, new string(' ', marker.Length)));
            }

            return lines;
        }

        private List<string> Table(TableNode table)
        {
            var rows = new List<(List<string> Cells, bool Header)>();
            if (table.Header is { } header)
            {
                rows.Add((header.Cells.Select(c => OneLine(Flatten(c.Inlines))).ToList(), true));
            }

            rows.AddRange(table.Body.Select(r => (r.Cells.Select(c => OneLine(Flatten(c.Inlines))).ToList(), false)));
            var columns = rows.Count == 0 ? 0 : rows.Max(r => r.Cells.Count);
            var widths = new int[columns];
            foreach (var (cells, _) in rows)
            {
                for (var i = 0; i < cells.Count; i++)
                {
                    widths[i] = Math.Max(widths[i], cells[i].Length);
                }
            }

            var lines = new List<string>();
            foreach (var (cells, isHeader) in rows)
            {
                var padded = Enumerable.Range(0, columns).Select(i => (i < cells.Count ? cells[i] : string.Empty).PadRight(widths[i]));
                lines.Add("| " + string.Join(" | ", padded) + " |");
                if (isHeader)
                {
                    lines.Add("| " + string.Join(" | ", widths.Select(w => new string('-', Math.Max(1, w)))) + " |");
                }
            }

            return lines;
        }

        private static string OneLine(string text) => text.Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', ' ');

        private static List<string> Split(string text) =>
            text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();

        private static List<string> Prefix(List<string> lines, string first, string rest)
        {
            var result = new List<string>(lines.Count);
            for (var i = 0; i < lines.Count; i++)
            {
                var prefix = i == 0 ? first : rest;
                result.Add(lines[i].Length == 0 ? prefix.TrimEnd() : prefix + lines[i]);
            }

            return result;
        }

        private string Flatten(IEnumerable<InlineNode> nodes)
        {
            var builder = new StringBuilder();
            foreach (var node in nodes)
            {
                Append(builder, node);
            }

            return ExportText.Clean(builder.ToString());
        }

        private void Append(StringBuilder builder, InlineNode node)
        {
            switch (node)
            {
                case TextNode text:
                    builder.Append(text.Text);
                    break;
                case EmphasisNode e:
                    AppendAll(builder, e.Children);
                    break;
                case StrongNode s:
                    AppendAll(builder, s.Children);
                    break;
                case StrikethroughNode s:
                    AppendAll(builder, s.Children);
                    break;
                case StyledNode s:
                    AppendAll(builder, s.Children);
                    break;
                case CodeSpanNode code:
                    builder.Append(code.Code);
                    break;
                case LinkNode link:
                    var label = Flatten(link.Children);
                    builder.Append(label);
                    if (link.Resolution is ExternalLink external && !string.Equals(label, external.Uri.OriginalString, StringComparison.Ordinal))
                    {
                        builder.Append(" (").Append(external.Uri.AbsoluteUri).Append(')');
                    }

                    break;
                case ImageNode image:
                    builder.Append("[image: ").Append(string.IsNullOrWhiteSpace(image.Alt) ? image.Destination : image.Alt).Append(']');
                    break;
                case LineBreakNode lineBreak:
                    builder.Append(lineBreak.IsHard ? '\n' : ' ');
                    break;
                case FootnoteRefNode reference:
                    builder.Append('[').Append(reference.Number.ToString(CultureInfo.InvariantCulture)).Append(']');
                    break;
                case MathSpanNode math:
                    var rendered = MathText.Render(math.Latex);
                    builder.Append(rendered.IsError ? rendered.Source : rendered.ToPlainUnicode());
                    break;
            }
        }

        private void AppendAll(StringBuilder builder, IEnumerable<InlineNode> nodes)
        {
            foreach (var node in nodes)
            {
                Append(builder, node);
            }
        }
    }
}
