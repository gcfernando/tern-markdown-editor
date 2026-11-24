using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Tern.Desktop.Services;
using Tern.Export;
using Tern.Rendering.Preview;
using Tern.App.Shell;

namespace Tern.Desktop.Views;

/// <summary>Paginated PDF preview with direct printing and optional PDF handoff. Opening it never submits a print job.</summary>
public sealed class PrintPreviewWindow : Window
{
    private readonly string _markdown;
    private readonly string _title;
    private readonly Action<string>? _reportCleanupError;
    private readonly ActivityFeed _activities;
    private ActivityScope? _printingActivity;
    private readonly HashSet<string> _temporaryPdfs = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PreviewOptions _options;
    private readonly Func<string, byte[]?>? _images;
    private readonly TextBlock _status = MessageTones.Follow(GuideParts.Caption("Preparing printable pages…"));
    private readonly ContentControl _progress = new();
    private readonly TextBlock _selectionStatus = MessageTones.Follow(GuideParts.Caption(""));
    private readonly TextBlock _documentPage = GuideParts.Caption("");
    private readonly Button _print = new() { Content = "Print…", Classes = { "accent" }, IsEnabled = false };
    private readonly Button _openPdf = new() { Content = "Open selected PDF…", IsEnabled = false };
    private readonly Image _page = new() { Stretch = Stretch.Uniform };
    private readonly ScrollViewer _viewport;
    private readonly NumericUpDown _number = new() { Minimum = 1, Maximum = 1, Value = 1, Increment = 1, Width = GuideParts.Scaled(60), ShowButtonSpinner = false, FormatString = "0", IsEnabled = false };
    private readonly TextBlock _count = GuideParts.Body("of 0");
    private readonly RadioButton _all = new() { Content = "All", GroupName = "Print pages", IsChecked = true };
    private readonly RadioButton _current = new() { Content = "Current page", GroupName = "Print pages" };
    private readonly RadioButton _ranges = new() { Content = "Pages", GroupName = "Print pages" };
    private readonly Button _previous = new() { Content = new VectorIcon { Kind = "ChevronLeft" }, Classes = { "icon-button" } };
    private readonly Button _next = new() { Content = new VectorIcon { Kind = "ChevronRight" }, Classes = { "icon-button" } };
    internal TextBox PageRanges { get; } = new() { Width = GuideParts.Scaled(140), MaxLength = 32000, PlaceholderText = "1-3, 5", IsEnabled = false };
    private WindowsPdfPreview? _pdf;
    private PdfExportResult? _result;
    private Bitmap? _bitmap;
    private double _pageWidthDips;
    private bool _closed, _running, _rendering;
    private int _renderGeneration, _renders;
    private int _selectionVersion;
    private int[] _navigationPages = [];
    private bool _updatingNavigation;
    private string _instructions = "Print… chooses a Windows printer and copies for the selected pages. Open selected PDF… uses an external reader instead.";
    private readonly SemaphoreSlim _pageRender = new(1, 1);
    internal int PageCount => _result?.PageCount ?? 0;
    internal int DisplayedPage { get; private set; } = 1;
    internal Func<string, string?> OpenPdf { get; set; } = path => new SystemLauncher().TryOpenFile(path);
    internal Func<nint, string, byte[], CancellationToken, Task<bool>> PrintPdf { get; set; }
    internal string TemporaryDirectory { get; set; } = PrintPreviewFiles.DirectoryPath;
    internal Func<byte[], int[], Task<byte[]>> ExtractPages { get; set; } = (data, pages) => Task.Run(() => PrintPageSelection.Extract(data, pages));
    internal string? OutputPath { get; private set; }
    internal Task Preparation { get; private set; } = Task.CompletedTask;
    internal Task Navigation { get; private set; } = Task.CompletedTask;
    internal Task Printing { get; private set; } = Task.CompletedTask;

