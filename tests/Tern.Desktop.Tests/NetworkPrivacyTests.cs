using System.Net;
using System.Net.Http;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Tern.App.Settings;
using Tern.Desktop.Services;
using Tern.Desktop.Views;
using Tern.Export;
using Tern.Rendering.Preview;

namespace Tern.Desktop.Tests;

public sealed class NetworkPrivacyTests
{
    private const string Svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"10\"><rect width=\"20\" height=\"10\"/></svg>";
    private const string Url = "https://images.example/pixel.svg";

    [Theory]
    [InlineData(RemoteImagePolicy.Ask, false)]
    [InlineData(RemoteImagePolicy.Never, false)]
    [InlineData(RemoteImagePolicy.Always, true)]
    public async Task Policy_is_enforced_before_DNS_for_display_and_export(RemoteImagePolicy mode, bool expected)
    {
        var settings = new AppSettings { RemoteImages = mode };
        var access = new RemoteImageAccess(settings);
        var dns = 0; var calls = 0;
        using var loader = new RemoteImageLoader(new Handler((_, _) =>
        { calls++; return Task.FromResult(Image()); }), (_, _) =>
        { dns++; return Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }); }) { AccessAllowed = access.Allows };
        Assert.Equal(expected, await loader.LoadForDocumentAsync("document-a", Url, TestContext.Current.CancellationToken) is not null);
        Assert.Equal(expected, loader.FetchForDocument("document-a", Url) is not null);
        Assert.Equal(expected ? 2 : 0, calls); Assert.Equal(expected ? 2 : 0, dns);
        settings.OfflineMode = true;
        Assert.Null(await loader.LoadForDocumentAsync("document-a", Url, TestContext.Current.CancellationToken));
        Assert.Null(loader.FetchForDocument("document-a", Url));
        Assert.Equal(expected ? 2 : 0, calls);
    }

    [Fact]
    public void Consent_is_scoped_and_Never_or_Offline_override_both_approval_types()
    {
        var settings = new AppSettings(); var access = new RemoteImageAccess(settings);
        access.AllowOnce("a"); Assert.True(access.Allows("a")); Assert.False(access.Allows("b"));
        Assert.False(new RemoteImageAccess(settings).Allows("a"));
        access.AllowAlways("b"); Assert.True(new RemoteImageAccess(settings).Allows("b"));
        settings.RemoteImageTrustedFiles.Clear(); Assert.False(access.Allows("b")); Assert.True(access.Allows("a"));
        access.AllowAlways("b");
        settings.RemoteImages = RemoteImagePolicy.Never; Assert.False(access.Allows("a")); Assert.False(access.Allows("b"));
        settings.RemoteImages = RemoteImagePolicy.Always; settings.OfflineMode = true; Assert.False(access.Allows("b"));
    }

    [Fact]
    public void Privacy_settings_and_trust_round_trip_without_storing_image_addresses()
    {
        using var folder = new Folder();
        var file = Path.Combine(folder.Path, "note.md"); File.WriteAllText(file, "synthetic");
        var key = RemoteImageAccess.DocumentKey(file, Guid.NewGuid());
        var settings = new AppSettings { OfflineMode = true };
        new RemoteImageAccess(settings).AllowAlways(key);
        var store = new SettingsStore(folder.Path); store.Save(settings); var reloaded = store.Load();
        Assert.True(reloaded.OfflineMode); Assert.Equal(RemoteImagePolicy.Ask, reloaded.RemoteImages);
        Assert.Contains(key, reloaded.RemoteImageTrustedFiles); Assert.DoesNotContain(file, File.ReadAllText(store.FilePath));
        Assert.NotEqual(key, RemoteImageAccess.DocumentKey(null, Guid.NewGuid()));
    }

    [Fact]
    public async Task Offline_URL_import_fails_before_DNS_or_transport()
    {
        var calls = 0;
        using var loader = new MarkdownUrlLoader(new Handler((_, _) => { calls++; return Task.FromResult(Image()); }),
            (_, _) => { calls++; return Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }); }) { NetworkAllowed = () => false };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => loader.LoadBundleAsync("https://example.com/note.md", TestContext.Current.CancellationToken));
        Assert.Contains("Offline", error.Message); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Offline_AI_blocks_before_client_creation_or_credential_access()
    {
        var calls = 0;
        var service = new AIWorkspaceService(Path.GetTempPath(), createClient: () => { calls++; throw new InvalidOperationException(); })
            { NetworkAllowed = () => false };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync("missing", "synthetic", TestContext.Current.CancellationToken));
        Assert.Contains("Offline", error.Message); Assert.Equal(0, calls);
        Assert.Contains("Offline", service.WritingValidationError("missing", "synthetic")!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendWritingAsync("missing", "synthetic", () => true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Offline_blocks_AI_MCP_and_Copilot_at_transport_boundaries()
    {
        var calls = 0;
        using var ai = new AIConnectionClient(new Handler((_, _) => { calls++; return Task.FromResult(Image()); })) { NetworkAllowed = () => false };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ai.SendAsync(new Uri("https://example.com/v1/chat/completions"),
            "m", "", "Synthetic", TestContext.Current.CancellationToken));
        using var mcp = new MCPConnectionClient(new Handler((_, _) => { calls++; return Task.FromResult(Image()); })) { NetworkAllowed = () => false };
        await Assert.ThrowsAsync<InvalidOperationException>(() => mcp.InitializeAsync(new Uri("https://example.com/mcp"), "", TestContext.Current.CancellationToken));
        var copilot = new CopilotTextClient((_, _, _) => { calls++; return Task.FromResult(new CopilotProcessResult(0, "synthetic")); }) { NetworkAllowed = () => false };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => copilot.SendAsync("unused", "m", "synthetic-token", "Synthetic", TestContext.Current.CancellationToken));
        Assert.Contains("Offline", error.Message); Assert.Equal(0, calls);
    }

    [Fact]
    public void Image_banner_counts_loadable_references_and_nested_images_but_not_sanitized_HTML_or_code()
    {
        var preview = PreviewBuilder.Build("![direct](https://example.com/a.png)\n\n![reference][x]\n\n[x]: https://example.com/b.png\n\n> ![quote](https://example.com/c.png)\n\n<img src=\"https://example.com/d.png\">\n\n`![code](https://example.com/no.png)`", new PreviewOptions());
        // The existing sanitizer removes blocked remote HTML src attributes entirely.
        Assert.Equal(3, RemoteImageCount.Count(preview));
        Assert.Equal(0, RemoteImageCount.Count(PreviewBuilder.Build("<img src=\"https://example.com/blocked.png\">", new PreviewOptions())));
    }

    [AvaloniaTheory]
    [InlineData(ViewMode.Source)]
    [InlineData(ViewMode.Split)]
    [InlineData(ViewMode.Reading)]
    public async Task Document_banner_and_offline_status_toggle_preserve_editor_text(ViewMode mode)
    {
        using var fixture = new DesktopFixture();
        var file = await fixture.WriteAsync("privacy.md", "# Synthetic\n![pixel](https://example.com/a.png)\n\n![reference][image]\n\n[image]: https://example.com/b.png");
        await fixture.Shell.OpenFileAsync(file);
        fixture.Shell.ViewMode = mode;
        for (var attempt = 0; attempt < 100 && fixture.Shell.ActiveTab!.Analysis is null; attempt++)
        { await Task.Delay(10); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); }
        Assert.NotNull(fixture.Shell.ActiveTab!.Analysis);
        await fixture.Window.RebuildPreviewAsync();
        Assert.True(fixture.Window.FindControl<Border>("RemoteImagesBanner")!.IsVisible);
        Assert.Contains("2 remote images", fixture.Window.FindControl<TextBlock>("RemoteImagesText")!.Text);
        var before = fixture.Shell.ActiveTab!.Session.GetSnapshot().Text;
        var once = fixture.Window.FindControl<Button>("LoadRemoteImagesOnce")!;
        once.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await fixture.Window.RebuildPreviewAsync();
        Assert.False(fixture.Window.FindControl<Border>("RemoteImagesBanner")!.IsVisible);
        var offline = fixture.Window.FindControl<ToggleButton>("OfflineToggle")!;
        offline.IsChecked = true; offline.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await fixture.Window.RebuildPreviewAsync();
        Assert.True(fixture.Shell.Settings.OfflineMode);
        Assert.False(fixture.Window.CanRunCommand(Tern.App.Commands.CommandIds.OpenUrl));
        Assert.False(fixture.Window.CanRunCommand(Tern.App.Commands.CommandIds.AskAI));
        Assert.True(fixture.Window.FindControl<Border>("RemoteImagesBanner")!.IsVisible);
        Assert.False(once.IsVisible);
        Assert.Equal(before, fixture.Shell.ActiveTab.Session.GetSnapshot().Text);
    }

    [AvaloniaFact]
    public void Offline_toggle_keeps_the_warning_when_preferences_cannot_be_saved()
    {
        using var fixture = new DesktopFixture();
        var file = Path.Combine(fixture.Shell.Paths.SettingsDirectory, "settings.json");
        Directory.CreateDirectory(fixture.Shell.Paths.SettingsDirectory);
        File.WriteAllText(file, "{}");
        using (var locked = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var toggle = fixture.Window.FindControl<ToggleButton>("OfflineToggle")!;
            toggle.IsChecked = true;
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(fixture.Shell.Settings.OfflineMode);
            Assert.Contains("Settings could not be saved", fixture.Shell.StatusMessage);
            Assert.Contains("until you close Tern", fixture.Shell.StatusMessage);
        }
        Assert.False(new SettingsStore(fixture.Shell.Paths.SettingsDirectory).Load().OfflineMode);
    }

    [AvaloniaFact]
    public async Task Never_policy_fixture_through_preview_HTML_PDF_TXT_print_and_help_makes_no_requests()
    {
        const string markdown = "# Synthetic privacy fixture\n![remote](https://example.com/pixel.svg)";
        var settings = new AppSettings { RemoteImages = RemoteImagePolicy.Never };
        var access = new RemoteImageAccess(settings); var calls = 0;
        using var loader = new RemoteImageLoader(new Handler((_, _) => { calls++; return Task.FromResult(Image()); }),
            (_, _) => { calls++; return Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }); }) { AccessAllowed = access.Allows };
        using var fixture = new DesktopFixture();
        var preview = new Tern.PreviewView.PreviewView
        {
            RemoteImageLoader = (url, token) => loader.LoadForDocumentAsync("fixture", url, token),
            Document = PreviewBuilder.Build(markdown, new PreviewOptions()),
        };
        var host = new Window { Content = preview }; host.Show();
        try
        {
            var html = HtmlExporter.Export(markdown, new HtmlExportOptions { RemoteImageBytes = url => loader.FetchForDocument("fixture", url) });
            Assert.Equal(0, html.EmbeddedImageCount); Assert.Contains("image-placeholder", html.Html);
            var pdf = PdfExporter.Export(markdown, new PdfExportOptions { RemoteImageBytes = url => loader.FetchForDocument("fixture", url) });
            Assert.True(pdf.PageCount > 0); Assert.NotEmpty(pdf.Warnings);
            Assert.Contains("Synthetic privacy fixture", PlainTextExporter.Export(markdown));
            var print = new PrintPreviewWindow("Synthetic", markdown, new PreviewOptions(), url => loader.FetchForDocument("fixture", url));
            print.Show();
            try { await print.Preparation; Assert.True(print.PageCount > 0); }
            finally { print.Close(); }
            var help = new HelpAnswerView();
            Assert.True(await help.ShowAsync(markdown, TestContext.Current.CancellationToken, () => true));
            Assert.Null(help.Preview.RemoteImageLoader);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
            Assert.Equal(0, calls);
        }
        finally { host.Close(); }
    }

    [Fact]
    public async Task Local_AI_generation_can_complete_after_ninety_seconds()
    {
        using var client = new AIConnectionClient(new Handler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(90), token);
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"Synthetic result\"}}]}", Encoding.UTF8, "application/json") };
        }));
        Assert.Equal("Synthetic result", await client.SendAsync(new Uri("http://localhost/v1/chat/completions"),
            "local-model", "", "Synthetic request", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("https://example.com/v1/chat/completions", null, 300)]
    [InlineData("http://localhost:11434/api/chat", null, 300)]
    [InlineData("https://example.com/v1/chat/completions", 900, 900)]
    public void AI_deadlines_use_connection_override_or_local_remote_defaults(string endpoint, int? configured, int expected) =>
        Assert.Equal(TimeSpan.FromSeconds(expected), AIConnectionClient.RequestTimeout(new Uri(endpoint), configured));

    [Fact]
    public async Task Configured_AI_deadline_times_out_but_caller_cancellation_remains_cancellation()
    {
        using var client = new AIConnectionClient(new Handler(async (_, token) =>
        { await Task.Delay(Timeout.Infinite, token); return Image(); }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(new Uri("http://localhost/api/chat"), "m", "", "synthetic", null,
            TestContext.Current.CancellationToken, timeoutSeconds: 1));
        Assert.Contains("timed out", error.Message);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(new Uri("http://localhost/api/chat"), "m", "", "synthetic", null,
            cancellation.Token, timeoutSeconds: 5));
    }

    [Fact]
    public void AI_connection_timeout_survives_metadata_round_trip_and_invalid_values_are_rejected()
    {
        using var folder = new Folder();
        var profile = new AIConnectionProfile(Guid.NewGuid().ToString("N"), "Local", "ollama-chat", "http://localhost:11434/api/chat", "local", TimeoutSeconds: 900);
        var store = new AIProfileStore(folder.Path); store.Save([profile]); Assert.Equal(900, Assert.Single(store.Load()).TimeoutSeconds);
        Assert.Throws<InvalidOperationException>(() => store.Save([profile with { TimeoutSeconds = 0 }]));
    }

    private static HttpResponseMessage Image() => new(HttpStatusCode.OK) { Content = new StringContent(Svg, Encoding.UTF8, "image/svg+xml") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tern-network-privacy", Guid.NewGuid().ToString("N"));
        public Folder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
