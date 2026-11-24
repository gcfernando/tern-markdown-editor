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
using Tern.App.Presentation;
using Tern.App.Settings;
using Tern.Desktop.Services;
using Avalonia.Threading;

namespace Tern.Desktop.Views;

public enum ExportFormat
{
    Html,
    Pdf,
    Text,
}

public static class ExportFormatDisplayNames
{
    public static string DisplayName(this ExportFormat value) => value switch
    {
        ExportFormat.Html => "HTML page",
        ExportFormat.Pdf => "PDF document",
        ExportFormat.Text => "Plain text",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
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
    private readonly TextBlock _status = MessageTones.Follow(new() { TextWrapping = TextWrapping.Wrap });
    private readonly TextBox _notes = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 180, IsVisible = false };
    private readonly Button _export = new() { Content = "Export", IsDefault = true, MinWidth = 88, Classes = { "accent" } };
    private readonly Button _browse = new() { Content = "Browse…" };
    private readonly Button _close = new() { Content = "Close", IsCancel = true, MinWidth = 88 };
    private readonly Button _open = new() { Content = "Open file", IsVisible = false };
    private readonly Button _reveal = new() { Content = "Show in folder", IsVisible = false };
    private readonly ComboBox _paper = new() { ItemsSource = Enum.GetValues<PdfPageSize>(), ItemTemplate = EnumChoices.Template<PdfPageSize>(), SelectedIndex = 0 };
    private readonly NumericUpDown _margin = new() { Minimum = 10, Maximum = 40, Value = 20, Increment = 1 };
    private readonly CheckBox _properties = new() { Content = "Include document properties (front matter)", IsChecked = false };
    private readonly TextBlock _formatHelp = GuideParts.Caption("");
    private readonly CheckBox _numbers = new() { Content = "Print page numbers", IsChecked = true };
    private readonly ComboBox _theme = new() { ItemsSource = Enum.GetValues<ExportTheme>(), ItemTemplate = EnumChoices.Template<ExportTheme>(), SelectedIndex = 0 };
    private readonly TextBox _language = new() { Text = "en" };
    private readonly StackPanel _pdfOptions = new() { [!StackPanel.SpacingProperty] = GuideParts.Space(8).ToBinding() };
    private readonly StackPanel _htmlOptions = new() { [!StackPanel.SpacingProperty] = GuideParts.Space(8).ToBinding() };
    private readonly ContentControl _progress = new() { IsVisible = false };
    private readonly GuidanceStrip _guidance = new("Choose a format, then choose where to save your copy.", "export");
    private readonly Expander _notesDetails;
    private readonly ActivityFeed _activities;
    private readonly bool _reduceMotion;
    private readonly ExportPreferences? _preferences;
    private readonly Func<bool>? _savePreferences;
    private readonly string _preferenceScope;
    private readonly Dictionary<ExportFormat, ExportPreset> _pendingPresets = [];
    private ExportFormat _optionsFormat;
    internal string? SuggestedOutputFolder { get; private set; }
    private bool _running, _closed, _resetting, _choosingOutput;
    private string? _output;
    private CancellationTokenSource? _exportCancellation;
    internal Func<Task>? BeforeExport { get; set; }
    internal Func<ExportFormat, Task<string?>>? PickOutputFile { get; set; }
    internal Func<string, bool, string?> LaunchOutput { get; set; } = (path, reveal) => reveal
        ? new SystemLauncher().TryRevealInFileManager(path) : new SystemLauncher().TryOpenFile(path);
    internal bool IsExporting => _running;
    internal IReadOnlyList<string> Warnings { get; private set; } = [];
    internal bool Truncated { get; private set; }

