namespace Tern.Workspaces;

/// <summary>
/// Watches a workspace folder and raises batched, coalesced change sets after activity has settled.
/// Notifications are hints only: consumers must re-read the disk before trusting them. When the operating
/// system drops events the next set has <see cref="WorkspaceChangeSet.RescanRequired"/> set.
/// </summary>
public sealed class WorkspaceWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly WorkspaceFilter _filter;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _maxLatency;
    private readonly Timer _timer;
    private readonly Lock _gate = new();
    private readonly ChangeCoalescer _coalescer = new();
    private DateTime _firstEventUtc;
    private bool _disposed;

    public WorkspaceWatcher(string root, WorkspaceFilter? filter = null, TimeSpan? debounce = null, TimeSpan? maxLatency = null)
    {
        Root = WorkspacePaths.Normalize(root);
        _filter = filter ?? new WorkspaceFilter(Root);
        _debounce = debounce ?? TimeSpan.FromMilliseconds(300);
        _maxLatency = maxLatency ?? TimeSpan.FromSeconds(2);
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);

        _watcher = new FileSystemWatcher(Root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        _watcher.Created += (_, e) => OnEvent(e.FullPath, WorkspaceChangeKind.Created);
        _watcher.Changed += (_, e) => OnEvent(e.FullPath, WorkspaceChangeKind.Changed);
        _watcher.Deleted += (_, e) => OnEvent(e.FullPath, WorkspaceChangeKind.Deleted);
        _watcher.Renamed += (_, e) => OnRenamed(e.OldFullPath, e.FullPath);
        _watcher.Error += (_, _) => OnError();
    }

    public string Root { get; }

    /// <summary>Raised on a thread-pool thread once events have been quiet for the debounce interval (or the maximum latency passed).</summary>
    public event EventHandler<WorkspaceChangeSet>? Changed;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _watcher.EnableRaisingEvents = true;
    }

    public void Stop() => _watcher.EnableRaisingEvents = false;

    private void OnEvent(string path, WorkspaceChangeKind kind)
    {
        if (!_filter.IsRelevantChange(path))
        {
            return;
        }

        var isDirectory = kind != WorkspaceChangeKind.Deleted && Directory.Exists(path);
        if (kind != WorkspaceChangeKind.Deleted && !isDirectory && !_filter.IsDocument(path))
        {
            return;
        }

        lock (_gate)
        {
            switch (kind)
            {
                case WorkspaceChangeKind.Created:
                    _coalescer.Created(path, isDirectory);
                    break;
                case WorkspaceChangeKind.Changed:
                    // A folder's timestamp changes whenever its contents do; that carries no information.
                    if (!isDirectory)
                    {
                        _coalescer.Changed(path, false);
                    }

                    break;
                default:
                    _coalescer.Deleted(path);
                    break;
            }

            Schedule();
        }
    }

    private void OnRenamed(string oldPath, string newPath)
    {
        var oldRelevant = _filter.IsRelevantChange(oldPath);
        var newRelevant = _filter.IsRelevantChange(newPath);
        if (!oldRelevant && !newRelevant)
        {
            return;
        }

        var isDirectory = Directory.Exists(newPath);
        lock (_gate)
        {
            // Safe-save style "write temp, rename over target" shows up as a rename from an irrelevant name.
            if (!oldRelevant)
            {
                _coalescer.Changed(newPath, isDirectory);
            }
            else if (!newRelevant)
            {
                _coalescer.Deleted(oldPath);
            }
            else
            {
                _coalescer.Renamed(oldPath, newPath, isDirectory);
            }

            Schedule();
        }
    }

    private void OnError()
    {
        lock (_gate)
        {
            _coalescer.RequireRescan();
            Schedule();
        }

        try
        {
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            // The folder itself may be gone; the rescan will report that.
        }
    }

    // Caller holds the lock.
    private void Schedule()
    {
        if (_disposed)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (_firstEventUtc == default)
        {
            _firstEventUtc = now;
        }

        var untilMax = _firstEventUtc + _maxLatency - now;
        var due = untilMax < _debounce ? (untilMax < TimeSpan.Zero ? TimeSpan.Zero : untilMax) : _debounce;
        _timer.Change(due, Timeout.InfiniteTimeSpan);
    }

    private void Flush()
    {
        WorkspaceChangeSet set;
        lock (_gate)
        {
            if (_disposed || _coalescer.IsEmpty)
            {
                _firstEventUtc = default;
                return;
            }

            set = _coalescer.Drain();
            _firstEventUtc = default;
        }

        if (set.Changes.Count == 0 && !set.RescanRequired)
        {
            return;
        }

        try
        {
            Changed?.Invoke(this, set);
        }
#pragma warning disable CA1031 // A faulty subscriber must not take down the process from a timer thread.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _timer.Dispose();
    }
}
