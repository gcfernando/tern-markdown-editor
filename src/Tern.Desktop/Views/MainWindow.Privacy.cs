using Tern.App.Commands;
using Tern.App.Shell;
using Tern.Recovery;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private async Task ChangeWorkspacePrivacyAsync()
    {
        if (_shell.Workspace is not { IsNamedWorkspace: true } workspace) return;
        var makePrivate = !workspace.IsPrivate;
        var message = makePrivate
            ? "Private mode removes this workspace's existing saved-text History, search index and recovery copies. Search stays available in memory. New History, crash recovery and Don't save safety copies are disabled. Original notes are NOT encrypted. Save or close dirty notes first. The workspace reopens to apply this change."
            : "Enable protected History and recovery copies and a persistent search index for this workspace. The workspace reopens; original notes are unchanged.";
        if (await PromptWindow.AskAsync(this, "Change workspace privacy", message,
            ["Cancel", makePrivate ? "Make private" : "Enable secondary copies"], 0, 0, makePrivate ? 1 : -1) != 1) return;
        RememberCurrentDocumentView();
        await _shell.SetWorkspacePrivateAsync(workspace, makePrivate);
    }

    private async Task ClearSavedHistoryAsync(bool workspace)
    {
        var tab = _shown;
        var owner = workspace ? _shell.Workspace : null;
        var name = workspace ? owner?.Name : tab?.DisplayName;
        if (name is null) return;
        if (await PromptWindow.AskAsync(this, "Clear saved-text history",
            "Permanently remove saved versions for " + name + "? Source files stay unchanged. This cannot be undone. Later saves create new versions unless the workspace is Private.",
            ["Cancel", "Clear history"], 0, 0, 1) != 1) return;
        await _shell.ClearHistoryAsync(tab, owner);
    }

    private void ShowDiscardedDrafts()
    {
        try
        {
            var scan = _shell.RecentlyDiscarded();
            if (scan.Corrupt.Count > 0) _shell.SetStatus("Some discarded drafts could not be unlocked or read. Their files were kept.", StatusSeverity.Warning);
            ShowPicker("Recently discarded drafts — kept for seven days", "",
                query => scan.Entries.Where(entry => entry.Record.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Select(entry => new PickerItem(entry.Record.DisplayName,
                        entry.Record.SavedAtUtc.ToLocalTime().ToString("g") + " · opens an unsaved copy", "", entry.Record)).ToArray(),
                item => { if (item.Payload is RecoveryRecord record) _shell.ReopenDiscarded(record); });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _shell.SetStatus("Discarded drafts could not be read: " + _shell.PlainProblem(ex), StatusSeverity.Error); }
    }
}
