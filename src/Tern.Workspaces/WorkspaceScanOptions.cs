namespace Tern.Workspaces;

/// <summary>Controls which files a workspace scan reports.</summary>
public sealed class WorkspaceScanOptions
{
    public static readonly IReadOnlyList<string> DefaultMarkdownExtensions = [".md", ".markdown", ".mdown", ".mkd"];

    public static readonly IReadOnlyList<string> DefaultExcludedDirectories = [".git", "node_modules", ".obsidian", "bin", "obj"];

    public const string IgnoreFileName = ".ternignore";

    /// <summary>Extensions (with dot) treated as documents. Compared case-insensitively.</summary>
    public IList<string> Extensions { get; init; } = [.. DefaultMarkdownExtensions];

    /// <summary>Also include <c>.txt</c> files.</summary>
    public bool IncludeTextFiles { get; init; }

    /// <summary>Directory names skipped at any depth.</summary>
    public IList<string> ExcludedDirectoryNames { get; init; } = [.. DefaultExcludedDirectories];

    /// <summary>Skip directories whose name starts with a dot, or that carry the Hidden attribute.</summary>
    public bool ExcludeHiddenDirectories { get; init; }

    /// <summary>Extra gitignore-style patterns, applied before <c>.ternignore</c>.</summary>
    public IList<string> ExcludePatterns { get; init; } = [];

    /// <summary>Read <c>.ternignore</c> from the workspace root.</summary>
    public bool UseIgnoreFile { get; init; } = true;

    /// <summary>Maximum directory depth below the root (0 = root only). Null means unlimited.</summary>
    public int? MaxDepth { get; init; }

    /// <summary>Files larger than this are skipped. Null disables the cap.</summary>
    public long? MaxFileSizeBytes { get; init; } = 16L * 1024 * 1024;

    /// <summary>
    /// Follow directory symlinks/junctions. Even when enabled, a link is only followed if its target lies inside
    /// the workspace root, is not an ancestor of the link and has not been visited already.
    /// </summary>
    public bool FollowDirectoryLinks { get; init; }

    /// <summary>Omit directories that contain no matching files from the tree.</summary>
    public bool PruneEmptyDirectories { get; init; } = true;

    internal HashSet<string> EffectiveExtensions()
    {
        var set = new HashSet<string>(Extensions, StringComparer.OrdinalIgnoreCase);
        if (IncludeTextFiles)
        {
            set.Add(".txt");
        }

        return set;
    }
}

/// <summary>A document found by a scan.</summary>
public sealed record WorkspaceFile(string FullPath, string RelativePath, long Length, DateTime LastWriteUtc, bool ViaLink = false);

/// <summary>A node of the explorer tree. Directories list their children with folders first, then files, each sorted by name.</summary>
public sealed class FileTreeNode
{
    private readonly List<FileTreeNode> _children = [];

    internal FileTreeNode(string name, string fullPath, string relativePath, bool isDirectory, long length, DateTime lastWriteUtc, bool isLink)
    {
        Name = name;
        FullPath = fullPath;
        RelativePath = relativePath;
        IsDirectory = isDirectory;
        Length = length;
        LastWriteUtc = lastWriteUtc;
        IsLink = isLink;
    }

    public string Name { get; }

    public string FullPath { get; }

    /// <summary>Forward-slash path relative to the workspace root; empty for the root node.</summary>
    public string RelativePath { get; }

    public bool IsDirectory { get; }

    public long Length { get; }

    public DateTime LastWriteUtc { get; }

    public bool IsLink { get; }
    public bool IsWorkspaceRoot { get; internal set; }

    public IReadOnlyList<FileTreeNode> Children => _children;

    /// <summary>Number of document files in this subtree.</summary>
    public int FileCount { get; internal set; }

    internal void Add(FileTreeNode child) => _children.Add(child);

    internal void Sort() =>
        _children.Sort((a, b) => a.IsDirectory != b.IsDirectory
            ? (a.IsDirectory ? -1 : 1)
            : NaturalComparer.Instance.Compare(a.Name, b.Name));

    internal void RemoveEmptyDirectories() => _children.RemoveAll(c => c.IsDirectory && c.FileCount == 0);
}

/// <summary>Result of scanning a workspace: the explorer tree, the flat file list and anything that could not be read.</summary>
public sealed class WorkspaceSnapshot
{
    public WorkspaceSnapshot(string root, FileTreeNode tree, IReadOnlyList<WorkspaceFile> files, IReadOnlyList<ScanWarning> warnings)
    {
        Root = root;
        Tree = tree;
        Files = files;
        Warnings = warnings;
    }

    public string Root { get; }

    public FileTreeNode Tree { get; }

    public IReadOnlyList<WorkspaceFile> Files { get; }

    public IReadOnlyList<ScanWarning> Warnings { get; }
}

public enum ScanWarningKind
{
    UnreadableDirectory,
    FileTooLarge,
    SkippedLink,
    InvalidWorkspaceDefinition,
}

public sealed record ScanWarning(ScanWarningKind Kind, string Path, string Message);

/// <summary>Orders names the way people expect ("note2" before "note10"), ignoring case.</summary>
internal sealed class NaturalComparer : IComparer<string>
{
    public static NaturalComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var si = i;
                var sj = j;
                while (i < x.Length && char.IsAsciiDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && char.IsAsciiDigit(y[j]))
                {
                    j++;
                }

                var a = x.AsSpan(si, i - si).TrimStart('0');
                var b = y.AsSpan(sj, j - sj).TrimStart('0');
                var byLength = a.Length.CompareTo(b.Length);
                if (byLength != 0)
                {
                    return byLength;
                }

                var byValue = a.CompareTo(b, StringComparison.Ordinal);
                if (byValue != 0)
                {
                    return byValue;
                }
            }
            else
            {
                var byChar = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (byChar != 0)
                {
                    return byChar;
                }

                i++;
                j++;
            }
        }

        var rest = (x.Length - i).CompareTo(y.Length - j);
        return rest != 0 ? rest : string.CompareOrdinal(x, y);
    }
}
