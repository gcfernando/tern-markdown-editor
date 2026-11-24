using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Tern.Desktop.Services;

/// <summary>Uses the Windows PDF engine to display the same bytes that will be handed to the print reader.</summary>
internal sealed class WindowsPdfPreview : IDisposable
{
    private readonly InMemoryRandomAccessStream _source;
    private readonly PdfDocument _pdf;
    private WindowsPdfPreview(InMemoryRandomAccessStream source, PdfDocument pdf) { _source = source; _pdf = pdf; }
    public int PageCount => checked((int)_pdf.PageCount);
    public (double Width, double Height) PageSize(int page)
    {
        using var pdfPage = _pdf.GetPage(checked((uint)(page - 1)));
        return (pdfPage.Size.Width, pdfPage.Size.Height);
    }
    public static async Task<WindowsPdfPreview> LoadAsync(byte[] data)
    {
        var source = new InMemoryRandomAccessStream();
        try
        {
            using (var writer = new DataWriter(source.GetOutputStreamAt(0)))
            { writer.WriteBytes(data); await writer.StoreAsync(); await writer.FlushAsync(); }
            source.Seek(0);
            return new WindowsPdfPreview(source, await PdfDocument.LoadFromStreamAsync(source));
        }
        catch { source.Dispose(); throw; }
    }
    public async Task<byte[]> RenderAsync(int page, uint width)
    {
        using var pdfPage = _pdf.GetPage(checked((uint)(page - 1)));
        using var output = new InMemoryRandomAccessStream();
        await pdfPage.RenderToStreamAsync(output, new PdfPageRenderOptions
        { DestinationWidth = Math.Clamp(width, 400u, 2400u), BackgroundColor = new Windows.UI.Color { A = 255, R = 255, G = 255, B = 255 } });
        using var reader = new DataReader(output.GetInputStreamAt(0));
        var count = checked((uint)output.Size);
        await reader.LoadAsync(count);
        var data = new byte[count]; reader.ReadBytes(data);
        return data;
    }
    public async Task<RasterizedPdfPage> RenderPixelsAsync(int page, uint width)
    {
        var png = await RenderAsync(page, width);
        using var source = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(source.GetOutputStreamAt(0)))
        { writer.WriteBytes(png); await writer.StoreAsync(); await writer.FlushAsync(); }
        source.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(source);
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return new RasterizedPdfPage(checked((int)decoder.PixelWidth), checked((int)decoder.PixelHeight), pixels.DetachPixelData());
    }
    public void Dispose() => _source.Dispose();
}

internal sealed record RasterizedPdfPage(int Width, int Height, byte[] Pixels);
