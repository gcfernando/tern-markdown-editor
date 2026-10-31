using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Tern.Desktop.Services;

/// <summary>Windows text printing with the system printer dialog. No external reader or runtime download is required.</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsTextPrinter
{
    public static async Task<bool> ShowDialogAndPrintAsync(nint owner, string title, string text)
    {
        var dialog = new PrintDialog
        {
            Size = (uint)Marshal.SizeOf<PrintDialog>(), Owner = owner,
            // Return a printer DC; driver handles copies/collation. Page ranges are disabled for text pagination.
            Flags = 0x100 | 0x4 | 0x8 | 0x40000 | 0x80000,
            Copies = 1, StartPage = uint.MaxValue,
        };
        try
        {
            var result = PrintDlgEx(ref dialog);
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            if (dialog.Result != 1) return false;
            var dc = dialog.DeviceContext;
            await Task.Run(() => Print(dc, title, text));
            return true;
        }
        finally
        {
            if (dialog.DeviceContext != 0 && DeleteDC(dialog.DeviceContext) == 0) System.Diagnostics.Trace.TraceWarning("Could not release printer device context.");
            if (dialog.DeviceMode != 0) GlobalFree(dialog.DeviceMode);
            if (dialog.DeviceNames != 0) GlobalFree(dialog.DeviceNames);
        }
    }

    private static void Print(nint dc, string title, string text)
    {
        var dpiX = GetDeviceCaps(dc, 88);
        var dpiY = GetDeviceCaps(dc, 90);
        var width = GetDeviceCaps(dc, 8);
        var height = GetDeviceCaps(dc, 10);
        if (dpiX <= 0 || dpiY <= 0 || width <= dpiX || height <= dpiY)
            throw new InvalidOperationException("The printer reported invalid page dimensions.");
        var marginX = dpiX / 2;
        var marginY = dpiY / 2;
        var lineHeight = (int)Math.Ceiling(dpiY * 14.0 / 72);
        var font = CreateFont(-(dpiY * 11 / 72), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, "Consolas");
        if (font == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var previous = SelectObject(dc, font);
        var started = false;
        try
        {
            var info = new DocumentInfo { Size = Marshal.SizeOf<DocumentInfo>(), Name = Marshal.StringToHGlobalUni(title) };
            try { Check(StartDoc(dc, ref info)); started = true; }
            finally { Marshal.FreeHGlobal(info.Name); }
            Check(StartPage(dc));
            var y = marginY;
            using var reader = new StringReader(text);
            while (reader.ReadLine() is { } paragraph)
            {
                var offset = 0;
                do
                {
                    if (y + lineHeight > height - marginY)
                    {
                        Check(EndPage(dc));
                        Check(StartPage(dc));
                        y = marginY;
                    }
                    var chunk = paragraph.Substring(offset, Math.Min(2048, paragraph.Length - offset)).Replace('\t', ' ');
                    var count = chunk.Length;
                    if (count > 0)
                    {
                        Check(GetTextExtentExPoint(dc, chunk, count, width - 2 * marginX, out var fit, 0, out _));
                        count = Math.Clamp(fit, 1, count);
                        if (count < chunk.Length)
                        {
                            var space = chunk.LastIndexOf(' ', count - 1, count);
                            if (space > 0) count = space + 1;
                            else if (count > 1 && char.IsHighSurrogate(chunk[count - 1])) count--;
                        }
                        Check(TextOut(dc, marginX, y, chunk, count));
                    }
                    offset += count;
                    y += lineHeight;
                }
                while (offset < paragraph.Length);
            }
            Check(EndPage(dc));
            Check(EndDoc(dc));
            started = false;
        }
        finally
        {
            if (started && AbortDoc(dc) <= 0) System.Diagnostics.Trace.TraceWarning("Could not cancel the failed printer job.");
            SelectObject(dc, previous);
            if (DeleteObject(font) == 0) System.Diagnostics.Trace.TraceWarning("Could not release printer font.");
        }
    }

    private static void Check(int result)
    {
        if (result <= 0) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrintDialog
    {
        public uint Size;
        public nint Owner, DeviceMode, DeviceNames, DeviceContext;
        public uint Flags, Flags2, ExclusionFlags, PageRanges, MaxPageRanges;
        public nint PageRangeBuffer;
        public uint MinPage, MaxPage, Copies;
        public nint Instance, TemplateName, Callback;
        public uint PropertyPages;
        public nint PropertyPageBuffer;
        public uint StartPage, Result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DocumentInfo
    {
        public int Size;
        public nint Name, Output, DataType;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize { public int Width, Height; }

    [LibraryImport("comdlg32.dll", EntryPoint = "PrintDlgExW")]
    private static partial int PrintDlgEx(ref PrintDialog dialog);
    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalFree(nint memory);
    [LibraryImport("gdi32.dll")]
    private static partial int DeleteDC(nint dc);
    [LibraryImport("gdi32.dll")]
    private static partial int GetDeviceCaps(nint dc, int index);
    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strike, uint charset, uint output, uint clip, uint quality, uint pitch, string face);
    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint value);
    [LibraryImport("gdi32.dll")]
    private static partial int DeleteObject(nint value);
    [LibraryImport("gdi32.dll", EntryPoint = "StartDocW", SetLastError = true)]
    private static partial int StartDoc(nint dc, ref DocumentInfo info);
    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial int StartPage(nint dc);
    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial int EndPage(nint dc);
    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial int EndDoc(nint dc);
    [LibraryImport("gdi32.dll")]
    private static partial int AbortDoc(nint dc);
    [LibraryImport("gdi32.dll", EntryPoint = "TextOutW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int TextOut(nint dc, int x, int y, string text, int length);
    [LibraryImport("gdi32.dll", EntryPoint = "GetTextExtentExPointW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int GetTextExtentExPoint(nint dc, string text, int length, int maxWidth, out int fit, nint widths, out NativeSize size);
}
