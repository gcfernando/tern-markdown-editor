using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Rendering.Preview;
using Tern.Rendering.Resources;

namespace Tern.PreviewView;

/// <summary>
/// Renders a <see cref="PreviewDocument"/> with native Avalonia controls. Colours come from the <c>Tern.Preview.*</c>
/// resource keys (see <see cref="PreviewKeys"/>), so the host swaps themes by changing resources. The control never opens
/// anything itself: links are reported through <see cref="LinkActivated"/> and the host decides what happens.
/// </summary>
public sealed class PreviewView : Decorator
{
    private const double PagePadding = 24;
    private const double FullWidthPadding = 16;

    public static readonly StyledProperty<PreviewDocument?> DocumentProperty =
        AvaloniaProperty.Register<PreviewView, PreviewDocument?>(nameof(Document));

    public static readonly StyledProperty<double> ReadingWidthProperty =
        AvaloniaProperty.Register<PreviewView, double>(nameof(ReadingWidth), 820, validate: value => value >= 200);

    public static readonly StyledProperty<bool> UseFullWidthProperty =
        AvaloniaProperty.Register<PreviewView, bool>(nameof(UseFullWidth), true);

    public static readonly StyledProperty<bool> ReduceMotionProperty =
        AvaloniaProperty.Register<PreviewView, bool>(nameof(ReduceMotion));

    public static readonly StyledProperty<double> FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner<PreviewView>(new StyledPropertyMetadata<double>(16));

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<PreviewView>();

    private readonly ScrollViewer _scroller;
    private readonly Border _page;
    private readonly BlockPanel _panel;
    private readonly Border _banner;
    private readonly TextBlock _bannerText;
    private readonly SearchHighlightLayer _searchHighlight;

    private BlockFactory _factory;
    private Func<string, CancellationToken, Task<byte[]?>>? _remoteImageLoader;

    /// <summary>Host-provided bounded image retrieval/cache. The preview itself never opens a network connection.</summary>
    public Func<string, CancellationToken, Task<byte[]?>>? RemoteImageLoader
    {
        get => _remoteImageLoader;
        set
        {
            if (_remoteImageLoader == value) return;
            _remoteImageLoader = value;
            Rebuild();
        }
    }
    private BlockSlot? _anchorSlot;
    private int _anchorIndex;
    private double _anchorFraction;
    private int? _anchorRelativeLine;
    private double? _expectedOffset;
    private double _lastOffset;
    private int _reportedLine = 1;
    private bool _programmatic;
    private bool _correctionQueued;
    private bool _draggingScrollbar;
    private double _dragProgress;
    private Control? _flashed;
    private DispatcherTimer? _flashTimer;
    private bool _rebuildOnAttach;
    private Task _imageCleanup = Task.CompletedTask;

    internal Task ImageCleanupCompletion => _imageCleanup;

    static PreviewView()
    {
        FocusableProperty.OverrideDefaultValue<PreviewView>(true);
        AutomationProperties.NameProperty.OverrideDefaultValue<PreviewView>("Markdown preview");
    }

