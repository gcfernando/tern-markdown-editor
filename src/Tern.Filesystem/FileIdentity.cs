using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tern.Filesystem;

/// <summary>Identity checks for operations that must not overwrite a source through an alias.</summary>
public static partial class FileIdentity
{
    public static bool AreSameFile(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Path.GetFullPath(first).Equals(Path.GetFullPath(second), comparison)) return true;
        if (!File.Exists(first) || !File.Exists(second)) return false;
        if (OperatingSystem.IsWindows())
        {
            using var firstHandle = OpenIdentityHandle(first);
            using var secondHandle = OpenIdentityHandle(second);
            var a = ReadIdentity(firstHandle);
            var b = ReadIdentity(secondHandle);
            return SameIdentity(a, b);
        }

        return ResolvePhysicalPath(first).Equals(ResolvePhysicalPath(second), comparison);
    }

    /// <summary>Finds an alias using fresh identities, reading the target once rather than reopening it for every candidate.</summary>
    public static Task<string?> FindSameFileAsync(IEnumerable<string> candidates, string target, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<string?>(cancellationToken);
        var fullTarget = Path.GetFullPath(target);
        var paths = candidates.Select(path => (Original: path, Full: Path.GetFullPath(path))).ToArray();
        if (paths.Length == 0) return Task.FromResult<string?>(null);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var direct = paths.FirstOrDefault(path => path.Full.Equals(fullTarget, comparison));
        if (direct.Original is not null) return Task.FromResult<string?>(direct.Original);
        return Task.Run(() => FindSameFileCoreAsync(paths, fullTarget, cancellationToken), cancellationToken);
    }

    private static async Task<string?> FindSameFileCoreAsync((string Original, string Full)[] paths, string fullTarget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (paths.Length == 0) return null;
        if (!OperatingSystem.IsWindows())
        {
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (AreSameFile(path.Full, fullTarget)) return path.Original;
            }
            return null;
        }
        var direct = paths.FirstOrDefault(path => path.Full.Equals(fullTarget, StringComparison.OrdinalIgnoreCase));
        if (direct.Original is not null) return direct.Original;
        if (!File.Exists(fullTarget)) return null;

        using var targetHandle = OpenIdentityHandle(fullTarget);
        var identity = ReadIdentity(targetHandle);
        var workers = Math.Min(4, paths.Length);
        var matches = await Task.WhenAll(Enumerable.Range(0, workers).Select(worker => Task.Run(() =>
        {
            for (var index = worker; index < paths.Length; index += workers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(paths[index].Full)) continue;
                using var candidateHandle = OpenIdentityHandle(paths[index].Full);
                if (SameIdentity(identity, ReadIdentity(candidateHandle))) return index;
            }
            return paths.Length;
        }, cancellationToken))).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        using var currentHandle = OpenIdentityHandle(fullTarget);
        if (!SameIdentity(identity, ReadIdentity(currentHandle)))
            throw new IOException("The file changed while checking its identity. Try opening it again.");
        var match = matches.Min();
        return match < paths.Length ? paths[match].Original : null;
    }

    /// <summary>
    /// Creation time (UTC ticks) of the file a path currently resolves to, or null when it does not exist or cannot be read.
    /// It survives moves, renames and Tern's replace-style saves, but differs for a file created later at the same path.
    /// </summary>
    public static long? TryGetCreationTicks(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return File.GetCreationTimeUtc(handle).Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
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
            // Querying LinkTarget opens a reparse-point handle on Windows. Ordinary
            // components need only the attribute check above, including during scans.
            if ((attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget is { } target)
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

    private static bool SameIdentity(ByHandleFileInformation first, ByHandleFileInformation second) =>
        first.VolumeSerialNumber == second.VolumeSerialNumber
        && first.FileIndexHigh == second.FileIndexHigh && first.FileIndexLow == second.FileIndexLow;

    private static SafeFileHandle OpenIdentityHandle(string path)
    {
        // Zero desired access queries identity without opening file content.
        var handle = CreateFile(path, 0, FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, 0, 0);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new IOException("Cannot check file identity: " + path, new Win32Exception(error));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFile(string path, uint desiredAccess, FileShare share,
        nint securityAttributes, FileMode creationDisposition, uint flags, nint template);

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
