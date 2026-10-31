using System.Globalization;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Tern.Rendering.Html;
using Tern.Rendering.Mathematics;
using Tern.Rendering.Mermaid;
using Tern.Rendering.Preview;
using Tern.Rendering.Resources;

namespace Tern.Export;

/// <summary>Lays out a <see cref="PreviewDocument"/> onto PDF pages.</summary>
internal sealed class PdfRenderer : IDisposable
{
    private const double BaselineFactor = 1.05;
    private const double CellPaddingX = 5;
    private const double CellPaddingY = 3;

    private static readonly XColor Ink = XColor.FromArgb(31, 35, 40);
    private static readonly XColor Muted = XColor.FromArgb(89, 99, 110);
    private static readonly XColor LinkColor = XColor.FromArgb(9, 105, 218);
    private static readonly XColor Rule = XColor.FromArgb(209, 217, 224);
    private static readonly XColor Soft = XColor.FromArgb(246, 248, 250);
    private static readonly XColor CodeBackground = XColor.FromArgb(238, 241, 244);
    private static readonly XColor MarkColor = XColor.FromArgb(255, 241, 150);
    private static readonly XColor ErrorColor = XColor.FromArgb(207, 34, 46);

    private readonly PdfDocument _document;
    private readonly PdfExportOptions _options;
    private readonly SafeResourceResolver _resolver;
    private readonly ImageEmbedder _images;
    private readonly List<string> _warnings;
    private readonly PdfTypography _typography = new();
    private readonly PdfDiagramPainter _painter;
    private readonly List<PdfPage> _pages = [];
    private readonly List<LinkTarget> _links = [];
    private readonly List<PendingLink> _pendingLinks = [];
    private readonly Dictionary<string, (int Page, double Y)> _anchors = new(StringComparer.Ordinal);
    private readonly List<(int Level, PdfOutline Outline)> _outlineStack = [];
    private readonly List<Bar> _bars = [];
    private readonly Dictionary<EmbeddedImage, PdfImage?> _imageCache = [];

    private XGraphics? _gfx;
    private PdfPage? _page;
    private double _pageWidth;
    private double _pageHeight;
    private double _top;
    private double _bottom;
    private double _x;
    private double _width;
    private double _y;
    private double _gap;
    private double _size;
    private double _blockGap;
    private XColor _color = Ink;
    private int _listDepth;
    private Action? _repeatHeader;
    private bool _repeating;

    public PdfRenderer(PdfDocument document, PdfExportOptions options, SafeResourceResolver resolver, ImageEmbedder images, List<string> warnings)
    {
        _document = document;
        _options = options;
        _resolver = resolver;
        _images = images;
        _warnings = warnings;
        _painter = new PdfDiagramPainter(_typography);
        _size = options.BaseFontSize;
        _blockGap = _size * 0.7;
        _x = options.Margins.Left;
        _width = PageWidth(options.PageSize) - options.Margins.Left - options.Margins.Right;
    }

    /// <summary>The number of pages created so far.</summary>
    public int PageCount => _pages.Count;

    public static double PageWidth(PdfPageSize size) => size == PdfPageSize.Letter ? 612 : 595.276;

    public static double PageHeight(PdfPageSize size) => size == PdfPageSize.Letter ? 792 : 841.89;

    /// <summary>Lays out the whole document. Returns false when the page limit cut it short.</summary>
    public bool Render(PreviewDocument document)
    {
        try
        {
            NewPage();
            Blocks(document.Blocks);
            if (document.Truncated)
            {
                _gap = _size;
                Paragraph([new TextNode(default, "This document is very large. Only the first part is included.")], _size, Muted, TableAlignment.None, bold: false, italic: true);
            }

            return true;
        }
        catch (PageLimitException)
        {
            _warnings.Add($"The page limit of {_options.MaxPages} was reached; the rest of the document was left out.");
            return false;
        }
        finally
        {
            FlushBars();
            _gfx?.Dispose();
            _gfx = null;
        }
    }

    /// <summary>Draws "n / N" in the bottom margin of every page.</summary>
    public void AddPageNumbers()
    {
        var font = _typography.Font(default, 9);
        var brush = new XSolidBrush(Muted);
        for (var i = 0; i < _pages.Count; i++)
        {
            using var gfx = XGraphics.FromPdfPage(_pages[i], XGraphicsPdfPageOptions.Append);
            var y = _pageHeight - (_options.Margins.Bottom / 2);
            gfx.DrawString($"{(i + 1).ToString(CultureInfo.InvariantCulture)} / {_pages.Count.ToString(CultureInfo.InvariantCulture)}", font, brush, new XPoint(_pageWidth / 2, y), XStringFormats.Center);
        }
    }

    /// <summary>Adds clickable annotations for external links and links to headings.</summary>
    public void AddLinkAnnotations()
    {
        foreach (var pending in _pendingLinks)
        {
            var target = _links[pending.Link - 1];
            var page = _pages[pending.Page];
            var rect = new PdfRectangle(new XRect(pending.Rect.X, _pageHeight - pending.Rect.Bottom, pending.Rect.Width, pending.Rect.Height));
            if (target.Url is not null)
            {
                page.AddWebLink(rect, target.Url);
            }
            else if (target.Anchor is not null && _anchors.TryGetValue(target.Anchor, out var destination))
            {
                page.AddDocumentLink(rect, destination.Page + 1, new XPoint(0, destination.Y));
            }
        }
    }

