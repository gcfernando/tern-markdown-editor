using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Tern.Filesystem;
using Tern.Markdown;
using Tern.Workspaces;

namespace Tern.Search;

public sealed record IndexProgress(int Processed, int Total, string? CurrentPath);

/// <param name="Scanned">Files found by the scan.</param>
/// <param name="Indexed">Files (re)written to the index because their content changed.</param>
/// <param name="Unchanged">Files skipped because mtime+size, or failing that the content hash, matched.</param>
/// <param name="Failed">Files that could not be read (locked, vanished); their previous index entry is kept.</param>
public sealed record IndexingSummary(int Scanned, int Indexed, int Unchanged, int Removed, int Failed, TimeSpan Elapsed, bool IndexWasRecreated);

/// <summary>
/// Keeps a <see cref="SearchIndex"/> in step with a workspace: background initial indexing with progress and
/// cancellation, incremental updates from <see cref="WorkspaceWatcher"/> change sets (verified against mtime, size and
/// content hash, never trusted blindly), removal of deleted files, and an in-memory overlay for unsaved buffers.
/// All index-changing operations are serialised.
/// </summary>
public sealed class WorkspaceIndexer : IDisposable
{
    private const int BatchSize = 200;

    private readonly string _root;
    private readonly SearchIndex _index;
    private readonly WorkspaceScanOptions _options;
    private readonly WorkspaceFilter _filter;
    private readonly MarkdownProfile _profile;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WorkspaceWatcher? _watcher;
    private bool _disposed;

    public WorkspaceIndexer(string root, SearchIndex index, WorkspaceScanOptions? options = null, MarkdownProfile profile = MarkdownProfile.Extended, OverlayStore? overlay = null)
    {
        _root = WorkspacePaths.Normalize(root);
        _index = index;
        _options = options ?? new WorkspaceScanOptions();
        _filter = new WorkspaceFilter(_root, _options);
        _profile = profile;
        Overlay = overlay ?? new OverlayStore(_root, profile);
    }

    public OverlayStore Overlay { get; }

    /// <summary>Raised when a background update triggered by an attached watcher fails unexpectedly.</summary>
    public event EventHandler<Exception>? BackgroundError;

    public SearchService CreateSearchService() => new(_index, Overlay);

    /// <summary>Makes search results reflect <paramref name="text"/> for <paramref name="path"/> without writing anything to disk or to the index.</summary>
    public void SetOverlay(string path, string text) => Overlay.Set(path, text);

    public void ClearOverlay(string path) => Overlay.Clear(path);

    /// <summary>Feeds the watcher's change sets into <see cref="ApplyChangesAsync"/>.</summary>
    public void Attach(WorkspaceWatcher watcher)
    {
        Detach();
        _watcher = watcher;
        watcher.Changed += OnWatcherChanged;
    }

    public void Detach()
    {
        if (_watcher is { } watcher)
        {
            watcher.Changed -= OnWatcherChanged;
            _watcher = null;
        }
    }

