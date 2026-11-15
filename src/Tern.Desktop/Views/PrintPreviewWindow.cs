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

namespace Tern.Desktop.Views;

/// <summary>Paginated PDF preview and selected-page handoff. Opening it never submits a print job.</summary>
public sealed class PrintPreviewWindow : Window
{
    private readonly string _markdown;
    private readonly PreviewOptions _options;
    private readonly Func<string, byte[]?>? _images;
    private readonly TextBlock _status = GuideParts.Caption("Preparing printable pages…");
    private readonly TextBlock _selectionStatus = GuideParts.Caption("");
    private readonly TextBlock _documentPage = GuideParts.Caption("");
    private readonly Button _print = new() { Content = "Open selected PDF…", Classes = { "accent" }, IsEnabled = false };
    private readonly Image _page = new() { Stretch = Stretch.Uniform };
    private readonly ScrollViewer _viewport;
    private readonly NumericUpDown _number = new() { Minimum = 1, Maximum = 1, Value = 1, Increment = 1, Width = GuideParts.Scaled(60), ShowButtonSpinner = false, FormatString = "0", IsEnabled = false };
    private readonly TextBlock _count = GuideParts.Body("of 0");
    private readonly RadioButton _all = new() { Content = "All", GroupName = "Print pages", IsChecked = true };
    private readonly RadioButton _current = new() { Content = "Current page", GroupName = "Print pages" };
    private readonly RadioButton _ranges = new() { Content = "Pages", GroupName = "Print pages" };
    private readonly Button _previous = new() { Content = "‹" };
    private readonly Button _next = new() { Content = "›" };
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
    private string _instructions = "Open the selected PDF, then use Ctrl+P in your PDF reader for printer and copies.";
    private readonly SemaphoreSlim _pageRender = new(1, 1);
    internal int PageCount => _result?.PageCount ?? 0;
    internal int DisplayedPage { get; private set; } = 1;
    internal Func<string, string?> OpenPdf { get; set; } = path => new SystemLauncher().TryOpenFile(path);
    internal Func<byte[], int[], Task<byte[]>> ExtractPages { get; set; } = (data, pages) => Task.Run(() => PrintPageSelection.Extract(data, pages));
    internal string? OutputPath { get; private set; }
    internal Task Preparation { get; private set; } = Task.CompletedTask;
    internal Task Navigation { get; private set; } = Task.CompletedTask;

