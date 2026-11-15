using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Tern.Rendering.Resources;

namespace Tern.PreviewView;

/// <summary>What a link inside a text block points at: a resolved destination or a footnote.</summary>
internal sealed record LinkTarget(ResourceDecision? Resolution, string Destination, int FootnoteNumber = 0, string? Title = null);

/// <summary>A clickable character range of a <see cref="RichTextBlock"/> and the inline runs that draw it.</summary>
internal sealed record LinkSpan(int Start, int Length, LinkTarget Target, IReadOnlyList<Avalonia.Controls.Documents.Inline> Runs);

internal sealed class LinkInvokedEventArgs(LinkTarget target) : EventArgs
{
    public LinkTarget Target { get; } = target;
}

/// <summary>
/// Selectable text with clickable, keyboard-reachable links. Links are plain coloured runs; clicks are mapped to them by
/// hit-testing the text layout, which keeps selection across the whole paragraph working.
/// </summary>
internal sealed class RichTextBlock : SelectableTextBlock
{
    private readonly List<(int SourceStart, int SourceLength, int Start, int Length)> _searchSources = [];

    internal string DisplayText => Inlines is { Count: > 0 } ? ReadText(Inlines) : Text ?? string.Empty;

    private static string ReadText(IEnumerable<Inline> inlines) => string.Concat(inlines.Select(inline => inline switch
    {
        Run run => run.Text,
        LineBreak => "\n",
        Span span => ReadText(span.Inlines),
        InlineUIContainer => "\uFFFC",
        _ => string.Empty,
    }));

    internal void AddSearchSource(int sourceStart, int sourceLength, int start, int length) =>
        _searchSources.Add((sourceStart, sourceLength, start, length));

    private readonly Dictionary<(int Start, int Length, int DisplayStart), SearchTextProjection> _searchProjections = [];
    private string? _projectionSource;
    private int _projectionBase;

