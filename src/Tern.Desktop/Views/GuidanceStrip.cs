using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.Desktop.Themes;

namespace Tern.Desktop.Views;

/// <summary>Persistent contextual help, shared by the main window and dialogs.</summary>
internal sealed class GuidanceStrip : Border
{
    private readonly TextBlock _message = GuideParts.Caption("");
    public string Message { get => _message.Text ?? ""; set => _message.Text = value; }
    public GuidanceStrip() : this("Open a note or choose File → New document to start writing.") { }

    public GuidanceStrip(string message, string topic = "")
    {
        Name = "NextStepGuidance";
        Message = message;
        Padding = new Thickness(0, 4, 0, 0);
        BorderThickness = new Thickness(0, 1, 0, 0);
        this[!BorderBrushProperty] = this.GetResourceObservable(ThemeManager.Border).ToBinding();
        _message.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(_message, "Next step guidance");
        var more = new Button { Content = "Learn more", Classes = { "flat" }, FontSize = GuideParts.Scaled(12), MinHeight = 24, Padding = new Thickness(8, 2) };
        more.IsVisible = topic != "Learn Tern";
        AutomationProperties.SetName(more, "Learn more about this screen");
        more.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not Window owner) return;
            var current = owner;
            while (current is not MainWindow && current.Owner is Window parent) current = parent;
            if (current is MainWindow main) await main.ShowContextGuidanceAsync(topic, owner);
            else new HelpWindow(CommandRegistry.CreateDefault(), initialQuestion: topic.Length > 0 ? topic : null).Show(owner);
        };
        var grid = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12, Children = { _message, more } };
        Grid.SetColumn(more, 1); Child = grid;
    }

    internal static void Install(Window window)
    {
        if (window.Content is not Control content) return;
        if (content is HelpTutorView) return;
        var title = window.Title ?? "Tern";
        var message = title switch
        {
            "Learn Tern" => "Choose a goal in Guides, or ask a Tern question in Ask & learn.",
            "About Tern" => "Read the version and notices. Close returns to your note.",
            "Print preview" => "Review this snapshot, then open the printable PDF to inspect page breaks and print.",
            _ when title.Contains("connection", StringComparison.OrdinalIgnoreCase) => "Choose a service, configure its details, then validate and save the connection.",
            _ when title.Contains("Settings", StringComparison.OrdinalIgnoreCase) => "Choose a category, review your changes, then use Apply or Done. Cancel keeps your earlier settings.",
            _ when title.Contains("image", StringComparison.OrdinalIgnoreCase) => "Choose an image, review its description and destination, then confirm the insertion.",
            _ when title.Contains("link", StringComparison.OrdinalIgnoreCase) => "Review the link label and exact destination before confirming.",
            _ => "Review this screen, then choose an action. Learn more opens installed help."
        };
        var strip = new GuidanceStrip(message, title) { Margin = new Thickness(12, 0, 12, 8) };
        if (window is HelpWindow && content is TabControl sections)
        {
            strip.IsVisible = sections.SelectedIndex == 0;
            sections.SelectionChanged += (_, _) => strip.IsVisible = sections.SelectedIndex == 0;
        }
        if (content is Grid grid)
        {
            if (grid.RowDefinitions.Count == 0) grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            Grid.SetRow(strip, grid.RowDefinitions.Count);
            Grid.SetColumnSpan(strip, Math.Max(1, grid.ColumnDefinitions.Count));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); grid.Children.Add(strip);
        }
        else
        {
            window.Content = null;
            var frame = new Grid { RowDefinitions = new("*,Auto"), Children = { content, strip } };
            Grid.SetRow(strip, 1); window.Content = frame;
        }
    }
}
