using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Layout;
using Tern.Desktop.Themes;

namespace Tern.Desktop.Views;

/// <summary>Shared dialog hierarchy and cards; colors follow the active Windows-aware theme.</summary>
internal static class GuideParts
{
    // Dialogs open after settings are applied. Keep explicit readable sizes in step
    // with the same UI-scale resource used by application controls.
    public static double Scaled(double size)
    {
        var app = Application.Current;
        return app?.TryGetResource("Tern.UiFontSize", app.ActualThemeVariant, out var value) == true && value is double uiSize
            ? size * uiSize / 14 : size;
    }
    public static TextBlock Title(string text) => new() { Text = text, FontSize = Scaled(22), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    public static TextBlock Body(string text) => new() { Text = text, FontSize = Scaled(14), TextWrapping = TextWrapping.Wrap, LineHeight = Scaled(21) };
    public static TextBlock Caption(string text) => new() { Text = text, FontSize = Scaled(12), TextWrapping = TextWrapping.Wrap, LineHeight = Scaled(18) };
    public static Border Card(Control content) => new()
    {
        Padding = new Thickness(16), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = content,
        [!Border.BackgroundProperty] = content.GetResourceObservable(ThemeManager.Surface).ToBinding(),
        [!Border.BorderBrushProperty] = content.GetResourceObservable(ThemeManager.Border).ToBinding(),
    };
    public static StackPanel Vertical(params Control[] controls)
    {
        var panel = new StackPanel { Spacing = 10 };
        foreach (var control in controls) panel.Children.Add(control);
        return panel;
    }

    public static WrapPanel Actions(params Button[] buttons)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, ItemSpacing = 8, LineSpacing = 8 };
        foreach (var button in buttons) { button.MinHeight = 34; button.MinWidth = 88; panel.Children.Add(button); }
        return panel;
    }

    public static Grid Frame(Control header, Control body, Control footer)
    {
        var grid = new Grid { Margin = new Thickness(24), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 20, Children = { header, body, footer } };
        Grid.SetRow(body, 1);
        Grid.SetRow(footer, 2);
        return grid;
    }
}
