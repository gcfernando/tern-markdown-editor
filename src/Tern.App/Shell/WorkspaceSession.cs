using Tern.Markdown;
using Tern.Search;
using Tern.Workspaces;

namespace Tern.App.Shell;

/// <summary>
/// An open folder: its file tree, link graph, full-text index and change watcher, kept consistent with each other.
/// Unsaved editor text is layered over disk content so search and link checks reflect what the user sees.
/// </summary>
public sealed class WorkspaceSession : IDisposable
{
    private readonly WorkspaceScanner _scanner;
    private readonly OverlayTextProvider _overlayText;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private SearchIndex? _index;
    private WorkspaceIndexer? _indexer;
    private WorkspaceWatcher? _watcher;
    private SearchService? _search;
    private bool _disposed;
    private readonly Lock _pendingGate = new();
    private readonly ChangeCoalescer _pendingChanges = new();
    private bool _refreshRunning;
    private bool _initializing = true;
    private readonly Dictionary<string, (string? Text, long Version)> _pendingText = new(WorkspacePaths.Comparer);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _textVersions = new(WorkspacePaths.Comparer);
    private bool _graphRunning;
    private Task _graphWork = Task.CompletedTask;
    private long _textVersion;
    private long _lastTextChange;

    public event EventHandler? GraphChanged;
    internal Task GraphIdle => _graphWork;

    private WorkspaceSession(string root, WorkspaceScanOptions options, MarkdownProfile profile)
    {
        Root = root;
        Options = options;
        _overlayText = new OverlayTextProvider(new DiskDocumentTextProvider(options.MaxFileSizeBytes ?? DocumentFileServiceLimit));
        Profile = profile;
        _scanner = new WorkspaceScanner(options);
        Graph = new LinkGraph(root, new LinkGraphOptions { Profile = profile });
        FileOperations = new WorkspaceFileOperations(root);
    }

    public string Root { get; }

    /// <summary>False for a browsed physical folder; true for a named workspace or a restored legacy workspace.</summary>
    public bool IsNamedWorkspace { get; internal set; } = true;
    public string Name { get; internal set; } = string.Empty;
    public string? LastActiveDocumentPath { get; internal set; }
    public Tern.App.Settings.ViewMode PreferredViewMode { get; internal set; }

    public WorkspaceScanOptions Options { get; }

    public MarkdownProfile Profile { get; }

    public WorkspaceSnapshot Snapshot { get; private set; } = null!;

    public LinkGraph Graph { get; }

    public WorkspaceFileOperations FileOperations { get; }

    /// <summary>Non-fatal problems such as a corrupt index that was rebuilt, for display in the status area.</summary>
    public IList<string> Notices { get; } = [];

    /// <summary>Raised on the creating thread after the tree, graph or index changed because files changed on disk.</summary>
    public event EventHandler<WorkspaceChangeSet>? FilesChanged;

