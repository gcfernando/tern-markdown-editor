using Tern.Filesystem;

namespace Tern.Workspaces;

/// <summary>Supplies document text by path, so analysis can run against editor buffers as well as files on disk.</summary>
public interface IDocumentTextProvider
{
    /// <summary>The current text of the document, or null when it cannot be read.</summary>
    ValueTask<string?> ReadTextAsync(string path, CancellationToken cancellationToken);
}

/// <summary>Reads documents from disk through <see cref="TextFileCodec"/>, so the text matches what the editor would show.</summary>
public sealed class DiskDocumentTextProvider(long maximumBytes = DocumentFileService.MaximumEagerLoadBytes) : IDocumentTextProvider
{
    /// <summary>Set while a workspace is prepared so another reader can reuse these bytes.</summary>
    public SharedFileReads? SharedReads { get; set; }

    public ValueTask<string?> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (CloudFiles.IsOnlineOnly(path)) return ValueTask.FromResult<string?>(null);
            var bytes = SharedReads is { } shared ? shared.Read(path, () => ReadBytes(path)) : ReadBytes(path);
            return ValueTask.FromResult<string?>(bytes is null || bytes.Length > maximumBytes ? null : TextFileCodec.Decode(bytes).Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult<string?>(null);
        }
    }

    // Small notes read much faster synchronously than through overlapped I/O, and callers are already on worker threads.
    private byte[]? ReadBytes(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // Recheck on the opened handle: a file can grow after the directory scan.
        var length = RandomAccess.GetLength(handle);
        if (length > maximumBytes) return null;
        var bytes = new byte[length];
        for (var total = 0; total < bytes.Length;)
        {
            var read = RandomAccess.Read(handle, bytes.AsSpan(total), total);
            if (read == 0) throw new EndOfStreamException();
            total += read;
        }
        return bytes;
    }
}

/// <summary>Serves buffer text for paths that are open in the editor and falls back to another provider for everything else.</summary>
public sealed class OverlayTextProvider : IDocumentTextProvider
{
    private readonly IDocumentTextProvider _fallback;
    private readonly Dictionary<string, string> _buffers = new(WorkspacePaths.Comparer);
    private readonly Lock _gate = new();

    public OverlayTextProvider(IDocumentTextProvider? fallback = null)
    {
        _fallback = fallback ?? new DiskDocumentTextProvider();
    }

    public void Set(string path, string text)
    {
        lock (_gate)
        {
            _buffers[WorkspacePaths.Normalize(path)] = text;
        }
    }

    public void Clear(string path)
    {
        lock (_gate)
        {
            _buffers.Remove(WorkspacePaths.Normalize(path));
        }
    }

    public ValueTask<string?> ReadTextAsync(string path, CancellationToken cancellationToken) =>
        ReadTextAsync(path, _fallback, cancellationToken);

    public ValueTask<string?> ReadTextAsync(string path, IDocumentTextProvider fallback, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_buffers.TryGetValue(WorkspacePaths.Normalize(path), out var text))
            {
                return ValueTask.FromResult<string?>(text);
            }
        }

        return fallback.ReadTextAsync(path, cancellationToken);
    }
}
