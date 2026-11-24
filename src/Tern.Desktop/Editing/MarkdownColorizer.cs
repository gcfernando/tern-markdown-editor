using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Tern.Desktop.Themes;
using Tern.PreviewView;

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
    private sealed record FenceState(string Marker, string? Language, CodeLexicalState Lexical);
    private FenceState?[] _fenceAfter = [];
    private int _dirtyStart;
    private int _dirtyEnd;
    private int _frontMatterEnd;
    private bool _refreshQueued;
    internal int StateLinesVisited { get; private set; }
    internal Action<TextDocument, int, int>? RepaintStateChanges { get; set; }
    internal Action<TextDocument>? RefreshStateRequested { get; set; }
    public bool HybridEnabled { get; set; }
    public int CaretLine { get; set; }

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
        _refreshQueued = false;
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
        if (!_refreshQueued && RefreshStateRequested is { } refresh)
        {
            _refreshQueued = true;
            refresh(_tracked);
        }
    }

    internal void RefreshState(TextDocument document)
    {
        if (!ReferenceEquals(_tracked, document)) return;
        _refreshQueued = false;
        if (_stale) EnsureKinds(document);
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
                var before = index > 0 ? _fenceAfter[index - 1] : null;
                if (before is null || !CodeSyntaxHighlighter.IsKnownLanguage(before.Language))
                    Paint(line, 0, text.Length, ThemeManager.SyntaxCode);
                else
                    foreach (var token in CodeSyntaxHighlighter.TokenizeLine(text, before.Language, before.Lexical, out _))
                        Paint(line, token.Start, token.Length, CodeSyntaxHighlighter.ThemeKey(token.Kind));
                return;
        }

        var heading = Heading().Match(text);
        if (heading.Success)
        {
            Paint(line, 0, text.Length, ThemeManager.SyntaxHeading, bold: true);
            if (HybridEnabled && line.LineNumber != CaretLine)
            {
                ChangeLinePart(line.Offset, line.EndOffset, element =>
                    element.TextRunProperties.SetFontRenderingEmSize(
                        CurrentContext.GlobalTextRunProperties.FontRenderingEmSize * (1.7 - heading.Groups[1].Length * 0.08)));
                Paint(line, heading.Groups[1].Index, heading.Groups[1].Length, ThemeManager.Muted);
            }
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
        if (HybridEnabled && line.LineNumber != CaretLine)
        {
            if (quote.Success) Paint(line, quote.Index, quote.Length, ThemeManager.Muted);
            if (list.Success) Paint(line, list.Index, list.Length, ThemeManager.Muted);
            foreach (var expression in new[] { Strong(), Emphasis(), InlineCode() })
                foreach (Match match in expression.Matches(text))
                {
                    var width = match.Value.StartsWith("**", StringComparison.Ordinal) || match.Value.StartsWith("__", StringComparison.Ordinal)
                        || match.Value.StartsWith("~~", StringComparison.Ordinal) ? 2 : 1;
                    Paint(line, match.Index, width, ThemeManager.Muted);
                    Paint(line, match.Index + match.Length - width, width, ThemeManager.Muted);
                }
            foreach (Match match in Link().Matches(text))
                for (var i = 0; i < match.Length; i++)
                    if (match.Value[i] is '[' or ']' or '(' or ')' or '!')
                        Paint(line, match.Index + i, 1, ThemeManager.Muted);
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
        var changedStart = int.MaxValue;
        var lastVisited = i;
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
            if (previousEnd != _frontMatterEnd) { _dirtyEnd = int.MaxValue; changedStart = 0; }
        }

        FenceState? open = i == 0 ? null : _fenceAfter[i - 1];
        for (; i < _kinds.Length; i++)
        {
            StateLinesVisited++;
            lastVisited = i;
            var oldKind = _kinds[i];
            var oldFence = _fenceAfter[i];
            var kind = LineKind.Normal;
            var text = document.GetText(document.GetLineByNumber(i + 1));
            var fence = Fence().Match(text);
            if (open is null)
            {
                if (fence.Success)
                {
                    open = new(fence.Groups[1].Value, CodeSyntaxHighlighter.NormalizeLanguage(text[fence.Length..]), default);
                    kind = LineKind.FenceEdge;
                }
            }
            else if (fence.Success && fence.Groups[1].Value[0] == open.Marker[0] && fence.Groups[1].Length >= open.Marker.Length
                && string.IsNullOrWhiteSpace(text[fence.Length..]))
            {
                kind = LineKind.FenceEdge;
                open = null;
            }
            else
            {
                kind = LineKind.FenceBody;
                // Continuation is a compact value, not cached tokens or a copy of the fence.
                // Keep the existing pathological-line guard for both state and painting.
                if (text.Length <= 32_768 && CodeSyntaxHighlighter.IsKnownLanguage(open.Language))
                {
                    CodeSyntaxHighlighter.TokenizeLine(text, open.Language, open.Lexical, out var next);
                    open = open with { Lexical = next };
                }
            }
            _kinds[i] = kind;
            _fenceAfter[i] = open;
            if (oldKind != kind || oldFence != open) changedStart = Math.Min(changedStart, i);
            if (i >= _dirtyEnd && oldKind == kind && oldFence == open) break;
        }

        _stale = false;
        if (changedStart != int.MaxValue)
        {
            // Include the convergence line: its incoming state can have changed even
            // when its outgoing state is the same. Existing visual lines need repaint.
            var first = document.GetLineByNumber(changedStart + 1).Offset;
            var last = document.GetLineByNumber(Math.Min(document.LineCount, lastVisited + 1)).EndOffset;
            RepaintStateChanges?.Invoke(document, first, Math.Max(0, last - first));
        }
    }
}
