using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Layout;
using Tern.Desktop.Themes;

namespace Tern.Desktop.Views;

/// <summary>Shared dialog hierarchy and cards; colors follow the active Windows-aware theme.</summary>
internal static class GuideParts
{
    public static IObservable<object?> Font(string name) =>
        Application.Current!.GetResourceObservable("Tern.FontSize." + name);

    public static IObservable<object?> LineHeight(string name) =>
        Application.Current!.GetResourceObservable("Tern.LineHeight." + name);

    public static IObservable<object?> Space(int size) =>
        Application.Current!.GetResourceObservable("Tern.Space." + size);

    public static IObservable<object?> Radius(string name) =>
        Application.Current!.GetResourceObservable("Tern.Radius." + name);

    public static double SpaceValue(int size) =>
        (double)Application.Current!.Resources["Tern.Space." + size]!;

    // Geometry breakpoints are evaluated at layout time; text uses live bindings.
    public static double Scaled(double size)
    {
        var app = Application.Current;
        return app?.TryGetResource("Tern.FontSize.Body", app.ActualThemeVariant, out var value) == true && value is double uiSize
            ? size * uiSize / 14 : size;
    }
    public static TextBlock Title(string text) => new() { Text = text,
        [!TextBlock.FontSizeProperty] = Font("Title").ToBinding(), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    public static TextBlock Body(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap,
        [!TextBlock.FontSizeProperty] = Font("Body").ToBinding(), [!TextBlock.LineHeightProperty] = LineHeight("Body").ToBinding() };
    public static TextBlock Caption(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap,
        [!TextBlock.FontSizeProperty] = Font("Caption").ToBinding(), [!TextBlock.LineHeightProperty] = LineHeight("Caption").ToBinding() };
    public static Expander Details(string header, Control content) => new()
    {
        Header = header, Content = content, HorizontalAlignment = HorizontalAlignment.Stretch,
        Classes = { "tern-details", "library-shortcuts" },
        HeaderTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((text, _) =>
            new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center })
    };
    public static Border Card(Control content) => new()
    {
        BorderThickness = new Thickness(1), Child = content,
        [!Border.PaddingProperty] = content.GetResourceObservable("Tern.Inset.16").ToBinding(),
        [!Border.CornerRadiusProperty] = content.GetResourceObservable("Tern.Radius.Card").ToBinding(),
        [!Border.BackgroundProperty] = content.GetResourceObservable(ThemeManager.Surface).ToBinding(),
        [!Border.BorderBrushProperty] = content.GetResourceObservable(ThemeManager.Border).ToBinding(),
    };
    public static StackPanel Vertical(params Control[] controls)
    {
        var panel = new StackPanel { [!StackPanel.SpacingProperty] = Application.Current!.GetResourceObservable("Tern.Space.12").ToBinding() };
        foreach (var control in controls) panel.Children.Add(control);
        return panel;
    }

    /// <summary>Equal-width progress items; labels wrap rather than pushing dialog actions off screen.</summary>
    public static Grid ConnectionSteps(int activeStep, params string[] labels)
    {
        var grid = new Grid { [!Grid.ColumnSpacingProperty] = Application.Current!.GetResourceObservable("Tern.Space.8").ToBinding() };
        AutomationProperties.SetName(grid, "Connection setup progress");
        for (var i = 0; i < labels.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var text = Caption($"{i + 1}  {labels[i]}");
            text.FontWeight = i == activeStep ? FontWeight.SemiBold : FontWeight.Normal;
            var item = new Border
            {
                [!Border.PaddingProperty] = Application.Current!.GetResourceObservable("Tern.Inset.8").ToBinding(),
                [!Border.CornerRadiusProperty] = Application.Current!.GetResourceObservable("Tern.Radius.Control").ToBinding(),
                BorderThickness = new Thickness(0, 0, 0, 2), Child = text,
                Classes = { "connection-step" }
            };
            if (i == activeStep) item.Classes.Add("current");
            if (i < activeStep) item.Classes.Add("complete");
            AutomationProperties.SetName(item, $"Step {i + 1}: {labels[i]}{(i == activeStep ? ", current" : i < activeStep ? ", complete" : "")}");
            Grid.SetColumn(item, i);
            grid.Children.Add(item);
        }
        return grid;
    }

    public static DialogActions Actions(Button primary, Button cancel, params Button[] secondary) => new(primary, cancel, secondary);

    public static DialogActions Actions(Button close) => new(close, close);

    public static WrapPanel ButtonRow(params Button[] buttons)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, [!WrapPanel.ItemSpacingProperty] = GuideParts.Space(8).ToBinding(), [!WrapPanel.LineSpacingProperty] = GuideParts.Space(8).ToBinding() };
        foreach (var button in buttons) { button.MinHeight = 34; button.MinWidth = 88; panel.Children.Add(button); }
        return panel;
    }

    /// <summary>A frame whose window cannot be made so short that the footer buttons are cut off.</summary>
    public static Grid FrameWithReachableFooter(Window window, Control header, Control body, Control footer, double minimumBody = 96)
    {
        var frame = Frame(header, body, footer);
        var smallest = window.MinHeight;
        void Fit()
        {
            var needed = header.Bounds.Height + footer.Bounds.Height + frame.RowSpacing * 2 + frame.Margin.Top + frame.Margin.Bottom + Scaled(minimumBody);
            if (window.Screens?.ScreenFromWindow(window) is { } screen) needed = Math.Min(needed, screen.WorkingArea.Height / screen.Scaling);
            window.MinHeight = Math.Max(smallest, needed);
            if (window.Height < window.MinHeight) window.Height = window.MinHeight;
        }
        header.SizeChanged += (_, _) => Fit();
        footer.SizeChanged += (_, _) => Fit();
        return frame;
    }

    public static Grid Frame(Control header, Control body, Control footer)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Children = { header, body, footer },
            [!Grid.MarginProperty] = Application.Current!.GetResourceObservable("Tern.Inset.24").ToBinding(),
            [!Grid.RowSpacingProperty] = Application.Current!.GetResourceObservable("Tern.Space.16").ToBinding() };
        Grid.SetRow(body, 1);
        Grid.SetRow(footer, 2);
        return grid;
    }
}
