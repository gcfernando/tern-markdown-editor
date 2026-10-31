using System.Security.Cryptography;
using Tern.Rendering.Preview;
using Tern.Rendering.Resources;

namespace Tern.Export;

/// <summary>Thrown when an export cannot be produced at all, for example when no usable system font exists for PDF output.</summary>
public sealed class ExportException : Exception
{
    /// <summary>Initialises the exception with a message.</summary>
    public ExportException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises the exception with a message and its cause.</summary>
    public ExportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initialises an empty exception.</summary>
    public ExportException()
    {
    }
}

/// <summary>Defaults shared by all exporters.</summary>
public static class ExportDefaults
{
    /// <summary>
    /// Preview options suited to exporting: the same parsing as the live preview, but with a generous time and node budget
    /// because an export may take longer than an interactive redraw.
    /// </summary>
    public static PreviewOptions PreviewOptions { get; } = new()
    {
        MaxBuildTime = TimeSpan.FromSeconds(30),
        MaxNodes = 1_000_000,
    };
}

/// <summary>A raster image whose bytes have been read and whose format has been verified from its content, not its file name.</summary>
/// <param name="MediaType">The verified media type, for example <c>image/png</c>.</param>
/// <param name="Data">The image file bytes.</param>
public sealed record EmbeddedImage(string MediaType, byte[] Data)
{
    /// <summary>The image as a <c>data:</c> URI.</summary>
    public string ToDataUri() => string.Concat("data:", MediaType, ";base64,", Convert.ToBase64String(Data));
}

/// <summary>Recognises raster image formats from their leading bytes.</summary>
internal static class ImageSniffer
{
    public static string? Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[..8].SequenceEqual<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }

        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (data.Length >= 6 && data[..4].SequenceEqual<byte>([0x47, 0x49, 0x46, 0x38]))
        {
            return "image/gif";
        }

        if (data.Length >= 12 && data[..4].SequenceEqual<byte>([0x52, 0x49, 0x46, 0x46]) && data[8..12].SequenceEqual<byte>([0x57, 0x45, 0x42, 0x50]))
        {
            return "image/webp";
        }

        if (data.Length >= 26 && data[0] == 0x42 && data[1] == 0x4D)
        {
            return "image/bmp";
        }

        if (data.Length >= 6 && data[..4].SequenceEqual<byte>([0x00, 0x00, 0x01, 0x00]))
        {
            return "image/x-icon";
        }

        return null;
    }
}

/// <summary>Reads images that the resolver allowed, enforcing per-image and total size budgets.</summary>
internal sealed class ImageEmbedder(long maxImageBytes, long maxTotalBytes, List<string> warnings, Func<string, byte[]?>? remoteImageBytes = null)
{
    private readonly Dictionary<string, EmbeddedImage?> _cache = new(StringComparer.Ordinal);
    private long _total;

    public int Count { get; private set; }