    public PrintPreviewWindow(string title, string markdown, PreviewOptions options, Func<string, byte[]?>? images = null)
    {
        _markdown = markdown; _options = options; _images = images;
        DialogParts.Setup(this, "Print preview — " + title, 1080, 860, stableViewport: true, guidance: false);
        AutomationProperties.SetName(_page, "Printable PDF page");
        AutomationProperties.SetName(_status, "Print preview status");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(PageRanges, "Pages to print, for example 1-3, 5");
        AutomationProperties.SetName(_number, "Preview page number");
        AutomationProperties.SetName(_previous, "Previous preview page"); AutomationProperties.SetName(_next, "Next preview page");
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
            option.IsCheckedChanged += (_, _) => { if (option.IsChecked == true) SelectionChanged(); };
        PageRanges.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) SelectionChanged(); };
        var pageLabel = GuideParts.Caption("Preview page");
        var controls = new WrapPanel { ItemSpacing = 12, LineSpacing = 10, Children = { _all, _current, _ranges, PageRanges,
            new Border { Width = 1, Height = 24, Margin = new Thickness(4, 0), Background = Brushes.Gray }, pageLabel, _previous, _number, _count, _next, zoom } };
        foreach (var control in controls.Children)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            if (control is RadioButton or TextBox or NumericUpDown or Button or ComboBox) control.MinHeight = GuideParts.Scaled(34);
        }
        _previous.MinWidth = _next.MinWidth = GuideParts.Scaled(34);
        _count.VerticalAlignment = VerticalAlignment.Center;
        var summary = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 16, Children = { _selectionStatus, _documentPage } };
        Grid.SetColumn(_documentPage, 1);
        var header = GuideParts.Card(GuideParts.Vertical(controls, summary)); header.Padding = new Thickness(12);
        var close = new Button { Content = "Close", IsCancel = true }; close.Click += (_, _) => Close();
        _print.Click += async (_, _) => await OpenPrintablePdfAsync();
        var body = new Border { Background = new SolidColorBrush(Color.Parse("#30393F")), Child = _viewport };
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12, Children = { _status, GuideParts.Actions(close, _print) } };
        Grid.SetColumn(footer.Children[1], 1);
        _status.VerticalAlignment = VerticalAlignment.Center;
        Content = GuideParts.Frame(header, body, footer);
        Opened += (_, _) => Preparation = PrepareAsync();
        Closed += (_, _) => { _closed = true; ++_renderGeneration; _bitmap?.Dispose(); _bitmap = null; if (_renders == 0) _pdf?.Dispose(); };
    }

    private async Task PrepareAsync()
    {
        try
        {
            _result = await Task.Run(() => PdfExporter.Export(_markdown, new PdfExportOptions { Preview = _options, IncludeFrontMatter = false,
                RemoteImageBytes = _images, SvgImageBytes = SvgExportRasterizer.Render }));
            if (_closed) return;
            if (_result.Truncated) { _status.Text = "The PDF is incomplete and cannot be printed. Reduce the document size and try again."; return; }
            _pdf = await WindowsPdfPreview.LoadAsync(_result.Data);
            if (_closed) { _pdf.Dispose(); return; }
            UpdateSelection();
            await ShowPageAsync(1);
            _instructions += _result.Warnings.Count > 0 ? " Export notes: " + string.Join("; ", _result.Warnings) : "";
            _status.Text = _instructions;
        }
        catch (Exception ex) when (ex is ExportException or IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        { if (!_closed) _status.Text = "Could not prepare printing: " + ex.Message; }
    }

    internal async Task ShowPageAsync(int page)
    {
        if (_closed || _pdf is null || page < 1 || page > _pdf.PageCount) return;
        if (_ranges.IsChecked == true && Selection(out var selected, out _) && !selected.Contains(page)) page = selected[0];
        var generation = ++_renderGeneration;
        _rendering = true; _renders++; _print.IsEnabled = false;
        await _pageRender.WaitAsync();
        try
        {
            if (_closed || generation != _renderGeneration) return;
            var size = _pdf.PageSize(page);
            var bytes = await _pdf.RenderAsync(page, 1200);
            if (_closed || generation != _renderGeneration) return;
            var bitmap = new Bitmap(new MemoryStream(bytes, writable: false));
            _pageWidthDips = size.Width;
            var previous = _bitmap; _bitmap = bitmap; _page.Source = bitmap; previous?.Dispose();
            if (DisplayedPage != page && _current.IsChecked == true) InvalidateOutput();
            DisplayedPage = page;
            _viewport.Offset = default;
            AutomationProperties.SetName(_page, $"Printable PDF page {page} of {_pdf.PageCount}");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        { if (!_closed) _status.Text = "Could not display this PDF page: " + ex.Message; }
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
        PageRanges.IsEnabled = _ranges.IsChecked == true && _result is not null;
        var valid = Selection(out var pages, out var error);
        _selectionStatus.Text = error ?? (valid ? (_all.IsChecked == true ? $"All {pages.Length} pages selected" : $"{pages.Length} page{(pages.Length == 1 ? "" : "s")} selected") : "Preparing pages…");
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
        try
        {
            outputPath ??= Path.Combine(Path.GetTempPath(), "Tern", "PrintPreview", Guid.NewGuid().ToString("N") + ".pdf");
            var bytes = pages.Length == _result!.PageCount ? _result.Data : await ExtractPages(_result.Data, pages);
            if (_closed || version != _selectionVersion) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            await File.WriteAllBytesAsync(outputPath, bytes);
            if (_closed || version != _selectionVersion) return false;
            OutputPath = outputPath;
            if (OpenPdf(outputPath) is { } error) { _status.Text = "Could not open a PDF reader: " + error + " PDF saved at " + outputPath; return false; }
            _status.Text = $"Opened {pages.Length} selected page{(pages.Length == 1 ? "" : "s")} (document pages {PageSummary(pages)}). Press Ctrl+P in your PDF reader.";
            return true;
        }
        catch (Exception ex) when (ex is ExportException or IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { if (!_closed && version == _selectionVersion) _status.Text = "Could not prepare printing: " + ex.Message; return false; }
        finally { _running = false; if (!_closed) UpdateSelection(); }
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
