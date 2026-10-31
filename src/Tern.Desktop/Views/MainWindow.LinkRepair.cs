using Avalonia.Interactivity;
using Tern.App.Documents;
using Tern.Desktop.ViewModels;
using Tern.Markdown;
using Tern.Workspaces;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private void OnRepairLinkClick(object? sender, RoutedEventArgs e)
    {
        if (LinkList.SelectedItem is not LinkRowView row || _shown is not { FilePath: not null, IsReadOnly: false } tab || _shell.WorkspaceForPath(tab.FilePath) is not { } workspace) return;
        var revision = tab.Session.Revision;
        ShowPicker("Choose the correct link target…", string.Empty,
            query => [.. workspace.Snapshot.Files.Where(f => f.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(40)
                .Select(f => new PickerItem(Path.GetFileName(f.FullPath), f.RelativePath, string.Empty, f.FullPath))],
            async item =>
            {
                HideOverlay();
                if (item.Payload is string target && ReferenceEquals(tab, _shown) && tab.Session.IsCurrent(revision))
                    await RepairLinkAsync(tab, row, target);
                else
                    _shell.StatusMessage = "The document changed; select the broken link again.";
            });
    }

    internal async Task RepairLinkAsync(DocumentTab tab, LinkRowView row, string target)
    {
        if (tab.IsReadOnly || tab.FilePath is not { } source || _shell.WorkspaceForPath(source) is not { } workspace || !workspace.IsInside(target)) return;
        var snapshot = tab.Session.GetSnapshot();
        var profile = tab.Profile;
        var analysis = await Task.Run(() => MarkdownAnalyzer.Analyze(snapshot.Text, profile));
        if (_windowClosed || !_shell.Tabs.Contains(tab) || tab.IsReadOnly || tab.FilePath != source
            || !ReferenceEquals(workspace, _shell.WorkspaceForPath(source)) || !tab.Session.IsCurrent(snapshot.Revision))
        {
            _shell.StatusMessage = "The document changed; select the broken link again.";
            return;
        }

        var relative = Path.GetRelativePath(Path.GetDirectoryName(source)!, target).Replace(Path.DirectorySeparatorChar, '/');
        var escaped = string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
        var edits = LinkRewriter.Rewrite(snapshot.Text, analysis, link =>
        {
            if (link.Line != row.Line || link.Destination != row.Destination) return null;
            var hash = link.Destination.IndexOf('#', StringComparison.Ordinal);
            var fragment = hash < 0 ? string.Empty : link.Destination[hash..];
            return (link.Kind == LinkKind.WikiLink ? relative : escaped) + fragment;
        });
        if (edits.Count == 0) return;
        tab.ApplyEdits(edits);
        _shell.StatusMessage = "Link repaired in the editor. Save to update the file; Undo to revert.";
    }
}
