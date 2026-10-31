using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Tern.Desktop.Services;

namespace Tern.Desktop.Tests;

public sealed class RemoteImageLoaderTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aF8sAAAAASUVORK5CYII=");
    private const string Svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"10\"><rect width=\"20\" height=\"10\" fill=\"#00aaff\"/></svg>";

    private static Task<IPAddress[]> PublicDns(string _, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });
    }

    [Fact]
    public async Task Every_request_fetches_fresh_bytes_and_never_writes_disk()
    {
        using var directory = new CacheDirectory();
        var calls = 0;
        using var cache = new RemoteImageLoader( new Handler((request, _) =>
        {
            calls++;
            Assert.True(request.Headers.CacheControl!.NoCache);
            Assert.True(request.Headers.CacheControl.NoStore);
            return Task.FromResult(Image(calls == 1 ? Png : Encoding.UTF8.GetBytes(Svg)));
        }), PublicDns);
        const string url = "https://images.example/photo";
        Assert.Equal(Png, await cache.LoadAsync(url, CancellationToken.None));
        Assert.Equal(Encoding.UTF8.GetBytes(Svg), await cache.LoadAsync(url, CancellationToken.None));
        Assert.Equal(2, calls);
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }
    [Fact]
    public async Task Relative_redirect_to_a_real_svg_returns_original_validated_bytes()
    {
        using var directory = new CacheDirectory();
        var calls = 0;
        using var cache = new RemoteImageLoader( new Handler((request, _) =>
        {
            calls++;
            if (request.RequestUri!.AbsolutePath == "/first") return Task.FromResult(Redirect("/image.svg"));
            return Task.FromResult(Image(Encoding.UTF8.GetBytes(Svg)));
        }), PublicDns);
        Assert.Equal(Encoding.UTF8.GetBytes(Svg), await cache.LoadAsync("https://images.example/first", CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Authentic_python_badge_keeps_its_embedded_logo_and_original_bytes()
    {
        using var directory = new CacheDirectory();
        var bytes = ActualRemoteBadgeFixture.Bytes;
        using var cache = new RemoteImageLoader( new Handler((_, _) => Task.FromResult(Image(bytes))), PublicDns);
        Assert.Equal(bytes, await cache.LoadAsync("https://img.shields.io/badge/Python-Object_Model-3776AB?style=for-the-badge&logo=python", CancellationToken.None));
    }

    [Fact]
    public async Task Actual_corpus_assets_are_fetched_byte_identically_without_persistence()
    {
        using var directory = new CacheDirectory();
        var token = TestContext.Current.CancellationToken;
        var assetsSetting = Environment.GetEnvironmentVariable("TERN_PARITY_ASSETS");
        var manifestSetting = Environment.GetEnvironmentVariable("TERN_PARITY_ASSETS_MANIFEST");
        var assets = new List<(string Url, byte[] Bytes)>();
        if (!string.IsNullOrWhiteSpace(assetsSetting) || !string.IsNullOrWhiteSpace(manifestSetting))
        {
            var manifest = manifestSetting ?? (Directory.Exists(assetsSetting)
                ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(assetsSetting!))!, "assets-manifest.json")
                : assetsSetting!);
            Assert.True(File.Exists(manifest), "The requested corpus asset manifest must exist; this test never silently skips it.");
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(manifest, token));
            foreach (var entry in json.RootElement.EnumerateArray())
            {
                Assert.Equal("Downloaded", entry.GetProperty("status").GetString());
                var source = entry.GetProperty("path").GetString()!;
                if (!System.IO.Path.IsPathRooted(source)) source = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(manifest)!, source);
                var bytes = await File.ReadAllBytesAsync(source, token);
                Assert.Equal(entry.GetProperty("bytes").GetInt32(), bytes.Length);
                Assert.Equal(entry.GetProperty("sha256").GetString()!.ToUpperInvariant(), Convert.ToHexString(SHA256.HashData(bytes)));
                assets.Add((entry.GetProperty("url").GetString()!, bytes));
            }
            Assert.Equal(46, assets.Count); // Exact downloaded asset set for the user-requested corpus snapshot.
        }
        else
        {
            // The normal suite remains self-contained and still exercises real SVG bytes and a valid PNG.
            assets.Add(("https://images.example/offline.png", Png));
            assets.Add(("https://img.shields.io/badge/Python-Object_Model-3776AB?style=for-the-badge&logo=python", ActualRemoteBadgeFixture.Bytes));
            Assert.Equal(2, assets.Count);
        }
        var networkAttempts = 0;
        using var cache = new RemoteImageLoader( new Handler((request, _) =>
        {
            Interlocked.Increment(ref networkAttempts);
            var bytes = assets.Single(item => new Uri(item.Url).AbsoluteUri == request.RequestUri!.AbsoluteUri).Bytes;
            return Task.FromResult(Image(bytes));
        }), PublicDns);        foreach (var (url, expected) in assets)
        {
            var actual = await cache.LoadAsync(url, token);
            Assert.NotNull(actual);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), Convert.ToHexString(SHA256.HashData(actual)));
        }
        Assert.Equal(assets.Count, networkAttempts);
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task Embedded_svg_recursion_and_cumulative_bytes_are_bounded()
    {
        using var directory = new CacheDirectory();
        var nested = "<svg width='1' height='1'/>";
        for (var i = 0; i < 10; i++) nested = "<svg width='1' height='1'><image href='data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(nested)) + "'/></svg>";
        var inner = "<svg width='1' height='1'><!--" + new string('x', 500_000) + "--></svg>";
        var cumulative = "<svg width='1' height='1'><image href='data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(inner)) + "'/></svg>";
        var payload = nested;
        using var cache = new RemoteImageLoader( new Handler((_, _) => Task.FromResult(Image(Encoding.UTF8.GetBytes(payload)))), PublicDns);
        Assert.Null(await cache.LoadAsync("https://images.example/nested.svg", CancellationToken.None));
        payload = cumulative;
        Assert.Null(await cache.LoadAsync("https://images.example/cumulative.svg", CancellationToken.None));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Theory]
    [InlineData("http://127.0.0.1/image.png")]
    [InlineData("http://2130706433/image.png")]
    [InlineData("https://10.0.0.1/image.png")]
    [InlineData("http://169.254.169.254/image.png")]
    [InlineData("http://[::1]/image.png")]
    [InlineData("http://[::ffff:127.0.0.1]/image.png")]
    [InlineData("https://localhost/image.png")]
    [InlineData("https://private.local/image.png")]
    [InlineData("https://user:password@images.example/image.png")]
    [InlineData("file://server/share/image.png")]
    [InlineData("data:image/png;base64,abc")]
    public async Task Non_public_or_non_http_destinations_never_reach_transport(string destination)
    {
        using var directory = new CacheDirectory();
        var calls = 0;
        using var cache = new RemoteImageLoader( new Handler((_, _) =>
        { calls++; return Task.FromResult(Image(Png)); }), PublicDns);
        Assert.Null(await cache.LoadAsync(destination, CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("192.168.1.2")]
    [InlineData("172.16.0.2")]
    [InlineData("100.100.100.200")]
    [InlineData("168.63.129.16")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("2002:7f00:1::1")]
    public async Task Private_dns_answers_are_denied_before_any_http_request(string address)
    {
        using var directory = new CacheDirectory();
        var calls = 0;
        using var cache = new RemoteImageLoader( new Handler((_, _) =>
        { calls++; return Task.FromResult(Image(Png)); }), (_, _) => Task.FromResult(new[] { IPAddress.Parse(address) }));
        Assert.Null(await cache.LoadAsync("https://images.example/image.png", CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Rebinding_between_preflight_and_socket_resolution_is_denied()
    {
        using var directory = new CacheDirectory();
        var resolutions = 0;
        RemoteImageLoader? cache = null;
        using var handler = new Handler(async (_, token) =>
        {
            // Exercise the same connection callback used by the production handler, without opening a socket.
            await using var connection = await cache!.ConnectPublicAsync(new DnsEndPoint("images.example", 443), token);
            return Image(Png);
        });
        cache = new RemoteImageLoader( handler, (_, _) => Task.FromResult(new[]
        { IPAddress.Parse(++resolutions == 1 ? "93.184.216.34" : "127.0.0.1") }));
        using (cache)
        {
            Assert.Null(await cache.LoadAsync("https://images.example/image.png", CancellationToken.None));
            Assert.Equal(2, resolutions);
        }
    }

    [Fact]
    public async Task Redirects_cannot_escape_to_private_addresses_and_are_limited_to_three()
    {
        using var directory = new CacheDirectory();
        var calls = 0;
        using (var cache = new RemoteImageLoader( new Handler((_, _) =>
        { calls++; return Task.FromResult(Redirect("http://10.0.0.1/image.png")); }), PublicDns))
            Assert.Null(await cache.LoadAsync("https://images.example/first", CancellationToken.None));
        Assert.Equal(1, calls);
        calls = 0;
        using (var cache = new RemoteImageLoader( new Handler((_, _) =>
        { calls++; return Task.FromResult(Redirect("/next?hop=" + calls)); }), PublicDns))
            Assert.Null(await cache.LoadAsync("https://images.example/first", CancellationToken.None));
        Assert.Equal(4, calls);
    }

    [Theory]
    [InlineData("<html><body>not an image</body></html>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' width='40000' height='10'/>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' width='5000' height='5000'/>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><script>alert(1)</script></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10' onload='x'/>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><image href='https://example.com/image.png'/></svg>")]
    [InlineData("<!DOCTYPE svg [<!ENTITY x SYSTEM 'file:///private'>]><svg width='10' height='10'>&x;</svg>")]
    public async Task Invalid_active_or_oversized_svg_is_not_returned_or_cached(string content)
    {
        using var directory = new CacheDirectory();
        using var cache = new RemoteImageLoader( new Handler((_, _) => Task.FromResult(Image(Encoding.UTF8.GetBytes(content)))), PublicDns);
        Assert.Null(await cache.LoadAsync("https://images.example/image.svg", CancellationToken.None));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task Claimed_large_responses_and_streamed_overruns_are_bounded()
    {
        using var directory = new CacheDirectory();
        using (var cache = new RemoteImageLoader( new Handler((_, _) =>
        {
            var response = Image([]);
            response.Content.Headers.ContentLength = RemoteImageLoader.MaxImageBytes + 1L;
            return Task.FromResult(response);
        }), PublicDns)) Assert.Null(await cache.LoadAsync("https://images.example/length", CancellationToken.None));
        using (var cache = new RemoteImageLoader( new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new RepeatingStream(RemoteImageLoader.MaxImageBytes + 100L)) })), PublicDns))
            Assert.Null(await cache.LoadAsync("https://images.example/chunked", CancellationToken.None));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task Timeout_returns_null_but_caller_cancellation_propagates()
    {
        using var directory = new CacheDirectory();
        static async Task<HttpResponseMessage> Slow(HttpRequestMessage _, CancellationToken token)
        { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Image(Png); }
        using (var cache = new RemoteImageLoader( new Handler(Slow), PublicDns, TimeSpan.FromMilliseconds(30)))
            Assert.Null(await cache.LoadAsync("https://images.example/timeout", CancellationToken.None));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        using var second = new RemoteImageLoader( new Handler(Slow), PublicDns);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.LoadAsync("https://images.example/cancel", canceled.Token));
    }

    [Fact]
    public async Task Loading_images_does_not_modify_existing_files()
    {
        using var directory = new CacheDirectory();
        var foreign = System.IO.Path.Combine(directory.Path, "user-note.img");
        await File.WriteAllTextAsync(foreign, "keep", TestContext.Current.CancellationToken);
        using var cache = new RemoteImageLoader( new Handler((_, _) => Task.FromResult(Image(Png))), PublicDns);
        for (var i = 0; i < 3; i++) Assert.Equal(Png, await cache.LoadAsync("https://images.example/" + i, CancellationToken.None));
        Assert.Equal("keep", await File.ReadAllTextAsync(foreign, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task Concurrent_downloads_never_exceed_four()
    {
        using var directory = new CacheDirectory();
        var active = 0;
        var maximum = 0;
        using var cache = new RemoteImageLoader( new Handler(async (_, token) =>
        {
            var current = Interlocked.Increment(ref active);
            int before;
            do { before = Volatile.Read(ref maximum); if (current <= before) break; }
            while (Interlocked.CompareExchange(ref maximum, current, before) != before);
            try { await Task.Delay(30, token); return Image(Png); }
            finally { Interlocked.Decrement(ref active); }
        }), PublicDns);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => cache.LoadAsync("https://images.example/" + i, CancellationToken.None)));
        Assert.All(results, bytes => Assert.Equal(Png, bytes));
        Assert.InRange(maximum, 1, 4);
    }

    private static HttpResponseMessage Image(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class CacheDirectory : IDisposable
    {
        public CacheDirectory() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tern-image-cache-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); }
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class RepeatingStream(long length) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, length - _position);
            buffer.AsSpan(offset, read).Clear(); _position += read; return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
