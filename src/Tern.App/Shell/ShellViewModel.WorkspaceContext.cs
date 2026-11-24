using Tern.App.Settings;
using Tern.Workspaces;

namespace Tern.App.Shell;

public sealed partial class ShellViewModel
{
    private string? WorkspaceActivePath(WorkspaceSession workspace) => ActiveTab?.FilePath is { } active
        && ReferenceEquals(WorkspaceForPath(active), workspace) ? active : workspace.LastActiveDocumentPath;

    /// <summary>Writes the per-user context under local app data. Returns a failure description, or null on success; never touches the workspace folder.</summary>
    private async Task<string?> SaveWorkspaceContextAsync(WorkspaceSession workspace, string? activePath)
    {
        if (!workspace.IsNamedWorkspace) return null;
        var documents = Tabs.Where(tab => tab.FilePath is { } path && ReferenceEquals(WorkspaceForPath(path), workspace))
            .GroupBy(tab => tab.FilePath!, WorkspacePaths.Comparer).Select(group => group.First()).Take(SessionStore.MaxTabs).ToArray();
        var tabs = documents.Select(tab =>
        {
            var state = GetViewState(tab);
            return new TabState(Path.GetRelativePath(workspace.Root, tab.FilePath!), Math.Max(0, state?.CaretOffset ?? 0),
                state is { ScrollOffset: var scroll } && double.IsFinite(scroll) ? Math.Max(0, scroll) : 0);
        }).ToArray();
        var active = activePath is not null && documents.Any(tab => SamePath(tab.FilePath!, activePath))
            ? Path.GetRelativePath(workspace.Root, activePath) : tabs.FirstOrDefault()?.Path;
        var context = new WorkspaceWorkingContext(tabs, active, workspace.PreferredViewMode.ToString());
        try
        {
            await Task.Run(() => _contextStore.Save(workspace.Root, context)).ConfigureAwait(true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or NotSupportedException)
        {
            return "Could not save workspace context for " + workspace.Name + ": " + PlainProblem(ex);
        }
    }

    private async Task RestoreWorkspaceContextAsync(WorkspaceSession workspace, WorkspaceWorkingContext? context)
    {
        if (context is null) return;
        var restored = new List<Documents.DocumentTab>();
        var seen = new HashSet<string>(WorkspacePaths.Comparer);
        foreach (var tab in (context.Tabs ?? []).Take(SessionStore.MaxTabs))
        {
            if (_disposed) return;
            var path = ResolveWorkspaceDocument(workspace, tab.Path);
            if (path is null || !seen.Add(path))
            {
                workspace.Notices.Add("A saved workspace document was unavailable or outside this workspace: " + tab.Path);
                continue;
            }
            if (!Tabs.Any(open => open.FilePath is { } source && SamePath(source, path)))
                _viewStates[path] = new FileViewState(Math.Max(0, tab.CaretOffset), double.IsFinite(tab.ScrollOffset) ? Math.Max(0, tab.ScrollOffset) : 0);
            var opened = await OpenFileAsync(path, activate: false, recordRecent: false).ConfigureAwait(true);
            if (opened is not null) restored.Add(opened);
            else workspace.Notices.Add("A saved workspace document could not be reopened: " + tab.Path);
        }
        var active = ResolveWorkspaceDocument(workspace, context.ActiveTab);
        var selected = restored.FirstOrDefault(tab => tab.FilePath is { } path && active is not null && SamePath(path, active)) ?? restored.FirstOrDefault();
        if (selected is not null) SelectTab(selected, TabActivationReason.Programmatic);
        if (Enum.TryParse<ViewMode>(context.ViewMode, out var mode) && Enum.IsDefined(mode))
        {
            workspace.PreferredViewMode = mode;
            if (ReferenceEquals(Workspace, workspace)) ViewMode = mode;
        }
    }

    private string? ResolveWorkspaceDocument(WorkspaceSession workspace, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 4096 || Path.IsPathFullyQualified(relative)) return null;
        try
        {
            var path = Path.GetFullPath(Path.Combine(workspace.Root, relative));
            return workspace.IsInside(path) && File.Exists(path) && WorkspacePaths.IsPhysicallyContained(workspace.Root, path)
                && ReferenceEquals(WorkspaceForPath(path), workspace) ? path : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    partial void OnViewModeChanged(ViewMode value)
    {
        if (Workspace is { } workspace) workspace.PreferredViewMode = value;
    }
}
