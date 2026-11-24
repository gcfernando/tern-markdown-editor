using System.Collections.Specialized;
using Tern.Workspaces;

namespace Tern.App.Shell;

public sealed partial class ShellViewModel
{
    private readonly object _sessionPersistenceGate = new();
    private CancellationTokenSource? _pendingSessionSave;
    private long _sessionSaveGeneration;
    private int _sessionRestoreDepth;
    private bool _sessionExitCommitted;
    private WindowLayoutHint? _sessionWindow;

    private void InitializeSessionPersistence()
    {
        _sessionWindow = _sessionStore.Load().Window;
        Tabs.CollectionChanged += OnSessionStructureChanged;
        OpenWorkspaces.CollectionChanged += OnSessionStructureChanged;
    }

    private void OnSessionStructureChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueSessionSave();

    partial void OnWorkspaceChanged(WorkspaceSession? value) => QueueSessionSave();

    private SessionSnapshot CaptureSessionSnapshot()
    {
        if (_sessionReset) return SessionSnapshot.Empty;
        var documents = Tabs.Where(tab => tab.FilePath is not null).ToList();
        return new SessionSnapshot(Workspace?.Root,
            documents.Select(tab => new TabState(tab.FilePath!, GetViewState(tab)?.CaretOffset ?? 0,
                GetViewState(tab)?.ScrollOffset ?? 0) { DiskSha256 = tab.Session.Baseline?.Sha256 }).ToList(),
            ActiveTab?.FilePath, _sessionWindow, new Dictionary<string, FileViewState>(_viewStates))
        {
            ActiveTabIndex = ActiveTab is { } active ? documents.IndexOf(active) : null,
            OpenWorkspaceRoots = OpenWorkspaces.Where(workspace => workspace.IsNamedWorkspace).Select(workspace => workspace.Root).ToList(),
            OpenFolderRoots = OpenWorkspaces.Where(workspace => !workspace.IsNamedWorkspace).Select(workspace => workspace.Root).ToList(),
        };
    }

    private void CancelPendingSessionSave()
    {
        ++_sessionSaveGeneration;
        var pending = _pendingSessionSave;
        _pendingSessionSave = null;
        pending?.Cancel();
    }

    private void QueueSessionSave()
    {
        lock (_sessionPersistenceGate)
        {
            if (_disposed || _sessionReset || _sessionExitCommitted || _sessionRestoreDepth > 0) return;
            CancelPendingSessionSave();
            // Collection/selection notifications run on the host's UI context. The writer only receives this snapshot.
            var snapshot = CaptureSessionSnapshot();
            var cancellation = new CancellationTokenSource();
            _pendingSessionSave = cancellation;
            _ = PersistSessionAfterDelayAsync(snapshot, _sessionSaveGeneration, cancellation);
        }
    }

    private async Task PersistSessionAfterDelayAsync(SessionSnapshot snapshot, long generation, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellation.Token).ConfigureAwait(false);
            lock (_sessionPersistenceGate)
            {
                if (_disposed || _sessionReset || _sessionExitCommitted || _sessionRestoreDepth > 0
                    || generation != _sessionSaveGeneration) return;
                if (!_sessionStore.Save(snapshot))
                    SetStatus("The session could not be saved: " + _sessionStore.LastSaveError?.Message
                        + " Open documents are unchanged; their last saved session will be used after a restart.", StatusSeverity.Warning);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            lock (_sessionPersistenceGate)
            {
                if (ReferenceEquals(_pendingSessionSave, cancellation)) _pendingSessionSave = null;
            }
            cancellation.Dispose();
        }
    }

    private void StopLiveSessionPersistence()
    {
        lock (_sessionPersistenceGate)
        {
            _sessionExitCommitted = true;
            CancelPendingSessionSave();
        }
    }

    private bool ResetPersistedSession()
    {
        lock (_sessionPersistenceGate)
        {
            CancelPendingSessionSave();
            if (!_sessionStore.Save(SessionSnapshot.Empty)) return false;
            _sessionReset = true;
            return true;
        }
    }
}
