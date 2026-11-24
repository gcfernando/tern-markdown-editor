using Avalonia;
using Avalonia.Controls;

namespace Tern.Desktop.Views;

/// <summary>Feature-guide topic for help buttons. Kept apart from <c>Tag</c> so help is never treated as a runnable command.</summary>
public static class Help
{
    public static readonly AttachedProperty<string?> FeatureProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Feature", typeof(Help));

    public static string? GetFeature(Control control) => control.GetValue(FeatureProperty);

    public static void SetFeature(Control control, string? value) => control.SetValue(FeatureProperty, value);
}
