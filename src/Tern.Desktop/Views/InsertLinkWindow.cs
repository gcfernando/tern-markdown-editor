using Avalonia;
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
    internal Button InsertButton { get; } = new() { Content = "Insert link", IsDefault = true, Classes = { "accent" } };
    internal ComboBox HeadingInput { get; }
    internal string DestinationType { get; private set; } = "Web address";
    private readonly TextBlock _details = GuideParts.Caption("");
    private string? _source;
    private readonly HashSet<string> _headings;

    public InsertLinkWindow(string label, string? source, IReadOnlyList<HeadingInfo> headings, Func<Task<string?>>? saveNote = null)
    {
        _source = source;
        _headings = headings.Select(h => h.Anchor).ToHashSet(StringComparer.OrdinalIgnoreCase);
        DialogParts.Setup(this, "Insert link", 560, 480, guidance: false);
        LabelInput.Text = label;
        var browse = new Button { Content = "Browse…" };
        var save = new Button { Content = "Save note…", IsVisible = false, IsEnabled = saveNote is not null };
        var destinationLabel = GuideParts.Body("Web address");
        HeadingInput = new ComboBox { PlaceholderText = headings.Count == 0 ? "No headings in this note" : "Choose a heading", ItemsSource = headings.Select(h => new ComboBoxItem { Content = h.Text, Tag = "#" + h.Anchor }).ToArray(), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(HeadingInput, "Heading in this note");
        AutomationProperties.SetName(DestinationInput, "Link destination");
        AutomationProperties.SetName(LabelInput, "Link text (optional)");
        AutomationProperties.SetName(_details, "Link validation");
        AutomationProperties.SetLiveSetting(_details, AutomationLiveSetting.Polite);
        var field = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, Children = { DestinationInput, browse } };
        Grid.SetColumn(browse, 1);
        void Refresh()
        {
            var local = DestinationType == "Local file";
            var heading = DestinationType == "Heading in this note";
            destinationLabel.Text = heading ? "Heading" : DestinationType;
            field.IsVisible = !heading;
            HeadingInput.IsVisible = heading;
            HeadingInput.IsEnabled = _headings.Count > 0;
            browse.IsVisible = local;
            browse.IsEnabled = !local || _source is not null;
            DestinationInput.IsEnabled = !local || _source is not null;
            save.IsVisible = local && _source is null;
            UpdateTarget();
        }
        var choices = new StackPanel { Spacing = 6 };
        foreach (var type in new[] { "Web address", "Local file", "Heading in this note" })
        {
            var choice = new RadioButton { Content = type, GroupName = "Link destination type", IsChecked = type == DestinationType };
            choice.IsCheckedChanged += (_, _) => { if (choice.IsChecked == true) { DestinationType = type; Refresh(); } };
            choices.Children.Add(choice);
        }
        browse.Click += async (_, _) =>
        {
            if (_source is null) return;
            if (!StorageProvider.CanOpen) { _details.Text = "File picker unavailable. Enter the local file path."; return; }
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose link target", AllowMultiple = false });
                if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
                DestinationInput.Text = string.Join('/', Path.GetRelativePath(Path.GetDirectoryName(_source)!, path).Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
            { _details.Text = "Could not open the file picker. Enter the local path or try again. " + ex.Message; }
        };
        save.Click += async (_, _) =>
        {
            if (saveNote is null) return;
            save.IsEnabled = false;
            try { _source = await saveNote(); Refresh(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _details.Text = "Could not save the note: " + ex.Message; }
            finally { save.IsEnabled = true; }
        };
        HeadingInput.SelectionChanged += (_, _) => Refresh();
        DestinationInput.TextChanged += (_, _) => UpdateTarget();
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close((LinkInput?)null);
        InsertButton.Click += (_, _) =>
        {
            UpdateTarget();
            if (!InsertButton.IsEnabled) return;
            var target = Target();
            var text = LabelInput.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text)) text = HeadingInput.IsVisible && HeadingInput.SelectedItem is ComboBoxItem item ? item.Content?.ToString() : target;
            Close(new LinkInput(text ?? target, target));
        };
        var fields = GuideParts.Vertical(GuideParts.Body("Link to"), choices, GuideParts.Caption("Link text (optional)"), LabelInput,
            destinationLabel, field, HeadingInput, _details, save);
        Content = GuideParts.Frame(GuideParts.Title("Insert link"), new ScrollViewer { Content = fields, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, GuideParts.Actions(cancel, InsertButton));
        Opened += (_, _) => DestinationInput.Focus();
        Refresh();
    }

    private string Target() => DestinationType == "Heading in this note" ? (HeadingInput.SelectedItem as ComboBoxItem)?.Tag as string ?? "" : DestinationInput.Text?.Trim() ?? "";

    private void UpdateTarget()
    {
        var target = Target();
        InsertButton.IsEnabled = false;
        if (DestinationType == "Local file" && _source is null) { _details.Text = "Save this note to create a relative file link."; return; }
        if (DestinationType == "Heading in this note")
        {
            InsertButton.IsEnabled = target.StartsWith('#') && _headings.Contains(target[1..]);
            _details.Text = InsertButton.IsEnabled ? "Links to " + target : _headings.Count == 0 ? "Add a heading in Source, then reopen Insert link." : "Choose a heading in this note.";
            return;
        }
        if (target.Length == 0 || target.Any(char.IsControl)) { _details.Text = "Enter a " + DestinationType.ToLowerInvariant() + "."; return; }
        if (DestinationType == "Web address")
        {
            InsertButton.IsEnabled = Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && !string.IsNullOrWhiteSpace(uri.Host);
            _details.Text = InsertButton.IsEnabled ? "Valid web address. The address is not fetched." : "Enter a complete http:// or https:// address.";
            return;
        }
        try
        {
            var path = Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath
                : Path.GetFullPath(Uri.UnescapeDataString(target.Split('#')[0]), Path.GetDirectoryName(_source!)!);
            InsertButton.IsEnabled = File.Exists(path);
            _details.Text = (InsertButton.IsEnabled ? "Local file: " : "File not found: ") + path;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        { _details.Text = "Destination could not be resolved. " + ex.Message; }
    }
}
