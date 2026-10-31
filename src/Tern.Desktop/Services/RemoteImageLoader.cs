using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using SkiaSharp;

namespace Tern.Desktop.Services;

/// <summary>Fetches original remote image bytes afresh for each display request.
/// No cookies, credentials, proxies or automatic redirects are used. No image bytes are persisted or reused between requests.</summary>
public sealed class RemoteImageLoader : IDisposable
{
    internal const int MaxImageBytes = 16 * 1024 * 1024;
    private const int MaxSvgBytes = 1024 * 1024;
    private const long MaxImagePixels = 16_000_000;
    private readonly HttpClient _http;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _downloads = new(4, 4);

    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    public RemoteImageLoader() : this(null, null) { }

    /// <summary>Fetches fresh bytes for export on its background worker. No disk cache is used.</summary>
    public byte[]? FetchForExport(string destination) => LoadAsync(destination, CancellationToken.None).GetAwaiter().GetResult();

    internal RemoteImageLoader(HttpMessageHandler? transport,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver, TimeSpan? timeout = null)
    {

        _resolve = resolver ?? ((host, token) => Dns.GetHostAddressesAsync(host, token));
        _timeout = timeout ?? TimeSpan.FromSeconds(15);


        transport ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            Credentials = null,
            MaxConnectionsPerServer = 4,
            MaxResponseHeadersLength = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = (context, token) => ConnectPublicAsync(context.DnsEndPoint, token),
        };
        _http = new HttpClient(transport, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Returns validated image bytes or null on network or format failures.
    /// Caller cancellation propagates; a request timeout returns null. Nothing logs the URL or its query.</summary>
    public async Task<byte[]?> LoadAsync(string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0 || !TryDestination(destination, out var original)) return null;
        var entered = false;
        try
        {
            using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            requestLifetime.CancelAfter(_timeout);
            var token = requestLifetime.Token;
            await _downloads.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            var current = original;
            for (var redirects = 0; redirects <= 3; redirects++)
            {
                await ResolvePublicAsync(current.IdnHost, token).ConfigureAwait(false);
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.Accept.ParseAdd("image/*");
                request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
                request.Headers.UserAgent.ParseAdd("Tern/1.0");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (redirects == 3 || response.Headers.Location is not { } location
                        || !Uri.TryCreate(current, location, out var redirected) || !TryDestination(redirected.AbsoluteUri, out current)) return null;
                    continue;
                }
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxImageBytes) return null;
                await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var output = new MemoryStream();
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaxImageBytes + 1 - (int)output.Length)), token).ConfigureAwait(false);
                    if (read == 0) break;
                    output.Write(buffer, 0, read);
                    if (output.Length > MaxImageBytes) return null;
                }
                var bytes = output.ToArray();
                token.ThrowIfCancellationRequested();
                if (!IsImage(bytes)) return null;
                token.ThrowIfCancellationRequested();

                return bytes;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or SocketException
            or ArgumentException or InvalidOperationException or NotSupportedException) { }
        finally { if (entered) _downloads.Release(); }
        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }

    private static bool TryDestination(string destination, out Uri result)
    {
        result = null!;
        if (destination.Length > 8192 || !Uri.TryCreate(destination, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || uri.IsUnc || uri.UserInfo.Length != 0 || uri.IsLoopback
            || string.IsNullOrWhiteSpace(uri.IdnHost)) return false;
        var host = uri.IdnHost.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return false;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var address) && !IsPublic(address)) return false;
        result = new UriBuilder(uri) { Fragment = string.Empty }.Uri;
        return true;
    }

    private async Task<IPAddress[]> ResolvePublicAsync(string host, CancellationToken token)
    {
        var addresses = await _resolve(host.Trim('[', ']'), token).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublic(address)))
            throw new HttpRequestException("Remote image destination is not a public network address.");
        return addresses;
    }

    // Called at the actual socket connection as well as preflight. A changed DNS answer
    // cannot make the handler connect to a private address after a public preflight result.
    internal async ValueTask<Stream> ConnectPublicAsync(DnsEndPoint endpoint, CancellationToken token)
    {
        var addresses = await ResolvePublicAsync(endpoint.Host, token).ConfigureAwait(false);
        SocketException? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex) { socket.Dispose(); last = ex; }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("Remote image connection failed.", last);
    }

    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return b[0] is not (0 or 10 or 127) && b[0] < 224
                && !(b[0] == 100 && b[1] is >= 64 and <= 127)
                && !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31)
                && !(b[0] == 192 && (b[1] == 168 || b[1] == 0 && b[2] is 0 or 2))
                && !(b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100))
                && !(b[0] == 203 && b[1] == 0 && b[2] == 113)
                && !address.Equals(IPAddress.Parse("168.63.129.16"));
        }
        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId == 0
            && (b[0] & 0xE0) == 0x20 // Global unicast only; excludes ULA/link-local/multicast/NAT64-local.
            && !(b[0] == 0x20 && b[1] == 0x02) // 6to4 tunnels can embed non-public IPv4 addresses.
            && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0) // Teredo.
            && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8);
    }

    private static bool IsImage(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxImageBytes) return false;
        var raster = bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255
            || bytes.Length >= 6 && (bytes.AsSpan(0, 6).SequenceEqual("GIF87a"u8) || bytes.AsSpan(0, 6).SequenceEqual("GIF89a"u8))
            || bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8)
            || bytes.Length >= 2 && bytes[0] == 'B' && bytes[1] == 'M';
        try
        {
            if (!raster) return IsSvg(bytes);
            using var stream = new SKMemoryStream(bytes);
            using var codec = SKCodec.Create(stream);
            return codec is not null && codec.EncodedFormat is SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg
                    or SKEncodedImageFormat.Gif or SKEncodedImageFormat.Webp or SKEncodedImageFormat.Bmp
                && Dimensions(codec.Info.Width, codec.Info.Height);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DllNotFoundException or TypeInitializationException or XmlException or FormatException) { return false; }
    }

    private sealed class SvgBudget
    {
        public int Bytes;
        public int Nodes;
    }

    private static bool IsSvg(byte[] bytes, SvgBudget? budget = null, int depth = 0)
    {
        budget ??= new SvgBudget();
        if (depth > 8 || bytes.Length > MaxSvgBytes - budget.Bytes) return false;
        budget.Bytes += bytes.Length;
        using var input = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxSvgBytes, MaxCharactersFromEntities = 0 });
        if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "svg"
            || reader.NamespaceURI is not ("" or "http://www.w3.org/2000/svg")) return false;
        var width = SvgNumber(reader.GetAttribute("width"));
        var height = SvgNumber(reader.GetAttribute("height"));
        var viewBox = (reader.GetAttribute("viewBox") ?? string.Empty).Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (viewBox.Length == 4)
        {
            if (width <= 0) width = SvgNumber(viewBox[2]);
            if (height <= 0) height = SvgNumber(viewBox[3]);
        }
        if (!Dimensions(width, height) || width > 4096 || height > 4096) return false;
        do
        {
            if (++budget.Nodes > 10_000 || reader.Depth > 64 || reader.AttributeCount > 64) return false;
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.LocalName is "script" or "foreignObject" or "iframe" or "object" or "embed") return false;
            if (reader.MoveToFirstAttribute())
            {
                do
                {
                    if (reader.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase)) return false;
                    if (reader.LocalName == "href" && !reader.Value.StartsWith('#'))
                    {
                        const string embeddedSvg = "data:image/svg+xml;base64,";
                        if (!reader.Value.StartsWith(embeddedSvg, StringComparison.OrdinalIgnoreCase)) return false;
                        var data = Convert.FromBase64String(reader.Value[embeddedSvg.Length..]);
                        if (!IsSvg(data, budget, depth + 1)) return false;
                    }
                } while (reader.MoveToNextAttribute());
                reader.MoveToElement();
            }
        } while (reader.Read());
        return true;
    }

    private static double SvgNumber(string? value) => double.TryParse(value?.Trim().Replace("px", string.Empty, StringComparison.Ordinal),
        NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static bool Dimensions(double width, double height) => double.IsFinite(width) && double.IsFinite(height)
        && width > 0 && height > 0 && width <= 32768 && height <= 32768 && width * height <= MaxImagePixels;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _http.Dispose();
        _lifetime.Dispose();
    }
}
