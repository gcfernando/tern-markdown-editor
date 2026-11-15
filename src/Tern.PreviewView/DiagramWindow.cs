using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace Tern.PreviewView;

internal sealed class DiagramWindow : Window
{
    public DiagramWindow(DiagramControl original)
    {
        Title = original.Description; Width = 960; Height = 700; MinWidth = 360; MinHeight = 300;
        var diagram = new DiagramControl(original) { FitToWidth = true };
        AutomationProperties.SetName(diagram, original.Description);
        var zoom = 1.0;
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal };
        void Action(string text, System.Action action)
        {
            var button = new Button { Content = text, Margin = new Thickness(4) };
            button.Click += (_, _) => action(); toolbar.Children.Add(button);
        }
        Action("Zoom in", () => diagram.SetZoom(zoom = Math.Min(4, (diagram.Zoom ?? zoom) * 1.25)));
        Action("Zoom out", () => diagram.SetZoom(zoom = Math.Max(.2, (diagram.Zoom ?? zoom) / 1.25)));
        Action("Fit width", () => { diagram.FitToWidth = true; diagram.SetZoom(null); });
        Action("Reset", () => diagram.SetZoom(zoom = 1));
        Action("Close", Close);
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Margin = new Thickness(12) };
        grid.Children.Add(toolbar);
        var description = new TextBlock { Text = original.Description, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(4) };
        Grid.SetRow(description, 1); grid.Children.Add(description);
        var scroller = new ScrollViewer { Content = new FitScroller(diagram, showDiagramTools: false),
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroller, 2); grid.Children.Add(scroller);
        Content = grid;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Opened += (_, _) => toolbar.Children.OfType<Button>().First().Focus();
    }
}
