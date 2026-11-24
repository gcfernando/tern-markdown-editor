using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Tern.Desktop.Services;

internal sealed record PrinterProgress(string Phase, bool WaitingForUser = false, int? CompletedPages = null, int? TotalPages = null);

/// <summary>Windows text printing with the system printer dialog. No external reader or runtime download is required.</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsTextPrinter
{
    public static async Task<bool> ShowDialogAndPrintPdfAsync(nint owner, string title, byte[] pdf, CancellationToken cancellationToken,
        Action<PrinterProgress>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new PrintDialog
        {
            Size = (uint)Marshal.SizeOf<PrintDialog>(), Owner = owner,
            Flags = 0x100 | 0x4 | 0x8 | 0x40000 | 0x80000,
            Copies = 1, StartPage = uint.MaxValue,
        };
        try
        {
            progress?.Invoke(new("Choose a Windows printer and its options", WaitingForUser: true));
            var result = PrintDlgEx(ref dialog);
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            if (dialog.Result != 1) return false;
            progress?.Invoke(new("Preparing the selected Windows printer job"));
            await Task.Run(() => PrintPdfAsync(dialog.DeviceContext, title, pdf, cancellationToken, progress), cancellationToken);
            return true;
        }
        finally
        {
            if (dialog.DeviceContext != 0 && DeleteDC(dialog.DeviceContext) == 0) System.Diagnostics.Trace.TraceWarning("Could not release printer device context.");
            if (dialog.DeviceMode != 0) GlobalFree(dialog.DeviceMode);
            if (dialog.DeviceNames != 0) GlobalFree(dialog.DeviceNames);
        }
    }

    /// <summary>The Windows default printer, or null when none is installed.</summary>
    public static string? DefaultPrinterName()
    {
        var length = 0;
        GetDefaultPrinter(null, ref length);
        if (length <= 0) return null;
        var buffer = new char[length];
        return GetDefaultPrinter(buffer, ref length) ? new string(buffer, 0, Math.Max(0, length - 1)) : null;
    }

    /// <summary>Sends a PDF straight to the default printer, without a dialog. The user asked for this by choosing Print.</summary>
    public static async Task PrintPdfToDefaultAsync(string title, byte[] pdf, CancellationToken cancellationToken)
    {
        var printer = DefaultPrinterName() ?? throw new InvalidOperationException("No printer is installed. Add one in Windows Settings.");
        var dc = CreateDC("WINSPOOL", printer, null, 0);
        if (dc == 0) throw new InvalidOperationException($"Windows could not open the printer {printer}. Check that it is available.");
        try { await Task.Run(() => PrintPdfAsync(dc, title, pdf, cancellationToken, null), cancellationToken); }
        finally { if (DeleteDC(dc) == 0) System.Diagnostics.Trace.TraceWarning("Could not release printer device context."); }
    }

    private static async Task PrintPdfAsync(nint dc, string title, byte[] data, CancellationToken cancellationToken,
        Action<PrinterProgress>? progress)
    {
        var width = GetDeviceCaps(dc, 8);
        var height = GetDeviceCaps(dc, 10);
        var dpi = GetDeviceCaps(dc, 88);
        if (width <= 0 || height <= 0 || dpi <= 0)
            throw new InvalidOperationException("The printer reported invalid page dimensions.");
        using var pdf = await WindowsPdfPreview.LoadAsync(data);
        progress?.Invoke(new("Sending selected pages to Windows", CompletedPages: 0, TotalPages: pdf.PageCount));
        var started = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new DocumentInfo { Size = Marshal.SizeOf<DocumentInfo>(), Name = Marshal.StringToHGlobalUni(title) };
            try { Check(StartDoc(dc, ref info)); started = true; }
            finally { Marshal.FreeHGlobal(info.Name); }
            for (var page = 1; page <= pdf.PageCount; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var size = pdf.PageSize(page);
                var raster = await pdf.RenderPixelsAsync(page, checked((uint)Math.Ceiling(size.Width * Math.Min(dpi, 300) / 96.0)));
                cancellationToken.ThrowIfCancellationRequested();
                var scale = Math.Min((double)width / raster.Width, (double)height / raster.Height);
                var outputWidth = Math.Max(1, (int)Math.Floor(raster.Width * scale));
                var outputHeight = Math.Max(1, (int)Math.Floor(raster.Height * scale));
                var bitmap = new BitmapInfo
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfo>(), Width = raster.Width, Height = -raster.Height,
                    Planes = 1, BitCount = 32, ImageSize = checked((uint)raster.Pixels.Length),
                };
                Check(StartPage(dc));
                Check(SetStretchBltMode(dc, 4));
                Check(SetBrushOrgEx(dc, 0, 0, 0));
                var pixels = GCHandle.Alloc(raster.Pixels, GCHandleType.Pinned);
                try
                {
                    Check(StretchDIBits(dc, (width - outputWidth) / 2, (height - outputHeight) / 2, outputWidth, outputHeight,
                        0, 0, raster.Width, raster.Height, pixels.AddrOfPinnedObject(), ref bitmap, 0, 0x00CC0020));
                }
                finally { pixels.Free(); }
                Check(EndPage(dc));
                progress?.Invoke(new("Sending selected pages to Windows", CompletedPages: page, TotalPages: pdf.PageCount));
            }
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke(new("Finalizing the Windows printer handoff", CompletedPages: pdf.PageCount, TotalPages: pdf.PageCount));
            Check(EndDoc(dc));
            started = false;
        }
        finally
        {
            if (started && AbortDoc(dc) <= 0) System.Diagnostics.Trace.TraceWarning("Could not cancel the failed printer job.");
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
    private struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int PixelsPerMeterX, PixelsPerMeterY;
        public uint ColorsUsed, ColorsImportant;
    }

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial int SetStretchBltMode(nint dc, int mode);
    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial int SetBrushOrgEx(nint dc, int x, int y, nint previous);
    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial int StretchDIBits(nint dc, int x, int y, int width, int height,
        int sourceX, int sourceY, int sourceWidth, int sourceHeight, nint pixels, ref BitmapInfo info, uint usage, uint operation);

    [DllImport("winspool.drv", EntryPoint = "GetDefaultPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDefaultPrinter([Out] char[]? buffer, ref int length);
    [LibraryImport("gdi32.dll", EntryPoint = "CreateDCW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateDC(string driver, string device, string? port, nint mode);
    [LibraryImport("comdlg32.dll", EntryPoint = "PrintDlgExW")]
    private static partial int PrintDlgEx(ref PrintDialog dialog);
    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalFree(nint memory);
    [LibraryImport("gdi32.dll")]
    private static partial int DeleteDC(nint dc);
    [LibraryImport("gdi32.dll")]
    private static partial int GetDeviceCaps(nint dc, int index);
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
}
