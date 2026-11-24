using System.Collections.Concurrent;

namespace Tern.Workspaces;

/// <summary>
/// Lets link reading and search indexing share one disk read per file while a workspace is prepared. The second reader
/// takes the bytes and frees them, and the amount held is capped so very large workspaces cannot use excess memory.
/// </summary>
public sealed class SharedFileReads(long maximumHeldBytes = 64 * 1024 * 1024)
{
    private readonly ConcurrentDictionary<string, byte[]> _held = new(WorkspacePaths.Comparer);
    private long _heldBytes;

    public byte[]? Read(string path, Func<byte[]?> read)
    {
        if (_held.TryRemove(path, out var shared))
        {
            Interlocked.Add(ref _heldBytes, -shared.Length);
            return shared;
        }
        var bytes = read();
        if (bytes is null) return null;
        if (Interlocked.Add(ref _heldBytes, bytes.Length) > maximumHeldBytes || !_held.TryAdd(path, bytes))
            Interlocked.Add(ref _heldBytes, -bytes.Length);
        return bytes;
    }
}
