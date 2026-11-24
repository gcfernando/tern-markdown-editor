namespace Tern.Desktop.Services;

internal static class PrintPreviewFiles
{
    internal static string DirectoryPath => Path.Combine(Path.GetTempPath(), "Tern", "PrintPreview");

    internal static IReadOnlyList<string> Delete(IEnumerable<string> paths)
    {
        var failures = new List<string>();
        foreach (var path in paths)
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { failures.Add(path + ": " + ex.Message); }
        }
        return failures;
    }

    internal static IReadOnlyList<string> CleanupOld(string? directory = null, DateTime? utcNow = null)
    {
        directory ??= DirectoryPath;
        if (!Directory.Exists(directory)) return [];
        try
        {
            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                return ["Print preview cleanup refused a linked temporary folder: " + directory];
            var cutoff = (utcNow ?? DateTime.UtcNow).AddDays(-1);
            var paths = Directory.EnumerateFiles(directory, "*.pdf", SearchOption.TopDirectoryOnly)
                .Where(path => Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _)
                    && !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)
                    && File.GetLastWriteTimeUtc(path) < cutoff).ToArray();
            return Delete(paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return ["Print preview cleanup could not inspect " + directory + ": " + ex.Message]; }
    }
}