    internal IReadOnlyList<(int Start, int Length)> SearchRanges(bool activeOnly = false)
    {
        var ancestors = this.GetVisualAncestors().ToArray();
        if (ancestors.OfType<PreviewView>().FirstOrDefault() is not { } preview
            || ancestors.OfType<BlockHost>().FirstOrDefault() is not { } host) return [];
        var matches = activeOnly ? (preview.SearchMatch is { } active ? new[] { active } : []) : preview.SearchMatches;
        if (matches.Count == 0) return [];
        var source = matches[0].Source;
        var sourceBase = host.Slot.Node.Source.StartOffset;
        if (!ReferenceEquals(source, _projectionSource) || sourceBase != _projectionBase)
        {
            _searchProjections.Clear();
            _projectionSource = source;
            _projectionBase = sourceBase;
        }
        var text = DisplayText;
        var ranges = new List<(int, int)>();
        foreach (var span in _searchSources)
        {
            var sourceStart = sourceBase + span.SourceStart;
            if (sourceStart < 0 || sourceStart + span.SourceLength > source.Length
                || span.Start + span.Length > text.Length) continue;
            // Source-ordered non-overlapping matches permit a binary lookup per leaf.
            var low = 0;
            var high = matches.Count;
            while (low < high)
            {
                var mid = low + (high - low) / 2;
                if (matches[mid].Start + matches[mid].Length <= sourceStart) low = mid + 1;
                else high = mid;
            }
            for (var index = low; index < matches.Count; index++)
            {
                var match = matches[index];
                if (match.Start >= sourceStart + span.SourceLength) break;
                var start = Math.Max(sourceStart, match.Start);
                var end = Math.Min(sourceStart + span.SourceLength, match.Start + match.Length);
                if (end <= start) continue;
                var key = (sourceStart, span.SourceLength, span.Start);
                if (!_searchProjections.TryGetValue(key, out var projection))
                {
                    projection = new SearchTextProjection(source.Substring(sourceStart, span.SourceLength), text.Substring(span.Start, span.Length));
                    _searchProjections[key] = projection;
                }
                var range = projection.Project(start - sourceStart, end - sourceStart);
                if (range.Length > 0) ranges.Add((span.Start + range.Start, range.Length));
            }
        }
        return ranges;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new RichTextAutomationPeer(this);

    private sealed class RichTextAutomationPeer(RichTextBlock owner) : TextBlockAutomationPeer(owner)
    {
        protected override string? GetNameCore()
        {
            var explicitName = AutomationProperties.GetName(owner);
            if (!string.IsNullOrWhiteSpace(explicitName)) return explicitName;
            return owner.Inlines is { Count: > 0 } inlines ? Read(inlines) : owner.Text;
        }

        private static string Read(IEnumerable<Inline> inlines) => string.Concat(inlines.Select(inline => inline switch
        {
            Run run => run.Text,
            LineBreak => "\n",
            Span span => Read(span.Inlines),
            InlineUIContainer { Child: ImageView image } => image.Alt.Length > 0 ? image.Alt : "Image",
            InlineUIContainer { Child: { } child } => ControlAutomationPeer.CreatePeerForElement(child).GetName(),
            _ => string.Empty,
        }));
    }

    private readonly record struct ThemedValue(AvaloniaObject Target, AvaloniaProperty Property, string Key);

    private readonly List<ThemedValue> _themed = [];
    private readonly List<LinkSpan> _links = [];
    private int _focusedLink = -1;
    private Point _pressPoint;
    private bool _pressedOnLink;
    private IResourceHost? _resourceHost;

    public RichTextBlock()
    {
        TextWrapping = TextWrapping.Wrap;
        // Linked inline images defer input to this block. Give their containing
        // text surface a hit-testable background even when it has no text runs.
        Background = Brushes.Transparent;
        BindBrush(this, ForegroundProperty, PreviewKeys.Foreground);
        BindBrush(this, SelectionBrushProperty, PreviewKeys.Selection);
    }

    public event EventHandler<LinkInvokedEventArgs>? LinkInvoked;

    public IReadOnlyList<LinkSpan> Links => _links;

    public int FocusedLinkIndex => _focusedLink;

    /// <summary>Binds a brush property of this block or one of its runs to a theme key; it is refreshed when the theme changes.</summary>
    public void BindBrush(AvaloniaObject target, AvaloniaProperty property, string key)
    {
        _themed.Add(new ThemedValue(target, property, key));
        target.SetValue(property, PreviewTheme.Resolve(this, key));
    }

    public void AddLink(LinkSpan span)
    {
        _links.Add(span);
        Focusable = true;
    }

    /// <summary>Index of the link under <paramref name="point"/> (relative to this control), or -1.</summary>
    public int LinkIndexAt(Point point)
    {
        if (_links.Count == 0)
        {
            return -1;
        }

        var textPoint = point - new Point(Padding.Left, Padding.Top);
        // HitTestPoint returns a caret position: the right half of a final glyph (or
        // an embedded image) can point just past the link. Use the painted ranges
        // instead, including each line of a wrapped link and inline image bounds.
        for (var i = 0; i < _links.Count; i++)
        {
            if (TextLayout.HitTestTextRange(_links[i].Start, _links[i].Length).Any(rect => rect.Contains(textPoint)))
            {
                return i;
            }
        }

        return -1;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _resourceHost = this;
        _resourceHost.ResourcesChanged += OnResourcesChanged;
        ActualThemeVariantChanged += OnThemeVariantChanged;
        RefreshTheme();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_resourceHost is not null)
        {
            _resourceHost.ResourcesChanged -= OnResourcesChanged;
            _resourceHost = null;
        }

        ActualThemeVariantChanged -= OnThemeVariantChanged;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetPosition(this);
        _pressPoint = point;
        _pressedOnLink = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && LinkIndexAt(point) >= 0;
        base.OnPointerPressed(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressedOnLink || e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        _pressedOnLink = false;
        var point = e.GetPosition(this);
        // A drag that started on a link is a text selection, not a click.
        if (Math.Abs(point.X - _pressPoint.X) > 4 || Math.Abs(point.Y - _pressPoint.Y) > 4)
        {
            return;
        }

        var index = LinkIndexAt(point);
        if (index >= 0)
        {
            Invoke(index);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var index = LinkIndexAt(e.GetPosition(this));
        Cursor = index >= 0 ? new Cursor(StandardCursorType.Hand) : null;
        ToolTip.SetTip(this, index >= 0 ? _links[index].Target.Title ?? _links[index].Target.Destination : null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Cursor = null;
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (_links.Count > 0 && e.NavigationMethod == NavigationMethod.Tab)
        {
            SetFocusedLink(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? _links.Count - 1 : 0);
        }
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        SetFocusedLink(-1);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_links.Count > 0 && _focusedLink >= 0)
        {
            if (e.Key == Key.Tab && (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None)
            {
                var next = _focusedLink + (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                if (next >= 0 && next < _links.Count)
                {
                    SetFocusedLink(next);
                    e.Handled = true;
                    return;
                }
            }
            else if (e.Key is Key.Enter or Key.Space && e.KeyModifiers == KeyModifiers.None)
            {
                Invoke(_focusedLink);
                e.Handled = true;
                return;
            }
        }

        base.OnKeyDown(e);
    }

    private void Invoke(int index) => LinkInvoked?.Invoke(this, new LinkInvokedEventArgs(_links[index].Target));

    private void SetFocusedLink(int index)
    {
        if (index == _focusedLink)
        {
            return;
        }

        var previous = _focusedLink;
        _focusedLink = index;
        if (previous >= 0)
        {
            foreach (var run in _links[previous].Runs)
            {
                run.ClearValue(Avalonia.Controls.Documents.Inline.BackgroundProperty);
            }
        }

        RefreshTheme();
    }

    private void OnResourcesChanged(object? sender, ResourcesChangedEventArgs e) => RefreshTheme();

    private void OnThemeVariantChanged(object? sender, EventArgs e) => RefreshTheme();

    private void RefreshTheme()
    {
        foreach (var (target, property, key) in _themed)
        {
            target.SetValue(property, PreviewTheme.Resolve(this, key));
        }

        if (_focusedLink >= 0)
        {
            foreach (var run in _links[_focusedLink].Runs)
            {
                run.SetValue(Inline.BackgroundProperty, PreviewTheme.Resolve(this, PreviewKeys.FocusBackground));
                run.SetValue(TextElement.ForegroundProperty, PreviewTheme.Resolve(this, PreviewKeys.FocusForeground));
            }
        }
    }
}
