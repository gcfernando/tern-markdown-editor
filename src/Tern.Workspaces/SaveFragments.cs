using System.Text.RegularExpressions;

namespace Tern.Workspaces;

public enum SaveFragmentKind { SaveBackup, Temporary }

/// <summary>A leftover file from an interrupted save: a preserved competing version (backup) or a half-written copy (temporary).</summary>
public sealed record SaveFragment(string Path, SaveFragmentKind Kind, string OriginalPath, DateTime LastWriteUtc, long Length);

public sealed record SaveFragmentScan(IReadOnlyList<SaveFragment> Fragments, int DeletedTemporaryFiles);

/// <summary>Finds Tern's save leftovers (<c>.name.&lt;guid&gt;.tern-tmp|tern-save-backup|tmp</c>) older than a day (R-06).</summary>
public static partial class SaveFragmentScanner
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(1);
    private const int MaxDirectories = 20000;

    [GeneratedRegex(@"^\.(?<name>.+)\.[0-9a-fA-F]{32}\.(?<ext>tern-tmp|tern-save-backup|tmp)$", RegexOptions.CultureInvariant)]
    private static partial Regex FragmentName();

    /// <summary>Lists stale fragments. Stale temporary files are deleted when asked, since only a finished or abandoned save leaves one; backups are never deleted here.</summary>
    public static SaveFragmentScan Scan(string root, DateTime nowUtc, bool deleteStaleTemporaries, CancellationToken cancellationToken = default)
    {
        var found = new List<SaveFragment>();
        var deleted = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        var visited = 0;
        while (pending.Count > 0 && visited++ < MaxDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(directory).EnumerateFileSystemInfos().ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (entry is DirectoryInfo)
                {
                    if (entry.Name is not (".git" or "node_modules")) pending.Push(entry.FullName);
                    continue;
                }

                var match = FragmentName().Match(entry.Name);
                if (!match.Success || nowUtc - entry.LastWriteTimeUtc < StaleAfter) continue;
                var kind = match.Groups["ext"].Value == "tern-save-backup" ? SaveFragmentKind.SaveBackup : SaveFragmentKind.Temporary;
                if (kind == SaveFragmentKind.Temporary && deleteStaleTemporaries)
                {
                    try { entry.Delete(); deleted++; continue; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }

                var length = entry is FileInfo file ? file.Length : 0;
                found.Add(new SaveFragment(entry.FullName, kind, Path.Combine(directory, match.Groups["name"].Value), entry.LastWriteTimeUtc, length));
            }
        }

        return new SaveFragmentScan([.. found.OrderBy(f => f.OriginalPath, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.LastWriteUtc)], deleted);
    }
}