    public void Dispose()
    {
        _gfx?.Dispose();
        foreach (var image in _imageCache.Values)
        {
            image?.Image.Dispose();
        }

        _typography.Dispose();
    }

    private double ContentHeight => _pageHeight - _options.Margins.Top - _options.Margins.Bottom;

    private double LineHeight => _size * PdfTypography.LineSpacing;

    private void NewPage()
    {
        if (_gfx is not null)
        {
            FlushBars();
            _gfx.Dispose();
            _gfx = null;
        }

        if (_pages.Count >= _options.MaxPages)
        {
            throw new PageLimitException();
        }

        _page = _document.AddPage();
        _page.Size = _options.PageSize == PdfPageSize.Letter ? PdfSharp.PageSize.Letter : PdfSharp.PageSize.A4;
        _pageWidth = _page.Width.Point;
        _pageHeight = _page.Height.Point;
        _top = _options.Margins.Top;
        _bottom = _pageHeight - _options.Margins.Bottom;
        _gfx = XGraphics.FromPdfPage(_page);
        _pages.Add(_page);
        _y = _top;
        _gap = 0;
        foreach (var bar in _bars)
        {
            bar.StartY = _top;
        }

        if (_repeatHeader is not null && !_repeating)
        {
            _repeating = true;
            try
            {
                _repeatHeader();
            }
            finally
            {
                _repeating = false;
            }
        }
    }

    private void Begin(double minHeight)
    {
        if (_gap > 0 && _y > _top + 0.5)
        {
            _y += _gap;
        }

        _gap = 0;
        EnsureSpace(minHeight);
    }

    private void EnsureSpace(double height)
    {
        if (_y + height > _bottom + 0.01 && _y > _top + 0.5)
        {
            NewPage();
        }
    }

    private void FlushBars()
    {
        foreach (var bar in _bars)
        {
            DrawBar(bar, _y);
            bar.StartY = _y;
        }
    }

    private void DrawBar(Bar bar, double endY)
    {
        if (_gfx is not null && endY - bar.StartY > 0.5)
        {
            _gfx.DrawRectangle(new XSolidBrush(bar.Color), bar.X, bar.StartY, 3, endY - bar.StartY);
        }
    }

    private Bar PushBar(XColor color, double x)
    {
        Begin(LineHeight);
        var bar = new Bar(x, color) { StartY = _y };
        _bars.Add(bar);
        return bar;
    }

    private void PopBar(Bar bar)
    {
        DrawBar(bar, _y);
        _bars.Remove(bar);
        _gap = _blockGap;
    }

    private void Blocks(IEnumerable<BlockNode> blocks)
    {
        foreach (var block in blocks)
        {
            Block(block);
        }
    }

    private void Block(BlockNode block)
    {
        switch (block)
        {
            case HeadingNode heading:
                Heading(heading);
                break;
            case ParagraphNode paragraph:
                Paragraph(paragraph.Inlines, _size, _color, TableAlignment.None);
                _gap = _blockGap;
                break;
            case ListNode list:
                ListBlock(list);
                break;
            case BlockQuoteNode quote:
                Quote(quote);
                break;
            case AlertNode alert:
                Alert(alert);
                break;
            case CodeBlockNode code:
                CodeBlock(code.Code);
                break;
            case ThematicBreakNode:
                Begin(10);
                _gfx!.DrawLine(new XPen(Rule, 1), _x, _y + 5, _x + _width, _y + 5);
                _y += 10;
                _gap = _blockGap;
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
                HtmlBlock(safe);
                break;
            case DetailsNode details:
                Paragraph(details.Summary, _size, _color, TableAlignment.None, bold: true);
                _gap = _blockGap * 0.6;
                Blocks(details.Blocks);
                break;
            case FrontMatterNode front:
                FrontMatter(front);
                break;
            case DefinitionListNode definitions:
                DefinitionList(definitions);
                break;
        }
    }

    private void Heading(HeadingNode heading)
    {
        var level = Math.Clamp(heading.Level, 1, 6);
        var size = _size * level switch { 1 => 2.0, 2 => 1.65, 3 => 1.35, 4 => 1.15, 5 => 1.05, _ => 1.0 };
        var atoms = new List<Atom>();
        var style = new TextStyle(true, false, false, false, false, false, 0, 0);
        AddInlines(atoms, heading.Inlines, style);
        var lines = _typography.Break(atoms, _width, size, ContentHeight * 0.5);

        _gap = Math.Max(_gap, size * 0.8);
        Begin(lines[0].Height + (3 * LineHeight));
        RecordHeading(heading, level);
        var color = level == 6 ? Muted : _color;
        DrawLines(lines, color, TableAlignment.None);
        if (level <= 2)
        {
            _gfx!.DrawLine(new XPen(Rule, 0.8), _x, _y + 2, _x + _width, _y + 2);
            _y += 5;
        }

        _gap = size * 0.4;
    }

