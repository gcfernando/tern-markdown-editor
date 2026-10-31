using System.Threading.Channels;

namespace Tern.Workspaces;

/// <summary>Enumerates the documents of a workspace folder, honouring exclusion rules, and builds the explorer tree.</summary>
public sealed class WorkspaceScanner
{
    private static readonly EnumerationOptions ListingOptions = new()
    {
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private readonly WorkspaceScanOptions _options;

    public WorkspaceScanner(WorkspaceScanOptions? options = null)
    {
        _options = options ?? new WorkspaceScanOptions();
    }

    /// <summary>Scans the whole workspace. Unreadable directories are reported as warnings, never as failures.</summary>
    /// <exception cref="DirectoryNotFoundException">The root does not exist.</exception>
    /// <exception cref="OperationCanceledException">The scan was cancelled.</exception>
    public Task<WorkspaceSnapshot> ScanAsync(string root, IProgress<int>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(root, null, progress, cancellationToken), cancellationToken);

    /// <summary>Streams documents as they are discovered, so large workspaces can start indexing immediately.</summary>
    public async IAsyncEnumerable<WorkspaceFile> EnumerateFilesAsync(
        string root,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<WorkspaceFile>(new BoundedChannelOptions(512) { SingleReader = true, SingleWriter = true });
        var producer = Task.Run(
            () =>
            {
                try
                {
                    Scan(root, file => channel.Writer.WriteAsync(file, cancellationToken).AsTask().GetAwaiter().GetResult(), null, cancellationToken);
                    channel.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    channel.Writer.TryComplete(ex);
                }
            },
            CancellationToken.None);

        await foreach (var file in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return file;
        }

        await producer.ConfigureAwait(false);
    }

    private WorkspaceSnapshot Scan(string root, Action<WorkspaceFile>? onFile, IProgress<int>? progress, CancellationToken ct)
    {
        var fullRoot = WorkspacePaths.Normalize(root);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"Workspace folder not found: {fullRoot}");
        }