    /// <summary>Scans the workspace and brings the index in line with it. Safe to call repeatedly; unchanged files cost one stat.</summary>
    public async Task<IndexingSummary> IndexWorkspaceAsync(IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReconcileAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Applies a batch of watcher changes. File events are re-verified against the disk; folder events and overflow trigger a reconcile.</summary>
    public async Task<IndexingSummary> ApplyChangesAsync(WorkspaceChangeSet changes, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ApplyCoreAsync(changes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Re-reads one file if it changed on disk; removes it from the index if it is gone.</summary>
    public async Task<bool> RefreshFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var set = new WorkspaceChangeSet([new WorkspaceChange(WorkspaceChangeKind.Changed, path)], false);
        var summary = await ApplyChangesAsync(set, cancellationToken).ConfigureAwait(false);
        return summary.Indexed > 0 || summary.Removed > 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Detach();
        _gate.Dispose();
    }

    private void OnWatcherChanged(object? sender, WorkspaceChangeSet changes)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await ApplyChangesAsync(changes).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Shut down while an update was queued.
            }
            catch (Exception ex) when (ex is IOException or SqliteException or UnauthorizedAccessException)
            {
                BackgroundError?.Invoke(this, ex);
            }
        });
    }

    private async Task<IndexingSummary> ReconcileAsync(IProgress<IndexProgress>? progress, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var recreated = _index.WasRecreated;
        var snapshot = await new WorkspaceScanner(_options).ScanAsync(_root, null, ct).ConfigureAwait(false);
        var files = snapshot.Files;
        var stamps = ReadStamps(out var reset);
        recreated |= reset;

        var present = new HashSet<string>(files.Select(f => f.RelativePath), WorkspacePaths.Comparer);
        var removals = stamps.Keys.Where(p => !present.Contains(p)).ToList();

        int indexed = 0, unchanged = 0, failed = 0, processed = 0;
        for (var offset = 0; offset < files.Count; offset += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = files.Skip(offset).Take(BatchSize).ToList();
            var results = new ConcurrentBag<FileOutcome>();
            await Parallel.ForEachAsync(
                chunk,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8), CancellationToken = ct },
                (file, token) =>
                {
                    results.Add(Evaluate(file.RelativePath, file.FullPath, file.Length, file.LastWriteUtc, stamps.GetValueOrDefault(file.RelativePath)));
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);

            var upserts = results.Where(r => r.Document is not null).Select(r => r.Document!).ToList();
            var removalsNow = offset == 0 ? removals : [];
            WriteBatch(upserts, removalsNow, null);
            foreach (var touched in results.Where(r => r.Touch is not null))
            {
                _index.Touch(touched.RelativePath, touched.Touch!);
            }

            indexed += upserts.Count;
            unchanged += results.Count(r => r.Document is null && !r.Failed);
            failed += results.Count(r => r.Failed);
            processed += chunk.Count;
            progress?.Report(new IndexProgress(processed, files.Count, chunk[^1].RelativePath));
        }

        if (files.Count == 0 && removals.Count > 0)
        {
            WriteBatch([], removals, null);
        }

        progress?.Report(new IndexProgress(files.Count, files.Count, null));
        return new IndexingSummary(files.Count, indexed, unchanged, removals.Count, failed, DateTime.UtcNow - started, recreated);
    }

    private async Task<IndexingSummary> ApplyCoreAsync(WorkspaceChangeSet changes, CancellationToken ct)
    {
        var folderStructureChanged = changes.RescanRequired
            || changes.Changes.Any(c => c.IsDirectory && c.Kind is WorkspaceChangeKind.Created or WorkspaceChangeKind.Renamed);
        var started = DateTime.UtcNow;

        var removalFiles = new List<string>();
        var removalFolders = new List<string>();
        var toCheck = new List<string>();
        foreach (var change in changes.Changes)
        {
            switch (change.Kind)
            {
                case WorkspaceChangeKind.Deleted:
                    AddRemoval(change.Path, removalFiles, removalFolders);
                    break;
                case WorkspaceChangeKind.Renamed:
                    if (change.OldPath is not null)
                    {
                        AddRemoval(change.OldPath, removalFiles, removalFolders);
                    }

                    toCheck.Add(change.Path);
                    break;
                default:
                    toCheck.Add(change.Path);
                    break;
            }
        }

        if (folderStructureChanged)
        {
            WriteBatch([], removalFiles, removalFolders);
            var full = await ReconcileAsync(null, ct).ConfigureAwait(false);
            return full with { Removed = full.Removed + removalFiles.Count + removalFolders.Count };
        }

        var stamps = ReadStamps(out var reset);
        int indexed = 0, unchanged = 0, failed = 0;
        var upserts = new List<IndexedDocument>();
        var touches = new List<(string, IndexStamp)>();
        foreach (var path in toCheck.Distinct(WorkspacePaths.Comparer))
        {
            ct.ThrowIfCancellationRequested();
            var full = WorkspacePaths.Normalize(path);
            if (!_filter.IsRelevantChange(full) || !_filter.IsDocument(full))
            {
                continue;
            }

            var relative = WorkspacePaths.GetRelative(_root, full);
            if (!WorkspacePaths.IsPhysicallyContained(_root, full))
            {
                removalFiles.Add(relative);
                continue;
            }
            FileInfo info;
            try
            {
                info = new FileInfo(full);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!info.Exists)
            {
                if (stamps.ContainsKey(relative))
                {
                    removalFiles.Add(relative);
                }

                continue;
            }

            // A watcher event is an explicit hint that something changed, so coarse timestamps must not hide an edit.
            var outcome = Evaluate(relative, full, info.Length, info.LastWriteTimeUtc, stamps.GetValueOrDefault(relative), trustStat: false);
            if (outcome.Document is not null)
            {
                upserts.Add(outcome.Document);
                indexed++;
            }
            else if (outcome.Failed)
            {
                failed++;
            }
            else
            {
                unchanged++;
                if (outcome.Touch is not null)
                {
                    touches.Add((relative, outcome.Touch));
                }
            }
        }

        WriteBatch(upserts, removalFiles, removalFolders);
        foreach (var (relative, stamp) in touches)
        {
            _index.Touch(relative, stamp);
        }

        return new IndexingSummary(toCheck.Count, indexed, unchanged, removalFiles.Count + removalFolders.Count, failed, DateTime.UtcNow - started, reset);
    }

    private void AddRemoval(string path, List<string> files, List<string> folders)
    {
        var full = WorkspacePaths.Normalize(path);
        if (!WorkspacePaths.IsUnder(_root, full))
        {
            return;
        }

        var relative = WorkspacePaths.GetRelative(_root, full);
        files.Add(relative);
        folders.Add(relative + "/");
    }

    private IReadOnlyDictionary<string, IndexStamp> ReadStamps(out bool recreated)
    {
        recreated = false;
        try
        {
            return _index.GetAllStamps();
        }
        catch (SqliteException ex) when (SearchIndex.IsCorruption(ex))
        {
            _index.Reset("The index became corrupt while in use: " + ex.Message);
            recreated = true;
            return new Dictionary<string, IndexStamp>(WorkspacePaths.Comparer);
        }
    }

    private void WriteBatch(List<IndexedDocument> upserts, List<string> removals, List<string>? folders)
    {
        if (upserts.Count == 0 && removals.Count == 0 && (folders is null || folders.Count == 0))
        {
            return;
        }

        try
        {
            _index.Apply(upserts, removals, folders);
        }
        catch (SqliteException ex) when (SearchIndex.IsCorruption(ex))
        {
            _index.Reset("The index became corrupt while writing: " + ex.Message);
            _index.Apply(upserts, [], null);
        }
    }

    // Reads and extracts one file. Returns "unchanged" cheaply when mtime and size match the stored stamp.
    private FileOutcome Evaluate(string relative, string fullPath, long length, DateTime lastWriteUtc, IndexStamp? stored, bool trustStat = true)
    {
        if (!WorkspacePaths.IsPhysicallyContained(_root, fullPath))
        {
            return new FileOutcome(relative, null, null, true);
        }
        var ticks = lastWriteUtc.Ticks;
        if (trustStat && stored is not null && stored.MtimeTicks == ticks && stored.Size == length)
        {
            return new FileOutcome(relative, null, null, false);
        }

        byte[] bytes;
        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FileOutcome(relative, null, null, true);
        }

        var stamp = new IndexStamp(ticks, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
        if (stored is not null && string.Equals(stored.Hash, stamp.Hash, StringComparison.Ordinal))
        {
            return new FileOutcome(relative, null, stamp, false);
        }

        var text = TextFileCodec.Decode(bytes).Text;
        var content = MarkdownTextExtractor.Extract(relative, text, _profile);
        return new FileOutcome(relative, new IndexedDocument(relative, content, stamp), null, false);
    }

    private sealed record FileOutcome(string RelativePath, IndexedDocument? Document, IndexStamp? Touch, bool Failed);
}
