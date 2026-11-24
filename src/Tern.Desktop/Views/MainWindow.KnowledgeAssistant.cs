using Avalonia;
using Avalonia.Controls;
using Tern.App.Knowledge;
using Tern.Domain.Text;
using Tern.Desktop.Services;
using Tern.Workspaces;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private KnowledgeAssistantPanel? _knowledgeAssistant;
    private KnowledgeAssistantWindow? _knowledgeAssistantWindow;

    internal void ShowKnowledgeAssistant()
    {
        if (!_shell.Settings.KnowledgeAssistantEnabled)
        { _shell.StatusMessage = "Enable Explore your notes in Settings before opening the assistant."; return; }
        if (_knowledgeAssistantWindow is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        ReloadAIConnections();
        // AI is required. Setup and selection stay in Tern's one global AI place.
        // In Offline mode the first screen explains the block. Adding an AI connection would not help.
        var scope = ExploreTaskStore.For(_shell).ResumeWorkspace(_shell.OpenWorkspaces) ?? _shell.Workspace ?? _shell.OpenWorkspaces.FirstOrDefault();
        var pending = scope is not null && ExploreTaskStore.For(_shell).Load(scope.Root, scope.IsPrivate)?.HasPending == true;
        if (!pending && !EnsureKnowledgeConnection() && !_shell.Settings.OfflineMode) { _ = SetUpAIForKnowledgeAsync(); return; }
        var window = CreateKnowledgeAssistantWindow();
        _knowledgeAssistantWindow = window;
        _knowledgeAssistant = window.Assistant;
        window.Closed += (_, _) =>
        {
            _knowledgeAssistantWindow = null;
            _knowledgeAssistant = null;
            if (!_windowClosed) { Activate(); Editor.TextArea.Focus(); }
        };
        window.Show(this);
    }

    private bool EnsureKnowledgeConnection()
    {
        if (_aiWorkspace.SelectedConnection is { CanWrite: true }) return true;
        if (_aiWorkspace.Connections.FirstOrDefault(connection => connection.CanWrite) is not { } available) return false;
        SelectAIConnection(available.Id);
        return _aiWorkspace.SelectedConnection is { CanWrite: true };
    }

    private async Task SetUpAIForKnowledgeAsync()
    {
        _shell.StatusMessage = "Explore your notes uses AI. Add an AI connection to continue.";
        try { await ShowAIConnectionsAsync(this); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { _shell.StatusMessage = Tern.App.Shell.UserProblem.Explain(ex, "Could not open AI connections").Text; return; }
        if (_windowClosed) return;
        if (EnsureKnowledgeConnection()) ShowKnowledgeAssistant();
        else _shell.StatusMessage = "Explore your notes needs an AI connection. Add one, then open it again.";
    }

    internal KnowledgeAssistantWindow CreateKnowledgeAssistantWindow() => new(_shell, _aiWorkspace,
        async (path, line) => { await OpenDocumentPathAsync(path, line); ShowKnowledgeEditor(); },
        (answer, report, current, validate) => { ReviewKnowledgeInsertion(answer, report, current, validate); ShowKnowledgeEditor(); },
        () => ShowAIConnectionsAsync(_knowledgeAssistantWindow is { } window ? window : this),
        OpenFolderAsync,
        () => ShowSettingsAsync("Privacy"));

    private void ShowKnowledgeEditor()
    {
        if (_knowledgeAssistantWindow is { } window) window.WindowState = WindowState.Minimized;
        Activate();
        Editor.TextArea.Focus();
    }

    private void CloseKnowledgeAssistant()
    {
        _knowledgeAssistantWindow?.Close();
    }

    private void ReviewKnowledgeInsertion(KnowledgeAnswer answer, string? report, Func<bool> evidenceCurrent, Func<CancellationToken, Task> validateEvidence)
    {
        if (_shown is not { IsReadOnly: false } tab)
        {
            // Nothing writable is open, so the answer becomes a new unsaved note instead of a dead end.
            var question = _knowledgeAssistant?.LatestQuestion ?? "Answer from my notes";
            _shell.NewDocument(report ?? Tern.App.Knowledge.KnowledgeAnswerExport.ForDocument(question, answer));
            _knowledgeAssistant?.MarkInsertionComplete();
            _shell.StatusMessage = "Answer added to a new note. Save it when you are ready.";
            return;
        }
        var snapshot = tab.Session.GetSnapshot();
        var path = tab.FilePath;
        var offset = Editor.CaretOffset;
        var newline = tab.Session.Format.NewLineText;
        var markdown = report ?? Tern.App.Knowledge.KnowledgeAnswerExport.ForEmail(answer).Replace("Hi,\n\n", "", StringComparison.Ordinal);
        if (report is null) markdown += "\n\nNotes:\n" + string.Join('\n', answer.Citations.Select(citation =>
        {
            var source = citation.Source;
            var target = path is null ? new Uri(source.Path).AbsoluteUri : Path.GetRelativePath(Path.GetDirectoryName(path)!, source.Path).Replace('\\', '/');
            var destination = string.Join('/', target.Split('/').Select(Uri.EscapeDataString));
            if (path is null) destination = new Uri(source.Path).AbsoluteUri;
            var label = source.Title.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal);
            return $"- [{source.Id}] [{label}]({destination}) (line {source.Line})";
        }));
        markdown = markdown.ReplaceLineEndings(newline);
        var proposed = (offset > 0 ? newline + newline : "") + markdown + newline;
        bool TargetCurrent() => !_windowClosed && evidenceCurrent() && ReferenceEquals(_shell.ActiveTab, tab) && _shell.Tabs.Contains(tab)
            && !tab.IsReadOnly && WorkspacePaths.Comparer.Equals(tab.FilePath, path) && tab.Session.IsCurrent(snapshot.Revision);
        ShowOverlay(new AIEditReview("", proposed, offset, edits =>
        {
            if (!TargetCurrent()) return false;
            ContextFor(tab).Buffer.Apply(edits);
            _knowledgeAssistant?.MarkInsertionComplete();
            _shell.StatusMessage = "Added to your note. Press Undo to remove it.";
            return true;
        }, HideOverlay, CopyAIResult, HideOverlay, insertResult: true, copyText: markdown, validateApply: async token =>
        {
            if (!TargetCurrent()) return false;
            await validateEvidence(token);
            return TargetCurrent();
        }));
    }
}
