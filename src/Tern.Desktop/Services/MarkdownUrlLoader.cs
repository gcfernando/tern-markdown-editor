using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Tern.Workspaces;
using Tern.Domain.Text;
using Tern.Filesystem;

namespace Tern.Desktop.Services;

public sealed record MarkdownUrlDocument(Uri SourceUrl, string SuggestedFileName, string Text, TextFileFormat? Format = null)
{
    public string? OriginalText { get; init; }
    public IReadOnlyDictionary<string, byte[]> Assets { get; init; } = new Dictionary<string, byte[]>();
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string? CachedPath { get; init; }
    public string? RequestedUrl { get; init; }
}

/// <summary>One explicit public or loopback-server Markdown fetch. Never writes files or supplies credentials.</summary>
public sealed partial class MarkdownUrlLoader : IDisposable
{
    public const int MaximumBytes = 10_000_000;
    private readonly HttpClient _http;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly TimeSpan _timeout;

    public MarkdownUrlLoader() : this(null, null) { }
    internal MarkdownUrlLoader(HttpMessageHandler? handler, Func<string, CancellationToken, Task<IPAddress[]>>? resolver, TimeSpan? timeout = null)
    {
        _resolve = resolver ?? ((host, token) => Dns.GetHostAddressesAsync(host, token));
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false, Credentials = null,
            MaxResponseHeadersLength = 32, ConnectCallback = (context, token) => ConnectAllowedAsync(context.DnsEndPoint, token),
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<MarkdownUrlDocument> LoadAsync(string url, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = GetFetchAddress(url);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            for (var redirects = 0; redirects <= 3; redirects++)
            {
                await ResolveAllowedAsync(current.IdnHost, deadline.Token).ConfigureAwait(false);
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.Accept.ParseAdd("text/markdown, text/plain;q=0.9, application/octet-stream;q=0.5");
                request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (redirects == 3 || response.Headers.Location is not { } location || !Uri.TryCreate(current, location, out var next))
                        throw new InvalidOperationException("The server redirected too many times or did not provide a valid Markdown address.");
                    next = GetFetchAddress(next.AbsoluteUri);
                    if (IsExplicitLoopbackHost(next.IdnHost) && (!IsExplicitLoopbackHost(current.IdnHost) || !SameOrigin(current, next)))
                        throw new InvalidOperationException("A redirect cannot move a public request to a local server or switch to another local origin. Paste the intended local server address directly.");
                    if (current.Scheme == "https" && next.Scheme == "http") throw new InvalidOperationException("The server redirected a secure HTTPS address to unencrypted HTTP. Use a direct Markdown address.");
                    current = next; continue;
                }
                if (!response.IsSuccessStatusCode) throw ResponseFailure(response.StatusCode);
                var media = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
                if (media is "text/html" or "application/xhtml+xml") throw HtmlInsteadOfMarkdown();
                var namedMarkdown = current.AbsolutePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || current.AbsolutePath.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);
                if (media is not ("text/markdown" or "text/x-markdown" or "text/plain") && !(namedMarkdown && media is null or "application/octet-stream"))
                    throw new InvalidOperationException("This address does not return a Markdown text file. Use its Raw or Download link, not the web page that displays it.");
                if (response.Content.Headers.ContentLength > MaximumBytes) throw TooLarge();
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                using var output = new MemoryStream(); var buffer = new byte[64 * 1024];
                while (true)
                {
                    var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaximumBytes + 1 - (int)output.Length)), deadline.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    output.Write(buffer, 0, count);
                    if (output.Length > MaximumBytes) throw TooLarge();
                }
                var decoded = Decode(output.ToArray(), response.Content.Headers.ContentType?.CharSet);
                var text = decoded.Text;
                var beginning = text.TrimStart();
                if (text.Contains('\0')) throw new InvalidOperationException("The server returned binary content instead of Markdown text. Use its Raw or Download link for the original .md file.");
                if (beginning.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || beginning.StartsWith("<html", StringComparison.OrdinalIgnoreCase) || beginning.StartsWith("<head", StringComparison.OrdinalIgnoreCase) || beginning.StartsWith("<body", StringComparison.OrdinalIgnoreCase))
                    throw HtmlInsteadOfMarkdown();
                deadline.Token.ThrowIfCancellationRequested();
                return new MarkdownUrlDocument(current, SuggestedName(current), text, decoded.Format) { RequestedUrl = GetFetchAddress(url).AbsoluteUri };
            }
            throw new InvalidOperationException("The Markdown address could not be opened.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new InvalidOperationException("The Markdown request timed out. Check the address or try again later."); }
        catch (HttpRequestException)
        { throw new InvalidOperationException("The Markdown server could not be reached securely. Check its address, server status and network connection."); }
        catch (SocketException)
        { throw new InvalidOperationException("The Markdown server name could not be resolved. Check its address and your network connection."); }
        catch (IOException)
        { throw new InvalidOperationException("The Markdown response could not be read completely. No document has been opened."); }
    }

    internal static Uri ValidateUrl(string url)
    {
        if (url.Length > 8192 || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.IsUnc || string.IsNullOrWhiteSpace(uri.IdnHost))
            throw new ArgumentException("Enter a complete HTTP or HTTPS Markdown URL, for example https://example.com/note.md.");
        if (uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Only HTTP or HTTPS links can be opened here. Use Open File for a local Markdown file.");
        if (uri.UserInfo.Length != 0)
            throw new ArgumentException("Remove the username or password from the URL. Tern does not send sign-in credentials; open an authenticated download locally instead.");
        var host = uri.IdnHost.TrimEnd('.');
        if (!IsExplicitLoopbackHost(host) && (host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(host.Trim('[', ']'), out var address) && !RemoteImageLoader.IsPublic(address)))
            throw new ArgumentException("Use a public internet address or an explicit localhost/loopback server address. Other private-network addresses are not supported; use Open File for local files.");
        return new UriBuilder(uri) { Fragment = string.Empty }.Uri;
    }
    /// <summary>Validates and previews the fetch address without DNS or network access.</summary>
    public static Uri GetFetchAddress(string url)
    {
        var uri = ValidateUrl(url);
        if (!uri.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort) return uri;
        var segments = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped).Split('/');
        if (segments.Length < 5 || segments[2] != "blob" || segments.Any(string.IsNullOrEmpty)) return uri;
        var filename = Uri.UnescapeDataString(segments[^1]);
        if (!filename.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !filename.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase)) return uri;
        // Keep the complete escaped ref/path tail: branch names may themselves contain slashes.
        // GitHub's web-view query options are not forwarded to the separate raw-content host.
        var raw = new UriBuilder("https", "raw.githubusercontent.com")
        { Path = string.Join('/', segments.Take(2).Concat(segments.Skip(3))), Query = string.Empty, Fragment = string.Empty }.Uri;
        return ValidateUrl(raw.AbsoluteUri);
    }
    private static bool IsLoopbackAddress(IPAddress address) => IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
    private static bool IsExplicitLoopbackHost(string host) => host.Trim('[', ']').TrimEnd('.').Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(host.Trim('[', ']'), out var address) && IsLoopbackAddress(address);
    private static bool SameOrigin(Uri first, Uri second) => first.Scheme == second.Scheme && first.Port == second.Port && first.IdnHost.Equals(second.IdnHost, StringComparison.OrdinalIgnoreCase);
    private async Task<IPAddress[]> ResolveAllowedAsync(string host, CancellationToken token)
    {
        var addresses = await _resolve(host.Trim('[', ']'), token).ConfigureAwait(false);
        var loopback = IsExplicitLoopbackHost(host);
        if (addresses.Length == 0 || addresses.Any(a => loopback ? !IsLoopbackAddress(a) : !RemoteImageLoader.IsPublic(a)))
            throw new InvalidOperationException(loopback ? "The localhost address did not resolve only to loopback addresses. No request was sent." : "This server name resolves to a private or local address. Paste an explicit localhost address for a local server; other private-network hosts are not supported.");
        return addresses;
    }
    private async ValueTask<Stream> ConnectAllowedAsync(DnsEndPoint endpoint, CancellationToken token)
    {
        var addresses = await ResolveAllowedAsync(endpoint.Host, token).ConfigureAwait(false);
        SocketException? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), token).ConfigureAwait(false); return new NetworkStream(socket, ownsSocket: true); }
            catch (SocketException ex) { socket.Dispose(); last = ex; }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("The Markdown server could not be reached.", last);
    }
    private static DecodedText Decode(byte[] bytes, string? charset)
    {
        try
        {
            var hasBom = bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }) || bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })
                || bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) || bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF });
            TextEncodingKind? forced = hasBom ? null : TextEncodingKind.Utf8;
            if (!hasBom && !string.IsNullOrWhiteSpace(charset))
            {
                _ = TextFileCodec.AvailableEncodings;
                forced = TextEncodingIds.FromCodePage(Encoding.GetEncoding(charset.Trim('"')).CodePage);
            }
            var decoded = TextFileCodec.Decode(bytes, forced);
            if (!decoded.IsLossless) throw new InvalidOperationException("The response is not valid in its text encoding. No document has been opened.");
            return decoded;
        }
        catch (DecoderFallbackException) { throw new InvalidOperationException("The response is not valid UTF-8 or BOM-marked UTF-16 text. No document has been opened."); }
        catch (ArgumentException) { throw new InvalidOperationException("The server uses an unsupported text encoding. No document has been opened."); }
    }
    private static string SuggestedName(Uri uri)
    {
        var name = Uri.UnescapeDataString(uri.AbsolutePath.Split('/').LastOrDefault() ?? "");
        if (string.IsNullOrWhiteSpace(name)) return "Web Markdown.md";
        if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase)) name += ".md";
        return FileNameValidator.Validate(name).IsValid ? name : "Web Markdown.md";
    }
    private static InvalidOperationException TooLarge() => new("This Markdown download exceeds the 10 MB limit. Open a smaller Markdown file.");
    private static InvalidOperationException ResponseFailure(HttpStatusCode status) => new(status switch
    {
        HttpStatusCode.NotFound => "Resource not found (HTTP 404). Check the filename, branch and address, then retry.",
        HttpStatusCode.Unauthorized => "Authentication required (HTTP 401). Tern does not supply sign-in credentials. Download the file in your authenticated browser, then open it locally.",
        HttpStatusCode.Forbidden => "Permission denied (HTTP 403). Request access from the owner or download an accessible copy in your browser.",
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => "The server timed out. Retry later or check the server status.",
        HttpStatusCode.TooManyRequests => "The server is limiting requests (HTTP 429). Wait before retrying.",
        _ => $"The server returned HTTP {(int)status}. Check its status and retry; the address itself may still be valid."
    });
    private static InvalidOperationException HtmlInsteadOfMarkdown() => new("This server sent a web page or rendered slides, not the Markdown source. For a local presentation, open its original .md file using + Add → Open Files. For a website, use its Raw or Download link.");
    public void Dispose() => _http.Dispose();
}
