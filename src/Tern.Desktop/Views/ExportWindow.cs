using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Tern.Export;
using Tern.Markdown;
using Tern.Rendering.Preview;
using Tern.Filesystem;

namespace Tern.Desktop.Views;

public enum ExportFormat
{
    Html,
    Pdf,
    Text,
}

/// <summary>Exports the open document as HTML, PDF or plain text to a file the user picks. Everything is generated locally.</summary>
public sealed class ExportWindow : Window
{
    private readonly string _name;
    private readonly string _markdown;
    private readonly PreviewOptions _preview;
    private readonly IReadOnlyList<string> _protectedSources;
    private readonly Func<string, byte[]?>? _remoteImageBytes;
    private readonly ComboBox _format = new() { ItemsSource = new[] { "HTML page", "PDF document", "Plain text" }, Width = 220 };
    private readonly TextBox _path = new() { Width = 360, IsReadOnly = true, PlaceholderText = "Choose where to save" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };

    public ExportWindow(string documentName, string? documentPath, string markdown, MarkdownProfile profile, string? workspaceRoot, ExportFormat initial, IReadOnlyList<string>? protectedSources = null,
        Func<string, byte[]?>? remoteImageBytes = null, IReadOnlyList<string>? linkedImageFiles = null)
    {
        _name = documentName;
        _markdown = markdown;
        _preview = new PreviewOptions { Profile = profile, DocumentPath = documentPath, WorkspaceRoot = workspaceRoot,
            Resolver = new Tern.Rendering.Resources.ResourceResolverOptions { AdditionalImageFiles = linkedImageFiles ?? [] } };
        _protectedSources = (protectedSources ?? []).Concat(documentPath is null ? [] : [documentPath]).Distinct().ToArray();
        _remoteImageBytes = remoteImageBytes;
        DialogParts.Setup(this, "Export", 620, 300);
        _format.SelectedIndex = (int)initial;
        _format.SelectionChanged += (_, _) =>
        {
            // Changing the extension also changes the destination whose overwrite
            // prompt the user saw. Require a new selection for the new format.
            if (!string.IsNullOrWhiteSpace(_path.Text))
            {
                _path.Text = null;
                _status.Text = "Format changed. Choose an output file for the selected format.";
            }
        };
        _status.Classes.Add("muted");
        _status.IsVisible = !string.IsNullOrWhiteSpace(_status.Text);
        _status.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty) _status.IsVisible = !string.IsNullOrWhiteSpace(_status.Text);
        };
        AutomationProperties.SetName(_path, "Output file");

        var browse = new Button { Content = "Browse…" };
        AutomationProperties.SetName(browse, "Choose output file");
        browse.Click += async (_, _) => await BrowseAsync();
        var export = new Button { Content = "Export", IsDefault = true, MinWidth = 88 };
        export.Classes.Add("accent");
        AutomationProperties.SetName(export, "Export document");
        export.Click += async (_, _) => await ExportAsync(_path.Text);
        var cancel = new Button { Content = "Close", MinWidth = 88 };
        AutomationProperties.SetName(cancel, "Close export");
        cancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                DialogParts.Labeled("Format", _format),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _path, browse } },
                _status,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, export } },
            },
        };
    }

    internal ExportFormat Format
    {
        get => (ExportFormat)Math.Max(0, _format.SelectedIndex);
        set => _format.SelectedIndex = (int)value;
    }

    internal string StatusText => _status.Text ?? string.Empty;

    private string Extension => Format switch { ExportFormat.Html => "html", ExportFormat.Pdf => "pdf", _ => "txt" };

    private async Task BrowseAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export to",
            SuggestedFileName = Path.GetFileNameWithoutExtension(_name) + "." + Extension,
            DefaultExtension = Extension,
            FileTypeChoices = [new FilePickerFileType(Format.ToString()) { Patterns = ["*." + Extension] }],
        });
        if (file?.TryGetLocalPath() is { } local)
        {
            _path.Text = local;
        }
    }

    /// <summary>Writes the export; returns true on success. Failures are shown in the window rather than thrown.</summary>
    internal async Task<bool> ExportAsync(string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            _status.Text = "Choose an output file first.";
            return false;
        }

        var markdown = _markdown;
        var preview = _preview;
        var format = Format;
        if (!string.Equals(Path.GetExtension(outputPath), "." + Extension, StringComparison.OrdinalIgnoreCase))
        {
            _status.Text = "Choose a ." + Extension + " output file for the selected format.";
            return false;
        }
        try
        {
            if (_protectedSources.Any(source => FileIdentity.AreSameFile(source, outputPath)))
            {
                _status.Text = "Choose a separate output file. Export cannot replace an open Markdown source.";
                return false;
            }
            var warnings = await Task.Run(() => format switch
            {
                ExportFormat.Html => HtmlExporter.ExportToFile(markdown, outputPath, new HtmlExportOptions { Preview = preview, RemoteImageBytes = _remoteImageBytes }).Warnings,
                ExportFormat.Pdf => PdfExporter.ExportToFile(markdown, outputPath, new PdfExportOptions { Preview = preview, RemoteImageBytes = _remoteImageBytes }).Warnings,
                _ => ExportText(markdown, outputPath, preview),
            });
            _status.Text = warnings.Count == 0 ? "Exported to " + outputPath : $"Exported to {outputPath} with {warnings.Count} note(s): {warnings[0]}";
            return true;
        }
        catch (Exception ex) when (ex is ExportException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _status.Text = "Export failed: " + ex.Message;
            return false;
        }
    }

    private static IReadOnlyList<string> ExportText(string markdown, string path, PreviewOptions preview)
    {
        PlainTextExporter.ExportToFile(markdown, path, new PlainTextExportOptions { Preview = preview });
        return [];
    }
}
