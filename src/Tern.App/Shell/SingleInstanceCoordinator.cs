using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Tern.App.Shell;

/// <summary>Acquire and dispose on the entry thread, which owns the primary instance's mutex.</summary>
public sealed class SingleInstanceCoordinator : IDisposable
{
    private const int MaximumMessageBytes = 1024 * 1024;
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Channel<string[]> _requests = Channel.CreateBounded<string[]>(32);
    private readonly TaskCompletionSource<(Func<IReadOnlyList<string>, Task> Handler, Action<Exception> Error)> _handler =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly NamedPipeServerStream? _server;
    private readonly Task _listener = Task.CompletedTask;
    private readonly Task _consumer = Task.CompletedTask;
    private bool _disposed;

    public bool IsPrimary { get; }

    public SingleInstanceCoordinator(AppPaths paths)
    {
        var scope = Environment.UserDomainName + "\0" + Environment.UserName + "\0"
            + Path.GetFullPath(paths.StateDirectory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        _pipeName = "Tern-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
        _mutex = new Mutex(false, (OperatingSystem.IsWindows() ? @"Local\" : "") + _pipeName);
        try { IsPrimary = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; }
        if (!IsPrimary) return;
        try
        {
            _server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _listener = ListenAsync(_shutdown.Token);
            _consumer = ConsumeAsync(_shutdown.Token);
        }
        catch
        {
            _server?.Dispose();
            _mutex.ReleaseMutex();
            _mutex.Dispose();
            _shutdown.Dispose();
            throw;
        }
    }

    public void SetArgumentHandler(Func<IReadOnlyList<string>, Task> handler, Action<Exception> reportError)
    {
        if (!IsPrimary) throw new InvalidOperationException("Only the primary instance handles forwarded arguments.");
        if (!_handler.TrySetResult((handler, reportError))) throw new InvalidOperationException("The argument handler is already registered.");
    }

    public async Task ForwardAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (IsPrimary) throw new InvalidOperationException("The primary instance cannot forward to itself.");
        var paths = arguments.Where(argument => !string.IsNullOrWhiteSpace(argument) && !argument.StartsWith('-'))
            .Select(Path.GetFullPath).ToArray();
        if (paths.Length > 256) throw new InvalidDataException("Too many files were requested at once.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(paths);
        if (bytes.Length > MaximumMessageBytes) throw new InvalidDataException("The requested file list is too large.");
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await pipe.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await pipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        var acknowledgement = new byte[1];
        await pipe.ReadExactlyAsync(acknowledgement, cancellationToken).ConfigureAwait(false);
        if (acknowledgement[0] != 1)
            throw new IOException("The running instance could not accept more files. Switch to its window and try again.");
    }

    private async Task ListenAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await _server!.WaitForConnectionAsync(stopping).ConfigureAwait(false);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var token = requestTimeout.Token;
                var prefix = new byte[4];
                await _server.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
                if (length is < 2 or > MaximumMessageBytes) throw new InvalidDataException("Invalid forwarded argument length.");
                var bytes = new byte[length];
                await _server.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
                var arguments = JsonSerializer.Deserialize<string[]>(bytes);
                if (arguments is null || arguments.Length > 256
                    || arguments.Any(argument => string.IsNullOrWhiteSpace(argument) || !Path.IsPathFullyQualified(argument)))
                    throw new InvalidDataException("Invalid forwarded file list.");
                var accepted = _requests.Writer.TryWrite(arguments);
                await _server.WriteAsync(new byte[] { accepted ? (byte)1 : (byte)0 }, token).ConfigureAwait(false);
                await _server.FlushAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (stopping.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or OperationCanceledException)
            {
                Trace.TraceWarning("Tern argument forwarding request failed: {0}", ex.Message);
            }
            finally
            {
                if (!stopping.IsCancellationRequested && _server!.IsConnected) _server.Disconnect();
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken stopping)
    {
        try
        {
            var (handler, reportError) = await _handler.Task.WaitAsync(stopping).ConfigureAwait(false);
            await foreach (var request in _requests.Reader.ReadAllAsync(stopping).ConfigureAwait(false))
            {
                try { await handler(request).WaitAsync(stopping).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested) { break; }
                catch (Exception ex) { reportError(ex); }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _requests.Writer.TryComplete();
        _server?.Dispose();
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
        _ = FinishShutdownAsync();
    }

    private async Task FinishShutdownAsync()
    {
        try { await Task.WhenAll(_listener, _consumer).ConfigureAwait(false); }
        finally { _shutdown.Dispose(); }
    }
}