    public static async Task<WorkspaceSession> OpenAsync(
        string root,
        string indexDirectory,
        WorkspaceScanOptions? options = null,
        MarkdownProfile profile = MarkdownProfile.Extended,
        IProgress<IndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var full = WorkspacePaths.Normalize(root);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"The folder '{full}' does not exist.");
        }

        var session = new WorkspaceSession(full, options ?? new WorkspaceScanOptions(), profile);
        try
        {
            await session.InitializeAsync(indexDirectory, progress, cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            session.Dispose();
            throw;
        }

        return session;
    }

    private async Task InitializeAsync(string indexDirectory, IProgress<IndexProgress>? progress, CancellationToken ct)
    {
        // Buffer changes during preparation so sharing the initial scan cannot
        // lose files created, edited or renamed before the workspace becomes ready.
        _watcher = new WorkspaceWatcher(Root, new WorkspaceFilter(Root, Options));
        _watcher.Changed += OnWatcherChanged;
        _watcher.Start();
        Snapshot = await _scanner.ScanAsync(Root, cancellationToken: ct).ConfigureAwait(true);
        foreach (var warning in Snapshot.Warnings)
        {
            Notices.Add(warning.Message);
        }

        await Graph.LoadAsync(Snapshot.Files.Select(f => f.FullPath), _overlayText, cancellationToken: ct).ConfigureAwait(true);

        Directory.CreateDirectory(indexDirectory);
        var databasePath = Path.Combine(indexDirectory, IndexFileName(Root));
        _index = SearchIndex.Open(databasePath, Root);
        if (_index.WasRecreated)
        {
            Notices.Add($"The search index was rebuilt ({_index.RecreationReason}).");
        }

        _indexer = new WorkspaceIndexer(Root, _index, Options, Profile);
        _indexer.BackgroundError += (_, ex) => Notices.Add("Indexing problem: " + ex.Message);
        _search = _indexer.CreateSearchService();
        await _indexer.IndexSnapshotAsync(Snapshot, progress, ct).ConfigureAwait(true);

        lock (_pendingGate)
        {
            _initializing = false;
            if (!_pendingChanges.IsEmpty && !_disposed)
            { _refreshRunning = true; _ = Task.Run(DrainChangesAsync); }
        }
    }

    /// <summary>One index database per workspace, named by a hash of its path so different folders never share an index.</summary>
    public static string IndexFileName(string root)
    {
        var normalized = WorkspacePaths.Normalize(root);
        if (WorkspacePaths.IsCaseInsensitive) normalized = normalized.ToUpperInvariant();
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized));
        return "index-" + Convert.ToHexString(bytes, 0, 8).ToLowerInvariant() + ".db";
    }

    public SearchPage Search(string? query, int limit = 30, int offset = 0, CancellationToken cancellationToken = default, int snippetLength = 160) =>
        _search is null ? SearchPage.Empty : _search.Query(query, new SearchOptions(limit, offset, snippetLength), cancellationToken);

    public QuickOpenResults QuickOpen(string? query, int limit = 50) =>
        Tern.Search.QuickOpen.Search(query, Snapshot.Files.Select(f => f.RelativePath), HeadingCandidate.FromGraph(Graph), limit);

    public string ToFullPath(string relativePath) => Path.GetFullPath(Path.Combine(Root, relativePath));
    public bool HasUnsavedSearchBuffers => _indexer?.Overlay.Count > 0;

    /// <summary>Layers unsaved editor text over disk so search and the link graph see what the user sees.</summary>
    public void SetUnsavedText(string path, string text)
    {
        if (!IsInside(path))
        {
            return;
        }

        _overlayText.Set(path, text);
        _indexer?.SetOverlay(path, text);
        lock (_pendingGate)
        {
            if (_disposed) return;
            var version = Interlocked.Increment(ref _textVersion);
            _textVersions[path] = version;
            _pendingText[path] = (text, version);
            _lastTextChange = Environment.TickCount64;
            if (!_graphRunning)
            {
                _graphRunning = true;
                _graphWork = Task.Run(UpdatePendingGraphAsync);
            }
        }
    }

    public void ClearUnsavedText(string path, bool refreshGraph = true)
    {
        var hadOverlay = HasOverlayText(path);
        if (refreshGraph && !hadOverlay) return; // Preserve an already queued disk restore.
        _textVersions.TryRemove(path, out _);
        lock (_pendingGate) _pendingText.Remove(path);
        _overlayText.Clear(path);
        _indexer?.ClearOverlay(path);
        if (refreshGraph && hadOverlay)
        {
            lock (_pendingGate)
            {
                if (_disposed) return;
                var version = Interlocked.Increment(ref _textVersion);
                _textVersions[path] = version;
                _pendingText[path] = (null, version);
                _lastTextChange = Environment.TickCount64;
                if (!_graphRunning) { _graphRunning = true; _graphWork = Task.Run(UpdatePendingGraphAsync); }
            }
        }
    }

    /// <summary>Re-reads one saved file into the index and graph.</summary>
    public async Task NotifySavedAsync(string path, string text, CancellationToken cancellationToken = default)
    {
        if (!IsInside(path))
        {
            return;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (_disposed) return;
            ClearUnsavedText(path, refreshGraph: false);
            await Task.Run(() => Graph.Update(path, text), cancellationToken).ConfigureAwait(true);
            if (_indexer is not null)
                await _indexer.RefreshFileAsync(path, cancellationToken).ConfigureAwait(true);
            // Existing files do not change tree topology. New Save As destinations do.
            await RefreshSnapshotAsync(new WorkspaceChangeSet([new WorkspaceChange(WorkspaceChangeKind.Changed, path)], false), cancellationToken).ConfigureAwait(true);
        }
        finally { _refreshGate.Release(); }
    }

    private async Task UpdatePendingGraphAsync()
    {
        while (true)
        {
            await Task.Delay(180).ConfigureAwait(false);
            KeyValuePair<string, (string? Text, long Version)>[] pending;
            lock (_pendingGate)
            {
                if (_disposed || _pendingText.Count == 0) { _graphRunning = false; return; }
                if (Environment.TickCount64 - _lastTextChange < 180) continue;
                pending = _pendingText.ToArray();
                _pendingText.Clear();
            }
            foreach (var item in pending)
            {
                if (_disposed) break;
                try
                {
                    var text = item.Value.Text;
                    if (text is null && WorkspacePaths.IsPhysicallyContained(Root, item.Key))
                        text = await new DiskDocumentTextProvider(Options.MaxFileSizeBytes ?? DocumentFileServiceLimit).ReadTextAsync(item.Key, CancellationToken.None).ConfigureAwait(false);
                    if (text is not null) Graph.Update(item.Key, text, () => !_disposed && _textVersions.GetValueOrDefault(item.Key) == item.Value.Version);
                }
                catch (Exception ex) when (ex is ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException)
                { /* Keep the last valid graph for parser-rejected input. */ }
                finally
                {
                    if (item.Value.Text is null)
                        ((ICollection<KeyValuePair<string, long>>)_textVersions).Remove(new(item.Key, item.Value.Version));
                }
            }
            Post(() => { if (!_disposed) GraphChanged?.Invoke(this, EventArgs.Empty); });
        }
    }

    private async Task RefreshSnapshotAsync(WorkspaceChangeSet changes, CancellationToken ct = default)
    {
        var snapshot = Snapshot;
        if (!changes.RescanRequired && changes.Changes.Count == 0) return;
        var known = snapshot.Files.ToDictionary(f => f.FullPath, WorkspacePaths.Comparer);
        if (changes.RescanRequired || changes.Changes.Any(c => c.Kind != WorkspaceChangeKind.Changed
            || c.IsDirectory || !known.ContainsKey(c.Path)))
        { await ScanTreeCoreAsync(ct).ConfigureAwait(false); return; }
        // Preserve the tree when file membership and admission have not changed.
        var updates = new Dictionary<string, WorkspaceFile>(WorkspacePaths.Comparer);
        foreach (var change in changes.Changes)
        {
            var info = new FileInfo(change.Path);
            if (!info.Exists || Options.MaxFileSizeBytes is { } cap && info.Length > cap)
            { await ScanTreeCoreAsync(ct).ConfigureAwait(false); return; }
            var previous = known[change.Path];
            updates[previous.FullPath] = previous with { Length = info.Length, LastWriteUtc = info.LastWriteTimeUtc };
        }
        Snapshot = new WorkspaceSnapshot(snapshot.Root, snapshot.Tree,
            snapshot.Files.Select(f => updates.GetValueOrDefault(f.FullPath, f)).ToArray(), snapshot.Warnings);
    }

    public async Task RescanTreeAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try { if (!_disposed) await ScanTreeCoreAsync(cancellationToken).ConfigureAwait(true); }
        finally { _refreshGate.Release(); }
    }

    private async Task ScanTreeCoreAsync(CancellationToken cancellationToken) =>
        Snapshot = await _scanner.ScanAsync(Root, cancellationToken: cancellationToken).ConfigureAwait(true);

    public FindReplace CreateFindReplace() => new(_overlayText);

    public bool IsInside(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    public IEnumerable<string> FilePaths => Snapshot.Files.Select(f => f.FullPath);

    private void OnWatcherChanged(object? sender, WorkspaceChangeSet changes)
    {
        lock (_pendingGate)
        {
            if (_disposed) return;
            if (changes.RescanRequired) _pendingChanges.RequireRescan();
            foreach (var c in changes.Changes)
                switch (c.Kind)
                {
                    case WorkspaceChangeKind.Created: _pendingChanges.Created(c.Path, c.IsDirectory); break;
                    case WorkspaceChangeKind.Changed: _pendingChanges.Changed(c.Path, c.IsDirectory); break;
                    case WorkspaceChangeKind.Deleted: _pendingChanges.Deleted(c.Path); break;
                    case WorkspaceChangeKind.Renamed: _pendingChanges.Renamed(c.OldPath!, c.Path, c.IsDirectory); break;
                }
            if (_initializing || _refreshRunning) return;
            _refreshRunning = true;
            _ = Task.Run(DrainChangesAsync);
        }
    }

    private async Task DrainChangesAsync()
    {
        while (true)
        {
            WorkspaceChangeSet changes;
            lock (_pendingGate)
            {
                if (_disposed || _pendingChanges.IsEmpty) { _refreshRunning = false; return; }
                changes = _pendingChanges.Drain();
            }
            // Replacing an existing file can look like delete/create. Recheck
            // those hints before treating an ordinary save as a tree change.
            var known = Snapshot.Files.Select(f => f.FullPath).ToHashSet(WorkspacePaths.Comparer);
            changes = changes with { Changes = changes.Changes.Select(c =>
                c.Kind is WorkspaceChangeKind.Created or WorkspaceChangeKind.Deleted
                    && !c.IsDirectory && known.Contains(c.Path) && File.Exists(c.Path)
                    ? c with { Kind = WorkspaceChangeKind.Changed } : c).ToArray() };
            await HandleChangesAsync(changes).ConfigureAwait(false);
        }
    }

    private async Task HandleChangesAsync(WorkspaceChangeSet changes)
    {
        if (_disposed)
        {
            return;
        }

        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            await RefreshSnapshotAsync(changes).ConfigureAwait(false);
            foreach (var change in changes.Changes)
            {
                await ApplyToGraphAsync(change).ConfigureAwait(false);
            }

            if (changes.RescanRequired)
            {
                await Graph.LoadAsync(Snapshot.Files.Select(f => f.FullPath), _overlayText).ConfigureAwait(false);
            }

            if (_indexer is not null)
            {
                if (changes.RescanRequired || changes.Changes.Any(c => c.Kind != WorkspaceChangeKind.Changed || c.IsDirectory))
                    await _indexer.IndexSnapshotAsync(Snapshot).ConfigureAwait(false);
                else
                    await _indexer.ApplyChangesAsync(changes).ConfigureAwait(false);
            }

            Post(() => FilesChanged?.Invoke(this, changes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or ArgumentException
            or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            Notices.Add("Workspace refresh failed: " + ex.Message);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task ApplyToGraphAsync(WorkspaceChange change)
    {
        // Atomic saves can emit deletion/recreation hints after an unsaved
        // duplicate has been restored. Disk hints must never remove that draft.
        if (!change.IsDirectory && HasOverlayText(change.Path)) return;
        switch (change.Kind)
        {
            case WorkspaceChangeKind.Deleted:
                if (File.Exists(change.Path) && IsDocument(change.Path))
                { await ApplyToGraphAsync(change with { Kind = WorkspaceChangeKind.Changed }).ConfigureAwait(false); break; }
                if (Directory.Exists(change.Path)) break;
                Graph.Remove(change.Path);
                break;
            case WorkspaceChangeKind.Renamed when change.OldPath is not null:
                Graph.Rename(change.OldPath, change.Path);
                break;
            case WorkspaceChangeKind.Created or WorkspaceChangeKind.Changed:
                if (!change.IsDirectory && IsDocument(change.Path) && File.Exists(change.Path))
                {
                    try
                    {
                        if (!WorkspacePaths.IsPhysicallyContained(Root, change.Path)) { Graph.Remove(change.Path); break; }
                        if (new FileInfo(change.Path).Length > (Options.MaxFileSizeBytes ?? DocumentFileServiceLimit))
                        { Graph.Remove(change.Path); break; }
                        var text = await new DiskDocumentTextProvider(Options.MaxFileSizeBytes ?? DocumentFileServiceLimit)
                            .ReadTextAsync(change.Path, CancellationToken.None).ConfigureAwait(false);
                        if (text is null) break; // A transient sharing/read failure keeps the last valid graph.
                        // An open dirty buffer wins over watcher events from an older save.
                        if (!HasOverlayText(change.Path)) Graph.Update(change.Path, text);
                    }
                    catch (IOException)
                    {
                        // The writer still has the file open; the next event will carry the final content.
                    }
                }

                break;
        }
    }

    private const long DocumentFileServiceLimit = Tern.Filesystem.DocumentFileService.MaximumEagerLoadBytes;
    private bool HasOverlayText(string path) => _indexer?.Overlay.Contains(path) == true;

    private bool IsDocument(string path) =>
        Options.Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
        || Options.IncludeTextFiles && Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase);

    private void Post(Action action)
    {
        if (_context is null)
        {
            action();
        }
        else
        {
            _context.Post(_ => action(), null);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher?.Dispose();
        _indexer?.Dispose();
        _index?.Dispose();
        // Pending workers may still release this gate while shutting down.
        lock (_pendingGate) _pendingText.Clear();
    }
}
