using Tern.Workspaces;

namespace Tern.App.Shell;

public sealed partial class ShellViewModel
{
    /// <summary>Library X closes all tabs for the source after completing every save decision, then forgets the reference.</summary>
    public async Task<bool> CloseLibraryReferenceAsync(string source, bool online = false)
    {
        bool Matches(Tern.App.Documents.DocumentTab tab) => online
            ? tab.SourceUrl is { } url && NormalizeOnlineSource(url) == NormalizeOnlineSource(source)
            : tab.FilePath is { } path && SamePath(path, Path.GetFullPath(source));
        var tabs = Tabs.Where(Matches).ToArray();
        var previouslyActive = ActiveTab;
        if (!await ConfirmCloseAsync(tabs).ConfigureAwait(true))
        {
            if (previouslyActive is not null && Tabs.Contains(previouslyActive)) ActiveTab = previouslyActive;
            return false;
        }
        if (Tabs.Where(Matches).Any(tab => !tabs.Contains(tab)))
        { StatusMessage = "Another tab opened while closing. Review it and try again."; return false; }
        foreach (var tab in tabs) RemoveTab(tab, retireRecovery: true);
        Recent.Remove(online ? RecentKind.Url : RecentKind.File, source);
        StatusMessage = Recent.LastSaveError is { } error
            ? "Document closed; the reference could not be removed from saved history: " + error.Message
            : "Document closed and Library reference removed. Files remain on disk.";
        return true;
    }
}