    public PreviewView()
    {
        _factory = new BlockFactory(FontSize, OnLinkTarget, _remoteImageLoader);
        _panel = new BlockPanel();
        _panel.SetFactory(slot => _factory.CreateHost(slot));
        _panel.HeightsChanged += OnHeightsChanged;

        _page = new Border
        {
            Child = _panel,
        };
        UpdatePageWidth();

        _scroller = new ScrollViewer
        {
            AllowAutoHide = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _page,
        };
        SmoothScrolling.SetIsEnabled(this, true);
        SmoothScrolling.SetDuration(this, ReduceMotion ? TimeSpan.Zero : SmoothScrolling.StandardDuration);
        SmoothScrolling.SetSuppressMotion(this, ReduceMotion);
        _scroller.ScrollChanged += OnScrollChanged;
        _scroller.TemplateApplied += (_, _) => ConfigureReadingScrollbar();

        _bannerText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        PreviewTheme.Bind(_bannerText, TextBlock.ForegroundProperty, PreviewKeys.WarningForeground);
        _banner = new Border { Padding = new Thickness(16, 8), IsVisible = false, Child = _bannerText, BorderThickness = new Thickness(0, 0, 0, 1) };
        PreviewTheme.Bind(_banner, Border.BackgroundProperty, PreviewKeys.AlertWarning);
        PreviewTheme.Bind(_banner, Border.BorderBrushProperty, PreviewKeys.Border);
        AutomationProperties.SetName(_banner, "Preview truncated");

        var layout = new DockPanel();
        DockPanel.SetDock(_banner, Dock.Top);
        layout.Children.Add(_banner);
        _searchHighlight = new SearchHighlightLayer(this) { IsHitTestVisible = false, ClipToBounds = true };
        var reading = new Grid();
        reading.Children.Add(_scroller);
        reading.Children.Add(_searchHighlight);
        layout.Children.Add(reading);
        LayoutUpdated += (_, _) => { if (SearchMatch is not null) _searchHighlight.InvalidateVisual(); };

        var root = new Border { Child = layout };
        PreviewTheme.Bind(root, Border.BackgroundProperty, PreviewKeys.Background);
        Child = root;

        AddHandler(DoubleTappedEvent, OnDoubleTapped, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, (_, _) => { _programmatic = false; _expectedOffset = null; }, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(ScrollGestureEvent, (_, _) => { _programmatic = false; _expectedOffset = null; }, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnPointerReleasedForLine, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>The document to show. Unchanged top-level blocks keep their controls when it is replaced.</summary>
    public PreviewDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    /// <summary>Maximum text-column width in device-independent pixels when UseFullWidth is false.</summary>
    public double ReadingWidth
    {
        get => GetValue(ReadingWidthProperty);
        set => SetValue(ReadingWidthProperty, value);
    }

    /// <summary>Fills the available pane with ordinary-sized text. Disable for the centered ReadingWidth column.</summary>
    public bool UseFullWidth
    {
        get => GetValue(UseFullWidthProperty);
        set => SetValue(UseFullWidthProperty, value);
    }

    /// <summary>Base text size; headings and code scale from it.</summary>
    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    /// <summary>When true, scrolling to an anchor jumps without the brief highlight.</summary>
    public bool ReduceMotion
    {
        get => GetValue(ReduceMotionProperty);
        set => SetValue(ReduceMotionProperty, value);
    }

    /// <summary>While true, <see cref="TopSourceLineChanged"/> is not raised (the host is driving the scroll position).</summary>
    public bool SuppressNotifications { get; set; }

    /// <summary>The source line (1-based) at the top of the viewport.</summary>
    public int TopSourceLine => TopLineAt(_scroller.Offset.Y);

    /// <summary>A link that is not an in-document anchor was activated; the host decides what to do with it.</summary>
    public event EventHandler<LinkActivatedEventArgs>? LinkActivated;

    /// <summary>An in-document anchor link was activated. Set <see cref="AnchorRequestedEventArgs.Handled"/> to take over; otherwise the view scrolls to it.</summary>
    public event EventHandler<AnchorRequestedEventArgs>? AnchorRequested;

    /// <summary>A block was double-clicked (or Ctrl-clicked); the host should reveal this source line in the editor.</summary>
    public event EventHandler<SourceLineEventArgs>? SourceLineActivated;

    /// <summary>The user scrolled and the source line at the top of the viewport changed.</summary>
    public event EventHandler<SourceLineEventArgs>? TopSourceLineChanged;

    internal ScrollViewer Scroller => _scroller;

    private bool _canToggleTasks;
    /// <summary>The host permits task interaction for the currently rendered source.</summary>
    public bool CanToggleTasks
    {
        get => _canToggleTasks;
        set { if (_canToggleTasks == value) return; _canToggleTasks = value; Rebuild(); }
    }
    public event EventHandler<TaskActivatedEventArgs>? TaskActivated;
    public event EventHandler<ImageActionEventArgs>? ImageActionRequested;

    internal void RequestImageAction(Control source, string alt, ResourceDecision resolution, ImageAction action, Action retry)
    {
        if (Document is not { } document) return;
        var relative = -1;
        for (Visual? current = source; current is not null; current = current.GetVisualParent())
        {
            if (relative < 0 && current is Control control && control.GetValue(BlockFactory.RelativeLineProperty) is var line and >= 0) relative = line;
            if (current is BlockHost host)
            {
                ImageActionRequested?.Invoke(this, new ImageActionEventArgs(document, host.Slot.Node.Source.StartLine + Math.Max(0, relative), alt, resolution, action, retry));
                return;
            }
        }
    }

    internal void RequestTaskActivation(Control source, bool wasChecked)
    {
        if (!CanToggleTasks || Document is not { } document) return;
        var relative = -1;
        for (Visual? current = source; current is not null; current = current.GetVisualParent())
        {
            if (relative < 0 && current is Control control && control.GetValue(BlockFactory.RelativeLineProperty) is var line and >= 0) relative = line;
            if (current is BlockHost host)
            {
                TaskActivated?.Invoke(this, new TaskActivatedEventArgs(document, host.Slot.Node.Source.StartLine + Math.Max(0, relative), wasChecked));
                return;
            }
        }
    }

    /// <summary>Moves one reading page without requiring a precise scrollbar drag.</summary>
    public void ScrollByPage(int direction) => _scroller.Offset = new Vector(0,
        Math.Max(0, _scroller.Offset.Y + direction * Math.Max(48, _scroller.Viewport.Height - 48)));

    /// <summary>Returns to the beginning of the rendered document.</summary>
    public void ScrollToTop()
    {
        // A pending height correction must not restore the previous reading
        // anchor after an explicit Top command.
        _anchorSlot = null;
        _anchorRelativeLine = null;
        MarkProgrammatic();
        _expectedOffset = 0;
        _scroller.Offset = default;
    }

    private void ConfigureReadingScrollbar()
    {
        var sharedAppearance = this.TryFindResource("Tern.Scroll.SharedAppearance", out var shared) && shared is true;
        foreach (var bar in _scroller.GetVisualDescendants().OfType<ScrollBar>().Where(bar =>
            bar.Orientation == Orientation.Vertical && bar.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault() == _scroller))
        {
            bar.Classes.Add("reading-scrollbar");
            bar.AllowAutoHide = false;
            if (!sharedAppearance)
            {
                bar.Width = bar.MinWidth = 20;
                PreviewTheme.Bind(bar, ScrollBar.BackgroundProperty, PreviewKeys.CodeBackground);
                PreviewTheme.Bind(bar, ScrollBar.ForegroundProperty, PreviewKeys.Foreground);
            }
            AutomationProperties.SetName(bar, "Reading scrollbar");
            ToolTip.SetTip(bar, "Drag the grip to scroll. Click the track for a page, or use Page Up / Page Down.");
            bar.ApplyTemplate();
            foreach (var thumb in bar.GetVisualDescendants().OfType<Thumb>())
            {
                if (!sharedAppearance) { thumb.MinHeight = 40; thumb.MinWidth = 20; }
                thumb.DragStarted -= OnScrollbarDragStarted;
                thumb.DragCompleted -= OnScrollbarDragCompleted;
                thumb.DragStarted += OnScrollbarDragStarted;
                thumb.DragCompleted += OnScrollbarDragCompleted;
                thumb.ApplyTemplate();
                foreach (var border in thumb.GetVisualDescendants().OfType<Border>().Where(_ => !sharedAppearance))
                {
                    border.Opacity = 1;
                    border.CornerRadius = new CornerRadius(5);
                    PreviewTheme.Bind(border, Border.BackgroundProperty, PreviewKeys.Muted);
                }
            }
        }
    }

    internal BlockPanel Panel => _panel;

    private void OnScrollbarDragStarted(object? sender, VectorEventArgs e)
    {
        _draggingScrollbar = true;
        _dragProgress = _scroller.Offset.Y / Math.Max(1, _scroller.Extent.Height - _scroller.Viewport.Height);
        _programmatic = false;
        _expectedOffset = null;
    }

    private void OnScrollbarDragCompleted(object? sender, VectorEventArgs e)
    {
        _draggingScrollbar = false;
        CaptureAnchor(_scroller.Offset.Y);
    }

    internal Border Banner => _banner;

    internal TextBlock BannerText => _bannerText;

    internal sealed record SearchHighlight(int Start, int Length, string Source);
    internal SearchHighlight? SearchMatch { get; private set; }
    internal IReadOnlyList<SearchHighlight> SearchMatches { get; private set; } = [];

    /// <summary>Persistently highlights the selected source match in rendered text, including virtualized blocks.</summary>
    public void HighlightSearchMatch(int start, int length, string source)
    {
        HighlightSearchMatches(length > 0 ? [(start, length)] : [], 0, source);
    }

    /// <summary>Source-ordered matches for one current document, with a distinct active result.</summary>
    public void HighlightSearchMatches(IReadOnlyList<(int Start, int Length)> matches, int activeIndex, string source)
    {
        SearchMatches = matches.Select(m => new SearchHighlight(m.Start, m.Length, source)).ToArray();
        SearchMatch = activeIndex >= 0 && activeIndex < SearchMatches.Count ? SearchMatches[activeIndex] : null;
        _searchHighlight.InvalidateVisual();
    }

    /// <summary>Realizes the source block, then reveals the exact rendered glyphs, including nested scrollers.</summary>
    public bool RevealSearchMatch(int line)
    {
        if (SearchMatch is null) return false;
        ScrollToSourceLine(line);
        UpdateLayout();
        foreach (var text in this.GetVisualDescendants().OfType<RichTextBlock>())
        {
            if (!text.IsEffectivelyVisible) continue;
            var range = text.SearchRanges(activeOnly: true).FirstOrDefault();
            if (range.Length == 0) continue;
            var rect = text.TextLayout.HitTestTextRange(range.Start, range.Length).FirstOrDefault();
            if (rect.Width <= 0) continue;
            var target = rect.Translate(new Vector(text.Padding.Left, text.Padding.Top));
            text.BringIntoView(target.Inflate(12));
            UpdateLayout();
            // Keep later height corrections anchored at the revealed position.
            CaptureAnchor(_scroller.Offset.Y);
            _searchHighlight.InvalidateVisual();
            return true;
        }
        return false;
    }

    /// <summary>Scrolls so the block holding <paramref name="line"/> (1-based) is at the top of the viewport.</summary>
    public void ScrollToSourceLine(int line)
    {
        if (Document is null || _panel.Slots.Count == 0)
        {
            return;
        }

        var index = Math.Max(0, Document.FindBlockIndexAtLine(line));
        var slot = _panel.Slots[index];
        var node = slot.Node;
        // Realize the root at its beginning first. A line fraction is unrelated to
        // visual height when one list item contains a large image or code block.
        SetAnchor(slot, index, 0);
        _anchorRelativeLine = Math.Clamp(line - node.Source.StartLine, 0, Math.Max(0, node.Source.EndLine - node.Source.StartLine));
        ApplyAnchor();
        ExpandDetailsForLine(slot, line);
        UpdateLayout();
        ApplyAnchor();
    }


    /// <summary>Scrolls to a heading or safe explicit HTML anchor, with or without leading '#'.</summary>
    public bool ScrollToAnchor(string anchor)
    {
        if (Document is null || string.IsNullOrEmpty(anchor))
        {
            return false;
        }

        var wanted = Normalize(anchor);
        var line = Document.FindAnchorSourceLine(wanted)
            ?? Document.Outline.FirstOrDefault(o => string.Equals(o.Anchor, wanted, StringComparison.OrdinalIgnoreCase))?.Line;
        if (line is null)
        {
            return false;
        }

        ScrollToSourceLine(line.Value);
        if (!ReduceMotion)
        {
            Flash(Document.FindBlockIndexAtLine(line.Value));
        }

        return true;
    }

    private static void ExpandDetailsForLine(BlockSlot slot, int line)
    {
        if (slot.Host is not { } host) return;
        var detailsLines = Descendants(slot.Node).OfType<DetailsNode>()
            .Where(d => d.Source.ContainsLine(line)).Select(d => d.Source.StartLine).ToHashSet();
        for (var depth = 0; depth < 64 && detailsLines.Count > 0; depth++)
        {
            var expanded = false;
            foreach (var details in host.GetVisualDescendants().OfType<DetailsView>().ToList())
            {
                var sourceLine = slot.Node.Source.StartLine + details.GetValue(BlockFactory.RelativeLineProperty);
                if (!detailsLines.Remove(sourceLine)) continue;
                if (!details.IsExpanded) { details.IsExpanded = true; expanded = true; }
            }
            if (!expanded) break;
        }
    }

    private static IEnumerable<BlockNode> Descendants(BlockNode block)
    {
        yield return block;
        IEnumerable<BlockNode> children = block switch
        {
            DetailsNode d => d.Blocks,
            BlockQuoteNode q => q.Blocks,
            AlertNode a => a.Blocks,
            ListNode l => l.Items.SelectMany(i => i.Blocks),
            FootnoteSectionNode f => f.Items.SelectMany(i => i.Blocks),
            DefinitionListNode d => d.Items.SelectMany(i => i.Definitions),
            _ => [],
        };
        foreach (var child in children)
            foreach (var nested in Descendants(child)) yield return nested;
    }

    private static Control? FindSourceControl(BlockHost host, int relativeLine)
    {
        Control? target = null;
        var best = -1;
        foreach (var control in host.GetVisualDescendants().OfType<Control>())
        {
            var sourceLine = control.GetValue(BlockFactory.RelativeLineProperty);
            if (sourceLine < best || sourceLine < 0 || sourceLine > relativeLine) continue;
            best = sourceLine;
            target = control;
        }
        return target;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ReduceMotionProperty)
        {
            SmoothScrolling.SetDuration(this, ReduceMotion ? TimeSpan.Zero : SmoothScrolling.StandardDuration);
            SmoothScrolling.SetSuppressMotion(this, ReduceMotion);
        }
        if (change.Property == DocumentProperty)
        {
            ApplyDocument(change.GetOldValue<PreviewDocument?>(), change.GetNewValue<PreviewDocument?>());
        }
        else if (change.Property == ReadingWidthProperty || change.Property == UseFullWidthProperty)
        {
            SmoothScrolling.CancelWithin(this);
            UpdatePageWidth();
        }
        else if (change.Property == FontSizeProperty || change.Property == FontFamilyProperty)
        {
            Rebuild();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        var page = Math.Max(48, _scroller.Viewport.Height - 48);
        var target = e.Key switch
        {
            Key.Down => _scroller.Offset.Y + 48,
            Key.Up => _scroller.Offset.Y - 48,
            Key.PageDown or Key.Space => _scroller.Offset.Y + page,
            Key.PageUp => _scroller.Offset.Y - page,
            Key.Home => 0,
            Key.End => double.MaxValue,
            _ => double.NaN,
        };

        if (!double.IsNaN(target))
        {
            _scroller.Offset = new Vector(_scroller.Offset.X, Math.Max(0, target));
            e.Handled = true;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.Handled)
        {
            Focus();
        }
    }

    private static string Normalize(string anchor)
    {
        var text = anchor.TrimStart('#');
        try
        {
            return Uri.UnescapeDataString(text);
        }
        catch (UriFormatException)
        {
            return text;
        }
    }

    private void Rebuild()
    {
        SmoothScrolling.CancelWithin(this);
        ReleaseImages();
        _factory = new BlockFactory(FontSize, OnLinkTarget, _remoteImageLoader);
        var document = Document;
        if (document is null)
        {
            return;
        }

        var line = TopSourceLine;
        var slots = document.Blocks.Select(NewSlot).ToList();
        _panel.SetSlots(slots);
        RestoreByLine(line);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _rebuildOnAttach = true;
        // Inline controls can be orphaned by a text-layout/font rebuild without a reliable
        // final child detach. The preview owns all image lifetimes across those trees.
        ReleaseImages();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_rebuildOnAttach)
        {
            _rebuildOnAttach = false;
            Rebuild();
        }
    }

    private void ReleaseImages()
    {
        var cleanup = _factory.ReleaseImages();
        _imageCleanup = _imageCleanup.IsCompletedSuccessfully ? cleanup : Task.WhenAll(_imageCleanup, cleanup);
    }

    private BlockSlot NewSlot(BlockNode block) => new(block, NodeContent.Hash(block), HeightEstimator.Estimate(block, EstimateWidth(), FontSize));

    private void UpdatePageWidth()
    {
        var padding = UseFullWidth ? FullWidthPadding : PagePadding;
        _page.Padding = new Thickness(padding, 20, padding, 48);
        _page.MaxWidth = UseFullWidth ? double.PositiveInfinity : ReadingWidth + (padding * 2);
        _page.HorizontalAlignment = UseFullWidth ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        // Width changes remeasure the same block hosts. Existing source anchors and
        // OnHeightsChanged correction preserve position without rebuilding images or shrinking text.
    }

    private double EstimateWidth()
    {
        var padding = UseFullWidth ? FullWidthPadding : PagePadding;
        var available = _scroller.Viewport.Width > 0 ? _scroller.Viewport.Width
            : Bounds.Width > 0 ? Bounds.Width : ReadingWidth + (padding * 2);
        var contentWidth = Math.Max(1, available - (padding * 2));
        return UseFullWidth ? contentWidth : Math.Min(ReadingWidth, contentWidth);
    }

    private void ApplyDocument(PreviewDocument? oldDocument, PreviewDocument? document)
    {
        SmoothScrolling.CancelWithin(this);
        var anchorSlot = _anchorSlot;
        var anchorLine = oldDocument is null ? 1 : TopSourceLine;
        var fraction = _anchorFraction;
        var relativeLine = _anchorRelativeLine;

        if (document is null)
        {
            ReleaseImages();
            _panel.SetSlots([]);
            _banner.IsVisible = false;
            _anchorSlot = null;
            return;
        }

        var slots = ReuseSlots(document);
        _panel.SetSlots(slots);
        UpdateBanner(document);

        var index = anchorSlot is null ? -1 : slots.IndexOf(anchorSlot);
        if (index >= 0)
        {
            SetAnchor(slots[index], index, fraction);
            _anchorRelativeLine = relativeLine;
            ApplyAnchor();
        }
        else
        {
            RestoreByLine(anchorLine);
        }
    }

    private List<BlockSlot> ReuseSlots(PreviewDocument document)
    {
        // Blocks are matched on content and size, ignoring where they sit, so an edit only rebuilds the blocks it touched.
        var pool = new Dictionary<(ulong Hash, int Span), Queue<BlockSlot>>();
        foreach (var old in _panel.Slots)
        {
            var key = (old.Hash, old.Node.Source.EndLine - old.Node.Source.StartLine);
            if (!pool.TryGetValue(key, out var queue))
            {
                pool[key] = queue = new Queue<BlockSlot>();
            }

            queue.Enqueue(old);
        }

        var slots = new List<BlockSlot>(document.Blocks.Count);
        foreach (var block in document.Blocks)
        {
            var hash = NodeContent.Hash(block);
            if (pool.TryGetValue((hash, block.Source.EndLine - block.Source.StartLine), out var queue) && queue.Count > 0)
            {
                var reused = queue.Dequeue();
                reused.Node = block;
                slots.Add(reused);
            }
            else
            {
                slots.Add(new BlockSlot(block, hash, HeightEstimator.Estimate(block, EstimateWidth(), FontSize)));
            }
        }

        return slots;
    }

    private void RestoreByLine(int line)
    {
        if (Document is null || _panel.Slots.Count == 0)
        {
            return;
        }

        var index = Math.Max(0, Document.FindBlockIndexAtLine(line));
        var node = _panel.Slots[index].Node;
        var span = Math.Max(1, node.Source.EndLine - node.Source.StartLine + 1);
        var fraction = node is FootnoteSectionNode ? 0 : Math.Clamp((double)(line - node.Source.StartLine) / span, 0, 0.999);
        SetAnchor(_panel.Slots[index], index, fraction);
        ApplyAnchor();
    }

    private void UpdateBanner(PreviewDocument document)
    {
        _banner.IsVisible = document.Truncated;
        if (document.Truncated)
        {
            _bannerText.Text = $"Preview truncated: {Describe(document.Truncation)}. Edit continues to work.";
        }
    }

    private static string Describe(TruncationReason reason) => reason switch
    {
        TruncationReason.SourceTooLarge => "the document is too large",
        TruncationReason.NodeBudget => "it has too many elements",
        TruncationReason.TimeBudget => "rendering took too long",
        TruncationReason.DepthLimit => "content is nested too deeply",
        _ => "the preview was limited",
    };

    private void SetAnchor(BlockSlot slot, int index, double fraction)
    {
        _anchorSlot = slot;
        _anchorIndex = index;
        _anchorFraction = fraction;
        _anchorRelativeLine = null;
    }

    private double OffsetForAnchor()
    {
        if (_anchorRelativeLine is { } line && _anchorSlot?.Host is { } host
            && FindSourceControl(host, line)?.TranslatePoint(default, host) is { } point)
        {
            if (_anchorIndex == 0 && line == 0) return 0;
            return _page.Padding.Top + _panel.TopOf(_anchorIndex) + host.Margin.Top + point.Y;
        }
        return _anchorIndex == 0 && _anchorFraction == 0 ? 0
            : _page.Padding.Top + _panel.TopOf(_anchorIndex) + (_anchorFraction * _panel.HeightOf(_anchorIndex));
    }

    private void ApplyAnchor()
    {
        if (_anchorSlot is null || _anchorIndex >= _panel.Slots.Count)
        {
            return;
        }

        MarkProgrammatic();
        var target = OffsetForAnchor();
        _panel.SetViewport(target - _page.Padding.Top, _scroller.Viewport.Height);
        _expectedOffset = target;
        _scroller.Offset = new Vector(0, target);
        if (this.IsAttachedToVisualTree())
        {
            UpdateLayout();
        }

        // The real extent may only be known after the layout pass above; clamp to what the scroller allows.
        if (Math.Abs(_scroller.Offset.Y - target) > 0.5 && _scroller.Extent.Height - _scroller.Viewport.Height >= target)
        {
            _scroller.Offset = new Vector(0, target);
        }
    }

    private void MarkProgrammatic(bool cancelWheel = true)
    {
        if (cancelWheel) SmoothScrolling.CancelWithin(this);
        if (_programmatic)
        {
            return;
        }

        _programmatic = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _programmatic = false;
                _expectedOffset = null;
            },
            DispatcherPriority.Background);
    }

