using System.Net;
using System.Net.Http;
using System.Text;
using Tern.Desktop.Services;
using Tern.App.Documents;
using Tern.Domain.Documents;
using Tern.Domain.Text;
using Tern.Filesystem;
using Tern.Markdown;
using Tern.Rendering.Resources;

namespace Tern.Desktop.Tests;

public sealed class MarkdownUrlBundleTests
{
    [Fact]
    public async Task Redirect_relative_reference_HTML_assets_and_query_collisions_cache_and_save_portably()
    {
        using var folder = new TestFolder();
        var requests = new List<string>();
        const string original = "![one](<images/a%20b.png?q=1#part>)\n![same][pic]\n[pic]: images/a%20b.png?q=1\n![different](images/a%20b.png?q=2)\n<img src='images/html.png'>\n[PDF](../file.pdf)\n[Website](../guide)\n`![code](ignored.png)`";
        using var loader = Loader((uri, _) =>
        {
            requests.Add(uri.PathAndQuery);
            return Task.FromResult(uri.AbsolutePath switch
            {
                "/start.md" => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("/docs/note.md", UriKind.Relative) } },
                "/docs/note.md" => Text(original),
                _ => Binary(Encoding.UTF8.GetBytes(uri.PathAndQuery))
            });
        });
        var document = await loader.LoadBundleAsync("https://example.com/start.md", TestContext.Current.CancellationToken);
        Assert.Empty(document.Warnings); Assert.Equal(original, document.OriginalText);
        Assert.Equal(4, document.Assets.Count);
        Assert.Equal(1, requests.Count(r => r == "/docs/images/a%20b.png?q=1"));
        Assert.Contains("https://example.com/guide", document.Text, StringComparison.Ordinal);
        Assert.Contains("`![code](ignored.png)`", document.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(requests, p => p.Contains("ignored", StringComparison.Ordinal));
        Assert.Contains("#part", document.Text, StringComparison.Ordinal);
        var cached = await MarkdownUrlCache.WriteAsync(folder.Path, "https://example.com/start.md", document, TestContext.Current.CancellationToken);
        var offline = await MarkdownUrlCache.ReadAsync(folder.Path, "https://example.com/start.md", TestContext.Current.CancellationToken);
        Assert.NotNull(offline); Assert.Equal(cached.Text, offline.Text); Assert.Equal(4, offline.Assets.Count);
        var resolver = new SafeResourceResolver(new ResourceResolverOptions { DocumentDirectory = System.IO.Path.GetDirectoryName(offline.CachedPath) });
        foreach (var image in offline.Assets.Keys.Where(k => k.EndsWith(".png", StringComparison.Ordinal))) Assert.IsType<LocalFile>(resolver.ResolveImage(image));
        var format = LineEndingAnalyzer.Describe(TextEncodingKind.Utf8, false, document.Text);
        using var tab = new DocumentTab(new DocumentSession(null, document.Text, format, null), new DocumentFileService(), MarkdownProfile.Extended)
        { ResourceDocumentPath = cached.CachedPath, ResourceFiles = document.Assets };
        var saved = System.IO.Path.Combine(folder.Path, "portable", "note.md");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(saved)!);
        Assert.IsType<SaveSucceeded>(await tab.SaveAsAsync(saved, false, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(document.Text, await File.ReadAllTextAsync(saved, TestContext.Current.CancellationToken));
        foreach (var (name, bytes) in document.Assets) Assert.Equal(bytes, await File.ReadAllBytesAsync(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(saved)!, name), TestContext.Current.CancellationToken));
        var moved = System.IO.Path.Combine(folder.Path, "moved");
        Directory.Move(System.IO.Path.GetDirectoryName(saved)!, moved);
        foreach (var image in document.Assets.Keys.Where(k => k.EndsWith(".png", StringComparison.Ordinal)))
            Assert.IsType<LocalFile>(new SafeResourceResolver(new ResourceResolverOptions { DocumentDirectory = moved }).ResolveImage(image));
    }

    [Fact]
    public async Task Partial_assets_are_reported_left_absolute_and_retried_without_replacing_open_snapshot()
    {
        using var folder = new TestFolder();
        var failed = true;
        using var loader = Loader((uri, _) => Task.FromResult(uri.AbsolutePath.EndsWith(".md", StringComparison.Ordinal)
            ? Text("![a](a.png) ![b](b.png)") : failed && uri.AbsolutePath == "/b.png" ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Binary([1, 2, 3])));
        var partial = await loader.LoadBundleAsync("https://example.com/note.md", TestContext.Current.CancellationToken);
        Assert.Single(partial.Warnings); Assert.Contains("Permission denied", partial.Warnings[0], StringComparison.Ordinal);
        Assert.Contains("https://example.com/b.png", partial.Text, StringComparison.Ordinal);
        var first = await MarkdownUrlCache.WriteAsync(folder.Path, "https://example.com/note.md", partial, TestContext.Current.CancellationToken);
        Assert.Null(await MarkdownUrlCache.ReadAsync(folder.Path, "https://example.com/note.md", TestContext.Current.CancellationToken));
        failed = false;
        var complete = await loader.LoadBundleAsync("https://example.com/note.md", TestContext.Current.CancellationToken);
        var second = await MarkdownUrlCache.WriteAsync(folder.Path, "https://example.com/note.md", complete, TestContext.Current.CancellationToken);
        Assert.NotEqual(first.CachedPath, second.CachedPath); Assert.True(File.Exists(first.CachedPath));
        var read = await MarkdownUrlCache.ReadAsync(folder.Path, "https://example.com/note.md", TestContext.Current.CancellationToken);
        Assert.NotNull(read); Assert.Equal(2, read.Assets.Count);
    }

    [Fact]
    public async Task Cancellation_during_asset_fetch_publishes_nothing()
    {
        using var folder = new TestFolder();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var loader = Loader(async (uri, token) =>
        {
            if (uri.AbsolutePath.EndsWith(".md", StringComparison.Ordinal)) return Text("![a](a.png)");
            cancellation.Cancel(); await Task.Delay(Timeout.Infinite, token); return Binary([]);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loader.LoadBundleAsync("https://example.com/note.md", cancellation.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Path));
    }

    [Theory]
    [InlineData("Text/Html")]
    [InlineData("application/octet-stream")]
    public async Task Image_signin_page_is_reported_as_asset_failure_instead_of_a_complete_cached_image(string media)
    {
        using var loader = Loader((uri, _) => Task.FromResult(uri.AbsolutePath.EndsWith(".md", StringComparison.Ordinal)
            ? Text("![picture](private.png)")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("  <!doctype HTML><html>Sign in</html>", Encoding.UTF8, media) }));
        var document = await loader.LoadBundleAsync("https://example.com/note.md", TestContext.Current.CancellationToken);
        Assert.Empty(document.Assets);
        Assert.Contains("web/sign-in page", Assert.Single(document.Warnings), StringComparison.Ordinal);
        Assert.Equal("![picture](https://example.com/private.png)", document.Text);
    }

    [Fact]
    public async Task Conflicting_asset_prevents_save_without_overwriting_user_files_or_marking_saved()
    {
        using var folder = new TestFolder();
        var relative = "assets/" + new string('a', 64) + ".png";
        Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "assets"));
        await File.WriteAllBytesAsync(System.IO.Path.Combine(folder.Path, relative), [9], TestContext.Current.CancellationToken);
        const string text = "![a](assets/a.png)";
        using var tab = new DocumentTab(new DocumentSession(null, text, TextFileFormat.NewFileDefault, null), new DocumentFileService(), MarkdownProfile.Extended)
        { ResourceFiles = new Dictionary<string, byte[]> { [relative] = [1, 2] } };
        var destination = System.IO.Path.Combine(folder.Path, "note.md");
        Assert.IsType<SaveFailed>(await tab.SaveAsAsync(destination, false, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(tab.FilePath); Assert.False(File.Exists(destination));
        Assert.Equal(new byte[] { 9 }, await File.ReadAllBytesAsync(System.IO.Path.Combine(folder.Path, relative), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "not found")]
    [InlineData(HttpStatusCode.Unauthorized, "Authentication required")]
    [InlineData(HttpStatusCode.Forbidden, "Permission denied")]
    [InlineData(HttpStatusCode.GatewayTimeout, "timed out")]
    [InlineData(HttpStatusCode.TooManyRequests, "limiting requests")]
    public async Task HTTP_failures_have_distinct_actionable_messages(HttpStatusCode status, string expected)
    {
        using var loader = Loader((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => loader.LoadAsync("https://example.com/note.md", TestContext.Current.CancellationToken));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    private static MarkdownUrlLoader Loader(Func<Uri, CancellationToken, Task<HttpResponseMessage>> send) => new(new Handler(send), (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
    private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/markdown") };
    private static HttpResponseMessage Binary(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private sealed class Handler(Func<Uri, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request.RequestUri!, cancellationToken); }
    private sealed class TestFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tern-bundle-tests", Guid.NewGuid().ToString("N"));
        public TestFolder() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }
}
