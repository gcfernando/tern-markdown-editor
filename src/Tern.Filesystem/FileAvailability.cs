namespace Tern.Filesystem;

/// <summary>Why a file cannot be read: it is really gone, or its drive or share cannot be reached right now.</summary>
public enum FileAvailability
{
    Present,

    /// <summary>The volume answers but the file (or its folder) does not exist.</summary>
    Missing,

    /// <summary>The drive, share or device holding the file does not answer; the file may well still exist.</summary>
    Unreachable,
}

public static class FileAvailabilityProbe
{
    /// <summary>Classifies <paramref name="path"/>. A volume or share root that does not answer is Unreachable, never Missing (S-01).</summary>
    public static FileAvailability Probe(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full)) return FileAvailability.Present;
            var directory = Path.GetDirectoryName(full);
            if (directory is null || Directory.Exists(directory)) return FileAvailability.Missing;
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return FileAvailability.Missing;
            return RootAnswers(root) ? FileAvailability.Missing : FileAvailability.Unreachable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return FileAvailability.Unreachable;
        }
    }

    private static bool RootAnswers(string root)
    {
        if (OperatingSystem.IsWindows() && !root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            try { return new DriveInfo(root).IsReady; }
            catch (ArgumentException) { return false; }
        }

        return Directory.Exists(root);
    }
}
