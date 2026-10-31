using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Tern.Desktop.Themes;

namespace Tern.Desktop.Editing;

/// <summary>Colours Markdown source as it is typed: headings, emphasis, code, links, lists, quotes, front matter and fences.</summary>
public sealed partial class MarkdownColorizer : DocumentColorizingTransformer
{
    private enum LineKind : byte
    {
        Normal,
        FrontMatter,
        FenceEdge,
        FenceBody,
    }

    private LineKind[] _kinds = [];
    private TextDocument? _tracked;
    private bool _stale = true;

    [GeneratedRegex(@"^ {0,3}(#{1,6})(\s.*)?$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s*(?:>\s?)+")]
    private static partial Regex QuotePrefix();

    [GeneratedRegex(@"^\s*(?:[-*+]|\d{1,9}[.)])\s(?:\[[ xX]\]\s)?")]
    private static partial Regex ListPrefix();

    [GeneratedRegex(@"`[^`\n]+`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"\*\*[^*\n]+\*\*|__[^_\n]+__|~~[^~\n]+~~")]
    private static partial Regex Strong();

    [GeneratedRegex(@"(?<![*\w])\*[^*\s][^*\n]*\*(?!\*)|(?<![_\w])_[^_\s][^_\n]*_(?![_\w])")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"!?\[[^\]\n]*\]\([^)\n]*\)|\[\[[^\]\n]+\]\]|<https?://[^>\s]+>|https?://[^\s)>]+")]
    private static partial Regex Link();

    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^ {0,3}([-*_])(?:\s*\1){2,}\s*$")]
    private static partial Regex Rule();

    public void Attach(TextDocument? document)
    {
        if (_tracked is not null)
        {
            _tracked.TextChanged -= OnTextChanged;
        }

        _tracked = document;
        _stale = true;
        if (document is not null)
        {
            document.TextChanged += OnTextChanged;
        }
    }

    private void OnTextChanged(object? sender, EventArgs e) => _stale = true;

    protected override void ColorizeLine(DocumentLine line)
    {
        if (CurrentContext.Document != _tracked)
        {
            return;
        }

        EnsureKinds(CurrentContext.Document);
        var index = line.LineNumber - 1;
        var kind = index < _kinds.Length ? _kinds[index] : LineKind.Normal;
        var text = CurrentContext.Document.GetText(line);

        switch (kind)
        {
            case LineKind.FrontMatter:
                Paint(line, 0, text.Length, ThemeManager.SyntaxMeta);
                return;
            case LineKind.FenceEdge:
                Paint(line, 0, text.Length, ThemeManager.SyntaxMarker);
                return;
            case LineKind.FenceBody:
                Paint(line, 0, text.Length, ThemeManager.SyntaxCode);
                return;
        }

        var heading = Heading().Match(text);
        if (heading.Success)
        {
            Paint(line, 0, text.Length, ThemeManager.SyntaxHeading, bold: true);
            return;
        }

        if (Rule().IsMatch(text))
        {
            Paint(line, 0, text.Length, ThemeManager.SyntaxMarker);
            return;
        }

        var quote = QuotePrefix().Match(text);
        if (quote.Success)
        {
            Paint(line, 0, text.Length, ThemeManager.SyntaxQuote, italic: true);
        }

        var list = ListPrefix().Match(text);
        if (list.Success)
        {
            Paint(line, list.Index, list.Length, ThemeManager.Accent, bold: true);
        }

        foreach (Match m in Strong().Matches(text))
        {
            Paint(line, m.Index, m.Length, ThemeManager.Text, bold: true);
        }

        foreach (Match m in Emphasis().Matches(text))
        {
            Paint(line, m.Index, m.Length, ThemeManager.SyntaxEmphasis, italic: true);
        }

        foreach (Match m in InlineCode().Matches(text))
        {
            Paint(line, m.Index, m.Length, ThemeManager.SyntaxCode);
        }

        foreach (Match m in Link().Matches(text))
        {
            Paint(line, m.Index, m.Length, ThemeManager.SyntaxLink);
        }
    }

    private void Paint(DocumentLine line, int start, int length, string brushKey, bool bold = false, bool italic = false)
    {
        if (length <= 0)
        {
            return;
        }

        var brush = Application.Current?.TryGetResource(brushKey, Application.Current.ActualThemeVariant, out var value) == true
            ? value as IBrush
            : null;
        ChangeLinePart(line.Offset + start, line.Offset + start + length, element =>
        {
            if (brush is not null)
            {
                element.TextRunProperties.SetForegroundBrush(brush);
            }

            if (bold || italic)
            {
                var face = element.TextRunProperties.Typeface;
                element.TextRunProperties.SetTypeface(new Typeface(
                    face.FontFamily,
                    italic ? FontStyle.Italic : face.Style,
                    bold ? FontWeight.Bold : face.Weight,
                    face.Stretch));
            }
        });
    }

    private void EnsureKinds(TextDocument document)
    {
        if (!_stale && _kinds.Length == document.LineCount)
        {
            return;
        }

        var kinds = new LineKind[document.LineCount];
        var i = 0;
        if (document.LineCount > 0 && document.GetText(document.GetLineByNumber(1)).TrimEnd() == "---")
        {
            var close = -1;
            for (var n = 2; n <= document.LineCount; n++)
            {
                var t = document.GetText(document.GetLineByNumber(n)).TrimEnd();
                if (t is "---" or "...")
                {
                    close = n;
                    break;
                }
            }

            if (close > 0)
            {
                for (; i < close; i++)
                {
                    kinds[i] = LineKind.FrontMatter;
                }
            }
        }

        string? open = null;
        for (; i < kinds.Length; i++)
        {
            var text = document.GetText(document.GetLineByNumber(i + 1));
            var fence = Fence().Match(text);
            if (open is null)
            {
                if (fence.Success)
                {
                    open = fence.Groups[1].Value;
                    kinds[i] = LineKind.FenceEdge;
                }
            }
            else if (fence.Success && fence.Groups[1].Value[0] == open[0] && fence.Groups[1].Length >= open.Length)
            {
                kinds[i] = LineKind.FenceEdge;
                open = null;
            }
            else
            {
                kinds[i] = LineKind.FenceBody;
            }
        }

        _kinds = kinds;
        _stale = false;
    }
}
