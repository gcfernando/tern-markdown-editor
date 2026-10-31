namespace Tern.Filesystem;

/// <summary>
/// Raises a debounced notification when a watched file changes on disk. Watcher events are only a hint:
/// callers must still compare content before acting, and again before saving.
/// </summary>
public sealed class FileChangeMonitor : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly string _fileName;
    private readonly TimeSpan _debounce;
    private readonly Timer _timer;
    private bool _disposed;

    public FileChangeMonitor(string path, TimeSpan? debounce = null)
    {
        var fullPath = Path.GetFullPath(path);
        _fileName = Path.GetFileName(fullPath);
        _debounce = debounce ?? TimeSpan.FromMilliseconds(250);
        _timer = new Timer(_ => Changed?.Invoke(this, EventArgs.Empty), null, Timeout.Infinite, Timeout.Infinite);

        var directory = Path.GetDirectoryName(fullPath)!;
        _watcher = new FileSystemWatcher(directory)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
            IncludeSubdirectories = false,
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Deleted += OnFileEvent;
        _watcher.Renamed += (_, e) =>
        {
            if (Matches(e.Name) || Matches(e.OldName))
            {
                Schedule();
            }
        };
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Raised on a thread-pool thread; marshal to the UI thread before touching UI state.</summary>
    public event EventHandler? Changed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher.Dispose();
        _timer.Dispose();
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (Matches(e.Name))
        {
            Schedule();
        }
    }

    private bool Matches(string? name) =>
        string.Equals(name, _fileName, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void Schedule()
    {
        if (!_disposed)
        {
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }
}
