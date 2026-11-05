using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Tern.App.Commands;
using Tern.Desktop.Services;
using Tern.Domain.Text;
using Avalonia.Threading;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private readonly AIWorkspaceService _aiWorkspace;
    private bool _refreshingAI;
    private FileSystemWatcher? _aiWatcher;
    private readonly DispatcherTimer _aiReloadTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };

    internal AIWorkspaceService AIWorkspace => _aiWorkspace;

    private void ReloadAIConnections()
    {
        try
        {
            _aiWorkspace.Reload();
            _refreshingAI = true;
            AIConnectionPicker.ItemsSource = _aiWorkspace.Connections;
            AIConnectionPicker.SelectedItem = _aiWorkspace.SelectedConnection;
            AIBar.IsVisible = _aiWorkspace.Connections.Count > 0;
            MainToolbarGrid.RowSpacing = AIBar.IsVisible ? 4 : 0;
            AIConnectionAction.Content = _aiWorkspace.SelectedConnection is { Kind: "mcp" } ? "Open MCP tools" : "Write with AI";
            Avalonia.Automation.AutomationProperties.SetName(AIConnectionAction, AIConnectionAction.Content.ToString());
            RefreshAISummary();
            foreach (var panel in OverlayLayer.Children.OfType<AIPromptPanel>()) panel.RefreshConnections(_aiWorkspace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        { _shell.StatusMessage = "AI connections could not be loaded: " + ex.Message; }
        finally { _refreshingAI = false; }
    }

    private void RefreshAISummary() => AIConnectionSummary.Text = _aiWorkspace.SelectedConnection is { } profile
        ? profile.Summary + (profile.CanWrite ? " · " + _aiWorkspace.ValidationStatus(profile) : "")
        : "Set up once in Settings, then ask from any writing pane.";

    private void ObserveAIConnections()
    {
        AIConnectionManager.ConnectionsChanged += OnSavedAIConnectionsChanged;
        IntegrationProfileStore.ConnectionsChanged += OnSavedAIConnectionsChanged;
        _aiReloadTimer.Tick += (_, _) => { _aiReloadTimer.Stop(); if (!_windowClosed) ReloadAIConnections(); };
        try
        {
            Directory.CreateDirectory(_shell.Paths.SettingsDirectory);
            _aiWatcher = new FileSystemWatcher(_shell.Paths.SettingsDirectory, "*connections.json")
            { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            _aiWatcher.Changed += (_, _) => QueueAIRefresh();
            _aiWatcher.Created += (_, _) => QueueAIRefresh();
            _aiWatcher.Deleted += (_, _) => QueueAIRefresh();
            _aiWatcher.Renamed += (_, _) => QueueAIRefresh();
            _aiWatcher.Error += (_, _) => QueueAIRefresh();
            _aiWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { _shell.StatusMessage = "AI connections will refresh when Settings closes; live file updates are unavailable: " + ex.Message; }
    }

    private void QueueAIRefresh() => Dispatcher.UIThread.Post(() =>
    { if (!_windowClosed) { _aiReloadTimer.Stop(); _aiReloadTimer.Start(); } });

    private void OnSavedAIConnectionsChanged(object? sender, AIConnectionsChangedEventArgs e)
    {
        if (!Path.GetFullPath(e.Directory).Equals(Path.GetFullPath(_shell.Paths.SettingsDirectory), StringComparison.OrdinalIgnoreCase)) return;
        if (Dispatcher.UIThread.CheckAccess()) { if (!_windowClosed) ReloadAIConnections(); }
        else QueueAIRefresh();
    }

    private void StopObservingAIConnections()
    {
        AIConnectionManager.ConnectionsChanged -= OnSavedAIConnectionsChanged;
        IntegrationProfileStore.ConnectionsChanged -= OnSavedAIConnectionsChanged;
        _aiReloadTimer.Stop();
        _aiWatcher?.Dispose();
    }

    private void OnAIConnectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingAI && AIConnectionPicker.SelectedItem is WorkspaceConnection profile) SelectAIConnection(profile.Id);
    }

    private void SelectAIConnection(string id)
    {
        try { _aiWorkspace.Select(id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { _shell.StatusMessage = "The AI choice is active for this session, but could not be saved: " + ex.Message; }
        ReloadAIConnections();
    }

    private async void OnAIPrimaryActionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_aiWorkspace.SelectedConnection is { Kind: "mcp" } profile) await ShowMCPToolsAsync(profile.Id);
        else ShowAIWritingPrompt();
    }

    private async Task ShowMCPToolsAsync(string profileId)
    {
        HideOverlay();
        await new MCPConnectionsWindow(_shell.Paths.SettingsDirectory, selectedProfileId: profileId, useTools: true).ShowDialog(this);
        ReloadAIConnections();
    }

    private async Task ShowAIConnectionsAsync(Window owner)
    {
        await new AIConnectionsWindow(_shell.Paths.SettingsDirectory,
            openCopilot: parent => new CopilotConnectionWindow(_shell.Paths.SettingsDirectory).ShowDialog(parent),
            writingShortcut: WritingShortcut()).ShowDialog(owner);
        ReloadAIConnections();
    }

    private void OnMarkdownAIActionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string action }) ShowAIWritingPrompt(action);
    }

    internal void ShowAIWritingPrompt(string? initialAction = null, string? instruction = null, string? preferredScope = null)
    {
        if (_shown is not { IsReadOnly: false } tab || !CanRunCommand(CommandIds.AskAI)) return;
        ReloadAIConnections();
        HideOverlay();
        var snapshot = tab.Session.GetSnapshot();
        var selection = (Start: Editor.SelectionStart, Length: Editor.IsEffectivelyVisible ? Editor.SelectionLength : 0);
        var caret = Editor.CaretOffset;
        ComboBox? scope = null;
        if (selection.Length > 0)
        {
            var scopes = new[] { "Selected text", "Entire note" };
            scope = new ComboBox { ItemsSource = scopes, SelectedIndex = Math.Max(0, Array.IndexOf(scopes, preferredScope)) };
            AutomationProperties.SetName(scope, "AI text scope");
        }
        var action = initialAction;
        (int Start, int Length) Range() => Equals(scope?.SelectedItem, "Selected text")
            ? selection : (0, snapshot.Text.Length);
        bool TargetIsCurrent() => !_windowClosed && _shell.Tabs.Contains(tab) && ReferenceEquals(_shell.ActiveTab, tab)
            && !tab.IsReadOnly && tab.Session.IsCurrent(snapshot.Revision);
        bool InsertsResult() => action is "Summarize" or "Explain" or "Extract action items";
        string Message(string question, string text) => AIWorkspaceService.WritingMessage(question, text, InsertsResult());
        var panel = new AIPromptPanel(tab.DisplayName, _aiWorkspace, async (profileId, question, token) =>
        {
            if (!TargetIsCurrent())
                throw new InvalidOperationException("The note changed. Reopen Write with AI.");
            var range = Range();
            var original = snapshot.Text.Substring(range.Start, range.Length);
            var insert = InsertsResult();
            string response;
            try { response = await _aiWorkspace.SendWritingAsync(profileId, Message(question, original), TargetIsCurrent, token); }
            finally { if (!_windowClosed) RefreshAISummary(); }
            token.ThrowIfCancellationRequested();
            if (!TargetIsCurrent()) throw new InvalidOperationException("The active note changed while AI was working. Its text has been kept. Reopen Write with AI.");
            var newline = tab.Session.Format.NewLineText;
            var markdown = AIWorkspaceService.MarkdownResponse(response).ReplaceLineEndings(newline);
            if (string.IsNullOrWhiteSpace(markdown)) throw new InvalidOperationException("AI returned no Markdown. Your note has not changed.");
            var offset = range.Start;
            string? copyText = null;
            if (insert)
            {
                offset += range.Length;
                var atLineStart = offset == 0 || snapshot.Text[offset - 1] is '\r' or '\n';
                if (offset < snapshot.Text.Length && offset > 0 && snapshot.Text[offset - 1] == '\r' && snapshot.Text[offset] == '\n')
                    offset++;
                if (!atLineStart)
                    while (offset < snapshot.Text.Length && snapshot.Text[offset] is not ('\r' or '\n')) offset++;
                var suffix = offset == snapshot.Text.Length ? "" : atLineStart ? newline + newline : newline;
                copyText = markdown.TrimStart('\r', '\n').TrimEnd();
                markdown = (offset == 0 ? "" : atLineStart ? newline : newline + newline) + copyText + suffix;
            }
            else if (range.Start + range.Length < snapshot.Text.Length && original.EndsWith('\n') && !markdown.EndsWith('\n'))
                markdown += newline;
            HideOverlay();
            ShowOverlay(new AIEditReview(original, markdown, offset, edits =>
            {
                if (!TargetIsCurrent()) return false;
                ContextFor(tab).Buffer.Apply(edits);
                _shell.StatusMessage = "AI changes applied. Undo to restore the previous draft.";
                return true;
            }, HideOverlay, CopyAIResult, () =>
            {
                if (!ReferenceEquals(_shown, tab)) { HideOverlay(); return; }
                var current = TargetIsCurrent();
                if (current)
                {
                    Editor.CaretOffset = caret;
                    Editor.Select(selection.Start, selection.Length);
                }
                ShowAIWritingPrompt(action, question, current ? scope?.SelectedItem as string : null);
            }, insert, copyText));
            return "Suggestion ready for review.";
        }, HideOverlay, () => ShowAIConnectionsAsync(this), SelectAIConnection, WritingShortcut(),
            (profileId, question) =>
            {
                if (!TargetIsCurrent()) return "The note changed. Reopen Write with AI.";
                var range = Range();
                if (range.Length > AIConnectionClient.MaximumMessageCharacters) return "Choose a smaller scope: the request exceeds 32,768 characters.";
                return _aiWorkspace.WritingValidationError(profileId, Message(question, snapshot.Text.Substring(range.Start, range.Length)));
            }, scope: scope, initialAction: initialAction, changeAction: selected => action = selected, writingOnly: true,
            isEmptyDraft: snapshot.Text.Length == 0, initialInstruction: instruction);
        panel.MaxHeight = Math.Max(250, Body.Bounds.Height - 40);
        ShowOverlay(panel);
    }

    private async void CopyAIResult(string text)
    {
        try
        {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        { _shell.StatusMessage = "Could not copy the AI result: " + ex.Message; }
    }

    private string WritingShortcut() => Gesture.Describe(_shell.Registry.GetGesture(CommandIds.AskAI)) is { Length: > 0 } shortcut ? shortcut : "Edit → Write with AI";

    private Task<string> AnswerGuideQuestionAsync(string question, CancellationToken token)
    {
        if (_aiWorkspace.SelectedConnection is not { } profile)
            throw new InvalidOperationException("Choose an AI connection on Tern's main screen. Add one in Edit → Settings → AI connections if you have not set one up yet.");
        var message = GuideQuestionPrompt.Build(_shell.Registry, question, forCopilot: profile.Kind == "copilot");
        return _aiWorkspace.SendAsync(profile.Id, message, token);
    }
}
