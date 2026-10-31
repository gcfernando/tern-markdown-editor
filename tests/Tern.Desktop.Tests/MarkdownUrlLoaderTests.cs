using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Tern.Desktop.Services;

namespace Tern.Desktop.Tests;

public sealed class MarkdownUrlLoaderTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo/blob/main/README.md?plain=1#intro", "/owner/repo/main/README.md")]
    [InlineData("https://github.com/owner/repo/blob/feature%2Ftopic/docs/A%20B.md?raw=true", "/owner/repo/feature%2Ftopic/docs/A%20B.md")]
    [InlineData("https://github.com/owner/repo/blob/feature/topic/docs/guide.markdown", "/owner/repo/feature/topic/docs/guide.markdown")]
    public async Task Public_GitHub_file_links_fetch_raw_content_without_web_query_options(string url, string expectedPath)
    {
        var resolvedHosts = new List<string>();
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal("raw.githubusercontent.com", request.RequestUri!.Host);
            Assert.Equal("https", request.RequestUri.Scheme);
            Assert.Equal(expectedPath, request.RequestUri.AbsolutePath);
            Assert.Empty(request.RequestUri.Query); Assert.Empty(request.RequestUri.Fragment);
            return Task.FromResult(Text("# GitHub copy"));
        });
        using var loader = new MarkdownUrlLoader(handler, (host, token) => { resolvedHosts.Add(host); return PublicDns(host, token); });
        var document = await loader.LoadAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal("# GitHub copy", document.Text); Assert.Equal("raw.githubusercontent.com", document.SourceUrl.Host);
        Assert.Equal(new[] { "raw.githubusercontent.com" }, resolvedHosts); Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("https://github.com.evil.example/owner/repo/blob/main/a.md")]
    [InlineData("https://enterprise.example/owner/repo/blob/main/a.md")]
    [InlineData("https://github.com/owner/repo/issues/1")]
    [InlineData("https://github.com/owner/repo/blob/main/picture.png")]
    public void Unrelated_hosts_and_non_Markdown_pages_are_not_rewritten(string url)
    { Assert.Equal(new Uri(url), MarkdownUrlLoader.GetFetchAddress(url)); }

    [Theory]
    [InlineData("http://example.com/Readme.md")]
    [InlineData("https://example.com/Readme.md")]
    public async Task Explicit_public_HTTP_and_HTTPS_return_exact_unsaved_copy_data(string url)
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Null(request.Headers.Authorization);
            return Task.FromResult(Text("# Café 😀\n\nOriginal  spacing.\n"));
        });
        using var loader = new MarkdownUrlLoader(handler, PublicDns);
        var result = await loader.LoadAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal("# Café 😀\n\nOriginal  spacing.\n", result.Text);
        Assert.Equal("Readme.md", result.SuggestedFileName); Assert.Equal(new Uri(url), result.SourceUrl); Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("file:///C:/notes.md")]
    [InlineData("https://user:secret@example.com/a.md")]
    [InlineData("http://10.0.0.1/a.md")]
    [InlineData("http://169.254.169.254/a.md")]
    public async Task Private_and_credential_addresses_never_reach_transport(string url)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Text("never")));
        using var loader = new MarkdownUrlLoader(handler, PublicDns);
        await Assert.ThrowsAsync<ArgumentException>(() => loader.LoadAsync(url, TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("http://localhost:8000/a.md")]
    [InlineData("http://127.0.0.1:8000/a.md")]
    [InlineData("http://[::1]:8000/a.md")]
    public async Task Explicit_local_server_inputs_return_exact_text(string url)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Text("# Local copy\n")));
        using var loader = new MarkdownUrlLoader(handler, LoopbackDns);
        var document = await loader.LoadAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal("# Local copy\n", document.Text); Assert.Equal(new Uri(url), document.SourceUrl); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Localhost_must_not_resolve_to_mixed_or_public_addresses()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Text("never")));
        using var loader = new MarkdownUrlLoader(handler, (_, _) => Task.FromResult(new[] { IPAddress.Loopback, IPAddress.Parse("93.184.216.34") }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => loader.LoadAsync("http://localhost:8000/a.md", TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("http://localhost:8000/private.md")]
    [InlineData("http://127.0.0.1:8000/private.md")]
    [InlineData("http://[::1]:8000/private.md")]
    public async Task Public_request_cannot_redirect_to_loopback(string target)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Redirect(target)));
        using var loader = new MarkdownUrlLoader(handler, PublicDns);
        await Assert.ThrowsAsync<InvalidOperationException>(() => loader.LoadAsync("https://example.com/a.md", TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("/next.md", true)]
    [InlineData("http://localhost:8001/next.md", false)]
    [InlineData("http://127.0.0.1:8000/next.md", false)]
    [InlineData("http://10.0.0.1/next.md", false)]
    public async Task Local_redirects_remain_same_origin_or_are_rejected(string target, bool allowed)
    {
        using var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/a.md" ? Redirect(target) : Text("# Next")));
        using var loader = new MarkdownUrlLoader(handler, LoopbackDns);
        if (allowed)
        { Assert.Equal("# Next", (await loader.LoadAsync("http://localhost:8000/a.md", TestContext.Current.CancellationToken)).Text); Assert.Equal(2, handler.Calls); }
        else
        { var exception = await Record.ExceptionAsync(() => loader.LoadAsync("http://localhost:8000/a.md", TestContext.Current.CancellationToken)); Assert.True(exception is ArgumentException or InvalidOperationException); Assert.Equal(1, handler.Calls); }
    }

    [Fact]
    public async Task Local_request_may_redirect_to_a_validated_public_address()
    {
        using var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.Host == "localhost" ? Redirect("https://example.com/next.md") : Text("# Public next")));
        using var loader = new MarkdownUrlLoader(handler, (host, token) => host == "localhost" ? LoopbackDns(host, token) : PublicDns(host, token));
        var document = await loader.LoadAsync("http://localhost:8000/a.md", TestContext.Current.CancellationToken);
        Assert.Equal("example.com", document.SourceUrl.Host); Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Mixed_private_DNS_answers_are_rejected_before_transport()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Text("never")));
        using var loader = new MarkdownUrlLoader(handler, (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Loopback }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => loader.LoadAsync("https://example.com/a.md", TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Relative_redirect_returns_final_source_and_validates_every_hop()
    {
        using var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/a.md" ? Redirect("/final.md") : Text("# Final")));
        using var loader = new MarkdownUrlLoader(handler, PublicDns);
        var document = await loader.LoadAsync("https://example.com/a.md", TestContext.Current.CancellationToken);
        Assert.Equal("/final.md", document.SourceUrl.AbsolutePath); Assert.Equal("final.md", document.SuggestedFileName); Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("http://10.0.0.1/private.md")]
    [InlineData("http://example.com/plain.md")]
    public async Task Secure_URL_never_redirects_to_private_or_downgraded_address(string target)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Redirect(target)));
        using var loader = new MarkdownUrlLoader(handler, PublicDns);
        var error = await Record.ExceptionAsync(() => loader.LoadAsync("https://example.com/a.md", TestContext.Current.CancellationToken));
        Assert.True(error is ArgumentException or InvalidOperationException); Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("text/html", "<!doctype html><html>Sign in</html>")]
    [InlineData("text/plain", "<html>Not a raw file</html>")]
    [InlineData("application/pdf", "Not Markdown")]
    [InlineData("text/plain", "Binary\0content")]
    public async Task HTML_and_binary_responses_are_rejected_with_raw_link_guidance(string media, string body)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Text(body, media)));
        using var loader = new MarkdownUrlLoader(handler, PublicDns);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => loader.LoadAsync("https://example.com/a.md", TestContext.Current.CancellationToken));
        Assert.Contains("Raw", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Parameter", error.Message, StringComparison.Ordinal);
        if (media == "text/html" || body.StartsWith("<html", StringComparison.Ordinal))
        {
            Assert.Contains("rendered slides", error.Message, StringComparison.Ordinal);
            Assert.Contains("+ Add → Open Files", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Unknown_length_stream_stops_at_decimal_ten_megabytes_plus_one()
    {
        var stream = new RepeatedStream(MarkdownUrlLoader.MaximumBytes + 20_000);
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) { Headers = { ContentType = new MediaTypeHeaderValue("text/plain") } } }));
        using var loader = new MarkdownUrlLoader(handler, PublicDns);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => loader.LoadAsync("https://example.com/a.md", TestContext.Current.CancellationToken));
        Assert.Contains("10 MB", error.Message, StringComparison.Ordinal); Assert.Equal(MarkdownUrlLoader.MaximumBytes + 1, stream.ReadBytes);
    }

    [Fact]
    public async Task Invalid_UTF8_is_not_silently_replaced()
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xC3, 0x28]) { Headers = { ContentType = new MediaTypeHeaderValue("text/plain") } } }));
        using var loader = new MarkdownUrlLoader(handler, PublicDns);
        await Assert.ThrowsAsync<InvalidOperationException>(() => loader.LoadAsync("https://example.com/a.md", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Root_URL_uses_friendly_default_filename()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Text("# Imported")));
        using var loader = new MarkdownUrlLoader(handler, PublicDns);
        var result = await loader.LoadAsync("https://example.com/", TestContext.Current.CancellationToken);
        Assert.Equal("Web Markdown.md", result.SuggestedFileName);
    }

    [Fact]
    public async Task User_cancellation_propagates_without_partial_document()
    {
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var handler = new Handler(async (_, token) => { pending.Cancel(); await Task.Delay(Timeout.Infinite, token); return Text("never"); });
        using var loader = new MarkdownUrlLoader(handler, PublicDns);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loader.LoadAsync("https://example.com/a.md", pending.Token));
    }

    private static Task<IPAddress[]> PublicDns(string _, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }); }
    private static Task<IPAddress[]> LoopbackDns(string host, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(new[] { host.Contains(':') ? IPAddress.IPv6Loopback : IPAddress.Loopback }); }
    private static HttpResponseMessage Text(string body, string media = "text/plain") => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, media) };
    private static HttpResponseMessage Redirect(string target) => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri(target, UriKind.RelativeOrAbsolute) } };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { public int Calls { get; private set; } protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; return send(request, cancellationToken); } }
    private sealed class RepeatedStream(int length) : Stream
    {
        public int ReadBytes { get; private set; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => ReadBytes; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { var take = Math.Min(count, length - ReadBytes); Array.Fill(buffer, (byte)'a', offset, take); ReadBytes += take; return take; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); var take = Math.Min(buffer.Length, length - ReadBytes); buffer.Span[..take].Fill((byte)'a'); ReadBytes += take; return ValueTask.FromResult(take); }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
