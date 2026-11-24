using Tern.Workspaces;

namespace Tern.App.Shell;

public sealed partial class ShellViewModel
{
    private readonly HashSet<WorkspaceSession> _importingWorkspaces = [];

    public async Task<int> ImportFilesToWorkspaceAsync(WorkspaceSession workspace)
    {
        if (_disposed || !OpenWorkspaces.Contains(workspace))
        {
            SetStatus("Open the destination workspace before importing files.", StatusSeverity.Warning);
            return 0;
        }
        if (!_importingWorkspaces.Add(workspace))
        {
            SetStatus("An import to this workspace is already in progress.", StatusSeverity.Info);
            return 0;
        }
        ActivityScope? activity = null;
        var completed = 0;
        try
        {
            var selection = await _dialogs.PickFilesToOpenAsync().ConfigureAwait(true);
            var paths = selection.Distinct(WorkspacePaths.Comparer).ToArray();
            if (paths.Length == 0) return 0;
            activity = Activities.Begin("Import files", workspace.Root);
            var errors = new List<string>();
            foreach (var path in paths)
            {
                if (_disposed || !OpenWorkspaces.Contains(workspace))
                {
                    errors.Add("The destination was closed. Remaining files were not imported.");
                    break;
                }
                var result = await SerializeIdentityAsync(async () =>
                {
                    if (_disposed || !OpenWorkspaces.Contains(workspace))
                        return new FileOperationResult(false, null, null, FileOperationError.NotFound, "The destination was closed.");
                    return await Task.Run(() => workspace.FileOperations.ImportFile(path, workspace.Root)).ConfigureAwait(true);
                }, quiet: true).ConfigureAwait(true);
                if (result.Succeeded) completed++;
                else errors.Add(Path.GetFileName(path) + ": " + result.Message);
                activity.Report("Processing copy requests; existing files are never replaced", completed + errors.Count, paths.Length, "files");
            }
            activity.Report("Refreshing workspace files and index after copying");
            if (!_disposed && OpenWorkspaces.Contains(workspace)) await workspace.RescanTreeAsync().ConfigureAwait(true);
            var text = $"Imported {completed} file(s). Originals are unchanged. Review relative image and document links in the copies.";
            var details = text + (errors.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, errors) : "");
            if (completed == 0 && errors.Count > 0) activity.Fail(details);
            else activity.Complete(details, warning: errors.Count > 0);
            if (!_disposed) SetStatus(text + (errors.Count > 0 ? $" {errors.Count} problem(s); see Activity for details." : ""),
                errors.Count > 0 ? StatusSeverity.Warning : StatusSeverity.Success);
            return completed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            var message = $"Import stopped after {completed} completed file(s). Originals are unchanged: " + UserProblem.Plain(ex);
            activity?.Fail(message + " Details: " + ex.Message);
            if (!_disposed) ShowBanner(message, StatusSeverity.Error);
            return completed;
        }
        finally { activity?.Dispose(); _importingWorkspaces.Remove(workspace); }
    }
}
