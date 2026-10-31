using System.Net;
using System.Net.Http;
using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.Services;
using Tern.Desktop.ViewModels;
using Tern.Desktop.Views;
using Tern.Markdown;
using Tern.Rendering.Resources;

namespace Tern.Desktop.Tests;

public sealed class CompleteDownloadWorkflowTests
{
    private const string Url = "http://localhost:8000/note.md";

    [AvaloniaFact]
    public async Task Open_caches_the_document_and_dependencies_then_recent_reopens_without_network()
    {
        using var fixture = new DesktopFixture();
        var server = new AssetServer();
        MarkdownUrlDocument document;
        using (var loader = Loader(server)) document = await loader.LoadBundleAsync(Url);
        await fixture.Window.OpenUrlDocumentAsync(document);
        var opened = Assert.Single(fixture.Shell.Tabs);
        Assert.Equal(2, opened.ResourceFiles.Count);
        Assert.True(File.Exists(opened.ResourceDocumentPath));
        Assert.True(await fixture.Shell.CloseTabAsync(opened));
        server.Offline = true;
        fixture.Window.OnlineLoaderFactory = () => Loader(server);
        await fixture.Window.ShowNavAsync("Recent");
        var list = fixture.Window.FindControl<ListBox>("SideList")!;
        list.SelectedItem = Assert.Single(list.Items.OfType<SideRow>(), row => row.IsOnline);
        await UntilAsync(fixture, () => fixture.Shell.Tabs.Count == 1 && fixture.Shell.ActiveTab!.ResourceFiles.Count == 2);
        var reopened = Assert.Single(fixture.Shell.Tabs);
        Assert.Equal(document.Text, reopened.CurrentText);
        Assert.Equal(3, server.Requests);
        AssertLocalResources(reopened.ResourceDocumentPath!, document);
        Assert.False(reopened.IsDirty);
    }

    [AvaloniaFact]
    public async Task Download_and_Open_button_saves_all_assets_and_the_complete_folder_is_portable()
    {
        using var fixture = new DesktopFixture();
        var server = new AssetServer();
        var dialog = new OpenUrlWindow(Loader(server), Url);
        var result = dialog.ShowDialog<MarkdownUrlDocument?>(fixture.Window);
        var download = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Download and Open"));
        download.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var document = await result.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.NotNull(document);
        Assert.True(dialog.DownloadRequested);
        var destination = Path.Combine(fixture.Docs, "portable", "note.md");
        fixture.Dialogs.SavePath = destination;
        await fixture.Window.DownloadUrlDocumentAsync(document);
        Assert.Equal(destination, fixture.Shell.ActiveTab!.FilePath);
        AssertLocalResources(destination, document);
        Assert.False(fixture.Shell.ActiveTab.IsDirty);
        Assert.True(await fixture.Shell.CloseTabAsync(fixture.Shell.ActiveTab));
        var moved = Path.Combine(fixture.Docs, "moved");
        // Native watcher teardown and background Windows readers can briefly
        // retain a sharing handle. Permanent locks still fail this check.
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Move(Path.GetDirectoryName(destination)!, moved); break; }
            catch (Exception ex) when (attempt < 20 && ex is IOException or UnauthorizedAccessException)
            { await Task.Delay(25, TestContext.Current.CancellationToken); Dispatcher.UIThread.RunJobs(); }
        }
        AssertLocalResources(Path.Combine(moved, "note.md"), document);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Library_download_saves_complete_assets_and_can_use_a_complete_cache_offline(bool cached)
    {
        using var fixture = new DesktopFixture();
        var server = new AssetServer();
        MarkdownUrlDocument expected;
        using (var loader = Loader(server)) expected = await loader.LoadBundleAsync(Url);
        if (cached)
        {
            await MarkdownUrlCache.WriteAsync(MarkdownUrlCache.Root(fixture.Shell.Paths.StateDirectory), Url, expected);
            server.Offline = true;
        }
        var previousRequests = server.Requests;
        var destination = Path.Combine(fixture.Docs, "library", "note.md");
        fixture.Dialogs.SavePath = destination;
        fixture.Window.OnlineLoaderFactory = () => Loader(server);
        fixture.Shell.Recent.AddUrl(Url);
        await fixture.Window.ShowNavAsync("Recent");
        Pump(fixture);
        var button = Assert.Single(fixture.Window.GetVisualDescendants().OfType<Button>(),
            control => control.IsEffectivelyVisible && AutomationProperties.GetName(control) == "Download online file");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(fixture, () => fixture.Shell.ActiveTab?.FilePath == destination);
        AssertLocalResources(destination, expected);
        Assert.Equal(previousRequests + (cached ? 0 : 3), server.Requests);
    }

    private static void AssertLocalResources(string path, MarkdownUrlDocument document)
    {
        Assert.Equal(document.Text, File.ReadAllText(path));
        foreach (var asset in document.Assets)
            Assert.Equal(asset.Value, File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(path)!, asset.Key)));
        var resolver = new SafeResourceResolver(new ResourceResolverOptions { DocumentDirectory = Path.GetDirectoryName(path) });
        foreach (var image in MarkdownAnalyzer.Analyze(document.Text, MarkdownProfile.Extended).Links.Where(link => link.Kind == LinkKind.Image))
            Assert.IsType<LocalFile>(resolver.ResolveImage(image.Destination));
    }

    private static MarkdownUrlLoader Loader(AssetServer server) => new(new Handler(server), (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
    private static async Task UntilAsync(DesktopFixture fixture, Func<bool> complete)
    {
        for (var i = 0; i < 150; i++)
        { Pump(fixture); if (complete()) return; await Task.Delay(10, TestContext.Current.CancellationToken); }
        Assert.True(complete(), fixture.Shell.StatusMessage);
    }
    private static void Pump(DesktopFixture fixture)
    { Dispatcher.UIThread.RunJobs(); fixture.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private sealed class AssetServer
    {
        public int Requests;
        public bool Offline;
        public byte[] ImageBytes { get; } = CreateImage();
        private static byte[] CreateImage()
        {
            using var pixels = new SkiaSharp.SKBitmap(2, 2);
            pixels.Erase(SkiaSharp.SKColors.Teal);
            using var image = SkiaSharp.SKImage.FromBitmap(pixels);
            using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            return encoded.ToArray();
        }
    }
    private sealed class Handler(AssetServer server) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            server.Requests++;
            if (server.Offline) throw new HttpRequestException("Offline acceptance check must use the local cache.");
            HttpContent body = request.RequestUri!.AbsolutePath switch
            {
                "/note.md" => new StringContent("# Offline note\n\n![Picture](image.png)\n\n[Manual](manual.pdf)\n", Encoding.UTF8, "text/markdown"),
                "/image.png" => new ByteArrayContent(server.ImageBytes),
                _ => new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.4\nSample dependency")),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
        }
    }
}