    private void OnHeightsChanged(object? sender, EventArgs e)
    {
        if (_correctionQueued)
        {
            return;
        }

        _correctionQueued = true;
        Dispatcher.UIThread.Post(CorrectAnchor, DispatcherPriority.Render);
    }

    // Estimated heights turn into real ones as blocks are measured; keep the anchored block where it was on screen.
    private void CorrectAnchor()
    {
        _correctionQueued = false;
        if (_draggingScrollbar)
        {
            // A thumb controls a fraction of the whole document. Newly measured
            // blocks must not pull the grip back to an earlier source anchor.
            _scroller.Offset = new Vector(0, _dragProgress * Math.Max(0, _scroller.Extent.Height - _scroller.Viewport.Height));
            CaptureAnchor(_scroller.Offset.Y);
            return;
        }
        if (_anchorSlot is null || _anchorIndex >= _panel.Slots.Count || _panel.Slots[_anchorIndex] != _anchorSlot)
        {
            return;
        }

        var target = OffsetForAnchor();
        if (Math.Abs(target - _scroller.Offset.Y) > 0.5)
        {
            MarkProgrammatic(cancelWheel: false);
            _expectedOffset = target;
            SmoothScrolling.CorrectAnchor(_scroller, new Vector(_scroller.Offset.X, target));
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var offset = _scroller.Offset.Y;
        _panel.SetViewport(offset - _page.Padding.Top, _scroller.Viewport.Height);
        var moved = Math.Abs(offset - _lastOffset) > 0.5;
        _lastOffset = offset;
        if (_draggingScrollbar && e.ExtentDelta == default && e.ViewportDelta == default)
            _dragProgress = Math.Clamp(offset / Math.Max(1, _scroller.Extent.Height - _scroller.Viewport.Height), 0, 1);
        if (!moved)
        {
            return;
        }

        var ours = _expectedOffset is { } expected && Math.Abs(offset - expected) < 1.5;
        if (ours)
        {
            _expectedOffset = null;
        }
        else if (!_programmatic && (e.ExtentDelta == default && e.ViewportDelta == default))
        {
            CaptureAnchor(offset);
        }

        var line = TopLineAt(offset);
        if (ours || _programmatic)
        {
            _reportedLine = line;
            return;
        }

        if (line != _reportedLine)
        {
            _reportedLine = line;
            if (!SuppressNotifications)
            {
                TopSourceLineChanged?.Invoke(this, new SourceLineEventArgs(line));
            }
        }
    }

    private void CaptureAnchor(double offset)
    {
        if (_panel.Slots.Count == 0)
        {
            return;
        }

        var y = Math.Max(0, offset - _page.Padding.Top);
        var index = _panel.IndexAtOffset(y);
        var height = _panel.HeightOf(index);
        SetAnchor(_panel.Slots[index], index, height > 0 ? Math.Clamp((y - _panel.TopOf(index)) / height, 0, 1) : 0);
    }

    private int TopLineAt(double offset)
    {
        // Leading blank lines/front matter may have no rendered block. The
        // beginning of the document still corresponds to source line one.
        if (offset <= 0.5) return 1;
        if (_panel.Slots.Count == 0)
        {
            return 1;
        }

        var y = Math.Max(0, offset - _page.Padding.Top);
        var index = _panel.IndexAtOffset(y);
        var node = _panel.Slots[index].Node;
        if (node is FootnoteSectionNode)
        {
            return node.Source.StartLine;
        }

        var height = _panel.HeightOf(index);
        var fraction = height > 0 ? Math.Clamp((y - _panel.TopOf(index)) / height, 0, 1) : 0;
        var span = node.Source.EndLine - node.Source.StartLine + 1;
        return Math.Min(node.Source.EndLine, node.Source.StartLine + (int)Math.Floor((fraction * span) + 1e-6));
    }

    private void OnLinkTarget(LinkTarget target)
    {
        if (target.FootnoteNumber > 0)
        {
            ScrollToFootnote(target.FootnoteNumber);
            return;
        }

        switch (target.Resolution)
        {
            case null:
                return;
            case Anchor anchor:
                {
                    var args = new AnchorRequestedEventArgs(anchor.Fragment);
                    AnchorRequested?.Invoke(this, args);
                    if (!args.Handled)
                    {
                        ScrollToAnchor(anchor.Fragment);
                    }

                    return;
                }

            default:
                LinkActivated?.Invoke(this, new LinkActivatedEventArgs(target.Resolution, target.Destination));
                return;
        }
    }

    private void ScrollToFootnote(int number)
    {
        foreach (var block in Document?.Blocks ?? [])
        {
            if (block is FootnoteSectionNode section && section.Items.FirstOrDefault(i => i.Number == number) is { } item)
            {
                ScrollToSourceLine(item.Source.StartLine);
                return;
            }
        }
    }

    private void Flash(int blockIndex, IBrush? highlight = null)
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_panel.RealizedSlotAt(blockIndex)?.Host is not { } host)
                {
                    return;
                }