    private void RecordHeading(HeadingNode heading, int level)
    {
        var key = heading.Anchor.TrimStart('#');
        if (key.Length > 0)
        {
            _anchors.TryAdd(key, (_pages.Count - 1, _y));
        }

        var title = ExportText.Clean(heading.PlainText).Trim();
        title = title.Length == 0 ? "(untitled)" : title.Length > 200 ? title[..200] : title;
        while (_outlineStack.Count > 0 && _outlineStack[^1].Level >= level)
        {
            _outlineStack.RemoveAt(_outlineStack.Count - 1);
        }

        var collection = _outlineStack.Count > 0 ? _outlineStack[^1].Outline.Outlines : _document.Outlines;
        var outline = collection.Add(title, _page!, opened: level <= 2);
        outline.PageDestinationType = PdfPageDestinationType.Xyz;
        outline.Left = 0;
        outline.Top = Math.Max(0, _pageHeight - _y);
        _outlineStack.Add((level, outline));
    }

    private void Paragraph(IEnumerable<InlineNode> inlines, double size, XColor color, TableAlignment alignment, bool bold = false, bool italic = false)
    {
        var atoms = new List<Atom>();
        AddInlines(atoms, inlines, new TextStyle(bold, italic, false, false, false, false, 0, 0));
        ParagraphAtoms(atoms, size, color, alignment);
    }

    private void ParagraphAtoms(List<Atom> atoms, double size, XColor color, TableAlignment alignment)
    {
        if (!atoms.Any(a => a.Kind is AtomKind.Word or AtomKind.Image))
        {
            return;
        }

        var lines = _typography.Break(atoms, _width, size, ContentHeight * 0.8);
        DrawLines(lines, color, alignment);
    }

    private void DrawLines(List<LineBox> lines, XColor color, TableAlignment alignment)
    {
        Begin(lines[0].Height + (lines.Count > 1 ? lines[1].Height : 0));
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                EnsureSpace(lines[i].Height);
            }

