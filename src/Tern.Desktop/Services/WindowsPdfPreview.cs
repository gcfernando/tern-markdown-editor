using Windows.Data.Pdf;
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
        await pdfPage.RenderToStreamAsync(output, new PdfPageRenderOptions { DestinationWidth = Math.Clamp(width, 400u, 2400u) });
        using var reader = new DataReader(output.GetInputStreamAt(0));
        var count = checked((uint)output.Size);
        await reader.LoadAsync(count);
        var data = new byte[count]; reader.ReadBytes(data);
        return data;
    }
    public void Dispose() => _source.Dispose();
}
