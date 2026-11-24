using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Tern.Desktop.Services;

/// <summary>Encrypted temporary blocks with an invocation-only key that is never written to disk.</summary>
internal sealed class CopilotReplySpool : Stream
{
    private readonly FileStream _storage;
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly AesGcm _cipher;
    private byte[] _plain = [];
    private int _at;
    internal string FilePath { get; }

    internal CopilotReplySpool()
    {
        FilePath = Path.Combine(Path.GetTempPath(), "Tern-Copilot-Reply-" + Guid.NewGuid().ToString("N") + ".tmp");
        _cipher = new AesGcm(_key, 16);
        _storage = new FileStream(FilePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65_536,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose | FileOptions.SequentialScan);
    }

    internal void Rewind()
    {
        _storage.Position = 0;
        CryptographicOperations.ZeroMemory(_plain);
        _plain = [];
        _at = 0;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (!buffer.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, 65_536);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var encrypted = new byte[count];
            _cipher.Encrypt(nonce, buffer.Span[..count], encrypted, tag);
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, count);
            await _storage.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await _storage.WriteAsync(nonce, cancellationToken).ConfigureAwait(false);
            await _storage.WriteAsync(tag, cancellationToken).ConfigureAwait(false);
            await _storage.WriteAsync(encrypted, cancellationToken).ConfigureAwait(false);
            buffer = buffer[count..];
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.IsEmpty) return 0;
        if (_at == _plain.Length)
        {
            CryptographicOperations.ZeroMemory(_plain);
            var header = new byte[4];
            var first = await _storage.ReadAsync(header, cancellationToken).ConfigureAwait(false);
            if (first == 0) return 0;
            await _storage.ReadExactlyAsync(header.AsMemory(first), cancellationToken).ConfigureAwait(false);
            var count = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (count is < 1 or > 65_536) throw new IOException("The private AI reply could not be read.");
            var nonce = new byte[12]; var tag = new byte[16]; var encrypted = new byte[count];
            await _storage.ReadExactlyAsync(nonce, cancellationToken).ConfigureAwait(false);
            await _storage.ReadExactlyAsync(tag, cancellationToken).ConfigureAwait(false);
            await _storage.ReadExactlyAsync(encrypted, cancellationToken).ConfigureAwait(false);
            _plain = new byte[count];
            try { _cipher.Decrypt(nonce, encrypted, tag, _plain); }
            catch (CryptographicException) { throw new IOException("The private AI reply could not be unlocked."); }
            _at = 0;
        }
        var length = Math.Min(buffer.Length, _plain.Length - _at);
        _plain.AsMemory(_at, length).CopyTo(buffer);
        _at += length;
        return length;
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => _storage.FlushAsync(cancellationToken);
    public override void Flush() => _storage.Flush();
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _cipher.Dispose(); _storage.Dispose(); CryptographicOperations.ZeroMemory(_plain); CryptographicOperations.ZeroMemory(_key); }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        try { await _storage.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            _cipher.Dispose();
            CryptographicOperations.ZeroMemory(_plain);
            CryptographicOperations.ZeroMemory(_key);
            GC.SuppressFinalize(this);
        }
    }
}
