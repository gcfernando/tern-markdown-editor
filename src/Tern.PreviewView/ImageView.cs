using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Tern.Rendering.Resources;

namespace Tern.PreviewView;

/// <summary>
/// Shows an image that the resource policy already approved. Only existing local raster files and embedded data are
/// decoded; SVG is never executed and nothing is fetched from the network.
/// </summary>
internal sealed class ImageView : Border, IDisposable
{
    protected override AutomationPeer OnCreateAutomationPeer() => new ImageAutomationPeer(this);

    private sealed class ImageAutomationPeer(ImageView owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        // Loading text, raster surfaces and SVG internals all represent the same image.
        protected override IReadOnlyList<AutomationPeer>? GetChildrenCore() => null;
    }

    private const long MaxFileBytes = 16 * 1024 * 1024;
    private const long MaxPixels = 50_000_000;
    private const int MaxDecodeWidth = 2048;
    private const long MaxLivePixels = 32_000_000;
    private static readonly SemaphoreSlim DecodeSlots = new(2, 2);
    private static long _livePixels;
    private static int _activeDecodes;

    private static readonly HashSet<string> RasterExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp",
    };

    private readonly string _alt;
    private readonly object _ownership = new();
    private readonly List<Task> _loads = [];
    // Deterministic lifecycle checkpoint for tests; application callers leave this null.
    private readonly Action? _beforeDecode;
    private int _generation;
    private int _attachmentVersion;
    private bool _attached;
    private Func<CancellationToken, Task<DecodedImage?>>? _load;
    private readonly Func<string, CancellationToken, Task<byte[]?>>? _remoteImageLoader;
    private CancellationTokenSource? _loading;
    private DecodedImage? _loaded;
    private DecodedImage? _pending;
    private bool _disposed;

    internal static long LivePixels => Interlocked.Read(ref _livePixels);
    internal static int ActiveDecodes => Volatile.Read(ref _activeDecodes);

    /// <summary>Completes after started decoders have exited and relinquished canceled results.
    /// Native decoding cannot be interrupted while it is running.</summary>
    internal Task LoadingCompletion
    {
        get
        {
            lock (_ownership) return Task.WhenAll(_loads);
        }
    }

    public ImageView(ResourceDecision resolution, string alt, string? title, Action? beforeDecode = null,
        Func<string, CancellationToken, Task<byte[]?>>? remoteImageLoader = null)
    {
        _beforeDecode = beforeDecode;
        _remoteImageLoader = remoteImageLoader;
        _alt = alt;
        Alt = alt;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        AutomationProperties.SetName(this, alt.Length > 0 ? "Image: " + alt : "Image");
        State = ImageState.Loading;
        if (title is { Length: > 0 })
        {
            ToolTip.SetTip(this, title);
        }

        Child = Describe(resolution);
    }

    public string Alt { get; }

    /// <summary>What is currently shown: loading, loaded, unavailable or svg-placeholder.</summary>
    private ImageState _state = ImageState.Loading;
    public ImageState State
    {
        get => _state;
        private set
        {
            _state = value;
            AutomationProperties.SetItemStatus(this, value.ToString());
        }
    }

    public event EventHandler? StateChanged;

    private Control Describe(ResourceDecision resolution)
    {
        if (resolution is Blocked { Reason: BlockReason.RemoteResourceBlocked } remoteImage && _remoteImageLoader is not null)
        {
            _load = async token =>
            {
                var data = await _remoteImageLoader(remoteImage.Original, token).ConfigureAwait(false);
                if (data is null || data.Length > MaxFileBytes) return null;
                token.ThrowIfCancellationRequested();
                return LooksLikeSvg(data) ? LoadSvgBytes(data) : LoadBytes(data, _beforeDecode);
            };
            return Message(_alt.Length > 0 ? _alt : "Loading image…", muted: true);
        }
        switch (resolution)
        {
            case LocalFile { Exists: true } file when Path.GetExtension(file.CanonicalPath).Equals(".svg", StringComparison.OrdinalIgnoreCase):
                _load = _ => Task.FromResult(LoadFile(file.CanonicalPath, _beforeDecode, svg: true));
                return Message(_alt.Length > 0 ? _alt : "Loading image…", muted: true);
            case LocalFile { Exists: true } file when RasterExtensions.Contains(Path.GetExtension(file.CanonicalPath)):
                _load = _ => Task.FromResult(LoadFile(file.CanonicalPath, _beforeDecode));
                return Message(_alt.Length > 0 ? _alt : "Loading image…", muted: true);
            case InlineImage inline when inline.MediaType.Contains("svg", StringComparison.OrdinalIgnoreCase):
                _load = _ => Task.FromResult(LoadSvgBytes(inline.Data));
                return Message(_alt.Length > 0 ? _alt : "Loading image…", muted: true);
            case InlineImage inline when inline.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase):
                _load = _ => Task.FromResult(LoadBytes(inline.Data, _beforeDecode));
                return Message(_alt.Length > 0 ? _alt : "Loading image…", muted: true);
            default:
                return Unavailable();
        }
    }

    private Control Unavailable()
    {
        State = ImageState.Unavailable;
        var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
        if (_alt.Length > 0)
        {
            panel.Children.Add(Message(_alt, muted: false));
        }

        panel.Children.Add(Message("image unavailable", muted: true, italic: true));
        return panel;
    }

    private TextBlock Message(string text, bool muted, bool italic = false)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontStyle = italic ? FontStyle.Italic : FontStyle.Normal };
        PreviewTheme.Bind(block, TextBlock.ForegroundProperty, muted ? PreviewKeys.Muted : PreviewKeys.Foreground);
        return block;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _attachmentVersion++;
        if (!_disposed && _load is not null && _loaded is null && _loading is null)
        {
            StartLoad(_load);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        var version = ++_attachmentVersion;
        // InlineUIContainer can reparent its child during text layout. Releasing or replacing
        // Child during that transient detach would invalidate layout and start another decode.
        Dispatcher.UIThread.Post(() =>
        {
            if (!_attached && version == _attachmentVersion)
            {
                ReleaseResources();
                if (_load is not null)
                {
                    Child = Message(_alt.Length > 0 ? _alt : "Loading image…", muted: true);
                    State = ImageState.Loading;
                }
            }
        }, DispatcherPriority.Background);
        base.OnDetachedFromVisualTree(e);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _load = null;
        Child = null;
        ReleaseResources();
        GC.SuppressFinalize(this);
    }

    private void ReleaseResources()
    {
        lock (_ownership)
        {
            _generation++;
            _loading?.Cancel();
            _loading?.Dispose();
            _loading = null;
            _pending?.Dispose();
            _pending = null;
            _loaded?.Dispose();
            _loaded = null;
        }
    }

    private void StartLoad(Func<CancellationToken, Task<DecodedImage?>> load)
    {
        int generation;
        CancellationToken cancellationToken;
        lock (_ownership)
        {
            generation = ++_generation;
            _loading?.Cancel();
            _loading?.Dispose();
            _loading = new CancellationTokenSource();
            cancellationToken = _loading.Token;
        }
        var work = Task.Run(async () =>
        {
            DecodedImage? bitmap = null;
            var entered = false;
            var decoding = false;
            try
            {
                await DecodeSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                entered = true;
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref _activeDecodes);
                decoding = true;
                bitmap = await load(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                // An unreadable or corrupt image simply falls back to the "unavailable" presentation.
            }
            finally
            {
                if (decoding) Interlocked.Decrement(ref _activeDecodes);
                if (entered)
                {
                    DecodeSlots.Release();
                }
            }

            lock (_ownership)
            {
                if (cancellationToken.IsCancellationRequested || generation != _generation)
                {
                    bitmap?.Dispose();
                    return;
                }

                _pending = bitmap;
            }

            Dispatcher.UIThread.Post(() => Complete(generation));
        });
        lock (_ownership)
        {
            _loads.RemoveAll(task => task.IsCompletedSuccessfully);
            _loads.Add(work);
        }
    }

    private void Complete(int generation)
    {
        DecodedImage? bitmap;
        lock (_ownership)
        {
            if (generation != _generation)
            {
                return;
            }

            bitmap = _pending;
            _pending = null;
            _loaded = bitmap;
        }

        if (bitmap is null)
        {
            Child = Unavailable();
        }
        else if (bitmap.SvgData is { } svg)
        {
            if (SafeSvgImage.TryCreate(svg, out var control))
            {
                Child = control;
                State = ImageState.Loaded;
            }
            else Child = Unavailable();
        }
        else
        {
            Child = new Image { Source = bitmap.Bitmap, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
            State = ImageState.Loaded;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static DecodedImage? LoadSvgBytes(byte[] data) =>
        SafeSvgPolicy.TryNormalize(data, out var normalized, out _) ? new DecodedImage(null, 0, normalized) : null;

    private static DecodedImage? LoadFile(string path, Action? beforeDecode, bool svg = false)
    {
        var info = new FileInfo(path);
        var limit = svg ? SafeSvgPolicy.MaxBytes : MaxFileBytes;
        if (!info.Exists || info.Length > limit)
        {
            return null;
        }

        byte[] data;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            if (stream.Length > limit)
            {
                return null;
            }

            data = new byte[stream.Length];
            stream.ReadExactly(data);
        }

        return svg ? LoadSvgBytes(data) : LoadBytes(data, beforeDecode);
    }

    private static DecodedImage? LoadBytes(byte[] data, Action? beforeDecode)
    {
        if (data.Length > MaxFileBytes)
        {
            return null;
        }

        using var stream = new MemoryStream(data, writable: false);
        return Decode(stream, beforeDecode);
    }

    private static DecodedImage? Decode(Stream stream, Action? beforeDecode)
    {
        var (width, height) = ProbeSize(stream);
        stream.Position = 0;
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
        {
            return null;
        }

        var scale = Math.Min(1, (double)MaxDecodeWidth / Math.Max(width, height));
        var pixels = (long)Math.Ceiling(width * scale) * (long)Math.Ceiling(height * scale);
        while (true)
        {
            var current = Interlocked.Read(ref _livePixels);
            if (current + pixels > MaxLivePixels)
            {
                return null;
            }

            if (Interlocked.CompareExchange(ref _livePixels, current + pixels, current) == current)
            {
                break;
            }
        }

        try
        {
            beforeDecode?.Invoke();
            var bitmap = scale == 1 ? new Bitmap(stream)
                : width >= height ? Bitmap.DecodeToWidth(stream, MaxDecodeWidth)
                : Bitmap.DecodeToHeight(stream, MaxDecodeWidth);
            return new DecodedImage(bitmap, pixels);
        }
        catch
        {
            Interlocked.Add(ref _livePixels, -pixels);
            throw;
        }
    }

    private static bool LooksLikeSvg(byte[] data)
    {
        if (data.Length == 0 || data.Length > 1024 * 1024) return false;
        if (data.Length >= 2 && (data[0] == 0xFF && data[1] == 0xFE || data[0] == 0xFE && data[1] == 0xFF)) return true;
        var index = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
        while (index < data.Length && data[index] is 9 or 10 or 13 or 32) index++;
        // The bounded XML parser still requires an SVG root and rejects active content.
        return index < data.Length && data[index] == '<';
    }

    private sealed class DecodedImage(Bitmap? bitmap, long pixels, byte[]? svgData = null) : IDisposable
    {
        private int _disposed;

        public Bitmap? Bitmap { get; } = bitmap;
        public byte[]? SvgData { get; } = svgData;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Bitmap?.Dispose();
            Interlocked.Add(ref _livePixels, -pixels);
        }
    }

    /// <summary>Reads raster pixel dimensions before decoding; unknown headers are refused.</summary>
    internal static (int Width, int Height) ProbeSize(Stream stream)
    {
        Span<byte> head = stackalloc byte[32];
        var read = stream.Read(head);
        if (read >= 24 && head[0] == 0x89 && head[1] == 'P' && head[2] == 'N' && head[3] == 'G')
        {
            return (ReadInt32BigEndian(head, 16), ReadInt32BigEndian(head, 20));
        }

        if (read >= 10 && head[0] == 'G' && head[1] == 'I' && head[2] == 'F')
        {
            return (head[6] | (head[7] << 8), head[8] | (head[9] << 8));
        }

        if (read >= 4 && head[0] == 0xFF && head[1] == 0xD8)
        {
            return ProbeJpeg(stream);
        }

        if (read >= 26 && head[0] == 'B' && head[1] == 'M')
        {
            var headerSize = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(head[14..]);
            if (headerSize == 12)
            {
                return (System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(head[18..]),
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(head[20..]));
            }

            if (headerSize >= 40)
            {
                var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(head[18..]);
                var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(head[22..]);
                return (width, height == int.MinValue ? 0 : Math.Abs(height));
            }
        }

        if (read >= 30 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
        {
            if (head[12..16].SequenceEqual("VP8X"u8))
            {
                return (1 + head[24] + (head[25] << 8) + (head[26] << 16),
                    1 + head[27] + (head[28] << 8) + (head[29] << 16));
            }

            if (head[12..16].SequenceEqual("VP8L"u8) && head[20] == 0x2F)
            {
                return (1 + head[21] + ((head[22] & 0x3F) << 8),
                    1 + (head[22] >> 6) + (head[23] << 2) + ((head[24] & 0x0F) << 10));
            }

            if (head[12..16].SequenceEqual("VP8 "u8) && head[23..26].SequenceEqual(new byte[] { 0x9D, 0x01, 0x2A }))
            {
                return ((head[26] | (head[27] << 8)) & 0x3FFF, (head[28] | (head[29] << 8)) & 0x3FFF);
            }
        }

        return (0, 0);
    }

    private static int ReadInt32BigEndian(ReadOnlySpan<byte> data, int offset) =>
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];

    private static (int Width, int Height) ProbeJpeg(Stream stream)
    {
        stream.Position = 2;
        var buffer = new byte[9];
        for (var guard = 0; guard < 256; guard++)
        {
            int b;
            do
            {
                b = stream.ReadByte();
            }
            while (b is not 0xFF and not -1);

            int marker;
            do
            {
                marker = stream.ReadByte();
            }
            while (marker == 0xFF);

            if (marker == -1)
            {
                return (0, 0);
            }

            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                continue;
            }

            if (stream.Read(buffer, 0, 2) < 2)
            {
                return (0, 0);
            }

            var length = (buffer[0] << 8) | buffer[1];
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                return stream.Read(buffer, 0, 5) < 5 ? (0, 0) : ((buffer[3] << 8) | buffer[4], (buffer[1] << 8) | buffer[2]);
            }

            stream.Seek(Math.Max(0, length - 2), SeekOrigin.Current);
        }

        return (0, 0);
    }
}

internal enum ImageState
{
    Loading,
    Loaded,
    Unavailable,
    SvgPlaceholder,
}
