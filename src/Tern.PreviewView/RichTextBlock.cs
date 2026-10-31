using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
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

        var hit = TextLayout.HitTestPoint(point - new Point(Padding.Left, Padding.Top));
        if (!hit.IsInside)
        {
            return -1;
        }

        var position = hit.TextPosition;
        for (var i = 0; i < _links.Count; i++)
        {
            if (position >= _links[i].Start && position < _links[i].Start + _links[i].Length)
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