    public PrintPreviewWindow(string title, string markdown, PreviewOptions options, Func<string, byte[]?>? images = null,
        Action<string>? reportCleanupError = null, ActivityFeed? activities = null)
    {
        _markdown = markdown; _options = options; _images = images; _title = title; _reportCleanupError = reportCleanupError;
        _activities = activities ?? new ActivityFeed();
        PrintPdf = (owner, name, data, token) => WindowsTextPrinter.ShowDialogAndPrintPdfAsync(owner, name, data, token,
            progress =>
            {
                if (progress.WaitingForUser) _printingActivity?.WaitForUser(progress.Phase);
                else _printingActivity?.Report(progress.Phase, progress.CompletedPages, progress.TotalPages, "pages handed to Windows");
            });
        DialogParts.Setup(this, "Print preview — " + title, 1080, 860, DialogGuidance.Print, stableViewport: true, guidance: false);
        AutomationProperties.SetName(_page, "Printable PDF page");
        AutomationProperties.SetName(_status, "Print preview status");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(PageRanges, "Pages to print, for example 1-3, 5");
        AutomationProperties.SetName(_number, "Preview page number");
        AutomationProperties.SetName(_previous, "Previous preview page"); AutomationProperties.SetName(_next, "Next preview page");
        ToolTip.SetTip(_previous, "Previous preview page"); ToolTip.SetTip(_next, "Next preview page");
        _previous.Click += (_, _) => _number.Value = Math.Max(1, (_number.Value ?? 1) - 1);
        _next.Click += (_, _) => _number.Value = Math.Min(_number.Maximum, (_number.Value ?? 1) + 1);
        _number.ValueChanged += (_, e) =>
        {
            if (!_updatingNavigation && e.NewValue is { } value && (int)value >= 1 && (int)value <= _navigationPages.Length)
                Navigation = ShowPageAsync(_navigationPages[(int)value - 1]);
        };
        var zoom = new ComboBox { ItemsSource = new[] { "Fit page", "100%", "150%" }, SelectedIndex = 1, MinWidth = 100 };
        AutomationProperties.SetName(zoom, "Print preview zoom");
        void FitPage()
        {
            if (_bitmap is null) return;
            var ratio = _bitmap.Size.Width / _bitmap.Size.Height;
            var width = zoom.SelectedIndex == 0 ? Math.Max(100, Math.Min(_viewport.Bounds.Width - 48, (_viewport.Bounds.Height - 32) * ratio))
                : _pageWidthDips * (zoom.SelectedIndex == 2 ? 1.5 : 1);
            _page.Width = width; _page.Height = width / ratio;
        }
        _viewport = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border { Padding = new Thickness(24, 16), HorizontalAlignment = HorizontalAlignment.Center, Child = _page } };
        _viewport.SizeChanged += (_, _) => FitPage();
        zoom.SelectionChanged += (_, _) => FitPage();
        _page.PropertyChanged += (_, e) => { if (e.Property == Image.SourceProperty) FitPage(); };
        foreach (var option in new[] { _all, _current, _ranges })
            // Also refresh on uncheck: the group clears the old option after the new one is checked.
            option.IsCheckedChanged += (_, _) => SelectionChanged();
        PageRanges.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) SelectionChanged(); };
        var pageLabel = GuideParts.Caption("Preview page");
        var controls = new WrapPanel { [!WrapPanel.ItemSpacingProperty] = GuideParts.Space(12).ToBinding(), [!WrapPanel.LineSpacingProperty] = GuideParts.Space(8).ToBinding(), Children = { _all, _current, _ranges, PageRanges,
            new Border { Width = 1, Height = 24, Margin = new Thickness(4, 0),
                [!Border.BackgroundProperty] = this.GetResourceObservable(Themes.ThemeManager.ControlBorder).ToBinding() }, pageLabel, _previous, _number, _count, _next, zoom } };
        foreach (var control in controls.Children)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            if (control is RadioButton or TextBox or NumericUpDown or Button or ComboBox) control.MinHeight = GuideParts.Scaled(34);
        }
        _previous.MinWidth = _next.MinWidth = GuideParts.Scaled(34);
        _count.VerticalAlignment = VerticalAlignment.Center;
        var summary = new Grid { ColumnDefinitions = new("*,Auto"), [!Grid.ColumnSpacingProperty] = GuideParts.Space(16).ToBinding(), Children = { _selectionStatus, _documentPage } };
        Grid.SetColumn(_documentPage, 1);
        var header = GuideParts.Card(GuideParts.Vertical(controls, summary)); header.Padding = new Thickness(12);
        var close = new Button { Content = "Close", IsCancel = true }; close.Click += (_, _) => Close();
        _print.Click += (_, _) => Printing = PrintSelectedAsync();
        _openPdf.Click += async (_, _) => await OpenPrintablePdfAsync();
        var body = new Border { Child = _viewport,
            [!Border.BackgroundProperty] = this.GetResourceObservable(Themes.ThemeManager.CanvasBackground).ToBinding() };
        var footer = GuideParts.Vertical(_progress, _status, GuideParts.Actions(_print, close, _openPdf));
        _status.VerticalAlignment = VerticalAlignment.Center;
        Content = GuideParts.FrameWithReachableFooter(this, header, body, footer);
        Opened += (_, _) =>
        {
            // Start keyboard focus on All without the Tab-only focus ring; pressing Tab still shows it.
            _all.Focus(NavigationMethod.Unspecified);
            Preparation = PrepareAsync();
        };
        Closed += (_, _) =>
        {
            _closed = true; _lifetime.Cancel(); ++_renderGeneration; _bitmap?.Dispose(); _bitmap = null;
            if (_renders == 0) _pdf?.Dispose();
            if (!_running) { CleanupTemporaryPdfs(); _lifetime.Dispose(); }
        };
    }

    private async Task PrepareAsync()
    {
        var token = _lifetime.Token;
        using var activity = _activities.Begin("Preparing print preview", _title, cancel: () => { if (!_closed) _lifetime.Cancel(); });
        _progress.Content = new LoadingState(_activities, activity.Id);
        try
        {
            activity.Report("Rendering the printable PDF");
            _result = await Task.Run(() => PdfExporter.Export(_markdown, new PdfExportOptions { Preview = _options, IncludeFrontMatter = false,
                RemoteImageBytes = _images, SvgImageBytes = SvgExportRasterizer.Render }, token), token);
            token.ThrowIfCancellationRequested();
            if (_result.Truncated)
            {
                _status.Text = "The PDF is incomplete and cannot be printed. Reduce the document size and try again.";
                activity.Fail(_status.Text);
                return;
            }
            activity.Report("Loading the generated PDF for preview");
            _pdf = await WindowsPdfPreview.LoadAsync(_result.Data);
            if (_closed || token.IsCancellationRequested)
            { _pdf.Dispose(); token.ThrowIfCancellationRequested(); activity.Cancel("Preview closed; no print job was submitted."); return; }
            UpdateSelection();
            activity.Report("Rendering the first preview page");
            await ShowPageAsync(1);
            token.ThrowIfCancellationRequested();
            if (_bitmap is null) { activity.Fail("The first preview page could not be displayed; no print job was submitted."); return; }
            _instructions += _result.Warnings.Count > 0 ? " Export notes: " + string.Join("; ", _result.Warnings) : "";
            _status.Text = _instructions;
            activity.Complete(_result.Warnings.Count > 0 ? string.Join("; ", _result.Warnings) : null,
                _result.Warnings.Count > 0, "Print preview ready; nothing printed");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            activity.Cancel("Print preparation canceled; no print job was submitted.");
            if (!_closed) _status.Text = "Print preparation canceled. Close and reopen Print preview to retry.";
        }
        catch (Exception ex) when (ex is ExportException or IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        { activity.Fail(ex.Message); if (!_closed) _status.Text = "Could not prepare printing: " + Tern.App.Shell.UserProblem.Plain(ex); }
    }

    internal async Task ShowPageAsync(int page)
    {
        if (_closed || _pdf is null || page < 1 || page > _pdf.PageCount) return;
        if (_ranges.IsChecked == true && Selection(out var selected, out _) && !selected.Contains(page)) page = selected[0];
        var generation = ++_renderGeneration;
        _rendering = true; _renders++; _print.IsEnabled = _openPdf.IsEnabled = false;
        using var activity = _activities.Begin("Rendering preview page", _title, queued: _pageRender.CurrentCount == 0);
        _progress.Content = new LoadingState(_activities, activity.Id);
        await _pageRender.WaitAsync();
        try
        {
            if (_closed || generation != _renderGeneration)
            { activity.Cancel("Superseded preview request; no document was changed."); return; }
            activity.Report("Rendering PDF preview page " + page);
            var size = _pdf.PageSize(page);
            var bytes = await _pdf.RenderAsync(page, 1200);
            if (_closed || generation != _renderGeneration)
            { activity.Cancel("Rendered preview was superseded; no document was changed."); return; }
            var bitmap = new Bitmap(new MemoryStream(bytes, writable: false));
            _pageWidthDips = size.Width;
            var previous = _bitmap; _bitmap = bitmap; _page.Source = bitmap; previous?.Dispose();
            if (DisplayedPage != page && _current.IsChecked == true) InvalidateOutput();
            DisplayedPage = page;
            _viewport.Offset = default;
            AutomationProperties.SetName(_page, $"Printable PDF page {page} of {_pdf.PageCount}");
            activity.Complete(outcome: "Preview page ready");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        { activity.Fail(ex.Message); if (!_closed) _status.Text = "Could not display this PDF page: " + Tern.App.Shell.UserProblem.Plain(ex); }
        finally
        {
            _pageRender.Release(); _renders--; _rendering = _renders > 0;
            if (!_closed) UpdateSelection();
            else if (_renders == 0) _pdf?.Dispose();
        }
    }

    private bool Selection(out int[] pages, out string? error)
    {
        pages = []; error = null;
        if (_result is null || _result.Truncated || _pdf is null) return false;
        if (_current.IsChecked == true) { pages = [DisplayedPage]; return true; }
        if (_ranges.IsChecked == true) return PrintPageSelection.TryParse(PageRanges.Text, _result.PageCount, out pages, out error);
        pages = Enumerable.Range(1, _result.PageCount).ToArray(); return true;
    }
    private void UpdateSelection()
    {
        PageRanges.IsEnabled = _ranges.IsChecked == true && _result is not null && !_running;
        _all.IsEnabled = _current.IsEnabled = _ranges.IsEnabled = !_running;
        var valid = Selection(out var pages, out var error);
        _selectionStatus.Text = error ?? (valid ? (_all.IsChecked == true && pages.Length != 1 ? $"All {pages.Length} pages selected" : $"{pages.Length} page{(pages.Length == 1 ? "" : "s")} selected") : "Preparing pages…");
        _navigationPages = _result is null ? [] : _ranges.IsChecked == true
            ? valid ? pages : [] : Enumerable.Range(1, _result.PageCount).ToArray();
        var position = Array.IndexOf(_navigationPages, DisplayedPage);
        _updatingNavigation = true;
        try
        {
            _number.Maximum = Math.Max(1, _navigationPages.Length);
            _number.Value = _navigationPages.Length == 0 ? null : Math.Max(1, position + 1);
        }
        finally { _updatingNavigation = false; }
        _number.IsEnabled = _pdf is not null && _navigationPages.Length > 0 && !_running;
        _previous.IsEnabled = position > 0 && !_running;
        _next.IsEnabled = position >= 0 && position + 1 < _navigationPages.Length && !_running;
        _count.Text = _navigationPages.Length == 0 ? "" : "of " + _navigationPages.Length;
        _documentPage.Text = _result is null ? "" : $"Document page {DisplayedPage} of {_result.PageCount}";
        _print.IsEnabled = valid && (_ranges.IsChecked != true || pages.Contains(DisplayedPage)) && !_running && !_rendering && !_closed;
        _openPdf.IsEnabled = _print.IsEnabled;
    }
    private void InvalidateOutput()
    {
        ++_selectionVersion;
        OutputPath = null;
        _status.Text = _instructions;
    }
    private void SelectionChanged()
    {
        InvalidateOutput();
        UpdateSelection();
        if (_pdf is not null && _ranges.IsChecked == true && Selection(out var pages, out _))
        {
            var target = pages.Contains(DisplayedPage) ? DisplayedPage : pages[0];
            if (_rendering || target != DisplayedPage) Navigation = ShowPageAsync(target);
        }
    }
    internal async Task<bool> OpenPrintablePdfAsync(string? outputPath = null)
    {
        if (_closed || _running || _rendering || !Selection(out var pages, out _)) return false;
        if (_ranges.IsChecked == true && !pages.Contains(DisplayedPage)) return false;
        var version = _selectionVersion;
        _running = true; UpdateSelection();
        using var activity = _activities.Begin("Preparing PDF reader handoff", _title);
        _progress.Content = new LoadingState(_activities, activity.Id);
        try
        {
            activity.Report("Preparing the selected PDF pages");
            if (outputPath is null)
            {
                outputPath = Path.Combine(TemporaryDirectory, Guid.NewGuid().ToString("N") + ".pdf");
                _temporaryPdfs.Add(outputPath);
            }
            var bytes = pages.Length == _result!.PageCount ? _result.Data : await ExtractPages(_result.Data, pages);
            if (_closed || version != _selectionVersion)
            { activity.Cancel("Reader handoff stopped before writing; no print job was submitted."); return false; }
            activity.Report("Writing the selected PDF copy");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            await File.WriteAllBytesAsync(outputPath, bytes);
            if (_closed || version != _selectionVersion)
            { activity.Cancel("PDF copy written, but reader handoff stopped; no print job was submitted."); return false; }
            OutputPath = outputPath;
            activity.Report("Handing the PDF copy to the configured external reader");
            if (OpenPdf(outputPath) is { } error)
            { _status.Text = "Could not open a PDF reader: " + error + " PDF saved at " + outputPath; activity.Fail(_status.Text); return false; }
            _status.Text = $"Opened {pages.Length} selected page{(pages.Length == 1 ? "" : "s")} (document pages {PageSummary(pages)}). Press Ctrl+P in your PDF reader.";
            activity.Complete(outcome: "PDF handed to external reader; printing unconfirmed");
            return true;
        }
        catch (Exception ex) when (ex is ExportException or IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { activity.Fail(ex.Message); if (!_closed && version == _selectionVersion) _status.Text = "Could not prepare printing: " + Tern.App.Shell.UserProblem.Plain(ex); return false; }
        finally
        {
            _running = false;
            if (!_closed) UpdateSelection();
            else { CleanupTemporaryPdfs(); _lifetime.Dispose(); }
        }
    }

    internal async Task<bool> PrintSelectedAsync()
    {
        if (_closed || _running || _rendering || !Selection(out var pages, out _)) return false;
        if (_ranges.IsChecked == true && !pages.Contains(DisplayedPage)) return false;
        var version = _selectionVersion;
        var token = _lifetime.Token;
        _running = true; UpdateSelection();
        using var activity = _activities.Begin("Printing selected pages", _title, cancel: () => { if (!_closed) _lifetime.Cancel(); });
        _progress.Content = new LoadingState(_activities, activity.Id);
        _printingActivity = activity;
        try
        {
            activity.Report("Preparing the selected PDF pages");
            var bytes = pages.Length == _result!.PageCount ? _result.Data : await ExtractPages(_result.Data, pages);
            if (_closed || version != _selectionVersion)
            { activity.Cancel("Stopped before printer handoff; the selected pages changed or the window closed."); return false; }
            var printed = await PrintPdf(TryGetPlatformHandle()?.Handle ?? 0, _title, bytes, token);
            if (printed) activity.Complete(outcome: "Handed to Windows printer; physical output unconfirmed");
            else activity.Cancel("Printing canceled before submission; no pages were sent.");
            if (!_closed)
                _status.Text = printed ? $"Sent {pages.Length} selected page{(pages.Length == 1 ? "" : "s")} (document pages {PageSummary(pages)}) to the chosen printer."
                    : "Printing canceled. No pages were sent.";
            return printed;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            activity.Cancel("Printing stopped. Windows may have received some pages; check its print queue.");
            if (!_closed) _status.Text = "Printing stopped. Some pages may already have reached Windows; check its print queue.";
            return false;
        }
        catch (Exception ex) when (ex is ExportException or IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        { activity.Fail(ex.Message); if (!_closed) _status.Text = "Could not print selected pages: " + Tern.App.Shell.UserProblem.Plain(ex); return false; }
        finally
        {
            _running = false;
            _printingActivity = null;
            if (!_closed) UpdateSelection();
            else { CleanupTemporaryPdfs(); _lifetime.Dispose(); }
        }
    }

    private void CleanupTemporaryPdfs()
    {
        var failures = PrintPreviewFiles.Delete(_temporaryPdfs);
        if (failures.Count == 0) { _temporaryPdfs.Clear(); return; }
        var message = "Some temporary print PDFs could not be removed. They may contain document text: " + string.Join("; ", failures);
        if (_reportCleanupError is not null) _reportCleanupError(message);
        else System.Diagnostics.Trace.TraceWarning(message);
    }
    private static string PageSummary(int[] pages)
    {
        var groups = new List<string>();
        for (var index = 0; index < pages.Length; index++)
        {
            var start = pages[index];
            while (index + 1 < pages.Length && pages[index + 1] == pages[index] + 1) index++;
            groups.Add(start == pages[index] ? start.ToString() : $"{start}–{pages[index]}");
        }
        var text = string.Join(", ", groups);
        return text.Length > 120 ? text[..117] + "…" : text;
    }
}