    public ExportWindow(string documentName, string? documentPath, string markdown, MarkdownProfile profile, string? workspaceRoot, ExportFormat initial, IReadOnlyList<string>? protectedSources = null,
        Func<string, byte[]?>? remoteImageBytes = null, IReadOnlyList<string>? linkedImageFiles = null, ActivityFeed? activities = null, bool reduceMotion = false,
        ExportPreferences? preferences = null, Func<bool>? savePreferences = null)
    {
        _name = documentName;
        _markdown = markdown;
        _preview = ExportDefaults.PreviewOptions with { Profile = profile, DocumentPath = documentPath, WorkspaceRoot = workspaceRoot,
            Resolver = new Tern.Rendering.Resources.ResourceResolverOptions { AdditionalImageFiles = linkedImageFiles ?? [] } };
        _protectedSources = (protectedSources ?? []).Concat(documentPath is null ? [] : [documentPath]).Distinct().ToArray();
        _remoteImageBytes = remoteImageBytes;
        _activities = activities ?? new ActivityFeed();
        _reduceMotion = reduceMotion;
        _preferences = preferences;
        _savePreferences = savePreferences;
        _preferenceScope = workspaceRoot ?? "";
        SuggestedOutputFolder = preferences?.WorkspaceFolders.FirstOrDefault(pair =>
            Tern.Workspaces.WorkspacePaths.Comparer.Equals(pair.Key, _preferenceScope)).Value;
        DialogParts.Setup(this, "Export a copy", 740, 760, DialogGuidance.Export, guidance: false);
        _theme.SelectedItem = Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? ExportTheme.Dark : ExportTheme.Light;
        Closed += (_, _) => _closed = true;
        Closing += (_, e) =>
        {
            if (!_running) return;
            e.Cancel = true;
            CancelExport();
        };
        _format.SelectedIndex = (int)initial;
        _optionsFormat = initial;
        RestoreOptions(initial);
        _format.SelectionChanged += (_, _) =>
        {
            _pendingPresets[_optionsFormat] = CaptureOptions();
            _optionsFormat = Format;
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
        _export.Click += async (_, _) => await RequestExportAsync();
        AutomationProperties.SetName(_close, "Close export");
        _close.Click += (_, _) => { if (_running) CancelExport(); else Close(); };
        _open.Click += (_, _) => OpenResult(false);
        _reveal.Click += (_, _) => OpenResult(true);
        var paperFields = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), [!Grid.ColumnSpacingProperty] = GuideParts.Space(16).ToBinding() };
        var paperField = Field("Paper size", _paper);
        var marginField = Field("Margins · mm", _margin);
        paperFields.Children.Add(paperField); paperFields.Children.Add(marginField); Grid.SetColumn(marginField, 1);
        paperFields.SizeChanged += (_, _) =>
        {
            var compact = paperFields.Bounds.Width < GuideParts.Scaled(440);
            paperFields.ColumnDefinitions = new(compact ? "*" : "*,*");
            paperFields.RowDefinitions = new(compact ? "Auto,Auto" : "Auto");
            paperFields.RowSpacing = compact ? GuideParts.SpaceValue(12) : 0;
            Grid.SetColumn(marginField, compact ? 0 : 1); Grid.SetRow(marginField, compact ? 1 : 0);
        };
        _pdfOptions.Children.Add(paperFields);
        _pdfOptions.Children.Add(_numbers);
        _htmlOptions.Children.Add(Field("Appearance", _theme));
        _htmlOptions.Children.Add(Field("Document language (for example en or sv)", _language));
        UpdateOptions();
        var destination = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), [!Grid.ColumnSpacingProperty] = GuideParts.Space(8).ToBinding() };
        destination.Children.Add(_path); destination.Children.Add(_browse); Grid.SetColumn(_browse, 1);
        var optionsCard = GuideParts.Card(GuideParts.Vertical(_pdfOptions, _htmlOptions, _properties));
        optionsCard[!Border.IsVisibleProperty] = _properties.GetObservable(CheckBox.IsVisibleProperty).ToBinding();
        var body = new StackPanel { [!StackPanel.SpacingProperty] = GuideParts.Space(12).ToBinding(), Children =
        { Field("1. Choose a format", _format), _formatHelp, Field("2. Choose where to save", destination),
          optionsCard } };
        _notesDetails = GuideParts.Details("Export notes", _notes);
        _notesDetails.IsVisible = false;
        var resultActions = GuideParts.ButtonRow(_open, _reveal); resultActions.HorizontalAlignment = HorizontalAlignment.Left;
        var primaryActions = GuideParts.Actions(_export, _close);
        var actions = new Grid { ColumnDefinitions = new("*,Auto"), [!Grid.ColumnSpacingProperty] = GuideParts.Space(12).ToBinding(), [!Grid.RowSpacingProperty] = GuideParts.Space(8).ToBinding(), Children = { resultActions, primaryActions } };
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
            if (!string.IsNullOrWhiteSpace(_path.Text)) _path.Text = Path.ChangeExtension(_path.Text, Extension);
            RestoreOptions(Format);
            UpdateOptions(); InvalidateResult(string.IsNullOrWhiteSpace(_path.Text)
                ? "Format changed. Choose an output file for the selected format."
                : "Format changed. The output folder is kept and the file extension is updated. Choose Export to create the new copy.");
        }
        finally { _resetting = false; }
    }

    private void OptionsChanged() { if (!_running && !_resetting) InvalidateResult("Options changed. Choose Export to create a new copy."); }

    internal ExportPreset CaptureOptions() => new(
        (_paper.SelectedItem as PdfPageSize? ?? PdfPageSize.A4).ToString(),
        _margin.Value ?? 20, _numbers.IsChecked == true,
        (_theme.SelectedItem as ExportTheme? ?? ExportTheme.Light).ToString(),
        _language.Text ?? "en", _properties.IsChecked == true);

    private void RestoreOptions(ExportFormat format)
    {
        var fallbackTheme = Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? ExportTheme.Dark : ExportTheme.Light;
        var preset = _pendingPresets.GetValueOrDefault(format) ?? _preferences?.Formats.GetValueOrDefault(format.ToString())
            ?? new ExportPreset(Theme: fallbackTheme.ToString());
        _paper.SelectedItem = Enum.TryParse<PdfPageSize>(preset.Paper, out var paper) && Enum.IsDefined(paper) ? paper : PdfPageSize.A4;
        _margin.Value = Math.Clamp(preset.Margin, 10, 40);
        _numbers.IsChecked = preset.PageNumbers;
        _theme.SelectedItem = Enum.TryParse<ExportTheme>(preset.Theme, out var theme) && Enum.IsDefined(theme) ? theme : fallbackTheme;
        _language.Text = preset.Language ?? "en";
        _properties.IsChecked = preset.IncludeFrontMatter;
    }

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
        if (_running || _closed || _choosingOutput) return;
        _choosingOutput = true;
        _export.IsEnabled = _browse.IsEnabled = _format.IsEnabled = false;
        var format = Format;
        try
        {
            string? local;
            if (PickOutputFile is { } pick) local = await pick(format);
            else
            {
                var folder = string.IsNullOrWhiteSpace(_path.Text)
                    ? SuggestedOutputFolder ?? Path.GetDirectoryName(_preview.DocumentPath)
                    : Path.GetDirectoryName(_path.Text);
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Export a copy",
                    SuggestedStartLocation = folder is null ? null : await StorageProvider.TryGetFolderFromPathAsync(folder),
                    SuggestedFileName = string.IsNullOrWhiteSpace(_path.Text)
                        ? Path.GetFileNameWithoutExtension(_name) + "." + Extension : Path.GetFileName(_path.Text),
                    DefaultExtension = Extension,
                    FileTypeChoices = [new FilePickerFileType(Format.DisplayName()) { Patterns = ["*." + Extension] }],
                });
                local = file?.TryGetLocalPath();
            }
            if (!_closed && !_running && Format == format && local is not null) _path.Text = local;
        }
        finally
        {
            _choosingOutput = false;
            if (!_closed && !_running) _export.IsEnabled = _browse.IsEnabled = _format.IsEnabled = true;
        }
    }

    internal async Task<bool> RequestExportAsync()
    {
        if (_running || _closed || _choosingOutput) return false;
        if (string.IsNullOrWhiteSpace(_path.Text))
        {
            await BrowseAsync();
            if (_closed) return false;
            if (string.IsNullOrWhiteSpace(_path.Text))
            {
                _status.Text = "Export canceled; no output file was chosen.";
                return false;
            }
        }
        return await ExportAsync(_path.Text);
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
        using var cancellation = new CancellationTokenSource();
        using var activity = _activities.Begin("Export " + format.DisplayName(), _name, cancel: cancellation.Cancel);
        try
        {
            SetRunning(true);
            _exportCancellation = cancellation;
            if (await FileIdentity.FindSameFileAsync(_protectedSources, outputPath, cancellation.Token) is not null)
            {
                _status.Text = "Choose a separate output file. Export cannot replace an open Markdown source.";
                activity.Fail(_status.Text);
                return false;
            }
            if (_closed) { activity.Cancel("Export window closed before writing."); return false; }
            _status.Text = "Creating " + format.DisplayName() + "…";
            _progress.Content = new LoadingState(_activities, activity.Id, reduceMotion: _reduceMotion);
            _progress.IsVisible = true;
            activity.Report("Preparing " + format.DisplayName() + " export");
            if (BeforeExport is { } before) await before().WaitAsync(cancellation.Token);
            activity.Report("Rendering and writing " + format.DisplayName() + "; verifying output");
            var result = await Task.Run(() =>
            {
                if (format == ExportFormat.Html)
                {
                    var r = HtmlExporter.ExportToFile(markdown, outputPath, html, cancellation.Token);
                    return (r.Warnings, r.Truncated, Counts: $"{r.EmbeddedImageCount} embedded image(s)");
                }
                if (format == ExportFormat.Pdf)
                {
                    var r = PdfExporter.ExportToFile(markdown, outputPath, pdf, cancellation.Token);
                    return (r.Warnings, r.Truncated, Counts: $"{r.PageCount} page(s)");
                }
                var document = PreviewBuilder.Build(markdown, preview, null, cancellation.Token);
                PlainTextExporter.ExportToFile(document, outputPath, new PlainTextExportOptions { Preview = preview, IncludeFrontMatter = false }, cancellation.Token);
                return (Warnings: (IReadOnlyList<string>)[], document.Truncated, Counts: "Plain text");
            }, cancellation.Token);
            var preferenceWarning = "";
            activity.Report("Output written; remembering export options");
            if (_preferences is not null)
            {
                _preferences.Formats[format.ToString()] = CaptureOptions();
                SuggestedOutputFolder = Path.GetDirectoryName(Path.GetFullPath(outputPath));
                _preferences.WorkspaceFolders[_preferenceScope] = SuggestedOutputFolder!;
                if (_savePreferences?.Invoke() == false)
                    preferenceWarning = "The exported copy was written, but export preferences could not be saved. They apply until Tern closes.";
            }
            var notes = result.Warnings.Concat(result.Truncated ? ["Output is incomplete: the rendering budget or page limit was reached."] : Array.Empty<string>())
                .Concat(preferenceWarning.Length > 0 ? [preferenceWarning] : []).ToArray();
            activity.Complete(string.Join("\n", notes), notes.Length > 0,
                result.Truncated ? "Exported incomplete copy; review notes" : notes.Length > 0 ? "Exported with notes" : "Exported");
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
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            activity.Cancel();
            if (!_closed)
            {
                _status.Text = "Export canceled. Any existing output file was kept.";
                _guidance.Message = "Nothing was exported. Review the options and select Export to try again.";
            }
            return false;
        }
        catch (Exception ex) when (ex is ExportException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            activity.Fail(ex.Message);
            if (!_closed) { _status.Text = "Export failed: " + Tern.App.Shell.UserProblem.Plain(ex); _guidance.Message = "Check the destination and its write permissions, then choose Export to retry."; }
            return false;
        }
        finally
        {
            _exportCancellation = null;
            if (!_closed) { SetRunning(false); _progress.IsVisible = false; _progress.Content = null; }
            else _running = false;
        }
    }

    private void CancelExport()
    {
        if (_exportCancellation is not { IsCancellationRequested: false } cancellation) return;
        _status.Text = "Canceling export…";
        _close.IsEnabled = false;
        cancellation.Cancel();
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
        if (value) _guidance.Message = "Creating your copy. Cancel stops the export without replacing an existing output file.";
        _close.Content = value ? "Cancel" : "Close";
        _close.IsEnabled = true;
        AutomationProperties.SetName(_close, value ? "Cancel export" : "Close export");
    }

    internal void OpenResult(bool reveal)
    {
        if (_output is not { } path || !File.Exists(path)) { InvalidateResult("The exported file is no longer at its output location. Choose Export to create a new copy."); return; }
        if (LaunchOutput(path, reveal) is { } error) InvalidateResult("Output was created, but could not be opened: " + error);
    }
}
