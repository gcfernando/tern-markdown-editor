using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tern.Filesystem;

/// <summary>Kernel change time detects edits even when length and last-write time are restored.</summary>
public sealed record FileContentStamp(uint Volume, ulong FileId, long ChangeTime, long Length);

public static partial class FileIdentity
{
    public static FileContentStamp? TryGetContentStamp(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var handle = CreateFile(path, 0x80, FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, 0, 0); // FILE_READ_ATTRIBUTES
            if (handle.IsInvalid) return null;
            var identity = ReadIdentity(handle);
            if (!GetFileInformationByHandleEx(handle, 0, out var basic, (uint)Marshal.SizeOf<FileBasicInformation>()) || basic.ChangeTime == 0) return null;
            var length = ((ulong)identity.FileSizeHigh << 32) | identity.FileSizeLow;
            if (length > long.MaxValue) return null;
            return new(identity.VolumeSerialNumber, ((ulong)identity.FileIndexHigh << 32) | identity.FileIndexLow, basic.ChangeTime, (long)length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInformation
    {
        public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        public uint Attributes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out FileBasicInformation information, uint size);
}
