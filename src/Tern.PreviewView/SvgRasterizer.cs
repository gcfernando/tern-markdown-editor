using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Tern.PreviewView;

/// <summary>Rasterizes the same bounded, inert SVG subset displayed in Reading. Requires the UI renderer.</summary>
public static class SvgRasterizer
{
    public static byte[]? RenderPng(byte[] bytes)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!SafeSvgImage.TryCreate(bytes, out var control) || control is null) return null;
        control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = control.DesiredSize;
        if (size.Width <= 0 || size.Height <= 0) return null;
        var scale = Math.Min(2, Math.Sqrt(16_000_000 / (size.Width * size.Height)));
        control.Arrange(new Rect(size));
        using var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width * scale)), Math.Max(1, (int)Math.Ceiling(size.Height * scale))), new Vector(96 * scale, 96 * scale));
        bitmap.Render(control);
        using var output = new MemoryStream(); bitmap.Save(output);
        // Avalonia's PNG encoder omits pixel density. Preserve the SVG's CSS-pixel
        // dimensions when a physical-page consumer reads the higher-resolution raster.
        return WithDensity(output.ToArray(), 96 * scale);
    }

    private static byte[] WithDensity(byte[] png, double dpi)
    {
        using var output = new MemoryStream(png.Length + 21);
        output.Write(png.AsSpan(0, 33)); // PNG signature and fixed-size IHDR chunk.
        Span<byte> chunk = stackalloc byte[21];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk, 9);
        "pHYs"u8.CopyTo(chunk[4..]);
        var pixelsPerMetre = (uint)Math.Round(dpi / .0254);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk[8..], pixelsPerMetre);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk[12..], pixelsPerMetre);
        chunk[16] = 1;
        uint crc = uint.MaxValue;
        foreach (var value in chunk[4..17])
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk[17..], ~crc);
        output.Write(chunk);
        // Replace an encoder-supplied density chunk if a future encoder adds one.
        var offset = 33;
        while (offset < png.Length)
        {
            var length = checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset)) + 12);
            if (!png.AsSpan(offset + 4, 4).SequenceEqual("pHYs"u8)) output.Write(png.AsSpan(offset, length));
            offset += length;
        }
        return output.ToArray();
    }
}
