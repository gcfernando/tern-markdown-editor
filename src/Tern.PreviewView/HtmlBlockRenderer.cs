using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Tern.Rendering.Html;
using Tern.Rendering.Resources;

namespace Tern.PreviewView;

/// <summary>
/// Draws sanitised HTML with ordinary controls, using resource decisions captured by the preview builder.
/// </summary>
internal sealed class HtmlBlockRenderer(Func<RichTextBlock> newText, double baseSize, FontFamily monoFamily,
    IReadOnlyDictionary<string, ResourceDecision> links, IReadOnlyDictionary<string, ResourceDecision> images,
    Func<ResourceDecision, string, string?, ImageView> createImage)
{
    private readonly Stack<(string Tag, InlineFormat Previous)> _formats = new();
    private readonly Stack<Container> _containers = new();
    private readonly List<List<Control>> _tableRows = [];
    private readonly StringBuilder _pre = new();
    private readonly Stack<(bool Ordered, int Counter)> _lists = new();
    private InlineFormat _format;
    private RichTextBlock? _text;
    private InlineBuilder? _builder;
    private double _headingScale = 1;
    private int _headingLevel;
    private TextAlignment _alignment = TextAlignment.Left;
    private bool _inPre;
    private bool _inCell;
    private bool _pendingMarker;
    private string _marker = string.Empty;

    private sealed class Container(StackPanel panel, Border? frame)
    {
        public StackPanel Panel { get; } = panel;

        public Border? Frame { get; } = frame;
    }

    public Control Render(IReadOnlyList<HtmlPiece> pieces)
    {
        var root = new StackPanel { Spacing = 6 };
        _containers.Push(new Container(root, null));
        foreach (var piece in pieces)
        {
            switch (piece.Kind)
            {
                case HtmlPieceKind.Text:
                    AddText(piece.Text);
                    break;
                case HtmlPieceKind.Open:
                case HtmlPieceKind.Void:
                    Open(piece);
                    break;
                case HtmlPieceKind.Close:
                    Close(piece.Tag);
                    break;
            }
        }

        Flush();
        while (_containers.Count > 1)
        {
            PopContainer();
        }

        return root;
    }

    private void AddText(string text)
    {
        if (_inPre)
        {
            _pre.Append(text);
            return;
        }

        var collapsed = CollapseWhitespace(text);
        if (_builder is null)
        {
            collapsed = collapsed.TrimStart();
            if (collapsed.Length == 0)
            {
                return;
            }
        }

        Ensure().AddText(collapsed, _format);
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lastSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastSpace)
                {
                    builder.Append(' ');
                }

                lastSpace = true;
            }
            else
            {
                builder.Append(ch);
                lastSpace = false;
            }
        }

        return builder.ToString();
    }

    private InlineBuilder Ensure()
    {
        if (_builder is not null)
        {
            return _builder;
        }

        _text = newText();
        _text.TextAlignment = _alignment;
        _text.FontSize = baseSize * _headingScale;
        _text.LineHeight = _text.FontSize * (_headingLevel > 0 ? 1.25 : 1.5);
        if (_headingLevel > 0)
        {
            _text.FontWeight = FontWeight.SemiBold;
            _text.BindBrush(_text, TextBlock.ForegroundProperty, PreviewKeys.Heading);
        }

        _text.TextWrapping = _inCell ? TextWrapping.WrapWithOverflow : TextWrapping.Wrap;
        _builder = new InlineBuilder(_text, baseSize, monoFamily);
        if (_pendingMarker)
        {
            _pendingMarker = false;
            _builder.AddText(_marker, new InlineFormat());
        }

        return _builder;
    }

    /// <summary>Moves the text collected so far into the current container (or table cell).</summary>
    private void Flush()
    {
        if (_builder is null || _text is null)
        {
            return;
        }

        _builder.EndLink();
        var text = _text;
        _text = null;
        _builder = null;
        if (_inCell)
        {
            _tableRows[^1].Add(TablePanel.Cell(text));
            return;
        }

        text.Margin = new Thickness(_lists.Count * 22, 0, 0, 0);
        if (_headingLevel is 1 or 2)
        {
            var frame = new Border { Child = text, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, baseSize * 0.3) };
            PreviewTheme.Bind(frame, Border.BorderBrushProperty, PreviewKeys.Border);
            _containers.Peek().Panel.Children.Add(frame);
        }
        else _containers.Peek().Panel.Children.Add(text);
    }

    private void Open(HtmlPiece piece)
    {
        var tag = piece.Tag;
        if (_inPre)
        {
            return;
        }

        switch (tag)
        {
            case "b" or "strong":
                Push(tag, _format.With(bold: true));
                break;
            case "i" or "em" or "cite" or "var":
                Push(tag, _format.With(italic: true));
                break;
            case "u" or "ins":
                Push(tag, _format.With(underline: true));
                break;
            case "s" or "del":
                Push(tag, _format.With(strike: true));
                break;
            case "mark":
                Push(tag, _format.With(mark: true));
                break;
            case "sub":
                Push(tag, _format.With(subscript: true));
                break;
            case "sup":
                Push(tag, _format.With(superscript: true));
                break;
            case "code" or "kbd" or "samp":
                Push(tag, _format.With(code: true));
                break;
            case "small":
                Push(tag, _format.With(small: true));
                break;
            case "span" or "abbr" or "q" or "figcaption" or "thead" or "tbody" or "tfoot" or "summary":
                if (tag == "summary")
                {
                    Flush();
                    Push(tag, _format.With(bold: true));
                }
                else
                {
                    Push(tag, _format);
                }

                break;
            case "a":
                Push(tag, _format);
                BeginAnchor(piece);
                break;
            case "br":
                Ensure().AddLineBreak();
                break;
            case "img":
                AddImage(piece);
                break;
            case "hr":
                Flush();
                _containers.Peek().Panel.Children.Add(Rule());
                break;
            case "p" or "div" or "figure":
                Flush();
                _alignment = AlignmentOf(piece);
                break;
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                Flush();
                _headingLevel = tag[1] - '0';
                _headingScale = HeadingScales[_headingLevel - 1];
                _alignment = AlignmentOf(piece);
                break;
            case "ul" or "ol":
                Flush();
                _lists.Push((tag == "ol", 0));
                break;
            case "li":
                Flush();
                StartListItem();
                break;
            case "dl":
                Flush();
                break;
            case "dt":
                Flush();
                Push(tag, _format.With(bold: true));
                break;
            case "dd":
                Flush();
                _lists.Push((false, -1));
                break;
            case "blockquote":
                Flush();
                PushQuote();
                break;
            case "pre":
                Flush();
                _inPre = true;
                _pre.Clear();
                break;
            case "table":
                Flush();
                _tableRows.Clear();
                break;
            case "tr":
                Flush();
                _tableRows.Add([]);
                break;
            case "td" or "th":
                Flush();
                _inCell = true;
                if (_tableRows.Count == 0)
                {
                    _tableRows.Add([]);
                }

                Push(tag, tag == "th" ? _format.With(bold: true) : _format);
                break;
            case "details":
                Flush();
                break;
        }
    }

    private void Close(string tag)
    {
        if (_inPre)
        {
            if (tag == "pre")
            {
                _inPre = false;
                var code = _pre.ToString().Trim('\r', '\n');
                _containers.Peek().Panel.Children.Add(new CodeBlockView(code, null, baseSize, monoFamily));
            }

            return;
        }

        switch (tag)
        {
            case "p" or "div" or "figure" or "figcaption" or "summary" or "dt" or "details":
                Flush();
                Pop(tag);
                _alignment = TextAlignment.Left;
                break;
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                Flush();
                _headingLevel = 0;
                _headingScale = 1;
                _alignment = TextAlignment.Left;
                break;
            case "ul" or "ol":
                Flush();
                if (_lists.Count > 0)
                {
                    _lists.Pop();
                }

                break;
            case "dd":
                Flush();
                if (_lists.Count > 0)
                {
                    _lists.Pop();
                }

                break;
            case "li":
                Flush();
                break;
            case "blockquote":
                Flush();
                if (_containers.Count > 1)
                {
                    PopContainer();
                }

                break;
            case "td" or "th":
                Flush();
                _inCell = false;
                Pop(tag);
                break;
            case "table":
                Flush();
                FinishTable();
                break;
            case "a":
                _builder?.EndLink();
                Pop(tag);
                break;
            default:
                Pop(tag);
                break;
        }
    }

    private static readonly double[] HeadingScales = [2, 1.5, 1.25, 1, 0.875, 0.85];

    private void Push(string tag, InlineFormat format)
    {
        _formats.Push((tag, _format));
        _format = format;
    }

    private void Pop(string tag)
    {
        if (!_formats.Any(f => f.Tag == tag))
        {
            return;
        }

        while (_formats.Count > 0)
        {
            var (name, previous) = _formats.Pop();
            _format = previous;
            if (name == tag)
            {
                break;
            }
        }
    }

    private static TextAlignment AlignmentOf(HtmlPiece piece)
    {
        var align = piece.Attributes.FirstOrDefault(a => a.Name == "align").Value;
        return align?.ToLowerInvariant() switch
        {
            "center" => TextAlignment.Center,
            "right" => TextAlignment.Right,
            _ => TextAlignment.Left,
        };
    }

    private void StartListItem()
    {
        if (_lists.Count == 0)
        {
            _marker = "• ";
        }
        else
        {
            var (ordered, counter) = _lists.Pop();
            counter++;
            _lists.Push((ordered, counter));
            _marker = ordered ? counter + ". " : "• ";
        }

        _pendingMarker = true;
    }

    private void BeginAnchor(HtmlPiece piece)
    {
        var href = piece.Attributes.FirstOrDefault(a => a.Name == "href").Value ?? string.Empty;
        var decision = links.TryGetValue(href, out var resolved) ? resolved : new Blocked(BlockReason.NoBaseDirectory, href, "The link was not authorised by the preview builder.");
        var broken = decision is Blocked;
        Ensure().BeginLink(new LinkTarget(decision, href), broken);
    }

    private void AddImage(HtmlPiece piece)
    {
        var alt = piece.Attributes.FirstOrDefault(a => a.Name == "alt").Value ?? string.Empty;
        var src = piece.Attributes.FirstOrDefault(a => a.Name == "src").Value ?? string.Empty;
        var decision = images.TryGetValue(src, out var resolved) ? resolved : new Blocked(BlockReason.NoBaseDirectory, src, "The image was not authorised by the preview builder.");
        Ensure().AddControl(createImage(decision, alt, null));
    }

    private Control Rule()
    {
        var rule = new Border { Height = 1, Margin = new Thickness(0, 8) };
        PreviewTheme.Bind(rule, Border.BackgroundProperty, PreviewKeys.Border);
        return rule;
    }

    private void PushQuote()
    {
        var panel = new StackPanel { Spacing = 6 };
        var frame = new Border { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 0, 0, 0), Child = panel };
        PreviewTheme.Bind(frame, Border.BorderBrushProperty, PreviewKeys.QuoteBar);
        _containers.Push(new Container(panel, frame));
    }

    private void PopContainer()
    {
        var container = _containers.Pop();
        if (container.Frame is not null)
        {
            _containers.Peek().Panel.Children.Add(container.Frame);
        }
    }

    private void FinishTable()
    {
        var rows = _tableRows.Where(r => r.Count > 0).ToList();
        _tableRows.Clear();
        if (rows.Count == 0)
        {
            return;
        }

        var columns = rows.Max(r => r.Count);
        var panel = new TablePanel(columns, rows.Count, 1);
        foreach (var row in rows)
        {
            foreach (var cell in row)
            {
                panel.AddCell(cell);
            }

            for (var pad = row.Count; pad < columns; pad++)
            {
                panel.AddCell(TablePanel.Cell(new TextBlock()));
            }
        }

        var frame = new FitScroller(panel);
        _containers.Peek().Panel.Children.Add(frame);
    }
}
