namespace Tern.Workspaces;

/// <summary>Path helpers shared by the workspace libraries: platform-correct comparison and workspace-relative forms.</summary>
public static class WorkspacePaths
{
    /// <summary>Windows and macOS file systems are case-insensitive by default; everything else is not.</summary>
    public static bool IsCaseInsensitive { get; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public static StringComparer Comparer { get; } = IsCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static StringComparison Comparison { get; } = IsCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Absolute path without a trailing separator (except for a filesystem root).</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        return full.Length > (root?.Length ?? 0) ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    /// <summary>True when <paramref name="path"/> equals <paramref name="directory"/> or lies beneath it.</summary>
    public static bool IsSameOrUnder(string directory, string path)
    {
        var dir = Normalize(directory);
        var candidate = Normalize(path);
        if (candidate.Equals(dir, Comparison))
        {
            return true;
        }

        return IsUnder(dir, candidate);
    }

    /// <summary>True when <paramref name="path"/> lies strictly beneath <paramref name="directory"/>.</summary>
    public static bool IsUnder(string directory, string path)
    {
        var dir = Normalize(directory);
        var candidate = Normalize(path);
        var prefix = dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;
        return candidate.Length > prefix.Length && candidate.StartsWith(prefix, Comparison);
    }

    /// <summary>The path relative to <paramref name="root"/>, using forward slashes. Empty for the root itself.</summary>
    public static string GetRelative(string root, string path)
    {
        var relative = Path.GetRelativePath(Normalize(root), Normalize(path));
        return relative == "." ? string.Empty : relative.Replace('\\', '/');
    }

    /// <summary>Relative path from a directory to a target, forward slashes, without percent-encoding.</summary>
    public static string GetRelativeFromDirectory(string directory, string target) =>
        Path.GetRelativePath(Normalize(directory), Normalize(target)).Replace('\\', '/');

    public static bool PathsEqual(string a, string b) => Normalize(a).Equals(Normalize(b), Comparison);

    /// <summary>
    /// Checks real locations immediately before reading a workspace file. File links and ancestor
    /// junctions are subject to the same root policy. This is not a filesystem handle sandbox;
    /// another process can still retarget a link after this check.
    /// </summary>
    public static bool IsPhysicallyContained(string root, string path)
    {
        try
        {
            var realRoot = Tern.Filesystem.FileIdentity.ResolvePhysicalPath(root);
            var realPath = Tern.Filesystem.FileIdentity.ResolvePhysicalPath(path);
            return IsSameOrUnder(realRoot, realPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Whether the file name carries one of the given extensions (each with a leading dot).</summary>
    public static bool HasExtension(string path, IReadOnlySet<string> extensions) =>
        extensions.Contains(Path.GetExtension(path));
}
