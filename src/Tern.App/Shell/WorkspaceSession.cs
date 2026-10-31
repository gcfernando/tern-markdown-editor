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
    private readonly OverlayTextProvider _overlayText = new(new DiskDocumentTextProvider());
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private SearchIndex? _index;
    private WorkspaceIndexer? _indexer;
    private WorkspaceWatcher? _watcher;
    private SearchService? _search;
    private bool _disposed;

    private WorkspaceSession(string root, WorkspaceScanOptions options, MarkdownProfile profile)
    {
        Root = root;
        Options = options;
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
        await _indexer.IndexWorkspaceAsync(progress, ct).ConfigureAwait(true);

        _watcher = new WorkspaceWatcher(Root);
        _watcher.Changed += OnWatcherChanged;
        _indexer.Attach(_watcher);
        _watcher.Start();
    }

    /// <summary>One index database per workspace, named by a hash of its path so different folders never share an index.</summary>
    public static string IndexFileName(string root)
    {
        var normalized = WorkspacePaths.Normalize(root);
        if (WorkspacePaths.IsCaseInsensitive) normalized = normalized.ToUpperInvariant();
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized));
        return "index-" + Convert.ToHexString(bytes, 0, 8).ToLowerInvariant() + ".db";
    }

    public SearchPage Search(string? query, int limit = 30, int offset = 0, CancellationToken cancellationToken = default) =>
        _search is null ? SearchPage.Empty : _search.Query(query, new SearchOptions(limit, offset), cancellationToken);

    public QuickOpenResults QuickOpen(string? query, int limit = 50) =>
        Tern.Search.QuickOpen.Search(query, Snapshot.Files.Select(f => f.RelativePath), HeadingCandidate.FromGraph(Graph), limit);

    public string ToFullPath(string relativePath) => Path.GetFullPath(Path.Combine(Root, relativePath));

    /// <summary>Layers unsaved editor text over disk so search and the link graph see what the user sees.</summary>
    public void SetUnsavedText(string path, string text)
    {
        if (!IsInside(path))
        {
            return;
        }

        _overlayText.Set(path, text);
        _indexer?.SetOverlay(path, text);
        Graph.Update(path, text);
    }

    public void ClearUnsavedText(string path)
    {
        _overlayText.Clear(path);
        _indexer?.ClearOverlay(path);
    }

    /// <summary>Re-reads one saved file into the index and graph.</summary>
    public async Task NotifySavedAsync(string path, string text, CancellationToken cancellationToken = default)
    {
        if (!IsInside(path))
        {
            return;
        }

        ClearUnsavedText(path);
        Graph.Update(path, text);
        if (_indexer is not null)
        {
            await _indexer.RefreshFileAsync(path, cancellationToken).ConfigureAwait(true);
        }

        await RescanTreeAsync(cancellationToken).ConfigureAwait(true);
    }

    public async Task RescanTreeAsync(CancellationToken cancellationToken = default) =>
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
        _ = HandleChangesAsync(changes);
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

            var snapshot = await _scanner.ScanAsync(Root).ConfigureAwait(false);
            foreach (var change in changes.Changes)
            {
                ApplyToGraph(change);
            }

            if (changes.RescanRequired)
            {
                await Graph.LoadAsync(snapshot.Files.Select(f => f.FullPath), _overlayText).ConfigureAwait(false);
            }

            Snapshot = snapshot;
            Post(() => FilesChanged?.Invoke(this, changes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Notices.Add("Workspace refresh failed: " + ex.Message);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void ApplyToGraph(WorkspaceChange change)
    {
        switch (change.Kind)
        {
            case WorkspaceChangeKind.Deleted:
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
                        Graph.Update(change.Path, File.ReadAllText(change.Path));
                    }
                    catch (IOException)
                    {
                        // The writer still has the file open; the next event will carry the final content.
                    }
                }

                break;
        }
    }

    private bool IsDocument(string path) =>
        Options.Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

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
        _refreshGate.Dispose();
    }
}
