using Tern.Filesystem;

namespace Tern.Workspaces;

/// <summary>Supplies document text by path, so analysis can run against editor buffers as well as files on disk.</summary>
public interface IDocumentTextProvider
{
    /// <summary>The current text of the document, or null when it cannot be read.</summary>
    ValueTask<string?> ReadTextAsync(string path, CancellationToken cancellationToken);
}

/// <summary>Reads documents from disk through <see cref="TextFileCodec"/>, so the text matches what the editor would show.</summary>
public sealed class DiskDocumentTextProvider : IDocumentTextProvider
{
    public async ValueTask<string?> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
            var bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            return TextFileCodec.Decode(bytes).Text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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

    public ValueTask<string?> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_buffers.TryGetValue(WorkspacePaths.Normalize(path), out var text))
            {
                return ValueTask.FromResult<string?>(text);
            }
        }

        return _fallback.ReadTextAsync(path, cancellationToken);
    }
}
