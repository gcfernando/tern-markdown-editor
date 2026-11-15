using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Tern.Markdown;

namespace Tern.Desktop.Views;

public sealed record LinkInput(string Label, string Destination);

public sealed class InsertLinkWindow : Window
{
    internal TextBox LabelInput { get; } = new();
    internal TextBox DestinationInput { get; } = new();
    internal Button InsertButton { get; } = new() { Content = "Insert link", IsDefault = true };
    private readonly TextBox _details = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true };
    private readonly string? _source;
    private readonly HashSet<string> _headings;

    public InsertLinkWindow(string label, string? source, IReadOnlyList<HeadingInfo> headings)
    {
        _source = source;
        _headings = headings.Select(h => h.Anchor).ToHashSet(StringComparer.OrdinalIgnoreCase);
        DialogParts.Setup(this, "Insert link", 600, 440);
        LabelInput.Text = label;
        var browse = new Button { Content = "Choose local file…" };
        browse.IsEnabled = source is not null;
        if (source is null) ToolTip.SetTip(browse, "Save this note first to create a relative local-file link. Web and heading links work in unsaved notes.");
        browse.Click += async (_, _) =>
        {
            if (!StorageProvider.CanOpen) { _details.Text = "The file picker is unavailable. Enter a path instead."; return; }
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose link target", AllowMultiple = false });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
            DestinationInput.Text = source is null ? new Uri(path).AbsoluteUri
                : string.Join('/', Path.GetRelativePath(Path.GetDirectoryName(source)!, path).Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
        };
        var heading = new ComboBox { PlaceholderText = "Link to a heading in this note", ItemsSource = headings.Select(h => new ComboBoxItem { Content = h.Text, Tag = "#" + h.Anchor }).ToArray(), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(heading, "Document heading suggestions");
        heading.SelectionChanged += (_, _) => { if (heading.SelectedItem is ComboBoxItem { Tag: string anchor }) DestinationInput.Text = anchor; };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        InsertButton.Click += (_, _) => { if (InsertButton.IsEnabled) Close(new LinkInput(LabelInput.Text?.Trim() is { Length: > 0 } value ? value : DestinationInput.Text!.Trim(), DestinationInput.Text!.Trim())); };
        Content = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 12, Children =
        {
            DialogParts.Labeled("Link text", LabelInput), DialogParts.Labeled("Destination", DestinationInput),
            GuideParts.Actions(browse), heading, _details,
            new TextBlock { Text = "Inserts one undoable link. Opening the link is a separate action.", TextWrapping = TextWrapping.Wrap },
            GuideParts.Actions(cancel, InsertButton)
        } };
        DestinationInput.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) UpdateTarget(); };
        Opened += (_, _) => DestinationInput.Focus();
        UpdateTarget();
    }

    private void UpdateTarget()
    {
        var destination = DestinationInput.Text?.Trim() ?? "";
        InsertButton.IsEnabled = destination.Length > 0 && !destination.Any(char.IsControl);
        if (!InsertButton.IsEnabled) { _details.Text = "Enter a destination, choose a local file or select a heading."; return; }
        if (destination.StartsWith('#')) { _details.Text = (_headings.Contains(Uri.UnescapeDataString(destination[1..])) ? "Internal heading: " : "Heading not found in this note: ") + destination; return; }
        if (Uri.TryCreate(destination, UriKind.Absolute, out var uri) && !uri.IsFile)
        { _details.Text = "External link (not fetched): " + uri.AbsoluteUri; return; }
        if (_source is null && !Path.IsPathRooted(destination)) { _details.Text = "Not yet checkable: save this note to resolve relative paths.\n" + destination; return; }
        try
        {
            var path = Uri.TryCreate(destination, UriKind.Absolute, out uri) && uri.IsFile ? uri.LocalPath
                : Path.GetFullPath(Uri.UnescapeDataString(destination.Split('#')[0]), Path.GetDirectoryName(_source ?? Path.GetFullPath(destination))!);
            _details.Text = (File.Exists(path) || Directory.Exists(path) ? "Local target: " : "Missing local target: ") + path;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        { InsertButton.IsEnabled = false; _details.Text = "Destination could not be resolved. " + ex.Message; }
    }
}
