using Tern.Markdown;
using Tern.Filesystem;
using Tern.Search;
using Tern.Workspaces;

namespace Tern.App.Shell;

/// <summary>A workspace problem: the sentence people read and the system's wording for Activity.</summary>
public sealed record WorkspaceProblem(string Reason, string? Details);

/// <summary>
/// An open folder: its file tree, link graph, full-text index and change watcher, kept consistent with each other.
/// Unsaved editor text is layered over disk content so search and link checks reflect what the user sees.
/// </summary>
public sealed partial class WorkspaceSession : IDisposable
{
    private readonly WorkspaceScanner _scanner;
    private readonly OverlayTextProvider _overlayText;
    private readonly DiskDocumentTextProvider _diskText;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private SearchIndex? _index;
    private WorkspaceIndexer? _indexer;
    private WorkspaceWatcher? _watcher;
    private SearchService? _search;
    private bool _disposed;
    // Cancellation can resume OpenAsync before Dispose finishes scheduling teardown.
    private readonly TaskCompletionSource _indexDisposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
    private readonly CancellationTokenSource _preparationCancellation = new();
    private bool _contentPaused;
    public bool IsPreparingContent { get; private set; }
    public bool IsContentReady { get; private set; }
    public string ContentStatus { get; private set; } = "Preparing workspace";
    public IndexProgress? ContentProgress { get; private set; }
    public event EventHandler? ContentStatusChanged;

    public void CancelContentPreparation()
    {
        if (IsPreparingContent) _preparationCancellation.Cancel();
    }

    private void ReportContentStatus(string text, IndexProgress? progress = null)
    {
        ContentStatus = text;
        ContentProgress = progress;
        // Files finish far faster than the UI can repaint. Coalesce to the latest state; first, last and phase changes still report.
        if (progress is { Processed: > 0 } p && p.Processed < p.Total
            && Environment.TickCount64 - Volatile.Read(ref _lastStatusPost) < StatusPostIntervalMs) return;
        if (Interlocked.Exchange(ref _statusPostPending, 1) == 1) return;
        Post(() =>
        {
            Volatile.Write(ref _statusPostPending, 0);
            Volatile.Write(ref _lastStatusPost, Environment.TickCount64);
            if (!_disposed) ContentStatusChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private const int StatusPostIntervalMs = 100;
    private int _statusPostPending;
    private long _lastStatusPost;

    public event EventHandler? GraphChanged;
    internal Task GraphIdle => _graphWork;

    private WorkspaceSession(string root, WorkspaceScanOptions options, MarkdownProfile profile)
    {
        Root = root;
        Options = options;
        _diskText = new DiskDocumentTextProvider(options.MaxFileSizeBytes ?? DocumentFileServiceLimit);
        _overlayText = new OverlayTextProvider(_diskText);
        Profile = profile;
        _scanner = new WorkspaceScanner(options);
        Graph = new LinkGraph(root, new LinkGraphOptions { Profile = profile });
        FileOperations = new WorkspaceFileOperations(root);
    }

    public string Root { get; }
    public bool IsPrivate { get; private set; }

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
    public NoticeLog Notices { get; } = new();

    /// <summary>Why live updates stopped after a background failure; null while updates run normally. Cleared by the next successful refresh.</summary>
    public string? UpdatesPausedReason { get { lock (_pendingGate) return _pausedReason; } }

    /// <summary>Raised on the creating thread when a background update or index operation fails, with a user-readable reason.</summary>
    public event EventHandler<string>? ProblemReported;
    public event EventHandler<WorkspaceProblem>? ProblemDetailsReported;

    private string? _pausedReason;

    /// <summary>Test seam: runs inside each change-processing cycle before the snapshot is refreshed.</summary>
    internal Func<WorkspaceChangeSet, Task>? ChangeProcessingHook { get; set; }

    /// <summary>Queues a full rescan and restarts live updates after a pause.</summary>
    public void RequestRescan()
    {
        lock (_pendingGate)
        {
            if (_disposed) return;
            _contentPaused = false;
            _pendingChanges.RequireRescan();
            if (_initializing || _refreshRunning) return;
            _refreshRunning = true;
            _ = Task.Run(DrainChangesAsync);
        }
    }

    private void ReportProblem(string reason, bool paused, string? details = null)
    {
        lock (_pendingGate) { if (paused) _pausedReason = reason; }
        Notices.Add(reason);
        Post(() =>
        {
            // The shell records the problem first, so anyone told about it can already see it.
            ProblemDetailsReported?.Invoke(this, new WorkspaceProblem(reason, details));
            ProblemReported?.Invoke(this, reason);
        });
    }

    /// <summary>Plain words for people. The system's own wording goes to Activity through the problem details.</summary>
    internal static string ProblemText(Exception error) => error switch
    {
        Microsoft.Data.Sqlite.SqliteException sqlite when SearchIndex.IsCorruption(sqlite) =>
            "Tern's search list for this folder is damaged. Press Rebuild to make it again. Your notes are safe.",
        Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 13 } =>
            "Your drive is full, so Tern cannot update its search list. Free some space, then press Rescan. Your notes are safe.",
        Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 5 or 6 } =>
            "Tern's search list is busy right now. Wait a moment, then press Rescan. Your notes are safe.",
        Microsoft.Data.Sqlite.SqliteException =>
            "Tern could not update its search list for this folder. Press Rescan to try again. Your notes are safe.",
        _ => UserProblem.Plain(error),
    };

    /// <summary>Raised on the creating thread after the tree, graph or index changed because files changed on disk.</summary>
    public event EventHandler<WorkspaceChangeSet>? FilesChanged;

    public static async Task<WorkspaceSession> OpenAsync(
        string root,
        string indexDirectory,
        WorkspaceScanOptions? options = null,
        MarkdownProfile profile = MarkdownProfile.Extended,
        IProgress<IndexProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool indexContent = true,
        Action<WorkspaceSession>? treeReady = null, bool? privateWorkspace = null)
    {
        var full = WorkspacePaths.Normalize(root);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"The folder '{full}' does not exist.");
        }

