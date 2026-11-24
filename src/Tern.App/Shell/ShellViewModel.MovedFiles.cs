using Tern.App.Documents;
using Tern.Filesystem;
using Tern.Workspaces;

namespace Tern.App.Shell;

public enum RecreateFolderChoice { Recreate, SaveAs, Cancel }

public sealed partial class ShellViewModel
{
    // Open documents whose file an outside tool moved or renamed, with the place it now lives (S-02).
    private readonly Dictionary<DocumentTab, string> _movedTargets = [];

    /// <summary>
    /// Called with the watcher's coalesced changes. A rename (of the file or of a folder above it) that covers an open document
    /// whose old path is gone offers to follow the file instead of leaving the tab pointing at a vanished location.
    /// </summary>
    public async Task OfferFollowForMovedFilesAsync(WorkspaceChangeSet changes)
    {
        foreach (var change in changes.Changes)
        {
            if (change.Kind != WorkspaceChangeKind.Renamed || change.OldPath is not { } oldPath) continue;
            var from = Path.GetFullPath(oldPath);
            var to = Path.GetFullPath(change.Path);
            foreach (var tab in Tabs.ToList())
            {
                if (tab.FilePath is not { } open || MapPath(open, from, to, change.IsDirectory) is not { } moved) continue;
                var available = await Task.Run(() => File.Exists(moved) && !File.Exists(open)).ConfigureAwait(true);
                if (!available || await FindOpenTabAsync(moved).ConfigureAwait(true) is not null) continue;
                if (_disposed || !Tabs.Contains(tab) || tab.FilePath != open) continue;
                _movedTargets[tab] = moved;
                if (ReferenceEquals(tab, ActiveTab)) ShowMovedBanner(tab);
            }
        }
    }

    internal bool TryGetMovedTarget(DocumentTab tab, out string newPath) => _movedTargets.TryGetValue(tab, out newPath!);

    internal void ShowMovedBanner(DocumentTab tab)
    {
        if (!_movedTargets.TryGetValue(tab, out var target)) return;
        ShowBanner($"{tab.DisplayName} was moved to {target} · Follow · Save here anyway…", StatusSeverity.Warning,
            new BannerAction("Follow", () => _ = FollowMovedFileSafeAsync(tab)),
            new BannerAction("Save here anyway…", () => { DismissBanner(); tab.AcceptMissingFile(); _movedTargets.Remove(tab); }));
    }

    private async Task FollowMovedFileSafeAsync(DocumentTab tab)
    {
        try { await FollowMovedFileAsync(tab).ConfigureAwait(true); }
        catch (Exception ex) when (ex is not OperationCanceledException) { ReportUnexpectedError("follow the moved file", ex); }
    }

    /// <summary>Points the open document at the file's new location, carrying history, Recent and recovery with it; unsaved edits stay.</summary>
    public async Task<bool> FollowMovedFileAsync(DocumentTab tab)
    {
        if (_disposed || !Tabs.Contains(tab) || tab.FilePath is not { } oldPath || !_movedTargets.TryGetValue(tab, out var target)) return false;
        var warnings = await SerializeIdentityAsync(async () =>
        {
            if (_disposed || !Tabs.Contains(tab) || tab.FilePath != oldPath
                || !_movedTargets.TryGetValue(tab, out var currentTarget) || currentTarget != target)
                return null;
            if (!await Task.Run(() => File.Exists(target)).ConfigureAwait(true)
                || await FindOpenTabAsync(target).ConfigureAwait(true) is not null)
            {
                _movedTargets.Remove(tab);
                SetStatus("The moved file is no longer available at " + target + ".", StatusSeverity.Warning);
                return null;
            }
            if (_disposed || !Tabs.Contains(tab) || tab.FilePath != oldPath
                || !_movedTargets.TryGetValue(tab, out currentTarget) || currentTarget != target)
                return null;
            return await ApplyIdentityChangeAsync(new IdentityChange(oldPath, target, IdentityChangeKind.Move)).ConfigureAwait(true);
        }, quiet: true).ConfigureAwait(true);
        if (warnings is null) return false;
        _movedTargets.Remove(tab);
        DismissBanner();
        await CheckTabForExternalChangeAsync(tab).ConfigureAwait(true);
        SetStatus($"{tab.DisplayName} now follows the file to {target}." + (warnings.Count > 0 ? " " + string.Join(" ", warnings) : string.Empty),
            warnings.Count > 0 ? StatusSeverity.Warning : StatusSeverity.Success);
        return true;
    }

    /// <summary>
    /// Before a save would have to create the document's missing folder, asks first (S-02). An unreachable drive is not asked about:
    /// the save reports it as unavailable instead of recreating anything.
    /// </summary>
    private async Task<bool> ConfirmSaveLocationAsync(DocumentTab tab)
    {
        if (tab.FilePath is not { } path || Path.GetDirectoryName(path) is not { Length: > 0 } folder || Directory.Exists(folder)) return true;
        if (_files.GetAvailability(path) == FileAvailability.Unreachable) return true;
        _movedTargets.TryGetValue(tab, out var movedTo);
        switch (await _dialogs.AskRecreateFolderAsync(folder, movedTo).ConfigureAwait(true))
        {
            case RecreateFolderChoice.Recreate:
                // The user consented to recreating the vanished location, so the earlier "deleted" conflict no longer blocks the save.
                if (tab.Session.Baseline is not null) tab.AcceptMissingFile();
                return true;
            case RecreateFolderChoice.SaveAs:
                await SaveTabAsAsync(tab).ConfigureAwait(true);
                return false;
            default:
                return false;
        }
    }
}
