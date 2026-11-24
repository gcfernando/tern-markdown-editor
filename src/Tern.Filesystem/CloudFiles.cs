using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tern.Filesystem;

/// <summary>Metadata-only cloud checks; background readers must not hydrate online-only content.</summary>
public static class CloudFiles
{
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    public static bool IsOnlineOnly(FileAttributes attributes) =>
        (attributes & (FileAttributes.Offline | RecallOnDataAccess)) != 0;

    public static bool IsOnlineOnly(string path) => IsOnlineOnly(File.GetAttributes(path));

    public static bool IsUnsafeReparsePoint(FileSystemInfo entry) =>
        IsUnsafeReparsePoint(entry.Attributes, () => ReadReparseTag(entry.FullName));

    internal static bool IsUnsafeReparsePoint(FileAttributes attributes, Func<uint> readTag) =>
        (attributes & FileAttributes.ReparsePoint) != 0 && !IsCloudTag(readTag());

    internal static bool IsCloudTag(uint tag) => (tag & 0xFFFF0FFFu) == 0x9000001Au;

    private static uint ReadReparseTag(string path)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        using var handle = FindFirstFile(path, out var information);
        if (handle.IsInvalid)
            throw new IOException("The attachment's reparse-point metadata could not be verified.", new Win32Exception(Marshal.GetLastWin32Error()));
        return information.ReparseTag;
    }

    [DllImport("kernel32.dll", EntryPoint = "FindFirstFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFindHandle FindFirstFile(string path, out FindData information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);

    private sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeFindHandle() : base(true) { }
        protected override bool ReleaseHandle() => FindClose(handle);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FindData
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint SizeHigh;
        public uint SizeLow;
        public uint ReparseTag;
        public uint Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string AlternateName;
    }
}
