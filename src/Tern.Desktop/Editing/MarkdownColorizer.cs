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
    private string?[] _fenceAfter = [];
    private int _dirtyStart;
    private int _dirtyEnd;
    private int _frontMatterEnd;
    internal int StateLinesVisited { get; private set; }

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
            _tracked.Changed -= OnTextChanged;
        }

        _tracked = document;
        _stale = true;
        _kinds = [];
        _fenceAfter = [];
        _dirtyStart = 0;
        _dirtyEnd = int.MaxValue;
        _frontMatterEnd = 0;
        if (document is not null)
        {
            document.Changed += OnTextChanged;
        }
    }

    private void OnTextChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_tracked is null) return;
        var first = _tracked.GetLineByOffset(Math.Min(e.Offset, _tracked.TextLength)).LineNumber - 1;
        var last = _tracked.GetLineByOffset(Math.Min(e.Offset + e.InsertionLength, _tracked.TextLength)).LineNumber - 1;
        _dirtyStart = _stale ? Math.Min(_dirtyStart, first) : first;
        _dirtyEnd = _stale ? Math.Max(_dirtyEnd, last) : last;
        _stale = true;
    }

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

        // Source text stays intact; skip cosmetic regex passes on pathological
        // single lines instead of letting them monopolize the UI thread.
        if (text.Length > 32_768) return;

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
        StateLinesVisited = 0;
        if (!_stale && _kinds.Length == document.LineCount)
        {
            return;
        }

        if (_kinds.Length != document.LineCount)
        {
            Array.Resize(ref _kinds, document.LineCount);
            Array.Resize(ref _fenceAfter, document.LineCount);
            _dirtyEnd = int.MaxValue;
        }
        var i = Math.Clamp(_dirtyStart, 0, document.LineCount - 1);
        if (i <= _frontMatterEnd)
        {
            var previousEnd = _frontMatterEnd;
            i = 0;
            _frontMatterEnd = 0;
            var close = -1;
            if (document.GetText(document.GetLineByNumber(1)).TrimEnd() == "---")
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
                _frontMatterEnd = close;
                for (; i < close; i++)
                {
                    _kinds[i] = LineKind.FrontMatter;
                    _fenceAfter[i] = null;
                }
            }
            if (previousEnd != _frontMatterEnd) _dirtyEnd = int.MaxValue;
        }

        string? open = i == 0 ? null : _fenceAfter[i - 1];
        for (; i < _kinds.Length; i++)
        {
            StateLinesVisited++;
            var oldKind = _kinds[i];
            var oldFence = _fenceAfter[i];
            var kind = LineKind.Normal;
            var text = document.GetText(document.GetLineByNumber(i + 1));
            var fence = Fence().Match(text);
            if (open is null)
            {
                if (fence.Success)
                {
                    open = fence.Groups[1].Value;
                    kind = LineKind.FenceEdge;
                }
            }
            else if (fence.Success && fence.Groups[1].Value[0] == open[0] && fence.Groups[1].Length >= open.Length)
            {
                kind = LineKind.FenceEdge;
                open = null;
            }
            else
            {
                kind = LineKind.FenceBody;
            }
            _kinds[i] = kind;
            _fenceAfter[i] = open;
            if (i >= _dirtyEnd && oldKind == kind && oldFence == open) break;
        }

        _stale = false;
    }
}
