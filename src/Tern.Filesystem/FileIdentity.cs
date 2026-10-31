using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tern.Filesystem;

/// <summary>Identity checks for operations that must not overwrite a source through an alias.</summary>
public static class FileIdentity
{
    public static bool AreSameFile(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Path.GetFullPath(first).Equals(Path.GetFullPath(second), comparison)) return true;
        if (!File.Exists(first) || !File.Exists(second)) return false;
        if (OperatingSystem.IsWindows())
        {
            using var firstHandle = File.OpenHandle(first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var secondHandle = File.OpenHandle(second, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var a = ReadIdentity(firstHandle);
            var b = ReadIdentity(secondHandle);
            return a.VolumeSerialNumber == b.VolumeSerialNumber && a.FileIndexHigh == b.FileIndexHigh && a.FileIndexLow == b.FileIndexLow;
        }

        return ResolvePhysicalPath(first).Equals(ResolvePhysicalPath(second), comparison);
    }

    /// <summary>Resolves file symlinks and ancestor directory links. Throws on unresolved cycles.</summary>
    public static string ResolvePhysicalPath(string path) => ResolvePhysicalPath(Path.GetFullPath(path), 0);

    private static string ResolvePhysicalPath(string path, int links)
    {
        if (links > 32) throw new IOException("Too many file links.");
        var root = Path.GetPathRoot(path)!;
        var current = root;
        foreach (var part in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // New save/export destinations may have a suffix that does not exist yet.
                continue;
            }
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is { } target)
            {
                var fullTarget = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(Path.GetDirectoryName(current)!, target));
                if (OperatingSystem.IsWindows() && fullTarget.StartsWith(@"\\", StringComparison.Ordinal))
                    throw new IOException("Automatic access through a link to a network share is not allowed.");
                current = ResolvePhysicalPath(fullTarget, links + 1);
            }
        }

        return current;
    }

    private static ByHandleFileInformation ReadIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var result)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return result;
    }

    // This fixed, system-only signature has no string marshalling or unmanaged allocation.
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
