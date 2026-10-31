using Tern.App.Documents;

namespace Tern.App.Shell;

public sealed partial class ShellViewModel
{
    private bool _confirmingClose;

    /// <summary>Closes a workspace and its document tabs after all save decisions have succeeded.</summary>
    public Task<bool> CloseWorkspaceAsync(WorkspaceSession? workspace = null) => SerializeWorkspacesAsync(async () =>
    {
        workspace ??= Workspace;
        if (workspace is null || !OpenWorkspaces.Contains(workspace)) return true;
        var activePath = WorkspaceActivePath(workspace);
        var tabs = Tabs.Where(t => t.FilePath is { } path && ReferenceEquals(WorkspaceForPath(path), workspace)).ToList();
        if (!await ConfirmCloseAsync(tabs).ConfigureAwait(true)) return false;
        if (Tabs.Any(t => !tabs.Contains(t) && t.FilePath is { } path && ReferenceEquals(WorkspaceForPath(path), workspace)))
        { StatusMessage = "Another document opened in this workspace while closing. Review it and try again."; return false; }
        var reviewed = tabs.Select(tab => (Tab: tab, tab.Session.Revision, tab.FilePath)).ToArray();
        if (!await SaveWorkspaceContextAsync(workspace, activePath).ConfigureAwait(true)) return false;
        if (reviewed.Any(state => state.Tab.Session.Revision != state.Revision || state.Tab.FilePath != state.FilePath)
            || Tabs.Any(t => !tabs.Contains(t) && t.FilePath is { } path && ReferenceEquals(WorkspaceForPath(path), workspace)))
        { StatusMessage = "A workspace document changed while saving its context. No documents were closed; review it and try again."; return false; }
        foreach (var tab in tabs) RemoveTab(tab, retireRecovery: true);
        DetachWorkspace(workspace);
        StatusMessage = "Workspace closed. Files remain on disk.";
        return true;
    });

    /// <summary>Completes all save decisions before detaching any workspace or document; standalone and unnamed tabs remain open.</summary>
    public Task<bool> CloseAllWorkspacesAsync() => SerializeWorkspacesAsync(async () =>
    {
        var workspaces = OpenWorkspaces.ToList();
        var activePaths = workspaces.ToDictionary(workspace => workspace, WorkspaceActivePath);
        var tabs = Tabs.Where(t => t.FilePath is { } path && workspaces.Any(w => w.IsInside(path))).ToList();
        if (!await ConfirmCloseAsync(tabs).ConfigureAwait(true)) return false;
        if (Tabs.Any(t => !tabs.Contains(t) && t.FilePath is { } path && workspaces.Any(w => w.IsInside(path))))
        { StatusMessage = "Another workspace document opened while closing. Review it and try again."; return false; }
        var reviewed = tabs.Select(tab => (Tab: tab, tab.Session.Revision, tab.FilePath)).ToArray();
        foreach (var workspace in workspaces)
            if (!await SaveWorkspaceContextAsync(workspace, activePaths[workspace]).ConfigureAwait(true)) return false;
        if (reviewed.Any(state => state.Tab.Session.Revision != state.Revision || state.Tab.FilePath != state.FilePath)
            || Tabs.Any(t => !tabs.Contains(t) && t.FilePath is { } path && workspaces.Any(workspace => workspace.IsInside(path))))
        { StatusMessage = "A workspace document changed while saving its context. No documents were closed; review it and try again."; return false; }
        foreach (var tab in tabs) RemoveTab(tab, retireRecovery: true);
        foreach (var workspace in workspaces) DetachWorkspace(workspace);
        StatusMessage = "All workspaces closed. Standalone documents and files on disk are unchanged.";
        return true;
    });

    /// <summary>Closes the open documents beneath a folder; the folder and files on disk are untouched.</summary>
    public async Task<bool> CloseFolderDocumentsAsync(string folder)
    {
        var tabs = Tabs.Where(t => t.FilePath is { } path && IsInFolder(path, folder)).ToList();
        if (!await ConfirmCloseAsync(tabs).ConfigureAwait(true)) return false;
        foreach (var tab in tabs) RemoveTab(tab, retireRecovery: true);
        StatusMessage = "Folder documents closed. Files remain on disk.";
        return true;
    }

    public async Task<bool> CloseDocumentsAsync()
    {
        var tabs = Tabs.ToList();
        if (!await ConfirmCloseAsync(tabs).ConfigureAwait(true)) return false;
        foreach (var tab in tabs) RemoveTab(tab, retireRecovery: true);
        return true;
    }

    /// <summary>Persists the reopenable session after save decisions, then retires explicitly closed recovery records.</summary>
    public async Task<bool> PrepareExitAsync(Tern.Workspaces.WindowLayoutHint layout)
    {
        var tabs = Tabs.ToList();
        if (!await ConfirmCloseAsync(tabs).ConfigureAwait(true)) return false;
        var reviewed = tabs.Select(tab => (Tab: tab, tab.Session.Revision, tab.FilePath)).ToArray();
        foreach (var workspace in OpenWorkspaces.ToList())
            if (!await SaveWorkspaceContextAsync(workspace, WorkspaceActivePath(workspace)).ConfigureAwait(true)) return false;
        if (reviewed.Any(state => state.Tab.Session.Revision != state.Revision || state.Tab.FilePath != state.FilePath)
            || Tabs.Any(tab => !tabs.Contains(tab)))
        { StatusMessage = "A document changed while saving workspace state. The application remains open; review it and try again."; return false; }
        if (!SaveSession(layout)) return false;
        SaveSettings();
        foreach (var tab in tabs) RemoveTab(tab, retireRecovery: true);
        return true;
    }

    private async Task<bool> ConfirmCloseAsync(IReadOnlyList<DocumentTab> tabs)
    {
        if (_confirmingClose) return false;
        _confirmingClose = true;
        var revisions = new Dictionary<DocumentTab, long>();
        try
        {
            // Complete every decision before removing any tab, so Cancel preserves the group.
            foreach (var tab in tabs)
            {
                if (tab.IsDirty)
                {
                    ActiveTab = tab;
                    var choice = await _dialogs.AskSaveChangesAsync(tab.DisplayName).ConfigureAwait(true);
                    if (choice == SaveChoice.Cancel) return false;
                    if (choice == SaveChoice.Save && (!await SaveTabAsync(tab).ConfigureAwait(true) || tab.IsDirty)) return false;
                }
                revisions[tab] = tab.Session.Revision;
            }

            if (revisions.Any(pair => pair.Key.Session.Revision != pair.Value))
            {
                StatusMessage = "A document changed while closing. Review it and try again.";
                return false;
            }
            return true;
        }
        finally
        {
            _confirmingClose = false;
        }
    }

    private static bool IsInFolder(string path, string folder)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
