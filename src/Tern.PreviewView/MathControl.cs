using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Tern.Rendering.Mathematics;

namespace Tern.PreviewView;

/// <summary>Draws a parsed formula natively with <see cref="FormattedText"/> and geometry.</summary>
internal sealed class MathControl : Control
{
    public static readonly StyledProperty<double> MathFontSizeProperty =
        AvaloniaProperty.Register<MathControl, double>(nameof(MathFontSize), 18);

    private MathResult? _result;
    private MathBox? _box;
    private IResourceHost? _resourceHost;

    static MathControl()
    {
        AffectsMeasure<MathControl>(MathFontSizeProperty);
        AffectsRender<MathControl>(MathFontSizeProperty);
    }

    public double MathFontSize
    {
        get => GetValue(MathFontSizeProperty);
        set => SetValue(MathFontSizeProperty, value);
    }

    /// <summary>The formula to draw. A result with <see cref="MathResult.IsError"/> draws nothing.</summary>
    public MathResult? Result
    {
        get => _result;
        set
        {
            _result = value;
            _box = null;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>Size of the laid out formula without padding.</summary>
    public Size ContentSize
    {
        get
        {
            var box = EnsureBox();
            return box is null ? default : new Size(box.Width, box.Height);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var box = EnsureBox();
        return box is null ? default : new Size(Math.Ceiling(box.Width) + 4, Math.Ceiling(box.Height) + 4);
    }

    public override void Render(DrawingContext context)
    {
        var box = EnsureBox();
        if (box is null)
        {
            return;
        }

        var paint = new MathPaint(PreviewTheme.Resolve(this, PreviewKeys.Foreground));
        box.Draw(context, new Point(2, 2 + box.Ascent), paint);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _resourceHost = this;
        _resourceHost.ResourcesChanged += OnResourcesChanged;
        ActualThemeVariantChanged += OnThemeChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_resourceHost is not null)
        {
            _resourceHost.ResourcesChanged -= OnResourcesChanged;
            _resourceHost = null;
        }

        ActualThemeVariantChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private MathBox? EnsureBox()
    {
        if (_box is null && _result is { IsError: false, Root: { } root })
        {
            _box = MathLayout.Build(root, MathFontSize);
        }

        return _box;
    }

    private void OnResourcesChanged(object? sender, ResourcesChangedEventArgs e) => InvalidateVisual();

    private void OnThemeChanged(object? sender, EventArgs e) => InvalidateVisual();
}