                _flashTimer?.Stop();
                _flashed?.ClearValue(Border.BackgroundProperty);
                host.Background = highlight ?? PreviewTheme.Resolve(this, PreviewKeys.Selection);
                _flashed = host;
                var timer = _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    host.ClearValue(Border.BackgroundProperty);
                    _flashed = null;
                    _flashTimer = null;
                };
                timer.Start();
            },
            DispatcherPriority.Background);
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e) => RaiseSourceLine(e.Source);

    private void OnPointerReleasedForLine(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left && e.KeyModifiers == KeyModifiers.Control)
        {
            RaiseSourceLine(e.Source);
        }
    }

    internal void RequestSourceEdit(Control source) => RaiseSourceLine(source);

    private void RaiseSourceLine(object? source)
    {
        if (source is not Visual visual)
        {
            return;
        }

        var relative = -1;
        for (var current = visual; current is not null; current = current.GetVisualParent())
        {
            if (relative < 0 && current is Control control && control.GetValue(BlockFactory.RelativeLineProperty) is var value and >= 0)
            {
                relative = value;
            }

            if (current is BlockHost host)
            {
                SourceLineActivated?.Invoke(this, new SourceLineEventArgs(host.Slot.Node.Source.StartLine + Math.Max(0, relative)));
                return;
            }
        }
    }
}
