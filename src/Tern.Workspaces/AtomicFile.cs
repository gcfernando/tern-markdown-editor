namespace Tern.Workspaces;

/// <summary>Writes a file so that readers (and a crash) only ever see the old or the new content, never a mixture.</summary>
public static class AtomicFile
{
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);

        var temp = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                for (var attempt = 0; ; attempt++)
                {
                    try { File.Replace(temp, fullPath, null, ignoreMetadataErrors: true); break; }
                    catch (IOException ex) when (OperatingSystem.IsWindows() && attempt < 6
                        && (ex.HResult & 0xffff) is 32 or 33 or 1175 && File.Exists(temp))
                    {
                        Thread.Sleep(25 * (attempt + 1));
                    }
                }
            }
            else
            {
                File.Move(temp, fullPath);
            }
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover temp file is harmless; it is never read back.
            }
        }
    }

    /// <summary>Moves an unreadable file aside (keeping its bytes for diagnosis) so a fresh one can be written. Returns the new name, or null if it could not be moved.</summary>
    public static string? Quarantine(string path, string suffix)
    {
        try
        {
            var target = path + "." + suffix;
            if (File.Exists(target))
            {
                target = $"{path}.{suffix}.{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            }

            File.Move(path, target);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