    /// <summary>Resolves a decision to image bytes. Returns null and records a warning when the image cannot be embedded.</summary>
    public EmbeddedImage? TryEmbed(ResourceDecision decision, string label)
    {
        switch (decision)
        {
            case LocalFile { IsActiveContentRisk: true } active when !Path.GetExtension(active.CanonicalPath).Equals(".svg", StringComparison.OrdinalIgnoreCase):
                warnings.Add($"Image '{label}' contains unsupported active content and was not embedded.");
                return null;
            case LocalFile { Exists: false }:
                warnings.Add($"Image '{label}' was not found.");
                return null;
            case LocalFile local:
                if (_cache.TryGetValue(local.CanonicalPath, out var cached))
                {
                    return cached;
                }

                var loaded = ReadFile(local.CanonicalPath, label,
                    allowSvg: Path.GetExtension(local.CanonicalPath).Equals(".svg", StringComparison.OrdinalIgnoreCase));
                _cache[local.CanonicalPath] = loaded;
                return loaded;
            case InlineImage inline:
                var key = "data:" + Convert.ToHexString(SHA256.HashData(inline.Data));
                if (_cache.TryGetValue(key, out var cachedInline))
                {
                    return cachedInline;
                }

                var accepted = Accept(inline.Data, label, allowSvg: inline.MediaType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase));
                _cache[key] = accepted;
                return accepted;
            case Blocked { Reason: BlockReason.RemoteResourceBlocked } remote:
                var remoteKey = "remote:" + remote.Original;
                if (_cache.TryGetValue(remoteKey, out var remoteCached)) return remoteCached;
                EmbeddedImage? remoteImage = null;
                try
                {
                    if (remoteImageBytes?.Invoke(remote.Original) is { } data)
                        remoteImage = Accept(data, label, allowSvg: true);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    warnings.Add($"Remote image '{label}' could not be read.");
                }
                _cache[remoteKey] = remoteImage;
                if (remoteImage is not null) return remoteImage;
                warnings.Add($"Remote image '{label}' was not loaded by the host.");
                return null;
            case Blocked blocked:
                warnings.Add($"Image '{label}' was blocked: {blocked.Reason}.");
                return null;
            default:
                return null;
        }
    }

    private EmbeddedImage? ReadFile(string path, string label, bool allowSvg = false)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > maxImageBytes)
            {
                warnings.Add($"Image '{label}' is larger than {maxImageBytes / 1024} KB and was not embedded.");
                return null;
            }

            var buffer = new byte[stream.Length];
            stream.ReadExactly(buffer);
            return Accept(buffer, label, allowSvg);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Image '{label}' could not be read: {ex.Message}");
            return null;
        }
    }

    private EmbeddedImage? Accept(byte[] data, string label, bool allowSvg = false)
    {
        if (data.Length > maxImageBytes || data.Length > maxTotalBytes - _total)
        {
            warnings.Add($"Image '{label}' exceeds the embedding budget and was not embedded.");
            return null;
        }
        var mediaType = ImageSniffer.Detect(data);
        if (mediaType is null && allowSvg && SafeSvgPolicy.TryNormalize(data, out var normalized, out _))
        {
            data = normalized;
            mediaType = "image/svg+xml";
        }
        if (mediaType is null)
        {
            warnings.Add($"Image '{label}' is not a recognised raster image and was not embedded.");
            return null;
        }

        if (data.Length > maxImageBytes || _total + data.Length > maxTotalBytes)
        {
            warnings.Add($"Image '{label}' exceeds the embedding budget and was not embedded.");
            return null;
        }

        _total += data.Length;
        Count++;
        return new EmbeddedImage(mediaType, data);
    }
}

/// <summary>Cleans text before it is written to an output format.</summary>
internal static class ExportText
{
    /// <summary>Removes control characters and noncharacters, keeping tabs and line breaks.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var needsWork = false;
        foreach (var c in text)
        {
            if (IsDisallowed(c))
            {
                needsWork = true;
                break;
            }
        }

        if (!needsWork)
        {
            return text;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!IsDisallowed(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static bool IsDisallowed(char c) =>
        (c < 0x20 && c is not ('\t' or '\n' or '\r')) || c == 0x7F || (c >= 0x80 && c < 0xA0) || c is '\uFFFE' or '\uFFFF';
}

/// <summary>Writes output files without leaving a half-written file behind.</summary>
internal static class AtomicFile
{
    public static void Write(string path, byte[] data, string? sourcePath = null)
    {
        var full = Path.GetFullPath(path);
        EnsureSeparate(full, sourcePath);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, data);
            EnsureSeparate(full, sourcePath);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static void EnsureSeparate(string outputPath, string? sourcePath)
    {
        if (!string.IsNullOrEmpty(sourcePath) && Tern.Filesystem.FileIdentity.AreSameFile(outputPath, sourcePath))
        {
            throw new ExportException("Choose an export destination separate from the Markdown source.");
        }
    }
}
