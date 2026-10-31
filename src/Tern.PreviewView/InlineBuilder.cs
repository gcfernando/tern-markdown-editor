using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Tern.PreviewView;

/// <summary>Formatting flags that apply to the next run appended to an <see cref="InlineBuilder"/>.</summary>
internal readonly record struct InlineFormat(
    bool Bold = false,
    bool Italic = false,
    bool Strike = false,
    bool Underline = false,
    bool Superscript = false,
    bool Subscript = false,
    bool Mark = false,
    bool Code = false,
    bool Small = false)
{
    public static InlineFormat Default => new();

    public InlineFormat With(bool? bold = null, bool? italic = null, bool? strike = null, bool? underline = null, bool? superscript = null, bool? subscript = null, bool? mark = null, bool? code = null, bool? small = null) =>
        new(bold ?? Bold, italic ?? Italic, strike ?? Strike, underline ?? Underline, superscript ?? Superscript, subscript ?? Subscript, mark ?? Mark, code ?? Code, small ?? Small);
}

/// <summary>
/// Appends runs to a <see cref="RichTextBlock"/> while tracking the character position, so link ranges line up with the
/// text layout that is later hit-tested. Soft breaks are one space, line breaks one character and an embedded control one.
/// </summary>
internal sealed class InlineBuilder(RichTextBlock block, double baseSize, FontFamily monoFamily)
{
    private readonly List<Inline> _linkRuns = [];
    private LinkTarget? _link;
    private bool _linkBroken;
    private int _linkStart;

    public int Position { get; private set; }

    public bool HasContent => Position > 0;

    public void AddText(string text, InlineFormat format)
    {
        if (text.Length == 0)
        {
            return;
        }

        var run = new Run(text);
        Style(run, format);
        Append(run, text.Length, format.Mark && !format.Code);
    }

    public void AddLineBreak()
    {
        Append(new LineBreak(), 1);
    }

    public void AddControl(Control control)
    {
        // Fixed text line height clips inline images/badges taller than the font. Let
        // the text layout include the embedded control's measured ascent/descent.
        block.ClearValue(TextBlock.LineHeightProperty);
        var container = new InlineUIContainer(control);
        if (_link is not null)
        {
            control.IsHitTestVisible = false;
        }

        Append(container, 1);
    }

    public void BeginLink(LinkTarget target, bool broken)
    {
        EndLink();
        _link = target;
        _linkBroken = broken;
        _linkStart = Position;
    }

    public void EndLink()
    {
        if (_link is not null && Position > _linkStart)
        {
            block.AddLink(new LinkSpan(_linkStart, Position - _linkStart, _link, [.. _linkRuns]));
        }

        _link = null;
        _linkRuns.Clear();
    }

    private void Append(Inline inline, int length, bool marked = false)
    {
        if (_link is not null && inline is Run run)
        {
            var key = marked
                ? (_linkBroken ? PreviewKeys.MarkLinkBroken : PreviewKeys.MarkLink)
                : (_linkBroken ? PreviewKeys.LinkBroken : PreviewKeys.Link);
            block.BindBrush(run, TextElement.ForegroundProperty, key);
            run.TextDecorations = TextDecorations.Underline;
            _linkRuns.Add(run);
        }

        block.Inlines!.Add(inline);
        Position += length;
    }

    private void Style(Run run, InlineFormat format)
    {
        if (format.Bold)
        {
            run.FontWeight = FontWeight.SemiBold;
        }

        if (format.Italic)
        {
            run.FontStyle = FontStyle.Italic;
        }

        var decorations = new TextDecorationCollection();
        if (format.Strike)
        {
            decorations.AddRange(TextDecorations.Strikethrough);
        }

        if (format.Underline)
        {
            decorations.AddRange(TextDecorations.Underline);
        }

        if (decorations.Count > 0)
        {
            run.TextDecorations = decorations;
        }

        if (format.Superscript || format.Subscript)
        {
            run.BaselineAlignment = format.Superscript ? BaselineAlignment.Superscript : BaselineAlignment.Subscript;
            run.FontSize = baseSize * 0.75;
        }
        else if (format.Small)
        {
            run.FontSize = baseSize * 0.85;
        }

        if (format.Mark)
        {
            block.BindBrush(run, TextElement.BackgroundProperty, PreviewKeys.Mark);
        }

        if (format.Code)
        {
            run.FontFamily = monoFamily;
            run.FontSize = baseSize * 0.9;
            block.BindBrush(run, TextElement.BackgroundProperty, PreviewKeys.CodeBackground);
            block.BindBrush(run, TextElement.ForegroundProperty, PreviewKeys.CodeForeground);
        }
    }
}
