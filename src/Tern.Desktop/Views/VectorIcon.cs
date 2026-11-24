using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;

namespace Tern.Desktop.Views;

public sealed class VectorIcon : Avalonia.Controls.Shapes.Path
{
    public static readonly StyledProperty<string> KindProperty =
        AvaloniaProperty.Register<VectorIcon, string>(nameof(Kind), "Close");

    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }

    protected override Type StyleKeyOverride => typeof(Avalonia.Controls.Shapes.Path);

    public VectorIcon()
    {
        this[!WidthProperty] = GuideParts.Space(16).ToBinding();
        this[!HeightProperty] = GuideParts.Space(16).ToBinding();
        Stretch = Stretch.Uniform;
        StrokeThickness = 1.5;
        StrokeLineCap = PenLineCap.Round;
        StrokeJoin = PenLineJoin.Round;
        this[!StrokeProperty] = new Binding(nameof(Button.Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor) { AncestorType = typeof(Button) },
        };
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        BindGeometry();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty) BindGeometry();
    }

    private void BindGeometry() =>
        this[!DataProperty] = this.GetResourceObservable("Tern.Icon." + Kind).ToBinding();

}
