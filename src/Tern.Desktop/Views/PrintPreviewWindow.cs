using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Tern.Desktop.Services;
using Tern.Export;
using Tern.Rendering.Preview;

namespace Tern.Desktop.Views;

/// <summary>A captured document preview with an explicit handoff to the installed PDF reader for paginated printing.</summary>
public sealed class PrintPreviewWindow : Window
{
    private readonly string _markdown;
    private readonly PreviewOptions _options;
    private readonly Func<string, byte[]?>? _images;
    private readonly TextBlock _status = GuideParts.Body("Preparing document preview…");
    private readonly Button _print = new() { Content = "Open printable PDF…", Classes = { "accent" }, IsEnabled = false };
    private readonly Tern.PreviewView.PreviewView _preview = new() { UseFullWidth = false, ReadingWidth = 760 };
    private bool _closed;
    private bool _running;
    internal Func<string, string?> OpenPdf { get; set; } = path => new SystemLauncher().TryOpenFile(path);
    internal string? OutputPath { get; private set; }
    internal Task Preparation { get; private set; } = Task.CompletedTask;

    public PrintPreviewWindow(string title, string markdown, PreviewOptions options, Func<string, byte[]?>? images = null)
    {
        _markdown = markdown;
        _options = options;
        _images = images;
        DialogParts.Setup(this, "Print preview", 1080, 820, stableViewport: true);
        AutomationProperties.SetName(_preview, "Print document preview");
        AutomationProperties.SetName(_status, "Print preview status");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        var close = new Button { Content = "Close", IsCancel = true };
        close.Click += (_, _) => Close();
        _print.Click += async (_, _) => await OpenPrintablePdfAsync();
        var page = GuideParts.Card(_preview);
        Content = GuideParts.Frame(GuideParts.Vertical(GuideParts.Title("Print “" + title + "”"),
            GuideParts.Caption("1. Review the document below.  2. Open the printable PDF to check page breaks.  3. Press Ctrl+P in your PDF reader to choose a printer, paper and copies.")),
            page, GuideParts.Vertical(_status, GuideParts.Actions(close, _print)));
        Opened += (_, _) => Preparation = PrepareAsync();
        Closed += (_, _) => _closed = true;
    }

    private async Task PrepareAsync()
    {
        try
        {
            var document = await Task.Run(() => PreviewBuilder.Build(_markdown, _options));
            if (_closed) return;
            _preview.Document = document;
            _print.IsEnabled = !document.Truncated;
            _status.Text = document.Truncated ? "The preview is incomplete. This document exceeds the rendering limit and cannot be printed from this preview."
                : "Document preview ready. The PDF reader shows the exact pagination before you print. Your Markdown is unchanged.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        { if (!_closed) _status.Text = "Could not prepare the preview: " + ex.Message; }
    }

    internal async Task<bool> OpenPrintablePdfAsync(string? outputPath = null)
    {
        if (_closed || _running || !_print.IsEnabled) return false;
        _running = true; _print.IsEnabled = false;
        _status.Text = "Creating the paginated PDF…";
        try
        {
            outputPath ??= Path.Combine(Path.GetTempPath(), "Tern", "PrintPreview", Guid.NewGuid().ToString("N") + ".pdf");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            var result = await Task.Run(() => PdfExporter.ExportToFile(_markdown, outputPath,
                new PdfExportOptions { Preview = _options, IncludeFrontMatter = false, RemoteImageBytes = _images, SvgImageBytes = SvgExportRasterizer.Render }));
            if (_closed) return false;
            OutputPath = outputPath;
            if (result.Truncated) { _status.Text = "The PDF is incomplete and was not opened for printing. Reduce the document size and try again."; return false; }
            if (OpenPdf(outputPath) is { } error) { _status.Text = "Could not open a PDF reader: " + error + " PDF saved at " + outputPath; return false; }
            _status.Text = $"Opened {result.PageCount} pages in your PDF reader. Press Ctrl+P there to print."
                + (result.Warnings.Count > 0 ? "\nReview these export notes before printing:\n" + string.Join("\n", result.Warnings) : "");
            return true;
        }
        catch (Exception ex) when (ex is ExportException or IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { if (!_closed) _status.Text = "Could not prepare printing: " + ex.Message; return false; }
        finally { _running = false; if (!_closed) _print.IsEnabled = true; }
    }
}
