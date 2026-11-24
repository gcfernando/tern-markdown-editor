using Tern.Workspaces;

namespace Tern.App.Shell;

public sealed partial class ShellViewModel
{
    private readonly Dictionary<string, IReadOnlyList<SaveFragment>> _saveFragments = new(WorkspacePaths.Comparer);

    /// <summary>The scan that started when the latest workspace opened; tests and shutdown can await it.</summary>
    public Task SaveFragmentScanTask { get; private set; } = Task.CompletedTask;

    /// <summary>Raised when the Review action of the save-fragment banner is chosen.</summary>
    public event EventHandler? SaveFragmentsReviewRequested;

    /// <summary>Stale save leftovers found in the open workspaces and folders.</summary>
    public IReadOnlyList<SaveFragment> SaveFragments =>
        [.. OpenWorkspaces.SelectMany(w => _saveFragments.TryGetValue(w.Root, out var list) ? list : [])];

    private void StartSaveFragmentScan(WorkspaceSession session) =>
        SaveFragmentScanTask = ScanSaveFragmentsAsync(session.Root);

    /// <summary>Deletes unfinished temporary copies older than a day and lists preserved backups so the user can open, compare or remove them.</summary>
    public async Task ScanSaveFragmentsAsync(string root)
    {
        SaveFragmentScan scan;
        try { scan = await Task.Run(() => SaveFragmentScanner.Scan(root, DateTime.UtcNow, deleteStaleTemporaries: true)).ConfigureAwait(true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { return; }
        if (_disposed) return;
        _saveFragments[root] = scan.Fragments;
        if (scan.Fragments.Count == 0) return;
        ShowBanner($"Found {scan.Fragments.Count} leftover file{(scan.Fragments.Count == 1 ? string.Empty : "s")} from an interrupted save in {Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar))}. They hold full copies of notes.",
            StatusSeverity.Warning, new BannerAction("Review", () => SaveFragmentsReviewRequested?.Invoke(this, EventArgs.Empty)));
    }

    /// <summary>Forgets a fragment that was removed or otherwise handled.</summary>
    public void ForgetSaveFragment(string path)
    {
        foreach (var root in _saveFragments.Keys.ToArray())
            _saveFragments[root] = [.. _saveFragments[root].Where(f => !WorkspacePaths.PathsEqual(f.Path, path))];
    }
}
