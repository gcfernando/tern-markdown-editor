using Tern.App.Commands;
using Tern.Domain.Text;
using Tern.Markdown;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    internal Func<string, Task<LinkInput?>>? LinkInputPicker { get; set; }

    private async Task InsertLinkAsync()
    {
        if (_shown is not { IsReadOnly: false } tab) return;
        var revision = tab.Session.Revision;
        var start = Editor.SelectionStart; var length = Editor.SelectionLength;
        var input = LinkInputPicker is { } picker ? await picker(Editor.SelectedText)
            : await new InsertLinkWindow(Editor.SelectedText, tab.FilePath, tab.Analysis?.Headings ?? [], async () =>
            {
                if (!ReferenceEquals(tab, _shown) || !tab.Session.IsCurrent(revision)) return null;
                return await _shell.SaveTabAsync(tab) && ReferenceEquals(tab, _shown) ? tab.FilePath : null;
            }, tab.FilePath is { } workspaceSource && _shell.WorkspaceForPath(workspaceSource) is { } linkWorkspace
                ? Tern.App.Panels.KnowledgeNavigation.WikiLinks(linkWorkspace.Graph, workspaceSource) : []).ShowDialog<LinkInput?>(this);
        if (input is null) return;
        if (!ReferenceEquals(tab, _shown) || !tab.Session.IsCurrent(revision) || !CanRunCommand(CommandIds.InsertLink))
        { _shell.StatusMessage = "The document changed while choosing the link. Insert the link again."; return; }
        if (input.IsWiki)
        {
            if (tab.FilePath is not { } source || _shell.WorkspaceForPath(source) is not { } workspace
                || !Tern.App.Panels.KnowledgeNavigation.WikiLinks(workspace.Graph, source).Any(candidate => candidate.Destination == input.Destination)
                || input.Label.Any(c => c is '[' or ']' or '|' || char.IsControl(c)))
            { _shell.StatusMessage = "The workspace target or link text is no longer valid. Choose the note again."; return; }
            var wiki = "[[" + input.Destination + (input.Label == input.Destination ? "" : "|" + input.Label) + "]]";
            ApplyEdit(new EditResult([new TextEdit(start, length, wiki)], start + wiki.Length, 0));
            return;
        }
        var label = input.Label.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        var destination = input.Destination.Replace(" ", "%20", StringComparison.Ordinal).Replace("(", "%28", StringComparison.Ordinal).Replace(")", "%29", StringComparison.Ordinal).Replace("<", "%3C", StringComparison.Ordinal).Replace(">", "%3E", StringComparison.Ordinal);
        if (destination.Any(char.IsControl) || destination.Length == 0) { _shell.StatusMessage = "Enter a valid link destination."; return; }
        var markdown = $"[{label}]({destination})";
        ApplyEdit(new EditResult([new TextEdit(start, length, markdown)], start + markdown.Length, 0));
    }
}
