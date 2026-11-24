namespace Tern.Workspaces;

/// <summary>
/// Decides which paths belong to a workspace. Shared by the scanner and the watcher so that both agree
/// on what is a document and what is excluded.
/// </summary>
public sealed class WorkspaceFilter
{
    private readonly HashSet<string> _extensions;
    private readonly HashSet<string> _excludedNames;
    private readonly bool _excludeHidden;
    private readonly IgnoreRules _rules;

    public WorkspaceFilter(string root, WorkspaceScanOptions? options = null, bool requireReadableIgnoreFile = false)
    {
        Options = options ?? new WorkspaceScanOptions();
        Root = WorkspacePaths.Normalize(root);
        _extensions = Options.EffectiveExtensions();
        _excludedNames = new HashSet<string>(Options.ExcludedDirectoryNames, WorkspacePaths.Comparer);
        _excludeHidden = Options.ExcludeHiddenDirectories;

        _rules = IgnoreRules.FromPatterns(Options.ExcludePatterns, WorkspacePaths.IsCaseInsensitive);
        if (Options.UseIgnoreFile)
        {
            _rules.AddRange(LoadIgnoreFile(Root, out var onlineOnly, requireReadableIgnoreFile));
            IgnoreFileIsOnlineOnly = onlineOnly;
        }
    }

    public string Root { get; }

    public WorkspaceScanOptions Options { get; }
    public bool IgnoreFileIsOnlineOnly { get; }

    public IReadOnlySet<string> Extensions => _extensions;

    public bool IsDocument(string path) => _extensions.Contains(Path.GetExtension(path));

    /// <summary>Whether a directory (given by name and root-relative path) should be entered.</summary>
    public bool IsDirectoryExcluded(string name, string relativePath, FileAttributes attributes)
    {
        if (_excludedNames.Contains(name))
        {
            return true;
        }

        if (_excludeHidden && (name.StartsWith('.') || (attributes & FileAttributes.Hidden) != 0))
        {
            return true;
        }

        return _rules.IsIgnored(relativePath, isDirectory: true);
    }

    public bool IsFileExcluded(string relativePath) => _rules.IsIgnored(relativePath, isDirectory: false);

    /// <summary>
    /// Whether a change at <paramref name="fullPath"/> could affect the workspace: it is inside the root, no ancestor
    /// directory is excluded, and it is either a document or something extensionless that might be a directory.
    /// </summary>
    public bool IsRelevantChange(string fullPath)
    {
        var path = WorkspacePaths.Normalize(fullPath);
        if (!WorkspacePaths.IsUnder(Root, path))
        {
            return false;
        }

        var relative = WorkspacePaths.GetRelative(Root, path);
        var segments = relative.Split('/');
        var walked = string.Empty;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            walked = walked.Length == 0 ? segments[i] : walked + "/" + segments[i];
            if (IsDirectoryExcluded(segments[i], walked, 0))
            {
                return false;
            }
        }

        var last = segments[^1];
        if (IsDocument(last))
        {
            return !IsFileExcluded(relative);
        }

        // Directory events carry no extension we can trust ("v1.2" is a perfectly good folder name).
        return !last.StartsWith(".tern-", StringComparison.Ordinal) && !last.EndsWith(".tern-tmp", StringComparison.Ordinal)
            && !IsDirectoryExcluded(last, relative, 0);
    }

    private static IgnoreRules LoadIgnoreFile(string root, out bool onlineOnly, bool required)
    {
        onlineOnly = false;
        var path = Path.Combine(root, WorkspaceScanOptions.IgnoreFileName);
        try
        {
            if (required)
            {
                try { _ = File.GetAttributes(path); }
                catch (FileNotFoundException) { return IgnoreRules.Empty; }
                if (!WorkspacePaths.IsPhysicallyContained(root, path) || Tern.Filesystem.CloudFiles.IsOnlineOnly(path))
                    throw new IOException("Workspace exclusions are unavailable or outside the workspace. Make .ternignore available locally.");
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > 1_048_576) throw new IOException("Workspace exclusions exceed the assistant's bounded reading limit.");
                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
                return IgnoreRules.Parse(Tern.Filesystem.TextFileCodec.Decode(bytes).Text, WorkspacePaths.IsCaseInsensitive);
            }
            if (!File.Exists(path)) return IgnoreRules.Empty;
            onlineOnly = Tern.Filesystem.CloudFiles.IsOnlineOnly(path);
            return onlineOnly ? IgnoreRules.Empty : IgnoreRules.Parse(File.ReadAllText(path), WorkspacePaths.IsCaseInsensitive);
        }
        catch (IOException) when (!required)
        {
            return IgnoreRules.Empty;
        }
        catch (UnauthorizedAccessException) when (!required)
        {
            return IgnoreRules.Empty;
        }
    }
}
