using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Tern.App.Shell;

namespace Tern.Desktop.Services;

/// <summary>Requires Windows recycling; failure never falls back to permanent deletion.</summary>
public sealed partial class WindowsRecycleBinService(nint ownerWindow = 0) : ILocalFileDeletion
{
    // RECYCLEONDELETE (Windows 8+) requires recycling, rather than ALLOWUNDO's best effort.
    // Tern asks for confirmation before this service is called. Suppress only the
    // duplicate Shell prompt; RECYCLEONDELETE still forbids permanent fallback.
    internal const uint OperationFlags = 0x00080000 | 0x00100000 | 0x0400 | 0x0004 | 0x20000000 | 0x0010;

    public Task MoveToRecycleBinAsync(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The Windows Recycle Bin is not available.");
        var fullPath = Path.GetFullPath(path);
        if ((File.GetAttributes(fullPath) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("Only a regular local file can be sent to the Recycle Bin.");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The Windows Recycle Bin is not available.");
                Recycle(fullPath, ownerWindow);
                completion.SetResult();
            }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true, Name = "Tern Recycle Bin" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return completion.Task;
    }

    internal static void CheckResult(int hresult, bool aborted = false)
    {
        if (hresult < 0) throw new IOException("Windows could not move the file to the Recycle Bin. The operation was not completed.", Marshal.GetExceptionForHR(hresult));
        if (aborted) throw new IOException("Moving the file to the Recycle Bin was canceled or stopped.");
    }

    [SupportedOSPlatform("windows")]
    private static unsafe void Recycle(string path, nint ownerWindow)
    {
        CheckResult(CoInitializeEx(0, 2)); // COINIT_APARTMENTTHREADED
        nint operation = 0, item = 0;
        try
        {
            var clsid = new Guid("3ad05575-8857-4850-9277-11b85bdb8e09");
            var operationId = new Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8");
            var itemId = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
            CheckResult(CoCreateInstance(in clsid, 0, 1, in operationId, out operation));
            CheckResult(SHCreateItemFromParsingName(path, 0, in itemId, out item));
            // IFileOperation inherits IUnknown. These slots follow shobjidl_core.h:
            // SetOperationFlags=5, DeleteItem=18, PerformOperations=21, GetAnyOperationsAborted=22.
            var table = *(nint**)operation;
            CheckResult(((delegate* unmanaged[Stdcall]<nint, uint, int>)table[5])(operation, OperationFlags));
            if (ownerWindow != 0) CheckResult(((delegate* unmanaged[Stdcall]<nint, nint, int>)table[9])(operation, ownerWindow));
            CheckResult(((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)table[18])(operation, item, 0));
            var result = ((delegate* unmanaged[Stdcall]<nint, int>)table[21])(operation);
            var aborted = 0;
            var abortResult = ((delegate* unmanaged[Stdcall]<nint, int*, int>)table[22])(operation, &aborted);
            CheckResult(result);
            CheckResult(abortResult, aborted != 0);
        }
        finally
        {
            Release(item);
            Release(operation);
            CoUninitialize();
        }
    }

    private static unsafe void Release(nint instance)
    {
        if (instance != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)instance)[2])(instance);
    }

    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(nint reserved, uint flags);
    [LibraryImport("ole32.dll")] private static partial void CoUninitialize();
    [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint result);
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string path, nint bindContext, in Guid iid, out nint item);
}
