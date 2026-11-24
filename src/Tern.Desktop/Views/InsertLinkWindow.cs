using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Tern.Markdown;

namespace Tern.Desktop.Views;

public sealed record LinkInput(string Label, string Destination, bool IsWiki = false);

public sealed class InsertLinkWindow : Window
{
    internal TextBox LabelInput { get; } = new();
    internal TextBox DestinationInput { get; } = new();
    internal Button InsertButton { get; } = new() { Content = "Insert link", IsDefault = true, Classes = { "accent" } };
    internal ComboBox HeadingInput { get; }
    internal ComboBox WorkspaceInput { get; }
    internal string DestinationType { get; private set; } = "Web address";
    private readonly TextBlock _details = GuideParts.Caption("");
    private readonly MessageBar _detailsBar;
    private string? _source;
    private readonly HashSet<string> _headings;
    internal const string OtherDriveWarning = "Warning: this file is on a different drive, so a link to it would not work. Copy the file to the same drive as this note, then choose the copy.";

    public InsertLinkWindow(string label, string? source, IReadOnlyList<HeadingInfo> headings, Func<Task<string?>>? saveNote = null,
        IReadOnlyList<Tern.App.Panels.WikiLinkCandidate>? workspaceNotes = null)
    {
        _source = source;
        _detailsBar = new MessageBar(_details, followText: true);
        _headings = headings.Select(h => h.Anchor).ToHashSet(StringComparer.OrdinalIgnoreCase);
        DialogParts.Setup(this, "Insert link", 560, 480, DialogGuidance.Link, guidance: false);
        LabelInput.Text = label;
        var browse = new Button { Content = "Browse…" };
        var save = new Button { Content = "Save document…", IsVisible = false, IsEnabled = saveNote is not null };
        var destinationLabel = GuideParts.Body("Web address");
        HeadingInput = new ComboBox { PlaceholderText = headings.Count == 0 ? "No headings in this document" : "Choose a heading", ItemsSource = headings.Select(h => new ComboBoxItem { Content = h.Text, Tag = "#" + h.Anchor }).ToArray(), HorizontalAlignment = HorizontalAlignment.Stretch };
        WorkspaceInput = new ComboBox { PlaceholderText = workspaceNotes is { Count: > 0 } ? "Choose a workspace note or heading" : "Open a saved note in a ready workspace first",
            ItemsSource = (workspaceNotes ?? []).Select(note => new ComboBoxItem { Content = note.Label + " - " + note.Destination, Tag = note.Destination }).ToArray(), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(WorkspaceInput, "Workspace note destination");
        AutomationProperties.SetName(HeadingInput, "Heading in this document");
        AutomationProperties.SetName(DestinationInput, "Link destination");
        AutomationProperties.SetName(LabelInput, "Link text (optional)");
        AutomationProperties.SetName(_details, "Link validation");
        AutomationProperties.SetLiveSetting(_details, AutomationLiveSetting.Polite);
        var field = new Grid { ColumnDefinitions = new("*,Auto"), [!Grid.ColumnSpacingProperty] = GuideParts.Space(8).ToBinding(), Children = { DestinationInput, browse } };
        Grid.SetColumn(browse, 1);
        void Refresh()
        {
            var local = DestinationType == "Local file";
            var heading = DestinationType == "Heading in this document";
            var workspace = DestinationType == "Workspace note";
            destinationLabel.Text = heading ? "Heading" : DestinationType;
            field.IsVisible = !heading && !workspace;
            HeadingInput.IsVisible = heading;
            WorkspaceInput.IsVisible = workspace;
            HeadingInput.IsEnabled = _headings.Count > 0;
            browse.IsVisible = local;
            browse.IsEnabled = !local || _source is not null;
            DestinationInput.IsEnabled = !local || _source is not null;
            save.IsVisible = local && _source is null;
            UpdateTarget();
        }
        var choices = new StackPanel { [!StackPanel.SpacingProperty] = GuideParts.Space(4).ToBinding() };
        foreach (var type in new[] { "Web address", "Local file", "Heading in this document", "Workspace note" })
        {
            var choice = new RadioButton { Content = type, GroupName = "Link destination type", IsChecked = type == DestinationType };
            choice.IsCheckedChanged += (_, _) => { if (choice.IsChecked == true) { DestinationType = type; Refresh(); } };
            choices.Children.Add(choice);
        }
        browse.Click += async (_, _) =>
        {
            if (_source is null) return;
            if (!StorageProvider.CanOpen) { _detailsBar.Show(MessageTone.Warning, "File picker unavailable. Enter the local file path."); return; }
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose link target", AllowMultiple = false });
                if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
                if (RelativeDestination(_source, path) is not { } destination) { DestinationInput.Text = ""; _detailsBar.Show(MessageTone.Warning, OtherDriveWarning); return; }
                DestinationInput.Text = destination;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
            { _detailsBar.Show(MessageTone.Error, "Could not open the file picker. Enter the local path or try again. " + Tern.App.Shell.UserProblem.Plain(ex)); }
        };
        save.Click += async (_, _) =>
        {
            if (saveNote is null) return;
            save.IsEnabled = false;
            try { _source = await saveNote(); Refresh(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _detailsBar.Show(MessageTone.Error, "Could not save the note: " + Tern.App.Shell.UserProblem.Plain(ex)); }
            finally { save.IsEnabled = true; }
        };
        HeadingInput.SelectionChanged += (_, _) => Refresh();
        WorkspaceInput.SelectionChanged += (_, _) => Refresh();
        DestinationInput.TextChanged += (_, _) => UpdateTarget();
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close((LinkInput?)null);
        InsertButton.Click += (_, _) =>
        {
            UpdateTarget();
            if (!InsertButton.IsEnabled) return;
            var target = DestinationType == "Local file" ? LocalDestination(_source!, Target()) ?? Target() : Target();
            var text = LabelInput.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text)) text = HeadingInput.IsVisible && HeadingInput.SelectedItem is ComboBoxItem item ? item.Content?.ToString() : target;
            Close(new LinkInput(text ?? target, target, DestinationType == "Workspace note"));
        };
        var fields = GuideParts.Vertical(GuideParts.Body("Link to"), choices, GuideParts.Caption("Link text (optional)"), LabelInput,
            destinationLabel, field, HeadingInput, WorkspaceInput, _detailsBar, save);
        Content = GuideParts.Frame(GuideParts.Title("Insert link"), new ScrollViewer { Content = fields, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, GuideParts.Actions(InsertButton, cancel));
        Opened += (_, _) => DestinationInput.Focus();
        Refresh();
    }

    /// <summary>A link path relative to the note, or null when the file is on another drive. Tern blocks file: links, so no working link exists then.</summary>
    internal static string? RelativeDestination(string sourceFile, string targetPath)
    {
        var relative = Path.GetRelativePath(Path.GetDirectoryName(sourceFile)!, targetPath);
        return Path.IsPathFullyQualified(relative) ? null : string.Join('/', relative.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
    }

    /// <summary>Turns a typed full path into a working relative link. Returns null when the file is on another drive.</summary>
    internal static string? LocalDestination(string sourceFile, string target)
    {
        var hash = target.IndexOf('#', StringComparison.Ordinal);
        var fragment = hash < 0 ? "" : target[hash..];
        var file = hash < 0 ? target : target[..hash];
        var full = Uri.TryCreate(file, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : Uri.UnescapeDataString(file);
        if (!Path.IsPathFullyQualified(full)) return target;
        return RelativeDestination(sourceFile, full) is { } relative ? relative + fragment : null;
    }

    private string Target() => DestinationType == "Workspace note" ? (WorkspaceInput.SelectedItem as ComboBoxItem)?.Tag as string ?? ""
        : DestinationType == "Heading in this document" ? (HeadingInput.SelectedItem as ComboBoxItem)?.Tag as string ?? "" : DestinationInput.Text?.Trim() ?? "";

    // The text can contain a heading or file name, so the level comes from the check and not from the words.
    private void Say(bool ready, string text) => _detailsBar.Show(ready ? MessageTone.Success : MessageTone.Warning, text);

    private void UpdateTarget()
    {
        var target = Target();
        InsertButton.IsEnabled = false;
        if (DestinationType == "Workspace note")
        {
            InsertButton.IsEnabled = WorkspaceInput.SelectedItem is ComboBoxItem;
            Say(InsertButton.IsEnabled, InsertButton.IsEnabled ? "Wiki link to [[" + target + "]]" : "Choose a note or heading from the current workspace.");
            return;
        }
        if (DestinationType == "Local file" && _source is null) { _detailsBar.Show(MessageTone.Warning, "Save this note to create a relative file link."); return; }
        if (DestinationType == "Heading in this document")
        {
            InsertButton.IsEnabled = target.StartsWith('#') && _headings.Contains(target[1..]);
            Say(InsertButton.IsEnabled, InsertButton.IsEnabled ? "Links to " + target : _headings.Count == 0 ? "Add a heading in Source, then reopen Insert link." : "Choose a heading in this note.");
            return;
        }
        if (target.Length == 0 || target.Any(char.IsControl)) { _detailsBar.Show(MessageTone.Info, "Enter a " + DestinationType.ToLowerInvariant() + "."); return; }
        if (DestinationType == "Web address")
        {
            InsertButton.IsEnabled = Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && !string.IsNullOrWhiteSpace(uri.Host);
            Say(InsertButton.IsEnabled, InsertButton.IsEnabled ? "Valid web address. The address is not fetched." : "Enter a complete http:// or https:// address.");
            return;
        }
        try
        {
            if (LocalDestination(_source!, target) is null) { _detailsBar.Show(MessageTone.Warning, OtherDriveWarning); return; }
            var path = Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath
                : Path.GetFullPath(Uri.UnescapeDataString(target.Split('#')[0]), Path.GetDirectoryName(_source!)!);
            InsertButton.IsEnabled = File.Exists(path);
            Say(InsertButton.IsEnabled, (InsertButton.IsEnabled ? "Local file: " : "File not found: ") + path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        { _detailsBar.Show(MessageTone.Error, "Tern could not read this location. Check it and try again. " + Tern.App.Shell.UserProblem.Plain(ex)); }
    }
}
