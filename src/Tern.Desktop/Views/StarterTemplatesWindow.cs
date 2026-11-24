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
    public StarterTemplatesWindow(IReadOnlyList<StarterTemplate>? templates = null, IReadOnlyList<string>? warnings = null,
        string? userTemplateDirectory = null)
    {
        var catalog = templates ?? StarterTemplates.All;
        DialogParts.Setup(this, "Starter templates", 900, 600, DialogGuidance.Templates, guidance: false);
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
            var items = catalog.Where(t => (t.Name + " " + t.Description).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            Catalog.ItemsSource = items;
            Catalog.SelectedItem = items.Contains(selected) ? selected : items.FirstOrDefault();
        }
        search.TextChanged += (_, _) => Filter();
        UseTemplate.Click += (_, _) => { if (Catalog.SelectedItem is StarterTemplate item) Close(item); };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close((StarterTemplate?)null);
        var left = new Grid { RowDefinitions = new("Auto,*"), [!Grid.RowSpacingProperty] = GuideParts.Space(12).ToBinding(), Children = { search, Catalog } };
        Grid.SetRow(Catalog, 1);
        var right = new Grid { RowDefinitions = new("Auto,Auto,*"), [!Grid.RowSpacingProperty] = GuideParts.Space(12).ToBinding(), Children = { name, description, preview } };
        Grid.SetRow(description, 1); Grid.SetRow(preview, 2);
        var body = new Grid { ColumnDefinitions = new("280,*"), [!Grid.ColumnSpacingProperty] = GuideParts.Space(24).ToBinding(), Children = { left, right } };
        Grid.SetColumn(right, 1);
        body.SizeChanged += (_, _) => body.ColumnDefinitions[0].Width = new GridLength(Math.Min(280, body.Bounds.Width * .4));
        var source = userTemplateDirectory is null ? "the Settings templates folder" : userTemplateDirectory;
        var header = GuideParts.Vertical(GuideParts.Title("Start with a template"),
            GuideParts.Caption("Creates a new, unsaved note. {{date}}, {{time}}, {{title}} and YYYY-MM-DD are filled when used."),
            GuideParts.Caption("Add .md templates in " + source + " or .tern\\templates in the active workspace."));
        if (warnings is { Count: > 0 })
            header.Children.Add(GuideParts.Caption($"Template loading reported {warnings.Count} issue(s); see Activity for details."));
        Content = GuideParts.Frame(header, body, GuideParts.Actions(UseTemplate, cancel));
        Filter();
    }
}
