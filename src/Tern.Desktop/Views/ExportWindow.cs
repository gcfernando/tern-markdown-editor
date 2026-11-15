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
using Tern.App.Shell;
using Tern.Desktop.Services;
using Avalonia.Threading;

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
    private readonly ComboBox _format = new() { ItemsSource = new[] { "HTML page (.html)", "PDF document (.pdf)", "Plain text (.txt)" }, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _path = new() { MinWidth = 120, IsReadOnly = true, PlaceholderText = "Choose where to save" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _notes = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 180, IsVisible = false };
    private readonly Button _export = new() { Content = "Export", IsDefault = true, MinWidth = 88, Classes = { "accent" } };
    private readonly Button _browse = new() { Content = "Browse…" };
    private readonly Button _close = new() { Content = "Close", IsCancel = true, MinWidth = 88 };
    private readonly Button _open = new() { Content = "Open file", IsVisible = false };
    private readonly Button _reveal = new() { Content = "Show in folder", IsVisible = false };
    private readonly ComboBox _paper = new() { ItemsSource = Enum.GetValues<PdfPageSize>(), SelectedIndex = 0 };
    private readonly NumericUpDown _margin = new() { Minimum = 10, Maximum = 40, Value = 20, Increment = 1 };
    private readonly CheckBox _properties = new() { Content = "Include document properties (front matter)", IsChecked = false };
    private readonly TextBlock _formatHelp = GuideParts.Caption("");
    private readonly CheckBox _numbers = new() { Content = "Print page numbers", IsChecked = true };
    private readonly ComboBox _theme = new() { ItemsSource = Enum.GetValues<ExportTheme>(), SelectedIndex = 0 };
    private readonly TextBox _language = new() { Text = "en" };
    private readonly StackPanel _pdfOptions = new() { Spacing = 8 };
    private readonly StackPanel _htmlOptions = new() { Spacing = 8 };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, IsVisible = false, Height = 4 };
    private readonly GuidanceStrip _guidance = new("Choose a format, then choose where to save your copy.", "export");
    private readonly Expander _notesDetails;
    private readonly ActivityFeed _activities;
    private readonly bool _reduceMotion;
    private bool _running, _closed, _resetting;
    private string? _output;
    internal Func<Task>? BeforeExport { get; set; }
    internal Func<string, bool, string?> LaunchOutput { get; set; } = (path, reveal) => reveal
        ? new SystemLauncher().TryRevealInFileManager(path) : new SystemLauncher().TryOpenFile(path);
    internal bool IsExporting => _running;
    internal IReadOnlyList<string> Warnings { get; private set; } = [];
    internal bool Truncated { get; private set; }

    public ExportWindow(string documentName, string? documentPath, string markdown, MarkdownProfile profile, string? workspaceRoot, ExportFormat initial, IReadOnlyList<string>? protectedSources = null,
        Func<string, byte[]?>? remoteImageBytes = null, IReadOnlyList<string>? linkedImageFiles = null, ActivityFeed? activities = null, bool reduceMotion = false)
    {
        _name = documentName;
        _markdown = markdown;
        _preview = ExportDefaults.PreviewOptions with { Profile = profile, DocumentPath = documentPath, WorkspaceRoot = workspaceRoot,
            Resolver = new Tern.Rendering.Resources.ResourceResolverOptions { AdditionalImageFiles = linkedImageFiles ?? [] } };
        _protectedSources = (protectedSources ?? []).Concat(documentPath is null ? [] : [documentPath]).Distinct().ToArray();
        _remoteImageBytes = remoteImageBytes;
        _activities = activities ?? new ActivityFeed();
        _reduceMotion = reduceMotion;
        DialogParts.Setup(this, "Export as", 740, 760, guidance: false);
        _theme.SelectedItem = Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? ExportTheme.Dark : ExportTheme.Light;
        Closed += (_, _) => _closed = true;
        _format.SelectedIndex = (int)initial;
        _format.SelectionChanged += (_, _) =>
        {
            ResetFormat();
        };
        _status.Classes.Add("muted");
        _status.IsVisible = !string.IsNullOrWhiteSpace(_status.Text);
        _status.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty) _status.IsVisible = !string.IsNullOrWhiteSpace(_status.Text);
        };
        AutomationProperties.SetName(_path, "Output file");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_notes, "All export warnings and omissions");
        AutomationProperties.SetName(_browse, "Choose output file");
        _browse.Click += async (_, _) => await BrowseAsync();
        AutomationProperties.SetName(_export, "Export document");
        _export.Click += async (_, _) => await ExportAsync(_path.Text);
        AutomationProperties.SetName(_close, "Close export");
        _close.Click += (_, _) => Close();
        _open.Click += (_, _) => OpenResult(false);
        _reveal.Click += (_, _) => OpenResult(true);
        var paperFields = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 16 };
        var paperField = Field("Paper size", _paper);
        var marginField = Field("Margins · mm", _margin);
        paperFields.Children.Add(paperField); paperFields.Children.Add(marginField); Grid.SetColumn(marginField, 1);
        paperFields.SizeChanged += (_, _) =>
        {
            var compact = paperFields.Bounds.Width < GuideParts.Scaled(440);
            paperFields.ColumnDefinitions = new(compact ? "*" : "*,*");
            paperFields.RowDefinitions = new(compact ? "Auto,Auto" : "Auto");
            paperFields.RowSpacing = compact ? 10 : 0;
            Grid.SetColumn(marginField, compact ? 0 : 1); Grid.SetRow(marginField, compact ? 1 : 0);
        };
        _pdfOptions.Children.Add(paperFields);
        _pdfOptions.Children.Add(_numbers);
        _htmlOptions.Children.Add(Field("Appearance", _theme));
        _htmlOptions.Children.Add(Field("Document language (for example en or sv)", _language));
        UpdateOptions();
        var destination = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        destination.Children.Add(_path); destination.Children.Add(_browse); Grid.SetColumn(_browse, 1);
        var optionsCard = GuideParts.Card(GuideParts.Vertical(_pdfOptions, _htmlOptions, _properties));
        optionsCard[!Border.IsVisibleProperty] = _properties.GetObservable(CheckBox.IsVisibleProperty).ToBinding();
        var body = new StackPanel { Spacing = 12, Children =
        { Field("1. Choose a format", _format), _formatHelp, Field("2. Choose where to save", destination),
          optionsCard } };
        _notesDetails = GuideParts.Details("Export notes", _notes);
        _notesDetails.IsVisible = false;
        var resultActions = GuideParts.Actions(_open, _reveal); resultActions.HorizontalAlignment = HorizontalAlignment.Left;
        var primaryActions = GuideParts.Actions(_close, _export);
        var actions = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12, RowSpacing = 8, Children = { resultActions, primaryActions } };
        Grid.SetColumn(primaryActions, 1);
        actions.SizeChanged += (_, _) =>
        {
            var compact = actions.Bounds.Width < GuideParts.Scaled(560);
            actions.ColumnDefinitions = new(compact ? "*" : "*,Auto");
            actions.RowDefinitions = new(compact ? "Auto,Auto" : "Auto");
            Grid.SetColumn(primaryActions, compact ? 0 : 1); Grid.SetRow(primaryActions, compact ? 1 : 0);
        };
        var completion = GuideParts.Vertical(_progress, _status, _notesDetails, actions, _guidance);
        Content = GuideParts.Frame(GuideParts.Vertical(GuideParts.Title("Export a copy"),
                GuideParts.Caption("Choose a format and destination, then select Export. Your Markdown stays editable and unchanged.")),
            new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = body }, completion);
        _path.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty && !_running && !_resetting) InvalidateResult("Choose Export to create the selected copy."); };
        _paper.SelectionChanged += (_, _) => OptionsChanged();
        _theme.SelectionChanged += (_, _) => OptionsChanged();
        _margin.ValueChanged += (_, _) => OptionsChanged();
        _numbers.IsCheckedChanged += (_, _) => OptionsChanged();
        _properties.IsCheckedChanged += (_, _) => OptionsChanged();
        _language.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) OptionsChanged(); };
    }

    private void ResetFormat()
    {
        if (_running) return;
        _resetting = true;
        try
        {
            _path.Text = null; _paper.SelectedIndex = 0; _margin.Value = 20; _numbers.IsChecked = true;
            _properties.IsChecked = false; _language.Text = "en";
            _theme.SelectedItem = Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? ExportTheme.Dark : ExportTheme.Light;
            UpdateOptions(); InvalidateResult("Format changed. Choose an output file for the selected format.");
        }
        finally { _resetting = false; }
    }

    private void OptionsChanged() { if (!_running && !_resetting) InvalidateResult("Options changed. Choose Export to create a new copy."); }

    private void InvalidateResult(string message)
    {
        _output = null; _open.IsVisible = _reveal.IsVisible = false;
        Warnings = []; Truncated = false; _notes.Text = ""; _notes.IsVisible = false;
        _notesDetails.IsVisible = _notesDetails.IsExpanded = false; _progress.IsVisible = false;
        ToolTip.SetTip(_status, null); _status.Text = message;
        _guidance.Message = string.IsNullOrWhiteSpace(_path.Text) ? "Choose where to save your " + Extension.ToUpperInvariant() + " copy." : "Review your options, then select Export.";
    }

    private static Control Field(string label, Control control)
    {
        AutomationProperties.SetName(control, label);
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        return GuideParts.Vertical(GuideParts.Caption(label), control);
    }

    internal ExportFormat Format
    {
        get => (ExportFormat)Math.Max(0, _format.SelectedIndex);
        set { if (!_running) _format.SelectedIndex = (int)value; }
    }

    internal string StatusText => _status.Text ?? string.Empty;

    private string Extension => Format switch { ExportFormat.Html => "html", ExportFormat.Pdf => "pdf", _ => "txt" };

    private async Task BrowseAsync()
    {
        if (_running || _closed) return;
        var format = Format;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export to",
            SuggestedFileName = Path.GetFileNameWithoutExtension(_name) + "." + Extension,
            DefaultExtension = Extension,
            FileTypeChoices = [new FilePickerFileType(Format.ToString()) { Patterns = ["*." + Extension] }],
        });
        if (!_closed && !_running && Format == format && file?.TryGetLocalPath() is { } local)
        {
            _path.Text = local;
        }
    }

    /// <summary>Writes the export; returns true on success. Failures are shown in the window rather than thrown.</summary>
    internal async Task<bool> ExportAsync(string? outputPath)
    {
        if (_running || _closed) return false;
        InvalidateResult("");
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            _status.Text = "Choose an output file first.";
            return false;
        }

        var markdown = _markdown;
        var preview = _preview;
        var format = Format;
        var pdf = new PdfExportOptions { Preview = preview, RemoteImageBytes = _remoteImageBytes, SvgImageBytes = SvgExportRasterizer.Render,
            PageSize = (PdfPageSize)Math.Max(0, _paper.SelectedIndex), Margins = PdfMargins.FromMillimetres((double)(_margin.Value ?? 20)), PageNumbers = _numbers.IsChecked == true,
            IncludeFrontMatter = _properties.IsChecked == true };
        var html = new HtmlExportOptions { Preview = preview, RemoteImageBytes = _remoteImageBytes,
            Theme = (ExportTheme)Math.Max(0, _theme.SelectedIndex), Language = _language.Text ?? "en", IncludeFrontMatter = _properties.IsChecked == true };
        if (!string.Equals(Path.GetExtension(outputPath), "." + Extension, StringComparison.OrdinalIgnoreCase))
        {
            _status.Text = "Choose a ." + Extension + " output file for the selected format.";
            return false;
        }
        _path.Text = outputPath;
        using var activity = _activities.Begin("Export " + format, _name);
        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        delay.Tick += (_, _) => { delay.Stop(); if (!_closed && _running) _progress.IsVisible = true; };
        try
        {
            if (_protectedSources.Any(source => FileIdentity.AreSameFile(source, outputPath)))
            {
                _status.Text = "Choose a separate output file. Export cannot replace an open Markdown source.";
                activity.Fail(_status.Text);
                return false;
            }
            SetRunning(true);
            _status.Text = "Creating " + format + "…";
            if (!_reduceMotion) delay.Start();
            if (BeforeExport is { } before) await before();
            var result = await Task.Run(() =>
            {
                if (format == ExportFormat.Html)
                {
                    var r = HtmlExporter.ExportToFile(markdown, outputPath, html);
                    return (r.Warnings, r.Truncated, Counts: $"{r.EmbeddedImageCount} embedded image(s)");
                }
                if (format == ExportFormat.Pdf)
                {
                    var r = PdfExporter.ExportToFile(markdown, outputPath, pdf);
                    return (r.Warnings, r.Truncated, Counts: $"{r.PageCount} page(s)");
                }
                var document = PreviewBuilder.Build(markdown, preview);
                PlainTextExporter.ExportToFile(document, outputPath, new PlainTextExportOptions { Preview = preview, IncludeFrontMatter = false });
                return (Warnings: (IReadOnlyList<string>)[], document.Truncated, Counts: "Plain text");
            });
            var notes = result.Warnings.Concat(result.Truncated ? ["Output is incomplete: the rendering budget or page limit was reached."] : Array.Empty<string>()).ToArray();
            activity.Complete(string.Join("\n", notes), notes.Length > 0);
            if (_closed) return true;
            Warnings = result.Warnings;
            Truncated = result.Truncated;
            _output = outputPath;
            ToolTip.SetTip(_status, outputPath);
            _status.Text = $"Export complete · {result.Counts}" + (result.Truncated ? " · incomplete" : string.Empty)
                + (notes.Length > 0 ? $" · {notes.Length} note(s) — review below." : string.Empty);
            _notes.Text = string.Join("\n\n", notes);
            _notes.IsVisible = notes.Length > 0;
            _notesDetails.Header = $"Export notes ({notes.Length})";
            _notesDetails.IsVisible = notes.Length > 0;
            _open.IsVisible = _reveal.IsVisible = !result.Truncated;
            _guidance.Message = result.Truncated ? "This copy is incomplete. Review the export notes before trying again." : "Your copy is ready. Open file to review it, or Show in folder to find it.";
            return true;
        }
        catch (Exception ex) when (ex is ExportException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            activity.Fail(ex.Message);
            if (!_closed) { _status.Text = "Export failed: " + ex.Message; _guidance.Message = "Check the destination and its write permissions, then choose Export to retry."; }
            return false;
        }
        finally { delay.Stop(); if (!_closed) { SetRunning(false); _progress.IsVisible = false; } }
    }

    private void UpdateOptions()
    {
        _pdfOptions.IsVisible = Format == ExportFormat.Pdf;
        _htmlOptions.IsVisible = Format == ExportFormat.Html;
        _properties.IsVisible = Format != ExportFormat.Text;
        _formatHelp.Text = Format switch
        {
            ExportFormat.Html => "A self-contained web page with headings, tables, code and embedded permitted images. Open it in any browser.",
            ExportFormat.Pdf => "A paginated document for sharing and printing. Headings, tables, code and permitted images keep their formatting.",
            _ => "Text only. Choose HTML or PDF to keep formatting and images."
        };
    }

    private void SetRunning(bool value)
    {
        _running = value;
        _format.IsEnabled = _browse.IsEnabled = _export.IsEnabled = _pdfOptions.IsEnabled = _htmlOptions.IsEnabled = _properties.IsEnabled = !value;
        if (value) _guidance.Message = "Creating your copy. Wait for completion before opening the exported file.";
        _close.Content = value ? "Close (export continues)" : "Close";
    }

    internal void OpenResult(bool reveal)
    {
        if (_output is not { } path || !File.Exists(path)) { InvalidateResult("The exported file is no longer at its output location. Choose Export to create a new copy."); return; }
        if (LaunchOutput(path, reveal) is { } error) InvalidateResult("Output was created, but could not be opened: " + error);
    }
}
