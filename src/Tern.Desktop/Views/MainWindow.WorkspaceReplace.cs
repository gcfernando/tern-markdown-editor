using Tern.App.Shell;
using Tern.Search;
using Tern.Workspaces;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    internal Func<Task<FindOptions?>>? WorkspaceReplaceInput { get; set; }

    internal async Task ReplaceInWorkspaceAsync()
    {
        if (_shell.Workspace is not { } workspace || _shell.IsBusy || _windowClosed) return;
        var options = WorkspaceReplaceInput is { } input ? await input()
            : await new WorkspaceReplaceWindow(workspace.Root).ShowDialog<FindOptions?>(this);
        if (options is null) return;
        if (_windowClosed || !ReferenceEquals(workspace, _shell.Workspace))
        { _shell.SetStatus("The workspace changed. Start Replace in workspace again.", StatusSeverity.Warning); return; }
        using var cancellation = new CancellationTokenSource();
        using var operation = _shell.BeginStatusOperation("Previewing workspace replacements", workspace.Root, cancellation.Cancel);
        try
        {
            operation.Report("Scanning file text for a review-only replacement preview");
            var paths = workspace.FilePaths.ToArray();
            var preview = await Task.Run(() => workspace.CreateFindReplace().PreviewAsync(paths, options, cancellation.Token), cancellation.Token);
            if (_windowClosed || !ReferenceEquals(workspace, _shell.Workspace)) { operation.Cancel(); return; }
            if (preview.HasError || preview.Truncated || preview.UnreadableFiles.Count > 0 || preview.Files.Any(file => file.TimedOut))
            {
                operation.Fail(preview.PatternError ?? "The preview is incomplete or some files are unreadable. No replacements were applied.");
                _shell.SetStatus("No replacements were applied. " + (preview.PatternError
                    ?? "The preview is incomplete or some files are unreadable. Narrow the search or fix workspace access and retry."), StatusSeverity.Warning);
                return;
            }
            var plan = new RepairPlan(preview.Files.Where(file => file.Edits.Count > 0).Select(file => new FileRepair(file.Path, null,
                file.OriginalText, file.Edits, file.Matches.Select(match => new LinkEditPreview(match.Line, match.Before, match.After, 1)).ToArray())).ToArray(), []);
            if (plan.TotalEdits == 0) { operation.Complete(outcome: "Replacement preview complete; no changes match"); _shell.SetStatus("No text changes match this workspace replacement.", StatusSeverity.Info); return; }
            operation.WaitForUser("Review proposed replacements; no replacement edits have been applied");
            await ReviewWorkspaceEditsAsync(workspace, plan, replacement: true);
            operation.Complete(outcome: "Replacement review closed; see reviewed-repair results for any applied edits");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { operation.Cancel(); if (!_windowClosed) _shell.SetStatus("Workspace replacement preview canceled. Notes were kept.", StatusSeverity.Info); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { operation.Fail(ex.Message); if (!_windowClosed) _shell.SetStatus("Workspace replacement failed: " + Tern.App.Shell.UserProblem.Plain(ex), StatusSeverity.Error); }
    }
}
