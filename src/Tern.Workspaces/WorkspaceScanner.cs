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

    /// <summary>Refreshes files in existing directories; null means a full scan is required to establish current topology or admission.</summary>
    public Task<WorkspaceSnapshot?> TryRefreshFilesAsync(WorkspaceSnapshot snapshot, WorkspaceChangeSet changes,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (changes.RescanRequired || changes.Changes.Any(change => change.IsDirectory
            || change.Kind is not (WorkspaceChangeKind.Changed or WorkspaceChangeKind.Created))) return null;
        var run = new ScanRun(new WorkspaceFilter(snapshot.Root, _options), _options, null, null, cancellationToken);
        return run.TryRefreshFiles(snapshot, changes);
    }, cancellationToken);

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
        private bool _walkingFromRoot;

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
            if (_filter.IgnoreFileIsOnlineOnly)
                _warnings.Add(new ScanWarning(ScanWarningKind.OnlineOnly, Path.Combine(_filter.Root, WorkspaceScanOptions.IgnoreFileName),
                    "The online-only .ternignore was not downloaded. Make it available locally and rescan to apply its rules."));
            var root = new DirectoryInfo(_filter.Root);
            var rootNode = new FileTreeNode(root.Name, _filter.Root, string.Empty, true, 0, root.LastWriteTimeUtc, false);
            _visited.Add(_filter.Root);
            _walkingFromRoot = true;
            ScanDirectory(root, rootNode, 0, viaLink: false);

            // Links are resolved last so that a target which is already part of the walk is never listed twice.
            foreach (var (link, node, depth) in _deferredLinks)
            {
                FollowLink(link, node, depth);
            }

            Finalize(rootNode);
            return new WorkspaceSnapshot(_filter.Root, rootNode, _files, _warnings);
        }

        public WorkspaceSnapshot? TryRefreshFiles(WorkspaceSnapshot snapshot, WorkspaceChangeSet changes)
        {
            var files = snapshot.Files.ToDictionary(file => file.FullPath, WorkspacePaths.Comparer);
            var tree = snapshot.Tree;
            foreach (var change in changes.Changes)
            {
                _ct.ThrowIfCancellationRequested();
                var path = WorkspacePaths.Normalize(change.Path);
                if (!_filter.IsRelevantChange(path) || snapshot.Warnings.Any(warning =>
                    WorkspacePaths.PathsEqual(warning.Path, path))) return null;
                var directory = Path.GetDirectoryName(path);
                var parent = directory is null ? null : FindDirectory(tree, directory);
                if (parent is null) return null;
                var info = new FileInfo(path);
                if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0) return null;
                var previous = files.GetValueOrDefault(path);
                var temporary = new FileTreeNode(parent.Name, parent.FullPath, parent.RelativePath,
                    true, parent.Length, parent.LastWriteUtc, parent.IsLink);
                var count = _files.Count;
                HandleFile(info, temporary, WorkspacePaths.GetRelative(_filter.Root, path),
                    previous?.ViaLink == true || HasLinkedAncestor(tree, directory!));
                if (_files.Count != count + 1) return null;
                var added = _files[^1];
                if (previous is not null && (previous.IsOnlineOnly != added.IsOnlineOnly || previous.ViaLink != added.ViaLink)) return null;
                files[added.FullPath] = added;
                if (previous is null) tree = WithFile(tree, parent.FullPath, temporary.Children[0]);
            }
            return new WorkspaceSnapshot(snapshot.Root, tree, files.Values.ToArray(), snapshot.Warnings);
        }

        private static FileTreeNode? FindDirectory(FileTreeNode node, string path)
        {
            if (WorkspacePaths.PathsEqual(node.FullPath, path)) return node;
            foreach (var child in node.Children.Where(child => child.IsDirectory && WorkspacePaths.IsSameOrUnder(child.FullPath, path)))
                if (FindDirectory(child, path) is { } found) return found;
            return null;
        }

        private static bool HasLinkedAncestor(FileTreeNode node, string directory)
        {
            if (node.IsLink) return true;
            return node.Children.Any(child => child.IsDirectory && WorkspacePaths.IsSameOrUnder(child.FullPath, directory)
                && HasLinkedAncestor(child, directory));
        }

        private static FileTreeNode WithFile(FileTreeNode node, string directory, FileTreeNode file)
        {
            var atParent = WorkspacePaths.PathsEqual(node.FullPath, directory);
            var copy = new FileTreeNode(node.Name, node.FullPath, node.RelativePath, node.IsDirectory,
                node.Length, node.LastWriteUtc, node.IsLink)
            { IsOnlineOnly = node.IsOnlineOnly, IsWorkspaceRoot = node.IsWorkspaceRoot };
            foreach (var child in node.Children)
            {
                if (atParent && WorkspacePaths.PathsEqual(child.FullPath, file.FullPath)) continue;
                copy.Add(!atParent && child.IsDirectory && WorkspacePaths.IsSameOrUnder(child.FullPath, directory)
                    ? WithFile(child, directory, file) : child);
            }
            if (atParent) copy.Add(file);
            copy.FileCount = copy.Children.Sum(child => child.IsDirectory ? child.FileCount : 1);
            copy.Sort();
            return copy;
        }

        private void ScanDirectory(DirectoryInfo directory, FileTreeNode node, int depth, bool viaLink)
        {
            _ct.ThrowIfCancellationRequested();
            try
            {
                var descriptor = Path.Combine(directory.FullName, WorkspaceDefinition.FileName);
                if (File.Exists(descriptor) && Tern.Filesystem.CloudFiles.IsOnlineOnly(descriptor))
                {
                    node.IsWorkspaceRoot = true;
                    _warnings.Add(new ScanWarning(ScanWarningKind.OnlineOnly, descriptor,
                        "The online-only workspace definition was not downloaded. Make it available locally to read its saved name."));
                }
                else node.IsWorkspaceRoot = WorkspaceDefinition.Load(directory.FullName) is not null;
            }
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
            // A walk from the root never enters a link unmarked, so only linked entries need the per-component check.
            var ordinary = _walkingFromRoot && !viaLink && (file.Attributes & FileAttributes.ReparsePoint) == 0;
            if (!ordinary && !WorkspacePaths.IsPhysicallyContained(_filter.Root, fullPath))
            {
                _warnings.Add(new ScanWarning(ScanWarningKind.SkippedLink, fullPath, "File link leaves the workspace or cannot be safely resolved."));
                return;
            }
            var onlineOnly = Tern.Filesystem.CloudFiles.IsOnlineOnly(file.Attributes);
            if (!onlineOnly && _options.MaxFileSizeBytes is { } cap && length > cap)
            {
                _warnings.Add(new ScanWarning(ScanWarningKind.FileTooLarge, fullPath, $"File is {length:N0} bytes; the limit is {cap:N0}."));
                return;
            }

            var record = new WorkspaceFile(fullPath, relative, length, written,
                viaLink || (file.Attributes & FileAttributes.ReparsePoint) != 0 && file.LinkTarget is not null,
                onlineOnly);
            parent.Add(new FileTreeNode(file.Name, fullPath, relative, false, length, written, record.ViaLink) { IsOnlineOnly = record.IsOnlineOnly });
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
