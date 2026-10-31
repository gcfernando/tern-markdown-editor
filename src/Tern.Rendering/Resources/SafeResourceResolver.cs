using System.Text.RegularExpressions;

namespace Tern.Rendering.Resources;

/// <summary>
/// Decides what may be done with a link or image destination found in a document. Nothing is ever fetched:
/// remote images are refused, external links are only classified, and local files must resolve (after
/// following symbolic links) to a location inside the document directory, the workspace root or a configured root.
/// </summary>
public sealed partial class SafeResourceResolver
{
    private const int MaxLinkHops = 40;

    private static readonly Dictionary<string, string> DataImageTypes = new(StringComparer.Ordinal)
    {
        ["image/png"] = "image/png",
        ["image/jpeg"] = "image/jpeg",
        ["image/gif"] = "image/gif",
        ["image/webp"] = "image/webp",
    };

    private static readonly HashSet<string> RiskyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".svg", ".svgz", ".html", ".htm", ".xhtml", ".xml", ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".msi", ".scr",
        ".js", ".mjs", ".vbs", ".vbe", ".wsf", ".hta", ".jar", ".lnk", ".url", ".reg", ".dll", ".sh", ".appref-ms",
    };

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly ResourceResolverOptions _options;
    private readonly string[] _roots;
    private readonly string?[] _canonicalRoots;
    private readonly HashSet<string> _imageFiles = new(StringComparer.FromComparison(PathComparison));

    /// <summary>Creates a resolver. Roots that cannot be normalised are ignored.</summary>
    public SafeResourceResolver(ResourceResolverOptions options)
    {
        _options = options;
        var roots = new List<string>();
        foreach (var candidate in new[] { options.DocumentDirectory, options.WorkspaceRoot }.Concat(options.AdditionalRoots))
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                var full = TrimSeparators(Path.GetFullPath(candidate));
                if (!roots.Contains(full, StringComparer.FromComparison(PathComparison)))
                {
                    roots.Add(full);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // An unusable root simply grants nothing.
            }
        }

        _roots = [.. roots];
        _canonicalRoots = [.. _roots.Select(CanonicalizeExisting)];
        foreach (var file in options.AdditionalImageFiles.Take(128))
        {
            try { if (Path.IsPathFullyQualified(file)) _imageFiles.Add(Path.GetFullPath(file)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { /* Ignore malformed saved grants. */ }
        }
    }

    /// <summary>The allowed roots, as full paths.</summary>
    public IReadOnlyList<string> Roots => _roots;

    /// <summary>Classifies a hyperlink destination.</summary>
    public ResourceDecision ResolveLink(string? destination) => Resolve(destination, isImage: false);

    /// <summary>Classifies an image destination. Remote images are never loaded.</summary>
    public ResourceDecision ResolveImage(string? destination) => Resolve(destination, isImage: true);

    private static Blocked Block(BlockReason reason, string original, string detail) =>
        new(reason, original.Length > 256 ? original[..256] + "…" : original, detail);

    private static string TrimSeparators(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        return path.Length > root.Length ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;
    }

    private ResourceDecision Resolve(string? destination, bool isImage)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            return Block(BlockReason.Empty, destination ?? string.Empty, "The destination is empty.");
        }

        var text = destination.Trim(' ');
        if (text.Length == 0)
        {
            return Block(BlockReason.Empty, destination, "The destination is empty.");
        }

        foreach (var c in text)
        {
            if (char.IsControl(c))
            {
                return Block(BlockReason.ControlCharacters, text, "The destination contains control characters.");
            }
        }

        if (text[0] == '#')
        {
            return isImage
                ? Block(BlockReason.InvalidPath, text, "An image cannot be a fragment reference.")
                : new Anchor(Decode(text[1..]));
        }

        if (TryGetScheme(text, out var scheme))
        {
            return ResolveScheme(text, scheme, isImage);
        }

        if (text.Length > _options.MaxDestinationLength)
        {
            return Block(BlockReason.PathTooLong, text, "The destination is too long.");
        }

        return ResolvePath(text, isImage);
    }

    private ResourceDecision ResolveScheme(string text, string scheme, bool isImage)
    {
        switch (scheme)
        {
            case "javascript" or "vbscript" or "livescript" or "mocha":
                return Block(BlockReason.ScriptScheme, text, "Script URLs are never followed.");
            case "file":
                return Block(BlockReason.FileUrl, text, "file: URLs are not allowed; use a relative path.");
            case "data":
                return isImage && _options.AllowDataImages
                    ? ResolveDataImage(text)
                    : Block(BlockReason.DataUrl, text, "data: URLs are only allowed for PNG, JPEG, GIF and WebP images.");
            case "http" or "https":
                if (isImage)
                {
                    return Block(BlockReason.RemoteResourceBlocked, text, "Remote images are never loaded.");
                }

                return text.Length > _options.MaxDestinationLength
                    ? Block(BlockReason.PathTooLong, text, "The URL is too long.")
                    : Uri.TryCreate(text, UriKind.Absolute, out var web)
                        && (web.Scheme == Uri.UriSchemeHttp || web.Scheme == Uri.UriSchemeHttps)
                        && web.Host.Length > 0
                        ? new ExternalLink(web, text)
                        : Block(BlockReason.InvalidUri, text, "The URL is malformed.");
            case "mailto":
                if (isImage)
                {
                    return Block(BlockReason.DisallowedScheme, text, "mailto: cannot be an image.");
                }

                return text.Length <= _options.MaxDestinationLength && Uri.TryCreate(text, UriKind.Absolute, out var mail) && mail.Scheme == Uri.UriSchemeMailto
                    ? new ExternalLink(mail, text)
                    : Block(BlockReason.InvalidUri, text, "The mail address is malformed.");
            default:
                return Block(BlockReason.DisallowedScheme, text, $"The '{scheme}:' scheme is not allowed.");
        }
    }

    private ResourceDecision ResolveDataImage(string text)
    {
        var comma = text.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0 || comma > 64)
        {
            return Block(BlockReason.InvalidDataUri, text, "The data URI has no payload.");
        }

        var header = text[5..comma].ToLowerInvariant();
        const string base64Suffix = ";base64";
        if (!header.EndsWith(base64Suffix, StringComparison.Ordinal)
            || !DataImageTypes.TryGetValue(header[..^base64Suffix.Length], out var mediaType))
        {
            return Block(BlockReason.DataUrl, text, "Only base64 PNG, JPEG, GIF and WebP data images are allowed.");
        }

        var payload = text.AsSpan(comma + 1);
        if (payload.Length / 4L * 3 > _options.MaxDataImageBytes + 3)
        {
            return Block(BlockReason.DataUrl, text, "The embedded image exceeds the size limit.");
        }

        var buffer = new byte[payload.Length / 4 * 3 + 3];
        if (!Convert.TryFromBase64Chars(payload, buffer, out var written))
        {
            return Block(BlockReason.InvalidDataUri, text, "The embedded image is not valid base64.");
        }

        if (written > _options.MaxDataImageBytes)
        {
            return Block(BlockReason.DataUrl, text, "The embedded image exceeds the size limit.");
        }

        var data = buffer.AsSpan(0, written);
        return HasSignature(mediaType, data)
            ? new InlineImage(mediaType, data.ToArray())
            : Block(BlockReason.InvalidDataUri, text, "The embedded data does not match its declared image type.");
    }

    private static bool HasSignature(string mediaType, ReadOnlySpan<byte> data) => mediaType switch
    {
        "image/png" => data.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
        "image/jpeg" => data.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
        "image/gif" => data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8),
        "image/webp" => data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8),
        _ => false,
    };

    private ResourceDecision ResolvePath(string text, bool isImage)
    {
        // Checked before the query split: in "\\?\C:\x" the '?' is part of the device prefix, not a query.
        if (text.Length >= 3 && text[0] is '\\' or '/' && text[1] is '\\' or '/' && text[2] is '?' or '.')
        {
            return Block(BlockReason.DevicePath, text, "Device paths are not allowed.");
        }

        var rest = text;
        string? fragment = null;
        var hash = rest.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            fragment = Decode(rest[(hash + 1)..]);
            rest = rest[..hash];
        }

        var query = rest.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            rest = rest[..query];
        }

        if (rest.Length == 0)
        {
            return Block(BlockReason.Empty, text, "The destination has no path.");
        }

        var decoded = Decode(rest);
        foreach (var c in decoded)
        {
            if (char.IsControl(c))
            {
                return Block(BlockReason.ControlCharacters, text, "The path decodes to control characters.");
            }
        }

        // A second round of percent-encoding is how "%252e%252e" smuggles traversal past a single decode.
        if (HiddenEncodingPattern().IsMatch(decoded))
        {
            return Block(BlockReason.InvalidPath, text, "The path is encoded more than once.");
        }

        var normalized = decoded.Replace('\\', '/');
        if (normalized.StartsWith("//", StringComparison.Ordinal))
        {
            var isDevice = normalized.Length >= 3 && normalized[2] is '.' or '?' && (normalized.Length == 3 || normalized[3] == '/');
            return isDevice
                ? Block(BlockReason.DevicePath, text, "Device paths are not allowed.")
                : Block(BlockReason.UncPath, text, "Network paths are not allowed.");
        }

        var hasDrive = normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':';
        if (hasDrive && !(OperatingSystem.IsWindows() && normalized.Length > 2 && normalized[2] == '/'))
        {
            return Block(BlockReason.AlternateDataStream, text, "Drive-relative paths and stray colons are not allowed.");
        }

        if (normalized.IndexOf(':', hasDrive ? 2 : 0) >= 0)
        {
            return Block(BlockReason.AlternateDataStream, text, "Colons (alternate data streams) are not allowed in paths.");
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (IsReservedDeviceName(segment))
            {
                return Block(BlockReason.DevicePath, text, "Reserved device names are not allowed.");
            }
        }

        if (normalized.Length > _options.MaxPathLength)
        {
            return Block(BlockReason.PathTooLong, text, "The path is too long.");
        }

        string combined;
        if (hasDrive)
        {
            combined = normalized;
        }
        else if (normalized[0] == '/')
        {
            if (_options.WorkspaceRoot is null)
            {
                return Block(BlockReason.NoBaseDirectory, text, "There is no workspace root to resolve this path against.");
            }

            combined = Path.Combine(_options.WorkspaceRoot, string.Join('/', segments));
        }
        else
        {
            var baseDirectory = _options.DocumentDirectory ?? _options.WorkspaceRoot;
            if (baseDirectory is null)
            {
                return Block(BlockReason.NoBaseDirectory, text, "There is no document location to resolve this path against.");
            }

            combined = Path.Combine(baseDirectory, normalized);
        }

        string full;
        try
        {
            full = TrimSeparators(Path.GetFullPath(combined));
        }
        catch (PathTooLongException)
        {
            return Block(BlockReason.PathTooLong, text, "The path is too long.");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Block(BlockReason.InvalidPath, text, "The path is not valid.");
        }

        if (full.Length > _options.MaxPathLength)
        {
            return Block(BlockReason.PathTooLong, text, "The path is too long.");
        }

        var selectedImage = isImage && _imageFiles.Contains(full);
        if (!selectedImage && !IsWithinAny(full, _roots))
        {
            return segments.Contains("..")
                ? Block(BlockReason.Traversal, text, "The path leaves the allowed folders.")
                : Block(BlockReason.OutsideAllowedRoots, text, "The path is outside the allowed folders.");
        }

        var canonical = CanonicalizeExisting(full);
        if (canonical is null || (selectedImage
            ? !string.Equals(canonical, full, PathComparison)
            : !IsWithinAny(canonical, _canonicalRoots!)))
        {
            return Block(BlockReason.SymlinkEscape, text, "The path resolves through a link to outside the allowed folders.");
        }

        var risky = RiskyExtensions.Contains(Path.GetExtension(full)) || RiskyExtensions.Contains(Path.GetExtension(canonical));
        if (!isImage)
        {
            return new LocalFile(canonical, fragment, risky, File.Exists(canonical) || Directory.Exists(canonical));
        }

        if (!_options.ImageExtensions.Contains(Path.GetExtension(full)) || !_options.ImageExtensions.Contains(Path.GetExtension(canonical)))
        {
            return Block(BlockReason.UnsupportedImageType, text, "The file type is not an allowed image format.");
        }

        if (!File.Exists(canonical))
        {
            return Block(BlockReason.NotFound, text, "The image file was not found.");
        }

        try
        {
            if (new FileInfo(canonical).Length > _options.MaxImageBytes)
            {
                return Block(BlockReason.FileTooLarge, text, "The image file exceeds the size limit.");
            }
        }
        catch (IOException)
        {
            return Block(BlockReason.NotFound, text, "The image file could not be read.");
        }

        return new LocalFile(canonical, fragment, IsActiveContentRisk: risky, Exists: true);
    }

    private static bool IsReservedDeviceName(string segment)
    {
        var name = segment.TrimEnd(' ', '.');
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            name = name[..dot];
        }

        name = name.TrimEnd(' ');
        if (name.Length is < 3 or > 6)
        {
            return false;
        }

        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || name.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || name.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.Length == 4
            && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && char.IsAsciiDigit(name[3]);
    }

    private static bool IsWithinAny(string path, string?[] roots)
    {
        foreach (var root in roots)
        {
            if (root is not null && IsWithin(path, root))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWithin(string path, string root)
    {
        if (!path.StartsWith(root, PathComparison))
        {
            return false;
        }

        return path.Length == root.Length
            || root[^1] == Path.DirectorySeparatorChar
            || root[^1] == Path.AltDirectorySeparatorChar
            || path[root.Length] == Path.DirectorySeparatorChar
            || path[root.Length] == Path.AltDirectorySeparatorChar;
    }

    /// <summary>
    /// Resolves every existing link along the path, component by component, so a link in a middle directory
    /// is followed just like one on the file. Components that do not exist are kept as written.
    /// </summary>
    private static string? CanonicalizeExisting(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        var pending = new LinkedList<string>(fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries));
        var current = root;
        var hops = 0;
        while (pending.First is { } node)
        {
            pending.RemoveFirst();
            var segment = node.Value;
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                current = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? root;
                continue;
            }

            var next = Path.Combine(current, segment);
            string? target = null;
            try
            {
                FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
                target = info.LinkTarget;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }

            if (target is null)
            {
                current = next;
                continue;
            }

            if (++hops > MaxLinkHops)
            {
                return null;
            }

            if (target.StartsWith(@"\??\", StringComparison.Ordinal))
            {
                target = target[4..];
            }

            string resolved;
            try
            {
                resolved = Path.GetFullPath(target, current);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }

            var resolvedRoot = Path.GetPathRoot(resolved);
            if (string.IsNullOrEmpty(resolvedRoot))
            {
                return null;
            }

            current = resolvedRoot;
            var parts = resolved[resolvedRoot.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            for (var i = parts.Length - 1; i >= 0; i--)
            {
                pending.AddFirst(parts[i]);
            }
        }

        return TrimSeparators(current);
    }

    private static bool TryGetScheme(string text, out string scheme)
    {
        scheme = string.Empty;
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 1 || !char.IsAsciiLetter(text[0]))
        {
            return false;
        }

        for (var i = 1; i < colon; i++)
        {
            var c = text[i];
            // A dot means "file.md:stream"; treating it as a scheme would hide the alternate-data-stream attempt.
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-'))
            {
                return false;
            }
        }

        scheme = text[..colon].ToLowerInvariant();
        return true;
    }

    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    [GeneratedRegex(@"%(?:2[5eEfF]|5[cC]|3[aA]|[01][0-9a-fA-F]|7[fF])")]
    private static partial Regex HiddenEncodingPattern();
}