        var run = new ScanRun(new WorkspaceFilter(fullRoot, _options), _options, onFile, progress, ct);
        return run.Execute();
    }

    private sealed class ScanRun
    {
        private readonly WorkspaceFilter _filter;
        private readonly WorkspaceScanOptions _options;
        private readonly Action<WorkspaceFile>? _onFile;
        private readonly IProgress<int>? _progress;
        private readonly CancellationToken _ct;
        private readonly List<WorkspaceFile> _files = [];
        private readonly List<ScanWarning> _warnings = [];
        private readonly List<(DirectoryInfo Link, FileTreeNode Node, int Depth)> _deferredLinks = [];
        private readonly HashSet<string> _visited = new(WorkspacePaths.Comparer);

        public ScanRun(WorkspaceFilter filter, WorkspaceScanOptions options, Action<WorkspaceFile>? onFile, IProgress<int>? progress, CancellationToken ct)
        {
            _filter = filter;
            _options = options;
            _onFile = onFile;
            _progress = progress;
            _ct = ct;
        }

        public WorkspaceSnapshot Execute()
        {
            var root = new DirectoryInfo(_filter.Root);
            var rootNode = new FileTreeNode(root.Name, _filter.Root, string.Empty, true, 0, root.LastWriteTimeUtc, false);
            _visited.Add(_filter.Root);
            ScanDirectory(root, rootNode, 0, viaLink: false);

            // Links are resolved last so that a target which is already part of the walk is never listed twice.
            foreach (var (link, node, depth) in _deferredLinks)
            {
                FollowLink(link, node, depth);
            }

            Finalize(rootNode);
            return new WorkspaceSnapshot(_filter.Root, rootNode, _files, _warnings);
        }

        private void ScanDirectory(DirectoryInfo directory, FileTreeNode node, int depth, bool viaLink)
        {
            _ct.ThrowIfCancellationRequested();
            try { node.IsWorkspaceRoot = WorkspaceDefinition.Load(directory.FullName) is not null; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                _warnings.Add(new ScanWarning(ScanWarningKind.InvalidWorkspaceDefinition, directory.FullName, "Workspace identity could not be read: " + ex.Message));
            }

            List<FileSystemInfo> entries;
            try
            {
                entries = [.. directory.EnumerateFileSystemInfos("*", ListingOptions)];
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                _warnings.Add(new ScanWarning(ScanWarningKind.UnreadableDirectory, directory.FullName, ex.Message));
                return;
            }

            foreach (var entry in entries)
            {
                _ct.ThrowIfCancellationRequested();
                var relative = WorkspacePaths.GetRelative(_filter.Root, Path.Combine(node.FullPath, entry.Name));

                if (entry is DirectoryInfo child)
                {
                    HandleDirectory(child, node, relative, depth);
                }
                else if (entry is FileInfo file)
                {
                    HandleFile(file, node, relative, viaLink);
                }
            }
        }

        private void HandleDirectory(DirectoryInfo child, FileTreeNode parent, string relative, int depth)
        {
            if (_filter.IsDirectoryExcluded(child.Name, relative, child.Attributes))
            {
                return;
            }

            if (_options.MaxDepth is { } max && depth + 1 > max)
            {
                return;
            }

            var isLink = child.LinkTarget is not null;
            var node = new FileTreeNode(child.Name, Path.Combine(parent.FullPath, child.Name), relative, true, 0, SafeWriteTime(child), isLink);
            if (isLink)
            {
                if (_options.FollowDirectoryLinks)
                {
                    parent.Add(node);
                    _deferredLinks.Add((child, node, depth + 1));
                }
                else
                {
                    _warnings.Add(new ScanWarning(ScanWarningKind.SkippedLink, node.FullPath, "Directory link not followed."));
                }

                return;
            }

            parent.Add(node);
            _visited.Add(node.FullPath);
            ScanDirectory(child, node, depth + 1, viaLink: false);
        }

        private void HandleFile(FileInfo file, FileTreeNode parent, string relative, bool viaLink)
        {
            if (!_filter.IsDocument(file.Name) || _filter.IsFileExcluded(relative))
            {
                return;
            }

            long length;
            DateTime written;
            try
            {
                // A dangling file link throws here; such an entry is not a readable document.
                length = file.Length;
                written = file.LastWriteTimeUtc;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            var fullPath = Path.Combine(parent.FullPath, file.Name);
            if (!WorkspacePaths.IsPhysicallyContained(_filter.Root, fullPath))
            {
                _warnings.Add(new ScanWarning(ScanWarningKind.SkippedLink, fullPath, "File link leaves the workspace or cannot be safely resolved."));
                return;
            }
            if (_options.MaxFileSizeBytes is { } cap && length > cap)
            {
                _warnings.Add(new ScanWarning(ScanWarningKind.FileTooLarge, fullPath, $"File is {length:N0} bytes; the limit is {cap:N0}."));
                return;
            }

            var record = new WorkspaceFile(fullPath, relative, length, written, viaLink || file.LinkTarget is not null);
            parent.Add(new FileTreeNode(file.Name, fullPath, relative, false, length, written, record.ViaLink));
            _files.Add(record);
            _onFile?.Invoke(record);
            if (_files.Count % 250 == 0)
            {
                _progress?.Report(_files.Count);
            }
        }

        private void FollowLink(DirectoryInfo link, FileTreeNode node, int depth)
        {
            FileSystemInfo? target;
            try
            {
                target = link.ResolveLinkTarget(returnFinalTarget: true);
            }
            catch (IOException)
            {
                target = null;
            }

            if (target is null || !target.Exists)
            {
                _warnings.Add(new ScanWarning(ScanWarningKind.SkippedLink, node.FullPath, "Link target does not exist."));
                return;
            }

            var resolved = WorkspacePaths.Normalize(target.FullName);
            var linkPath = WorkspacePaths.Normalize(node.FullPath);
            if (!WorkspacePaths.IsUnder(_filter.Root, resolved))
            {
                _warnings.Add(new ScanWarning(ScanWarningKind.SkippedLink, node.FullPath, "Link points outside the workspace."));
                return;
            }

            if (WorkspacePaths.IsSameOrUnder(resolved, linkPath) || !_visited.Add(resolved))
            {
                _warnings.Add(new ScanWarning(ScanWarningKind.SkippedLink, node.FullPath, "Link loops back or duplicates a folder that is already listed."));
                return;
            }

            ScanDirectory(new DirectoryInfo(resolved), node, depth, viaLink: true);
        }

        private void Finalize(FileTreeNode node)
        {
            var count = 0;
            foreach (var child in node.Children)
            {
                if (child.IsDirectory)
                {
                    Finalize(child);
                }

                count += child.IsDirectory ? child.FileCount : 1;
            }

            node.FileCount = count;
            if (_options.PruneEmptyDirectories)
            {
                node.RemoveEmptyDirectories();
            }

            node.Sort();
        }

        private static DateTime SafeWriteTime(FileSystemInfo info)
        {
            try
            {
                return info.LastWriteTimeUtc;
            }
            catch (IOException)
            {
                return DateTime.MinValue;
            }
        }
    }
}
