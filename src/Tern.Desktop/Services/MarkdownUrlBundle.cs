using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Tern.Markdown;

namespace Tern.Desktop.Services;

public sealed partial class MarkdownUrlLoader
{
    public const int MaximumAssetCount = 128;
    public const long MaximumBundleBytes = 100_000_000;
    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".md", ".markdown", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".svg", ".pdf", ".txt", ".csv", ".json", ".yaml", ".yml", ".zip", ".docx", ".xlsx", ".pptx" };

    /// <summary>Downloads one document and its direct file/image dependencies; never crawls linked pages.</summary>
    public async Task<MarkdownUrlDocument> LoadBundleAsync(string url, CancellationToken cancellationToken = default)
    {
        var document = await LoadAsync(url, cancellationToken).ConfigureAwait(false);
        var parsed = Markdig.Markdown.Parse(document.Text, MarkdownPipelines.For(MarkdownProfile.Extended));
        var links = MarkdownAnalyzer.Analyze(document.Text, parsed, MarkdownProfile.Extended).Links
            .Where(l => l.Kind is LinkKind.Image or LinkKind.Inline or LinkKind.ReferenceDefinition)
            .Select(l => (l.Range, l.Destination, Image: l.Kind == LinkKind.Image)).ToList();
        var referencedImages = parsed.Descendants().OfType<LinkInline>().Where(l => l.IsImage && l.Reference is not null)
            .Select(l => l.Url).ToHashSet(StringComparer.Ordinal);
        // The renderer recognises inert HTML images. Parse only actual HTML nodes, never code examples.
        foreach (var node in parsed.Descendants().Where(n => n is HtmlInline or HtmlBlock))
        {
            var start = node.Span.Start; var end = Math.Min(document.Text.Length, node.Span.End + 1);
            if (start < 0 || start >= end) continue;
            foreach (var image in ReadHtmlImages(document.Text[start..end]))
            {
                var value = image.Groups["double"].Success ? image.Groups["double"] : image.Groups["single"].Success ? image.Groups["single"] : image.Groups["bare"];
                links.Add((new SourceRange(start + value.Index, start + value.Index + value.Length), WebUtility.HtmlDecode(value.Value), true));
            }
        }
        var assets = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var fetched = new Dictionary<string, string>(StringComparer.Ordinal);
        var failures = new Dictionary<string, string>(StringComparer.Ordinal);
        var rewrites = new List<(SourceRange Range, string Value)>();
        long bytes = Encoding.UTF8.GetByteCount(document.Text);
        foreach (var link in links.DistinctBy(l => l.Range))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(link.Destination) || link.Destination.StartsWith('#') || link.Destination.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var decodedDestination = DecodeDestination(link.Destination);
            if (!Uri.TryCreate(document.SourceUrl, decodedDestination, out var resolved) || resolved.Scheme is not ("http" or "https")) continue;
            var fragment = resolved.Fragment;
            var fetch = new UriBuilder(resolved) { Fragment = string.Empty }.Uri;
            // Ordinary web navigation is left online. Explicit file links and all images are dependencies.
            if (!link.Image && !referencedImages.Contains(link.Destination) && !FileExtensions.Contains(Path.GetExtension(fetch.AbsolutePath)))
            { rewrites.Add((link.Range, resolved.AbsoluteUri)); continue; }
            var canonical = fetch.AbsoluteUri;
            if (canonical == document.SourceUrl.AbsoluteUri)
            { rewrites.Add((link.Range, fragment.Length > 0 ? fragment : "#")); continue; }
            if (failures.ContainsKey(canonical)) { rewrites.Add((link.Range, resolved.AbsoluteUri)); continue; }
            if (!fetched.TryGetValue(canonical, out var local))
            {
                try
                {
                    if (fetched.Count + failures.Count >= MaximumAssetCount) throw new InvalidOperationException("The document exceeds the 128-file dependency limit.");
                    if (IsExplicitLoopbackHost(fetch.IdnHost) && !SameOrigin(document.SourceUrl, fetch))
                        throw new InvalidOperationException("A document cannot download from a different local server.");
                    var expectsImage = link.Image || referencedImages.Contains(link.Destination)
                        || Tern.Rendering.Resources.ResourceResolverOptions.DefaultImageExtensions.Contains(Path.GetExtension(fetch.AbsolutePath));
                    var content = await LoadAssetAsync(fetch, expectsImage, cancellationToken).ConfigureAwait(false);
                    if (bytes + content.Bytes.Length > MaximumBundleBytes) throw new InvalidOperationException("The document and assets exceed the 100 MB total limit.");
                    bytes += content.Bytes.Length;
                    var extension = Path.GetExtension(fetch.AbsolutePath).ToLowerInvariant();
                    if (link.Image || referencedImages.Contains(link.Destination))
                        extension = content.MediaType?.ToLowerInvariant() switch
                        {
                            "image/png" => ".png", "image/jpeg" => ".jpg", "image/gif" => ".gif", "image/webp" => ".webp",
                            "image/bmp" => ".bmp", "image/svg+xml" => ".svg", "image/x-icon" or "image/vnd.microsoft.icon" => ".ico", _ => extension
                        };
                    if (extension.Length > 12 || extension.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.')) extension = ".bin";
                    local = "assets/" + Hash(canonical) + extension;
                    assets.Add(local, content.Bytes); fetched.Add(canonical, local);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                { failures.Add(canonical, canonical + ": " + ex.Message); rewrites.Add((link.Range, resolved.AbsoluteUri)); continue; }
            }
            rewrites.Add((link.Range, local + fragment));
        }
        var text = new StringBuilder(document.Text);
        foreach (var rewrite in rewrites.OrderByDescending(r => r.Range.Start))
            text.Remove(rewrite.Range.Start, rewrite.Range.Length).Insert(rewrite.Range.Start, rewrite.Value);
        return document with { Text = text.ToString(), OriginalText = document.Text, Assets = assets, Warnings = failures.Values.ToArray() };
    }

    private async Task<(byte[] Bytes, string? MediaType)> LoadAssetAsync(Uri address, bool expectsImage, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        var current = AssetAddress(address);
        try
        {
            for (var redirects = 0; redirects <= 3; redirects++)
            {
                await ResolveAllowedAsync(current.IdnHost, deadline.Token).ConfigureAwait(false);
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (redirects == 3 || response.Headers.Location is not { } location || !Uri.TryCreate(current, location, out var next)) throw new InvalidOperationException("Asset redirect limit reached.");
                    next = AssetAddress(next);
                    if (IsExplicitLoopbackHost(next.IdnHost) && !SameOrigin(current, next) || current.Scheme == "https" && next.Scheme == "http")
                        throw new InvalidOperationException("The asset redirected to an unsafe local or unencrypted address.");
                    current = next; continue;
                }
                if (!response.IsSuccessStatusCode) throw ResponseFailure(response.StatusCode);
                var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
                if (mediaType is "text/html" or "application/xhtml+xml") throw new InvalidOperationException("The asset returned a web/sign-in page instead of a file.");
                if (response.Content.Headers.ContentLength > 20_000_000) throw new InvalidOperationException("The asset exceeds the 20 MB per-file limit.");
                await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                using var output = new MemoryStream(); var buffer = new byte[65536];
                int count;
                while ((count = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
                { if (output.Length + count > 20_000_000) throw new InvalidOperationException("The asset exceeds the 20 MB per-file limit."); output.Write(buffer, 0, count); }
                var bytes = output.ToArray();
                if (expectsImage)
                {
                    var beginning = Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, 512))).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
                    if (beginning.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || beginning.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
                        || beginning.StartsWith("<head", StringComparison.OrdinalIgnoreCase) || beginning.StartsWith("<body", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The image returned a web/sign-in page instead of image data. Check access or retry its direct download address.");
                }
                return (bytes, mediaType);
            }
            throw new InvalidOperationException("The asset could not be downloaded.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new InvalidOperationException("The asset request timed out. Retry the download."); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Net.Sockets.SocketException)
        { throw new InvalidOperationException("The asset connection failed. Check the network and retry.", ex); }
    }

    internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string DecodeDestination(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length && "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~ ".Contains(value[i + 1])) i++;
            result.Append(value[i]);
        }
        return WebUtility.HtmlDecode(result.ToString());
    }

    private static Uri AssetAddress(Uri address)
    {
        var validated = ValidateUrl(address.AbsoluteUri);
        if (!validated.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase) || !validated.IsDefaultPort) return validated;
        var segments = validated.GetComponents(UriComponents.Path, UriFormat.UriEscaped).Split('/');
        if (segments.Length < 5 || segments[2] != "blob" || segments.Any(string.IsNullOrEmpty)) return validated;
        return ValidateUrl(new UriBuilder("https", "raw.githubusercontent.com") { Path = string.Join('/', segments.Take(2).Concat(segments.Skip(3))) }.Uri.AbsoluteUri);
    }

    private static Match[] ReadHtmlImages(string html)
    {
        try { return HtmlImage().Matches(html).Cast<Match>().ToArray(); }
        catch (RegexMatchTimeoutException) { throw new InvalidOperationException("The HTML image references are too complex to process. Simplify them and retry."); }
    }

    [GeneratedRegex("<img\\b[^>]*?\\bsrc\\s*=\\s*(?:\"(?<double>[^\"]+)\"|'(?<single>[^']+)'|(?<bare>[^\\s>]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex HtmlImage();
}

/// <summary>Immutable, atomically published remote snapshots. Failed/partial bundles are retried, never used as complete cache hits.</summary>
public static class MarkdownUrlCache
{
    public static string Root(string stateDirectory) => Path.Combine(stateDirectory, "remote-cache");
    private sealed record Manifest(string Source, string Name, string[] Assets, string[] Warnings);

    public static async Task<MarkdownUrlDocument?> ReadAsync(string root, string url, CancellationToken cancellationToken = default)
    {
        try
        {
            var key = MarkdownUrlLoader.Hash(MarkdownUrlLoader.GetFetchAddress(url).AbsoluteUri);
            var directory = Path.Combine(root, key);
            var pointer = Path.Combine(root, key + ".current"); RejectLinks(pointer);
            if (File.Exists(pointer))
            {
                var name = await File.ReadAllTextAsync(pointer, cancellationToken).ConfigureAwait(false);
                if (!Regex.IsMatch(name, "^[a-f0-9]{64}(?:-[a-f0-9]{32})?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200))) return null;
                directory = Path.Combine(root, name);
            }
            RejectLinks(directory);
            var originalPath = Path.Combine(directory, "original.md"); RejectLinks(originalPath);
            var metadata = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(Path.Combine(directory, "manifest.json"), cancellationToken).ConfigureAwait(false));
            if (metadata is null || metadata.Warnings is null || metadata.Assets is null || metadata.Warnings.Length != 0 || metadata.Assets.Length > MarkdownUrlLoader.MaximumAssetCount || metadata.Assets.Any(a => !IsAssetPath(a))) return null;
            var assets = new Dictionary<string, byte[]>();
            long assetBytes = 0;
            foreach (var asset in metadata.Assets)
            {
                var path = Path.Combine(directory, asset); RejectLinks(path);
                var size = new FileInfo(path).Length; assetBytes += size;
                if (size > 20_000_000 || assetBytes > MarkdownUrlLoader.MaximumBundleBytes) return null;
                assets.Add(asset, await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
            }
            var documentPath = Path.Combine(directory, "document.md"); RejectLinks(documentPath);
            return new MarkdownUrlDocument(new Uri(metadata.Source), metadata.Name, await File.ReadAllTextAsync(documentPath, cancellationToken).ConfigureAwait(false))
            { OriginalText = await File.ReadAllTextAsync(originalPath, cancellationToken).ConfigureAwait(false), Assets = assets, CachedPath = documentPath, RequestedUrl = MarkdownUrlLoader.GetFetchAddress(url).AbsoluteUri };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    public static async Task<MarkdownUrlDocument> WriteAsync(string root, string requestedUrl, MarkdownUrlDocument document, CancellationToken cancellationToken = default)
    {
        RejectLinks(root); Directory.CreateDirectory(root);
        var directory = Path.Combine(root, MarkdownUrlLoader.Hash(MarkdownUrlLoader.GetFetchAddress(requestedUrl).AbsoluteUri));
        // Unique snapshots preserve assets referenced by an open, older document during an explicit retry.
        var staging = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var (relative, bytes) in document.Assets)
            {
                if (!IsAssetPath(relative)) throw new IOException("Unsafe cached asset path.");
                var target = Path.Combine(staging, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);
            }
            await File.WriteAllTextAsync(Path.Combine(staging, "document.md"), document.Text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(staging, "original.md"), document.OriginalText ?? document.Text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(new Manifest(document.SourceUrl.AbsoluteUri, document.SuggestedFileName, document.Assets.Keys.ToArray(), document.Warnings.ToArray())), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(directory))
            {
                // Existing snapshots are immutable. A failed snapshot lives separately until reset; the pointer is replaced atomically.
                directory += "-" + Guid.NewGuid().ToString("N");
            }
            Directory.Move(staging, directory);
            var pointer = Path.Combine(root, MarkdownUrlLoader.Hash(MarkdownUrlLoader.GetFetchAddress(requestedUrl).AbsoluteUri) + ".current");
            var temporaryPointer = pointer + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllTextAsync(temporaryPointer, Path.GetFileName(directory), cancellationToken).ConfigureAwait(false); File.Move(temporaryPointer, pointer, true); }
            finally { if (File.Exists(temporaryPointer)) File.Delete(temporaryPointer); }
            return document with { CachedPath = Path.Combine(directory, "document.md") };
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    internal static bool IsAssetPath(string path) => Regex.IsMatch(path, @"^assets/[a-f0-9]{64}(?:\.[a-z0-9]{1,11})?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
    internal static void RejectLinks(string path)
    {
        for (FileSystemInfo? entry = Directory.Exists(path) ? new DirectoryInfo(Path.GetFullPath(path)) : new FileInfo(Path.GetFullPath(path)); entry is not null; entry = entry is DirectoryInfo d ? d.Parent : ((FileInfo)entry).Directory)
            if (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Downloaded files cannot be stored through a linked directory or file.");
    }
}
