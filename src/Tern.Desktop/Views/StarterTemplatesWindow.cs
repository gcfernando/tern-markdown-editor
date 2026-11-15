using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Tern.App.Help;

namespace Tern.Desktop.Views;

public sealed class StarterTemplatesWindow : Window
{
    internal ListBox Catalog { get; } = new();
    internal Button UseTemplate { get; } = new() { Content = "Use template", Classes = { "accent" }, IsDefault = true };
    public StarterTemplatesWindow()
    {
        DialogParts.Setup(this, "Starter templates", 900, 600, guidance: false);
        var search = new TextBox { PlaceholderText = "Search templates…" };
        var name = GuideParts.Title("");
        var description = GuideParts.Caption("");
        var preview = new TextBox { IsReadOnly = true, AcceptsReturn = true, FontFamily = new Avalonia.Media.FontFamily("Consolas, monospace"), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        Catalog.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<StarterTemplate>((t, _) => GuideParts.Body(t?.Name ?? ""));
        AutomationProperties.SetName(Catalog, "Starter template catalog");
        AutomationProperties.SetName(preview, "Template Markdown preview");
        AutomationProperties.SetName(search, "Search templates");
        Catalog.SelectionChanged += (_, _) =>
        {
            var item = Catalog.SelectedItem as StarterTemplate;
            name.Text = item?.Name ?? "No matching template";
            description.Text = item?.Description ?? "Try a shorter search.";
            preview.Text = item?.Markdown;
            UseTemplate.IsEnabled = item is not null;
        };
        void Filter()
        {
            var selected = Catalog.SelectedItem;
            var query = search.Text?.Trim() ?? "";
            var items = StarterTemplates.All.Where(t => (t.Name + " " + t.Description).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            Catalog.ItemsSource = items;
            Catalog.SelectedItem = items.Contains(selected) ? selected : items.FirstOrDefault();
        }
        search.TextChanged += (_, _) => Filter();
        UseTemplate.Click += (_, _) => { if (Catalog.SelectedItem is StarterTemplate item) Close(item); };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close((StarterTemplate?)null);
        var left = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 12, Children = { search, Catalog } };
        Grid.SetRow(Catalog, 1);
        var right = new Grid { RowDefinitions = new("Auto,Auto,*"), RowSpacing = 12, Children = { name, description, preview } };
        Grid.SetRow(description, 1); Grid.SetRow(preview, 2);
        var body = new Grid { ColumnDefinitions = new("280,*"), ColumnSpacing = 24, Children = { left, right } };
        Grid.SetColumn(right, 1);
        body.SizeChanged += (_, _) => body.ColumnDefinitions[0].Width = new GridLength(Math.Min(280, body.Bounds.Width * .4));
        Content = GuideParts.Frame(GuideParts.Vertical(GuideParts.Title("Start with a template"), GuideParts.Caption("Choose a structure. Tern creates a new, unsaved note.")), body, GuideParts.Actions(cancel, UseTemplate));
        Filter();
    }
}