            DrawLine(lines[i], _x, _y, _width, alignment, color);
            _y += lines[i].Height;
        }
    }

    private void DrawLine(LineBox line, double x, double top, double width, TableAlignment alignment, XColor color)
    {
        var offset = alignment switch
        {
            TableAlignment.Center => Math.Max(0, (width - line.Width) / 2),
            TableAlignment.Right => Math.Max(0, width - line.Width),
            _ => 0,
        };
        var gfx = _gfx!;
        var baseline = top + line.Baseline;
        var items = line.Items;
        var i = 0;
        while (i < items.Count)
        {
            var item = items[i];
            if (item.Atom.Kind == AtomKind.Image)
            {
                gfx.DrawImage(item.Atom.Image!.Image, x + offset + item.X, baseline - item.Height, item.Width, item.Height);
                i++;
                continue;
            }

            var style = item.Atom.Style;
            var run = new StringBuilder();
            var j = i;
            while (j < items.Count && items[j].Atom.Kind != AtomKind.Image && items[j].Atom.Style == style)
            {
                run.Append(items[j].Atom.Kind == AtomKind.Space ? " " : items[j].Atom.Text);
                j++;
            }

            var last = items[j - 1];
            var runX = x + offset + item.X;
            var runWidth = last.X + last.Width - item.X;
            var size = item.FontSize;
            var drawY = baseline + (style.Script > 0 ? -size * 0.35 : style.Script < 0 ? size * 0.2 : 0);
            if (style.Highlight)
            {
                gfx.DrawRectangle(new XSolidBrush(MarkColor), runX, drawY - (size * 0.95), runWidth, size * 1.25);
            }
            else if (style.Mono)
            {
                gfx.DrawRectangle(new XSolidBrush(CodeBackground), runX - 1, drawY - (size * 0.95), runWidth + 2, size * 1.25);
            }

            var textColor = style.Link > 0 ? LinkColor : color;
            var brush = new XSolidBrush(textColor);
            gfx.DrawString(run.ToString(), item.Font!, brush, new XPoint(runX, drawY), XStringFormats.BaseLineLeft);
            if (style.Strike)
            {
                gfx.DrawLine(new XPen(textColor, 0.7), runX, drawY - (size * 0.3), runX + runWidth, drawY - (size * 0.3));
            }

            if (style.Underline || style.Link > 0)
            {
                gfx.DrawLine(new XPen(textColor, 0.6), runX, drawY + 1.5, runX + runWidth, drawY + 1.5);
            }

            if (style.Link > 0)
            {
                AddLinkRect(style.Link, new XRect(runX, drawY - size, runWidth, size * 1.3));
            }

            i = j;
        }
    }

    private void AddLinkRect(int link, XRect rect)
    {
        var page = _pages.Count - 1;
        if (_pendingLinks.Count > 0 && _pendingLinks[^1] is var previous && previous.Page == page && previous.Link == link
            && Math.Abs(previous.Rect.Right - rect.X) < 8 && Math.Abs(previous.Rect.Y - rect.Y) < 0.5)
        {
            _pendingLinks[^1] = previous with { Rect = new XRect(previous.Rect.X, previous.Rect.Y, rect.Right - previous.Rect.X, rect.Height) };
            return;
        }

        _pendingLinks.Add(new PendingLink(page, rect, link));
    }

    private void ListBlock(ListNode list)
    {
        var lastNumber = list.StartNumber + list.Items.Count - 1;
        var font = _typography.Font(default, _size);
        var indent = list.Ordered
            ? Math.Max(_size * 2, _typography.Width(new string('9', lastNumber.ToString(CultureInfo.InvariantCulture).Length) + ".", font) + 12)
            : _size * 1.8;
        var savedGap = _blockGap;
        var savedX = _x;
        var savedWidth = _width;
        _blockGap = list.Tight ? 2 : _size * 0.5;
        var number = list.StartNumber;

        foreach (var item in list.Items)
        {
            Begin(LineHeight * 2);
            DrawMarker(list, item, number, font, indent);
            number++;
            _x = savedX + indent;
            _width = Math.Max(30, savedWidth - indent);
            _listDepth++;
            Blocks(item.Blocks);
            _listDepth--;
            _x = savedX;
            _width = savedWidth;
            _gap = _blockGap;
        }

        _blockGap = savedGap;
        _gap = savedGap;
    }

    private void DrawMarker(ListNode list, ListItemNode item, int number, XFont font, double indent)
    {
        var gfx = _gfx!;
        var baseline = _y + (_size * BaselineFactor);
        var right = _x + indent - 5;
        if (item.Task != TaskState.None)
        {
            var box = _size * 0.8;
            var left = right - box - 2;
            var top = baseline - box;
            var pen = new XPen(item.Task == TaskState.Checked ? XColor.FromArgb(26, 127, 55) : Muted, 1);
            gfx.DrawRectangle(pen, left, top, box, box);
            if (item.Task == TaskState.Checked)
            {
                var check = new XPen(XColor.FromArgb(26, 127, 55), 1.4);
                gfx.DrawLine(check, left + (box * 0.2), top + (box * 0.55), left + (box * 0.42), top + (box * 0.78));
                gfx.DrawLine(check, left + (box * 0.42), top + (box * 0.78), left + (box * 0.82), top + (box * 0.2));
            }

            return;
        }

        if (list.Ordered)
        {
            var text = number.ToString(CultureInfo.InvariantCulture) + ".";
            gfx.DrawString(text, font, new XSolidBrush(Muted), new XPoint(right - _typography.Width(text, font), baseline), XStringFormats.BaseLineLeft);
            return;
        }

        var radius = _size * 0.16;
        var cx = right - (indent * 0.35);
        var cy = baseline - (_size * 0.3);
        var brush = new XSolidBrush(_color);
        switch (_listDepth % 3)
        {
            case 0:
                gfx.DrawEllipse(brush, cx - radius, cy - radius, radius * 2, radius * 2);
                break;
            case 1:
                gfx.DrawEllipse(new XPen(_color, 0.8), cx - radius, cy - radius, radius * 2, radius * 2);
                break;
            default:
                gfx.DrawRectangle(brush, cx - radius, cy - radius, radius * 2, radius * 2);
                break;
        }
    }

    private void Quote(BlockQuoteNode quote)
    {
        var savedX = _x;
        var savedWidth = _width;
        var savedColor = _color;
        var bar = PushBar(Rule, _x);
        _x += 14;
        _width = Math.Max(30, _width - 14);
        _color = Muted;
        Blocks(quote.Blocks);
        _x = savedX;
        _width = savedWidth;
        _color = savedColor;
        PopBar(bar);
    }

    private void Alert(AlertNode alert)
    {
        var accent = alert.Kind switch
        {
            AlertKind.Tip => XColor.FromArgb(26, 127, 55),
            AlertKind.Important => XColor.FromArgb(130, 80, 223),
            AlertKind.Warning => XColor.FromArgb(154, 103, 0),
            AlertKind.Caution => ErrorColor,
            _ => LinkColor,
        };
        var savedX = _x;
        var savedWidth = _width;
        var bar = PushBar(accent, _x);
        _x += 14;
        _width = Math.Max(30, _width - 14);
        var title = new List<Atom> { new(AtomKind.Word, alert.Kind.ToString(), new TextStyle(true, false, false, false, false, false, 0, 0)) };
        ParagraphAtoms(title, _size, accent, TableAlignment.None);
        _gap = _blockGap * 0.5;
        Blocks(alert.Blocks);
        _x = savedX;
        _width = savedWidth;
        PopBar(bar);
    }

    private void CodeBlock(string code)
    {
        const double padding = 6;
        var size = _size * 0.88;
        var font = _typography.Font(new TextStyle(false, false, true, false, false, false, 0, 0), size);
        var lineHeight = size * 1.35;
        var charWidth = Math.Max(1, _typography.Width("MMMMMMMMMM", font) / 10);
        var columns = Math.Max(8, (int)((_width - (2 * padding)) / charWidth));

        var lines = new List<string>();
        foreach (var raw in PdfText.Safe(code.TrimEnd('\r', '\n')).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            if (raw.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            for (var start = 0; start < raw.Length; start += columns)
            {
                lines.Add(raw.Substring(start, Math.Min(columns, raw.Length - start)));
            }
        }

        Begin((Math.Min(lines.Count, 3) * lineHeight) + (2 * padding));
        var index = 0;
        var fill = new XSolidBrush(Soft);
        var border = new XPen(Rule, 0.7);
        var text = new XSolidBrush(Ink);
        while (index < lines.Count)
        {
            var available = _bottom - _y - (2 * padding);
            var count = (int)Math.Floor(available / lineHeight);
            if (count < 1)
            {
                if (_y > _top + 0.5)
                {
                    NewPage();
                    continue;
                }

                count = 1;
            }

            count = Math.Min(count, lines.Count - index);
            var height = (count * lineHeight) + (2 * padding);
            _gfx!.DrawRectangle(border, fill, _x, _y, _width, height);
            for (var k = 0; k < count; k++)
            {
                var line = lines[index + k];
                if (line.Length > 0)
                {
                    _gfx.DrawString(line, font, text, new XPoint(_x + padding, _y + padding + (k * lineHeight) + (size * BaselineFactor)), XStringFormats.BaseLineLeft);
                }
            }

            _y += height;
            index += count;
            if (index < lines.Count)
            {
                NewPage();
            }
        }

        _gap = _blockGap;
    }

    private void Table(TableNode table)
    {
        var rows = new List<(TableRowNode Row, bool Header)>();
        if (table.Header is { } header)
        {
            rows.Add((header, true));
        }

        rows.AddRange(table.Body.Select(r => (r, false)));
        var columns = Math.Max(1, rows.Count == 0 ? 1 : rows.Max(r => r.Row.Cells.Count));
        var cellAtoms = rows.Select(r => Enumerable.Range(0, columns).Select(c => CellAtoms(r.Row, c, r.Header)).ToList()).ToList();
        var alignments = Enumerable.Range(0, columns).Select(c => c < table.Alignments.Count ? table.Alignments[c] : TableAlignment.None).ToArray();
        var widths = ColumnWidths(cellAtoms, columns);
        var savedRepeat = _repeatHeader;

        for (var r = 0; r < rows.Count; r++)
        {
            var lines = Enumerable.Range(0, columns)
                .Select(c => _typography.Break(cellAtoms[r][c], widths[c] - (2 * CellPaddingX), _size, ContentHeight * 0.5))
                .ToArray();
            var isHeader = rows[r].Header;
            var rowAlignments = Enumerable.Range(0, columns).Select(c => rows[r].Row.Cells.Count > c ? rows[r].Row.Cells[c].Alignment : alignments[c]).ToArray();
            if (r == 0)
            {
                Begin(lines.Max(l => l[0].Height) + (2 * CellPaddingY) + LineHeight);
            }

            DrawRow(lines, widths, rowAlignments, isHeader);
            if (isHeader)
            {
                var headerLines = lines;
                var headerAlignments = rowAlignments;
                _repeatHeader = () => DrawRow(headerLines, widths, headerAlignments, true);
            }
        }

        _repeatHeader = savedRepeat;
        _gap = _blockGap;
    }

    private List<Atom> CellAtoms(TableRowNode row, int column, bool header)
    {
        var atoms = new List<Atom>();
        if (column < row.Cells.Count)
        {
            AddInlines(atoms, row.Cells[column].Inlines, new TextStyle(header, false, false, false, false, false, 0, 0));
        }

        return atoms;
    }

    private double[] ColumnWidths(List<List<List<Atom>>> cellAtoms, int columns)
    {
        var min = new double[columns];
        var preferred = new double[columns];
        foreach (var row in cellAtoms)
        {
            for (var c = 0; c < columns; c++)
            {
                double total = 0;
                double longest = 0;
                foreach (var atom in row[c])
                {
                    var width = _typography.AtomWidth(atom, _size, _width / 2);
                    total += width;
                    if (atom.Kind != AtomKind.Space)
                    {
                        longest = Math.Max(longest, width);
                    }
                }

                min[c] = Math.Max(min[c], longest + (2 * CellPaddingX));
                preferred[c] = Math.Max(preferred[c], total + (2 * CellPaddingX));
            }
        }

        var widths = new double[columns];
        var sumPreferred = preferred.Sum();
        var sumMin = min.Sum();
        if (sumPreferred <= _width)
        {
            return preferred;
        }

        if (sumMin >= _width)
        {
            for (var c = 0; c < columns; c++)
            {
                widths[c] = Math.Max(2 * CellPaddingX + 6, min[c] * _width / sumMin);
            }

            return widths;
        }

        var extra = _width - sumMin;
        var wanted = sumPreferred - sumMin;
        for (var c = 0; c < columns; c++)
        {
            widths[c] = min[c] + (extra * (preferred[c] - min[c]) / wanted);
        }

        return widths;
    }

    private void DrawRow(List<LineBox>[] cells, double[] widths, TableAlignment[] alignments, bool header)
    {
        var next = new int[cells.Length];
        var first = true;
        while (true)
        {
            var available = _bottom - _y - (2 * CellPaddingY);
            var counts = new int[cells.Length];
            var remaining = false;
            var anyFits = false;
            for (var c = 0; c < cells.Length; c++)
            {
                if (next[c] >= cells[c].Count)
                {
                    continue;
                }

                remaining = true;
                double used = 0;
                while (next[c] + counts[c] < cells[c].Count && used + cells[c][next[c] + counts[c]].Height <= available + 0.01)
                {
                    used += cells[c][next[c] + counts[c]].Height;
                    counts[c]++;
                }

                anyFits |= counts[c] > 0;
            }

            if (!remaining)
            {
                break;
            }

            var atTop = _y <= _top + 0.5;
            var fullHeight = cells.Max(c => c.Sum(l => l.Height)) + (2 * CellPaddingY);
            if (!atTop && ((first && fullHeight > _bottom - _y && fullHeight <= ContentHeight) || !anyFits))
            {
                NewPage();
                first = false;
                continue;
            }

            first = false;
            for (var c = 0; c < cells.Length; c++)
            {
                if (next[c] < cells[c].Count && counts[c] == 0)
                {
                    counts[c] = 1;
                }
            }

            double sliceHeight = 0;
            for (var c = 0; c < cells.Length; c++)
            {
                double h = 0;
                for (var k = 0; k < counts[c]; k++)
                {
                    h += cells[c][next[c] + k].Height;
                }

                sliceHeight = Math.Max(sliceHeight, h);
            }

            sliceHeight += 2 * CellPaddingY;
            var x = _x;
            for (var c = 0; c < cells.Length; c++)
            {
                var border = new XPen(Rule, 0.6);
                if (header)
                {
                    _gfx!.DrawRectangle(border, new XSolidBrush(Soft), x, _y, widths[c], sliceHeight);
                }
                else
                {
                    _gfx!.DrawRectangle(border, x, _y, widths[c], sliceHeight);
                }

                var lineTop = _y + CellPaddingY;
                for (var k = 0; k < counts[c]; k++)
                {
                    var line = cells[c][next[c] + k];
                    DrawLine(line, x + CellPaddingX, lineTop, widths[c] - (2 * CellPaddingX), alignments[c], _color);
                    lineTop += line.Height;
                }

                next[c] += counts[c];
                x += widths[c];
            }

            _y += sliceHeight;
            if (Enumerable.Range(0, cells.Length).All(c => next[c] >= cells[c].Count))
            {
                break;
            }

            NewPage();
        }
    }

    private void Footnotes(FootnoteSectionNode section)
    {
        _gap = Math.Max(_gap, _size * 1.2);
        Begin(LineHeight * 3);
        _gfx!.DrawLine(new XPen(Rule, 0.8), _x, _y, _x + 120, _y);
        _y += 4;
        var savedSize = _size;
        var savedColor = _color;
        _size = savedSize * 0.9;
        _color = Muted;
        foreach (var note in section.Items)
        {
            Begin(LineHeight * 2);
            _anchors.TryAdd("fn:" + note.Number.ToString(CultureInfo.InvariantCulture), (_pages.Count - 1, _y));
            var font = _typography.Font(default, _size);
            var label = note.Number.ToString(CultureInfo.InvariantCulture) + ".";
            const double indent = 22;
            _gfx!.DrawString(label, font, new XSolidBrush(Muted), new XPoint(_x + indent - 5 - _typography.Width(label, font), _y + (_size * BaselineFactor)), XStringFormats.BaseLineLeft);
            var savedX = _x;
            var savedWidth = _width;
            _x += indent;
            _width = Math.Max(30, _width - indent);
            Blocks(note.Blocks);
            _x = savedX;
            _width = savedWidth;
            _gap = 3;
        }

        _size = savedSize;
        _color = savedColor;
        _gap = _blockGap;
    }

    private void MathBlock(MathBlockNode node)
    {
        var result = MathText.Render(node.Latex);
        var text = result.IsError ? result.Source : result.ToPlainUnicode();
        var atoms = new List<Atom>();
        AddText(atoms, text, new TextStyle(false, !result.IsError, result.IsError, false, false, false, 0, 0));
        ParagraphAtoms(atoms, _size * 1.1, result.IsError ? ErrorColor : _color, TableAlignment.Center);
        _gap = _blockGap;
    }

    private void Diagram(DiagramNode node)
    {
        _ = MermaidDiagram.TryParse(node.DiagramSource, out var parsed);
        var measurer = new Func<string, (double, double)>(_painter.Measure);
        switch (parsed.Kind)
        {
            case MermaidResultKind.Flowchart when parsed.Flowchart!.Kind is GraphDiagramKind.Flowchart or GraphDiagramKind.EntityRelationship or GraphDiagramKind.State:
                var flow = parsed.Flowchart!.Layout(measurer);
                DrawDiagram(flow.Width, flow.Height, gfx => _painter.Draw(gfx, flow));
                return;
            case MermaidResultKind.Sequence:
                var sequence = parsed.Sequence!.Layout(measurer);
                DrawDiagram(sequence.Width, sequence.Height, gfx => _painter.Draw(gfx, sequence));
                return;
        }

        var notice = parsed.Kind is MermaidResultKind.Chart or MermaidResultKind.Flowchart
            ? $"{parsed.Chart?.Description ?? parsed.Flowchart!.Description} graphics are supported in the native viewer and HTML export; PDF currently shows the source."
            : parsed.Kind == MermaidResultKind.Unsupported
            ? $"Mermaid '{parsed.UnsupportedType}' diagrams cannot be drawn offline; the source is shown instead."
            : $"This Mermaid diagram could not be drawn: {parsed.ErrorMessage}" + (parsed.ErrorLine > 0 ? $" (line {parsed.ErrorLine.ToString(CultureInfo.InvariantCulture)})" : string.Empty);
        _warnings.Add(notice);
        CodeBlock(node.DiagramSource);
        _gap = 2;
        Paragraph([new TextNode(default, notice)], _size * 0.9, Muted, TableAlignment.None, italic: true);
        _gap = _blockGap;
    }

    private void DrawDiagram(double width, double height, Action<XGraphics> draw)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var scale = Math.Min(1, Math.Min(_width / width, ContentHeight * 0.9 / height));
        var drawnHeight = height * scale;
        Begin(drawnHeight);
        var gfx = _gfx!;
        var state = gfx.Save();
        gfx.TranslateTransform(_x + ((_width - (width * scale)) / 2), _y);
        gfx.ScaleTransform(scale);
        draw(gfx);
        gfx.Restore(state);
        _y += drawnHeight;
        _gap = _blockGap;
    }

    private void FrontMatter(FrontMatterNode node)
    {
        if (!_options.IncludeFrontMatter || node.Properties.Count == 0)
        {
            return;
        }

        var savedColor = _color;
        var savedX = _x;
        var savedWidth = _width;
        var bar = PushBar(Rule, _x);
        _x += 14;
        _width = Math.Max(30, _width - 14);
        _color = Muted;
        foreach (var property in node.Properties)
        {
            var atoms = new List<Atom>();
            AddText(atoms, property.Key + ":", new TextStyle(true, false, false, false, false, false, 0, 0));
            atoms.Add(new Atom(AtomKind.Space, " ", default));
            AddText(atoms, property.Value, default);
            ParagraphAtoms(atoms, _size * 0.95, Muted, TableAlignment.None);
            _gap = 1;
        }

        _color = savedColor;
        _x = savedX;
        _width = savedWidth;
        PopBar(bar);
    }

    private void DefinitionList(DefinitionListNode list)
    {
        foreach (var item in list.Items)
        {
            foreach (var term in item.Terms)
            {
                Paragraph(term, _size, _color, TableAlignment.None, bold: true);
                _gap = 1;
            }

            var savedX = _x;
            var savedWidth = _width;
            _x += _size * 1.8;
            _width = Math.Max(30, _width - (_size * 1.8));
            Blocks(item.Definitions);
            _x = savedX;
            _width = savedWidth;
        }
    }

    /// <summary>Flattens sanitized raw HTML into styled paragraphs; layout-heavy tags such as tables are reduced to their text.</summary>
    private void HtmlBlock(HtmlSafeBlockNode node)
    {
        var paragraphs = new List<List<Atom>> { new() };
        var styles = new Stack<TextStyle>();
        var style = default(TextStyle);

        void BreakParagraph()
        {
            if (paragraphs[^1].Count > 0)
            {
                paragraphs.Add([]);
            }
        }

        foreach (var piece in node.Pieces)
        {
            var tag = piece.Tag.ToLowerInvariant();
            switch (piece.Kind)
            {
                case HtmlPieceKind.Text:
                    AddText(paragraphs[^1], piece.Text, style);
                    break;
                case HtmlPieceKind.Void when tag == "br":
                    paragraphs[^1].Add(new Atom(AtomKind.LineBreak, string.Empty, style));
                    break;
                case HtmlPieceKind.Void when tag == "img":
                    var source = piece.GetAttribute("src") ?? string.Empty;
                    AddImage(paragraphs[^1], _resolver.ResolveImage(source), source, piece.GetAttribute("alt") ?? string.Empty, style);
                    break;
                case HtmlPieceKind.Void:
                    if (tag == "hr")
                    {
                        BreakParagraph();
                    }

                    break;
                case HtmlPieceKind.Open:
                    styles.Push(style);
                    if (IsBlockTag(tag))
                    {
                        BreakParagraph();
                    }

                    if (tag == "li")
                    {
                        AddText(paragraphs[^1], "• ", style);
                    }

                    style = ApplyTag(style, piece, tag);
                    break;
                case HtmlPieceKind.Close:
                    if (styles.Count > 0)
                    {
                        style = styles.Pop();
                    }

                    if (IsBlockTag(tag))
                    {
                        BreakParagraph();
                    }
                    else if (tag is "td" or "th")
                    {
                        paragraphs[^1].Add(new Atom(AtomKind.Space, " ", style));
                    }

                    break;
            }
        }

        foreach (var atoms in paragraphs)
        {
            ParagraphAtoms(atoms, _size, _color, TableAlignment.None);
            _gap = _blockGap;
        }
    }

    private static bool IsBlockTag(string tag) =>
        tag is "p" or "div" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "li" or "tr" or "ul" or "ol" or "blockquote" or "details" or "summary" or "table" or "figure" or "figcaption" or "pre";

    private TextStyle ApplyTag(TextStyle style, HtmlPiece piece, string tag) => tag switch
    {
        "b" or "strong" or "summary" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "th" => style with { Bold = true },
        "i" or "em" or "abbr" => style with { Italic = true },
        "code" or "kbd" or "pre" => style with { Mono = true },
        "s" or "del" => style with { Strike = true },
        "u" or "ins" => style with { Underline = true },
        "mark" => style with { Highlight = true },
        "sub" => style with { Script = -1 },
        "sup" => style with { Script = 1 },
        "a" => style with { Link = RegisterLink(_resolver.ResolveLink(piece.GetAttribute("href") ?? string.Empty)) },
        _ => style,
    };

    private void AddInlines(List<Atom> sink, IEnumerable<InlineNode> nodes, TextStyle style)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case TextNode text:
                    AddText(sink, text.Text, style);
                    break;
                case EmphasisNode e:
                    AddInlines(sink, e.Children, style with { Italic = true });
                    break;
                case StrongNode s:
                    AddInlines(sink, s.Children, style with { Bold = true });
                    break;
                case StrikethroughNode s:
                    AddInlines(sink, s.Children, style with { Strike = true });
                    break;
                case CodeSpanNode code:
                    AddText(sink, code.Code, style with { Mono = true });
                    break;
                case LinkNode link:
                    AddInlines(sink, link.Children, style with { Link = RegisterLink(link.Resolution) });
                    break;
                case ImageNode image:
                    AddImage(sink, image.Resolution, image.Destination, image.Alt, style);
                    break;
                case LineBreakNode lineBreak:
                    sink.Add(lineBreak.IsHard ? new Atom(AtomKind.LineBreak, string.Empty, style) : new Atom(AtomKind.Space, " ", style));
                    break;
                case FootnoteRefNode reference:
                    var anchor = new LinkTarget(null, "fn:" + reference.Number.ToString(CultureInfo.InvariantCulture));
                    _links.Add(anchor);
                    AddText(sink, "[" + reference.Number.ToString(CultureInfo.InvariantCulture) + "]", style with { Script = 1, Link = _links.Count });
                    break;
                case MathSpanNode math:
                    var rendered = MathText.Render(math.Latex);
                    AddText(sink, rendered.IsError ? rendered.Source : rendered.ToPlainUnicode(), style with { Italic = !rendered.IsError, Mono = rendered.IsError });
                    break;
                case StyledNode styled:
                    var inner = styled.Style switch
                    {
                        InlineStyle.Kbd => style with { Mono = true },
                        InlineStyle.Subscript => style with { Script = -1 },
                        InlineStyle.Superscript => style with { Script = 1 },
                        InlineStyle.Mark => style with { Highlight = true },
                        _ => style with { Underline = true },
                    };
                    AddInlines(sink, styled.Children, inner);
                    break;
            }
        }
    }

    private int RegisterLink(ResourceDecision decision)
    {
        switch (decision)
        {
            case ExternalLink external when external.Uri.Scheme is "http" or "https" or "mailto":
                _links.Add(new LinkTarget(external.Uri.AbsoluteUri, null));
                return _links.Count;
            case Anchor anchor when anchor.Fragment.TrimStart('#').Length > 0:
                _links.Add(new LinkTarget(null, anchor.Fragment.TrimStart('#')));
                return _links.Count;
            default:
                return 0;
        }
    }

    private static void AddText(List<Atom> sink, string text, TextStyle style)
    {
        var safe = PdfText.Safe(text);
        var i = 0;
        while (i < safe.Length)
        {
            var start = i;
            if (char.IsWhiteSpace(safe[i]))
            {
                while (i < safe.Length && char.IsWhiteSpace(safe[i]))
                {
                    i++;
                }

                sink.Add(new Atom(AtomKind.Space, " ", style));
            }
            else
            {
                while (i < safe.Length && !char.IsWhiteSpace(safe[i]))
                {
                    i++;
                }

                sink.Add(new Atom(AtomKind.Word, safe[start..i], style));
            }
        }
    }

    private void AddImage(List<Atom> sink, ResourceDecision decision, string destination, string alt, TextStyle style)
    {
        var label = string.IsNullOrWhiteSpace(alt) ? destination : alt;
        var image = LoadImage(decision, label);
        if (image is not null)
        {
            sink.Add(new Atom(AtomKind.Image, string.Empty, style, image));
            return;
        }

        AddText(sink, "[image: " + label + "]", style with { Italic = true });
    }

    private PdfImage? LoadImage(ResourceDecision decision, string label)
    {
        var embedded = _images.TryEmbed(decision, label);
        if (embedded is null)
        {
            return null;
        }

        if (_imageCache.TryGetValue(embedded, out var cached))
        {
            return cached;
        }

        PdfImage? loaded = null;
        if (embedded.MediaType is "image/png" or "image/jpeg")
        {
            try
            {
                var image = XImage.FromStream(new MemoryStream(embedded.Data));
                loaded = new PdfImage(image, image.PointWidth, image.PointHeight);
                if (loaded.Width <= 0 || loaded.Height <= 0)
                {
                    loaded = null;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotImplementedException or ArgumentException or IOException or FormatException or NotSupportedException)
            {
                _warnings.Add($"Image '{label}' could not be decoded: {ex.Message}");
            }
        }
        else
        {
            _warnings.Add($"Image '{label}' is {embedded.MediaType}; PDF export embeds PNG and JPEG images only.");
        }

        _imageCache[embedded] = loaded;
        return loaded;
    }

    private sealed class Bar(double x, XColor color)
    {
        public double X { get; } = x;

        public XColor Color { get; } = color;

        public double StartY { get; set; }
    }

    private sealed record LinkTarget(string? Url, string? Anchor);

    private sealed record PendingLink(int Page, XRect Rect, int Link);

    private sealed class PageLimitException : Exception
    {
    }
}