        var session = new WorkspaceSession(full, options ?? new WorkspaceScanOptions(), profile);
        session.IsPrivate = privateWorkspace ?? WorkspaceDefinition.Load(full)?.Private == true;
        try
        {
            await session.InitializeAsync(indexDirectory, progress, cancellationToken, indexContent, treeReady).ConfigureAwait(true);
        }
        catch
        {
            session.Dispose();
            await session._indexDisposal.Task.ConfigureAwait(false);
            throw;
        }

        if (session._disposed) await session._indexDisposal.Task.ConfigureAwait(false);
        return session;
    }

    /// <summary>True when this session maintains a full-text index; browsed folders are only listed and link-checked.</summary>
    public bool HasContentIndex => _indexer is not null;

    private async Task InitializeAsync(string indexDirectory, IProgress<IndexProgress>? progress, CancellationToken ct, bool indexContent,
        Action<WorkspaceSession>? treeReady)
    {
        // Buffer changes during preparation so sharing the initial scan cannot
        // lose files created, edited or renamed before the workspace becomes ready.
        await Task.Run(() =>
        {
            _watcher = new WorkspaceWatcher(Root, new WorkspaceFilter(Root, Options));
            _watcher.Changed += OnWatcherChanged;
            _watcher.Start();
        }, CancellationToken.None).ConfigureAwait(true);
        Snapshot = await _scanner.ScanAsync(Root, cancellationToken: ct).ConfigureAwait(true);
        foreach (var warning in Snapshot.Warnings)
        {
            Notices.Add(warning.Message);
        }

        if (indexContent)
        {
            // Opening can migrate or recreate the database, so it must not run on the UI thread.
            await Task.Run(() =>
            {
                if (!IsPrivate) Directory.CreateDirectory(indexDirectory);
                var databasePath = Path.Combine(indexDirectory, IndexFileName(Root));
                _index = IsPrivate ? SearchIndex.OpenInMemory(Root) : SearchIndex.Open(databasePath, Root);
                if (_index.WasRecreated)
                {
                    Notices.Add($"The search index was rebuilt ({_index.RecreationReason}).");
                }

                _indexer = new WorkspaceIndexer(Root, _index, Options, Profile);
                _indexer.BackgroundError += (_, ex) => ReportProblem("Indexing problem: " + ProblemText(ex), paused: false, ex.Message);
                _search = _indexer.CreateSearchService();
            }, CancellationToken.None).ConfigureAwait(true);
        }

        IsPreparingContent = true;
        ReportContentStatus("Reading links", new IndexProgress(0, Snapshot.Files.Count, null));
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(ct, _preparationCancellation.Token);
        await _refreshGate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            treeReady?.Invoke(this);
            await Task.Run(async () =>
            {
                var graphProgress = new CallbackProgress<int>(n =>
                    ReportContentStatus($"Reading links {n}/{Snapshot.Files.Count}", new IndexProgress(n, Snapshot.Files.Count, null)));
                if (_indexer is not null) _diskText.SharedReads = _indexer.SharedReads = new SharedFileReads();
                var links = Task.Run(() => Graph.LoadAsync(Snapshot.Files.Select(f => f.FullPath), _overlayText, _indexer is null ? graphProgress : null, preparation.Token), preparation.Token);
                if (_indexer is null) await links.ConfigureAwait(false);
                else
                {
                    ReportContentStatus($"Indexing 0/{Snapshot.Files.Count}", new IndexProgress(0, Snapshot.Files.Count, null));
                    var indexProgress = new CallbackProgress<IndexProgress>(p =>
                    {
                        ReportContentStatus($"Indexing {p.Processed}/{p.Total}", p);
                        progress?.Report(p);
                    });
                    // Links and search are independent, so they are prepared side by side.
                    var indexing = _indexer.IndexSnapshotAsync(Snapshot, indexProgress, preparation.Token);
                    IndexingSummary summary;
                    try
                    {
                        await Task.WhenAll(links, indexing).ConfigureAwait(false);
                        summary = await indexing.ConfigureAwait(false);
                    }
                    finally { _diskText.SharedReads = null; _indexer.SharedReads = null; }
                    if (summary.Failed > 0) Notices.Add($"{summary.Failed} document(s) could not be indexed. Rescan to retry.");
                }
            }, preparation.Token).ConfigureAwait(true);
            IsContentReady = true;
            ReportContentStatus(indexContent ? "Search ready" : "Links ready");
        }
        catch (OperationCanceledException) when (treeReady is not null && preparation.IsCancellationRequested)
        {
            _contentPaused = true;
            ReportContentStatus("Indexing stopped; the file tree is available. Search and links may be incomplete. Rescan to resume.");
        }
        catch (Exception ex) when (treeReady is not null && ex is not OperationCanceledException)
        {
            _contentPaused = true;
            ReportContentStatus("Indexing failed; the file tree is available. Search and links may be incomplete.");
            ReportProblem("Updates paused: " + ProblemText(ex), paused: true, ex.Message);
        }
        finally
        {
            IsPreparingContent = false;
            Post(() => { if (!_disposed) ContentStatusChanged?.Invoke(this, EventArgs.Empty); });
            _refreshGate.Release();
            lock (_pendingGate)
            {
                _initializing = false;
                if (!_pendingChanges.IsEmpty && !_disposed)
                { _refreshRunning = true; _ = Task.Run(DrainChangesAsync); }
            }
        }
    }

    private sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
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

    private (WorkspaceSnapshot Snapshot, HashSet<string> Paths)? _knownFiles;

    /// <summary>Constant-time membership in the current snapshot; rebuilt only when the snapshot changes.</summary>
    internal bool IsKnownFile(string path)
    {
        var snapshot = Snapshot;
        var known = _knownFiles;
        if (known is null || !ReferenceEquals(known.Value.Snapshot, snapshot))
        {
            known = (snapshot, snapshot.Files.Select(file => file.FullPath).ToHashSet(WorkspacePaths.Comparer));
            _knownFiles = known;
        }
        return known.Value.Paths.Contains(path);
    }

    internal async Task<string> ReadKnowledgeTextAsync(string path, int maximumCharacters, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
        var full = WorkspacePaths.Normalize(path);
        var filter = new WorkspaceFilter(Root, Options, requireReadableIgnoreFile: true);
        if (!WorkspacePaths.IsUnder(Root, full) || !filter.IsDocument(full) || !filter.IsRelevantChange(full)
            || !IsKnownFile(full)
            || !WorkspacePaths.IsPhysicallyContained(Root, full))
            throw new InvalidOperationException("The source is no longer authorized in this workspace. Find sources again.");
        var file = new FileInfo(full);
        if (!file.Exists || CloudFiles.IsOnlineOnly(full))
            throw new IOException("The source is missing, unavailable or online-only. Make it available locally and find sources again.");
        if (file.Length > (long)maximumCharacters * 4)
            throw new IOException("The source exceeds the assistant's bounded reading limit.");
        for (var directory = Path.GetDirectoryName(full); directory is not null && WorkspacePaths.IsUnder(Root, directory); directory = Path.GetDirectoryName(directory))
            if (filter.IsDirectoryExcluded(Path.GetFileName(directory), WorkspacePaths.GetRelative(Root, directory), File.GetAttributes(directory)))
                throw new InvalidOperationException("The source is in an excluded directory. Find sources again.");
        var text = await _overlayText.ReadTextAsync(full, new DiskDocumentTextProvider((long)maximumCharacters * 4), token).ConfigureAwait(false)
            ?? throw new IOException("The source could not be read. Check permissions and find sources again.");
        token.ThrowIfCancellationRequested();
        if (text.Length > maximumCharacters)
            throw new IOException("The source exceeds the assistant's bounded reading limit.");
        if (!WorkspacePaths.IsPhysicallyContained(Root, full) || !File.Exists(full))
            throw new IOException("The source changed location while being read. Find sources again.");
        return text;
    }

    public Task<IndexingSummary> RebuildSearchAsync(IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return (_indexer ?? throw new InvalidOperationException("This folder has no search index.")).RebuildAsync(Snapshot, progress, cancellationToken);
    }

    /// <summary>Layers unsaved editor text over disk so search and the link graph see what the user sees.</summary>
    public void SetUnsavedText(string path, string text)
    {
        if (!IsInside(path))
        {
            return;
        }

        path = WorkspacePaths.Normalize(path);
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
        path = WorkspacePaths.Normalize(path);
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
    public async Task NotifySavedAsync(string path, string text, CancellationToken cancellationToken = default,
        Action<string>? reportPhase = null)
    {
        if (!IsInside(path))
        {
            return;
        }

        reportPhase?.Invoke("Waiting for workspace updates");
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (_disposed) return;
            ClearUnsavedText(path, refreshGraph: false);
            reportPhase?.Invoke("Updating workspace links");
            await Task.Run(() => Graph.Update(path, text), cancellationToken).ConfigureAwait(true);
            if (_indexer is not null)
            {
                reportPhase?.Invoke("Updating the workspace search index");
                await _indexer.RefreshFileAsync(path, cancellationToken).ConfigureAwait(true);
            }
            // Existing files do not change tree topology. New Save As destinations do.
            reportPhase?.Invoke("Refreshing Library files");
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
                await _refreshGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_disposed) break;
                    var text = item.Value.Text;
                    if (text is null && WorkspacePaths.IsPhysicallyContained(Root, item.Key))
                        text = await new DiskDocumentTextProvider(Options.MaxFileSizeBytes ?? DocumentFileServiceLimit).ReadTextAsync(item.Key, CancellationToken.None).ConfigureAwait(false);
                    if (text is not null) Graph.Update(item.Key, text, () => !_disposed && _textVersions.GetValueOrDefault(item.Key) == item.Value.Version);
                }
                catch (Exception ex) when (ex is ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException)
                { /* Keep the last valid graph for parser-rejected input. */ }
                finally
                {
                    _refreshGate.Release();
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
        if (await _scanner.TryRefreshFilesAsync(snapshot, changes, ct).ConfigureAwait(false) is { } refreshed)
            Snapshot = refreshed;
        else await ScanTreeCoreAsync(ct).ConfigureAwait(false);
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
    public LinkRepairPlanner CreateLinkRepairPlanner() => new(Graph, _overlayText, Profile);

    public async Task<IReadOnlyList<(string Path, int Line, string Context)>> FindUnlinkedMentionsAsync(string path, CancellationToken token = default)
    {
        if (Graph.GetDocument(path) is not { } target) return [];
        var linked = Graph.GetBacklinks(path).Select(link => link.SourcePath).ToHashSet(WorkspacePaths.Comparer);
        var paths = Graph.Documents.Where(source => !WorkspacePaths.PathsEqual(source, path) && !linked.Contains(source)).ToArray();
        var mentions = new List<(string Path, int Line, string Context)>();
        foreach (var name in target.Aliases.Prepend(target.Title).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var preview = await CreateFindReplace().PreviewAsync(paths, new FindOptions(name, WholeWord: true,
                MaxMatchesPerFile: 1, MaxTotalMatches: int.MaxValue), token).ConfigureAwait(false);
            if (preview.PatternError is { } error) throw new InvalidOperationException(error);
            if (preview.UnreadableFiles.Count > 0) throw new IOException("Some notes could not be read. Check workspace access and retry unlinked mentions.");
            mentions.AddRange(preview.Files.SelectMany(file => file.Matches.Select(match => (file.Path, match.Line, match.Before))));
        }
        return mentions.DistinctBy(mention => mention.Path, WorkspacePaths.Comparer).OrderBy(mention => mention.Path, WorkspacePaths.Comparer).ToArray();
    }

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
            changes = changes with
            {
                Changes = changes.Changes.Select(c =>
                c.Kind is WorkspaceChangeKind.Created or WorkspaceChangeKind.Deleted
                    && !c.IsDirectory && IsKnownFile(c.Path) && File.Exists(c.Path)
                    ? c with { Kind = WorkspaceChangeKind.Changed } : c).ToArray()
            };
            bool ok;
            try { ok = await HandleChangesAsync(changes).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { ReportProblem("Updates paused: " + ProblemText(ex), paused: true, ex.Message); ok = false; }
            if (ok) continue;
            // Keep the lost batch as a rescan and stop; the next file event or Rescan resumes.
            lock (_pendingGate)
            {
                if (!_disposed) _pendingChanges.RequireRescan();
                _refreshRunning = false;
            }
            return;
        }
    }

    private async Task<bool> HandleChangesAsync(WorkspaceChangeSet changes)
    {
        if (_disposed)
        {
            return true;
        }

        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return true;
            }

            if (ChangeProcessingHook is { } hook) await hook(changes).ConfigureAwait(false);
            await RefreshSnapshotAsync(changes).ConfigureAwait(false);
            if (_contentPaused)
            {
                Post(() => FilesChanged?.Invoke(this, changes));
                return true;
            }
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

            lock (_pendingGate) _pausedReason = null;
            IsContentReady = true;
            ReportContentStatus(HasContentIndex ? "Search ready" : "Links ready");
            Post(() => FilesChanged?.Invoke(this, changes));
            return true;
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportProblem("Updates paused: workspace refresh failed (" + ProblemText(ex) + ")", paused: true, ex.Message);
            return false;
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
                        if (CloudFiles.IsOnlineOnly(change.Path)) { Graph.Remove(change.Path); break; }
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
        _preparationCancellation.Cancel();
        _watcher?.Dispose();
        _ = DisposeIndexAsync().ContinueWith(disposal =>
        {
            if (disposal.IsFaulted) _indexDisposal.TrySetException(disposal.Exception!.InnerExceptions);
            else if (disposal.IsCanceled) _indexDisposal.TrySetCanceled();
            else _indexDisposal.TrySetResult();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        // Pending workers may still release this gate while shutting down.
        lock (_pendingGate) _pendingText.Clear();
    }

    private async Task DisposeIndexAsync()
    {
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _indexer?.Dispose();
            _index?.Dispose();
            _preparationCancellation.Dispose();
        }
        finally { _refreshGate.Release(); }
    }
}
