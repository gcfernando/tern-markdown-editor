using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Tern.Desktop.Services;
using Tern.Desktop.Themes;

namespace Tern.Desktop.Views;

/// <summary>Explicit URL import; returns text only after a complete validated response.</summary>
public sealed class OpenUrlWindow : Window
{
    public bool DownloadRequested { get; private set; }

    public OpenUrlWindow() : this(new MarkdownUrlLoader(), cacheRoot: MarkdownUrlCache.Root(Tern.App.Shell.AppPaths.Default().StateDirectory)) { }
    public OpenUrlWindow(string? initialUrl, bool openImmediately = false) : this(new MarkdownUrlLoader(), initialUrl, openImmediately, MarkdownUrlCache.Root(Tern.App.Shell.AppPaths.Default().StateDirectory)) { }
    internal OpenUrlWindow(MarkdownUrlLoader loader, string? initialUrl = null, bool openImmediately = false, string? cacheRoot = null)
    {
        DialogParts.Setup(this, "Open Markdown URL", 660, 440);
        MinWidth = 500; MinHeight = 240;
        FontSize = GuideParts.Scaled(14);
        var url = new TextBox { PlaceholderText = "Paste an HTTP or HTTPS Markdown URL", MaxLength = 8192, FontSize = GuideParts.Scaled(14) };
        AutomationProperties.SetName(url, "Markdown URL");
        var open = new Button { Content = "Open", IsDefault = true };
        var download = new Button { Content = "Download and Open" };
        AutomationProperties.SetName(download, "Download and Open");
        var cancel = new Button { Content = "Cancel request", IsEnabled = false };
        var close = new Button { Content = "Close", IsCancel = true };
        var status = GuideParts.Body("No request has been made."); AutomationProperties.SetName(status, "Markdown URL status");
        var fetchAddress = GuideParts.Body("Paste a URL to preview the file address. No request starts yet.");
        AutomationProperties.SetName(fetchAddress, "Markdown fetch address");
        CancellationTokenSource? request = null; var closed = false;
        void SetStatus(string message, bool error = false)
        {
            status.Text = message;
            status[!TextBlock.ForegroundProperty] = status.GetResourceObservable(error ? ThemeManager.Danger : ThemeManager.Text).ToBinding();
            if (error) Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!closed) status.BringIntoView();
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        }
        bool ValidateInput(bool updateStatus)
        {
            var valid = false;
            try
            {
                fetchAddress.Text = "File address to fetch:\n" + MarkdownUrlLoader.GetFetchAddress(url.Text ?? "").AbsoluteUri;
                valid = true;
                if (updateStatus) SetStatus("Ready to request this address. No request has been made yet.");
            }
            catch (ArgumentException ex)
            {
                fetchAddress.Text = "Use a public Markdown file link or an explicit localhost server address.";
                if (updateStatus) SetStatus(string.IsNullOrWhiteSpace(url.Text)
                    ? "Enter a Markdown file URL to enable Open or Download and Open." : ex.Message,
                    error: !string.IsNullOrWhiteSpace(url.Text));
            }
            open.IsEnabled = download.IsEnabled = valid && request is null && !closed;
            return valid;
        }
        // TextChanged is deferred. Validate the property itself so keyboard/default
        // actions can never run with eligibility from the preceding text value.
        url.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && request is null) ValidateInput(updateStatus: true);
        };
        async Task FetchAsync(bool downloadRequested = false)
        {
            if (request is not null || closed) return;
            if (!ValidateInput(updateStatus: false)) { ValidateInput(updateStatus: true); return; }
            DownloadRequested = downloadRequested;
            var approved = url.Text ?? "";
            using var operation = new CancellationTokenSource(); request = operation;
            open.IsEnabled = false; download.IsEnabled = false; cancel.IsEnabled = true; url.IsReadOnly = true;
            SetStatus("Fetching the Markdown file…");
            try
            {
                SetStatus("Fetching Markdown and referenced files/images…");
                var cached = !downloadRequested && cacheRoot is not null ? await MarkdownUrlCache.ReadAsync(cacheRoot, approved, operation.Token) : null;
                var document = cached ?? await loader.LoadBundleAsync(approved, operation.Token);
                if (!closed && !operation.IsCancellationRequested) { SetStatus("Markdown loaded. Opening a cached copy…"); Close(document); }
            }
            catch (OperationCanceledException) { if (!closed) SetStatus("Canceled. No document was opened or saved."); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { if (!closed) SetStatus(ex.Message, error: true); }
            finally { request = null; cancel.IsEnabled = false; url.IsReadOnly = false; ValidateInput(updateStatus: false); }
        }
        open.Click += async (_, _) => await FetchAsync();
        download.Click += async (_, _) => await FetchAsync(downloadRequested: true);
        var reopening = openImmediately && !string.IsNullOrWhiteSpace(initialUrl);
        var autoRequested = reopening;
        Opened += async (_, _) => { if (autoRequested) { autoRequested = false; await FetchAsync(); } };
        cancel.Click += (_, _) => request?.Cancel();
        close.Click += (_, _) => Close();
        Closed += (_, _) => { closed = true; request?.Cancel(); loader.Dispose(); };
        var details = new Expander
        {
            Header = "Link details and limits",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = GuideParts.Vertical(fetchAddress,
                GuideParts.Body("Public GitHub file links convert automatically to raw content. Other sites may need their Raw or Download link. A server running on this computer can use localhost, 127.0.0.1 or ::1. No sign-in is supplied."),
                GuideParts.Body("Markdown: 10 MB. Up to 128 directly referenced files/images, 20 MB each and 100 MB total. Linked web pages are not crawled. Open keeps a local cache; Download and Open saves portable relative links with an assets folder. Missing assets are reported. HTTP is unencrypted; use HTTPS when available."))
        };
        AutomationProperties.SetName(details, "Markdown URL details");
        var body = GuideParts.Vertical(
            new TextBlock { Text = "Markdown URL", FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, url,
            GuideParts.Caption("Open uses the local cache. Download and Open lets you choose where to save the file and its assets."),
            details, status);
        var actions = GuideParts.Actions(download, open, cancel, close);
        var frame = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 16 };
        frame.Children.Add(new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        frame.Children.Add(actions); Grid.SetRow(actions, 1);
        Content = frame;
        if (initialUrl is not null) url.Text = initialUrl;
        ValidateInput(updateStatus: true);
    }
}
