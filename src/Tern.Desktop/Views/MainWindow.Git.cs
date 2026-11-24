using Avalonia.Threading;
using Tern.App.Shell;
using Tern.Workspaces;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private readonly CancellationTokenSource _gitCancellation = new();
    private readonly Dictionary<string, string> _gitErrors = new(WorkspacePaths.Comparer);
    private bool _readingGit;

    private void ConfigureGitAwareness()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => RefreshGitMarkers();
        timer.Start();
        Closed += (_, _) => { timer.Stop(); _gitCancellation.Cancel(); };
    }

    private async void RefreshGitMarkers()
    {
        if (_readingGit || _windowClosed || _shell.OpenWorkspaces.Count == 0) return;
        _readingGit = true;
        try
        {
            foreach (var workspace in _shell.OpenWorkspaces.ToArray())
            {
                try
                {
                    var snapshot = await GitAwareness.ReadAsync(workspace.Root, _gitCancellation.Token);
                    if (!_shell.OpenWorkspaces.Contains(workspace) || _windowClosed) continue;
                    _gitErrors.Remove(workspace.Root);
                    foreach (var row in _treeRoots.SelectMany(root => root.Flatten()).Where(row => !row.IsDirectory && workspace.IsInside(row.FullPath)))
                        row.GitState = snapshot.Files.TryGetValue(row.FullPath, out var state)
                            ? state == GitFileState.Untracked ? "Untracked in Git" : "Changed since last commit" : "";
                }
                catch (IOException ex)
                {
                    if (!_gitErrors.TryGetValue(workspace.Root, out var previous) || previous != ex.Message)
                        _shell.SetStatus("Git markers unavailable: " + _shell.PlainProblem(ex), StatusSeverity.Warning);
                    _gitErrors[workspace.Root] = ex.Message;
                }
            }
        }
        catch (OperationCanceledException) when (_gitCancellation.IsCancellationRequested) { }
        finally { _readingGit = false; }
    }

    private async Task ShowGitDiffAsync()
    {
        if (_shown is not { FilePath: { } path } tab) return;
        var current = tab.CurrentText;
        try
        {
            var committed = await GitAwareness.ReadHeadTextAsync(path, _gitCancellation.Token);
            if (!_windowClosed) await new GitDiffWindow(tab.DisplayName, committed, current).ShowDialog(this);
        }
        catch (OperationCanceledException) when (_gitCancellation.IsCancellationRequested) { }
        catch (IOException ex) { _shell.SetStatus("Git comparison unavailable: " + _shell.PlainProblem(ex), StatusSeverity.Warning); }
    }
}
