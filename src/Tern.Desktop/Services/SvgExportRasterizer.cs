using Avalonia.Threading;

namespace Tern.Desktop.Services;

internal static class SvgExportRasterizer
{
    internal static byte[]? Render(byte[] data)
    {
        try
        {
            return Dispatcher.UIThread.CheckAccess() ? Tern.PreviewView.SvgRasterizer.RenderPng(data)
                : Dispatcher.UIThread.InvokeAsync(() => Tern.PreviewView.SvgRasterizer.RenderPng(data)).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or IOException) { return null; }
    }
}
