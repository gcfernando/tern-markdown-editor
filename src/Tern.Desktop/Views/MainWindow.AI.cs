using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Tern.App.Commands;
using Tern.App.Documents;
using Tern.App.Shell;
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
            AIBar.IsVisible = true;
            UpdateAIEntryCaption();
            AIConnectionAction.IsEnabled = _aiWorkspace.SelectedConnection is not null && !_shell.Settings.OfflineMode;
            ToolTip.SetTip(AIConnectionAction, _shell.Settings.OfflineMode ? "Offline mode blocks AI and MCP. Turn Offline off to connect." : "Use the selected connection");
            ToolTip.SetShowOnDisabled(AIConnectionAction, true);
            AIConnectionAction.Content = _aiWorkspace.SelectedConnection is { Kind: "mcp" } ? "Open MCP tools" : "Write with AI";
            Avalonia.Automation.AutomationProperties.SetName(AIConnectionAction, AIConnectionAction.Content.ToString());
            RefreshAISummary();
            foreach (var panel in OverlayLayer.Children.OfType<AIPromptPanel>()) panel.RefreshConnections(_aiWorkspace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        { _shell.StatusMessage = "AI connections could not be loaded: " + _shell.PlainProblem(ex); }
        finally { _refreshingAI = false; }
    }

    private void RefreshAISummary()
    {
        var summary = _aiWorkspace.SelectedConnection is { } profile ? _aiWorkspace.Summary(profile) : null;
        AIConnectionSummary.Text = summary is null
            ? "Add a connection to start writing or asking questions."
            : $"{summary.Capabilities} · Model: {summary.Model}\n{summary.State}"
                + (summary.TestedAt is { } time ? $" · {time.LocalDateTime:g}" : "");
        AIConnectionDetails.IsVisible = summary is not null;
        AIConnectionDetailsText.Text = summary?.Description ?? string.Empty;
    }

    private void UpdateAIEntryCaption()
    {
        var profile = _aiWorkspace.SelectedConnection;
        var tight = Width < 1180 * _shell.Settings.UiScale && _shell.Settings.UiScale >= 1.5;
        AIEntryButton.Content = tight ? "AI" : profile is null ? "AI · set up" : $"AI · {profile.Name} · {(profile.CanWrite ? "Text" : "Tools")}";
        ToolTip.SetTip(AIEntryButton, profile?.DisplaySummary ?? "AI connection setup and optional writing tools");
    }

    private void ObserveAIConnections()
    {
        _aiWorkspace.SelectionChanged += OnGlobalAISelectionChanged;
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
        { _shell.StatusMessage = "AI connections will refresh when Settings closes; live file updates are unavailable: " + _shell.PlainProblem(ex); }
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
        _aiWorkspace.SelectionChanged -= OnGlobalAISelectionChanged;
        AIConnectionManager.ConnectionsChanged -= OnSavedAIConnectionsChanged;
        IntegrationProfileStore.ConnectionsChanged -= OnSavedAIConnectionsChanged;
        _aiReloadTimer.Stop();
        _aiWatcher?.Dispose();
    }

    private void OnGlobalAISelectionChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) { if (!_windowClosed) ReloadAIConnections(); }
        else QueueAIRefresh();
    }

    private void OnAIConnectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingAI && AIConnectionPicker.SelectedItem is WorkspaceConnection profile) SelectAIConnection(profile.Id);
    }

    private void SelectAIConnection(string id)
    {
        try { _aiWorkspace.Select(id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { _shell.StatusMessage = "The AI choice is active for this session, but could not be saved: " + _shell.PlainProblem(ex); }
        ReloadAIConnections();
    }

    private async void OnAIPrimaryActionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _aiEntryFlyout?.Hide();
        if (_aiWorkspace.SelectedConnection is { Kind: "mcp" } profile) await ShowMCPToolsAsync(profile.Id);
        else ShowAIWritingPrompt();
    }

    private async Task ShowMCPToolsAsync(string profileId)
    {
        if (!RequireNetwork()) return;
        HideOverlay();
        await new MCPConnectionsWindow(new MCPConnectionClient { NetworkAllowed = () => !_shell.Settings.OfflineMode }, _shell.Paths.SettingsDirectory, selectedProfileId: profileId, useTools: true).ShowDialog(this);
        ReloadAIConnections();
    }

    private async Task ShowAIConnectionsAsync(Window owner)
    {
        if (!RequireNetwork()) return;
        await new AIConnectionsWindow(_shell.Paths.SettingsDirectory,
            openCopilot: parent => new CopilotConnectionWindow(_shell.Paths.SettingsDirectory).ShowDialog(parent),
            writingShortcut: WritingShortcut()).ShowDialog(owner);
        ReloadAIConnections();
    }


    internal void ShowAIWritingPrompt(string? initialAction = null, string? instruction = null, string? preferredScope = null)
    {
        if (!RequireNetwork()) return;
        if (_shown is not { IsReadOnly: false } tab || !CanRunCommand(CommandIds.AskAI)) return;
        ReloadAIConnections();
        HideOverlay();
        var snapshot = tab.Session.GetSnapshot();
        var selection = (Start: Editor.SelectionStart, Length: Editor.IsEffectivelyVisible ? Editor.SelectionLength : 0);
        var caret = Editor.CaretOffset;
        // A selection is the user's stated target, so the whole note is offered only without one.
        var scopes = selection.Length > 0 ? new[] { "Selected text", "Instruction only" } : new[] { "Entire document", "Instruction only" };
        var scope = new ComboBox { ItemsSource = scopes, SelectedIndex = Math.Max(0, Array.IndexOf(scopes, preferredScope)) };
        AutomationProperties.SetName(scope, "AI text scope");
        var action = initialAction;
        (int Start, int Length) Range() => Equals(scope.SelectedItem, "Instruction only") ? (caret, 0)
            : Equals(scope.SelectedItem, "Selected text") ? selection : (0, snapshot.Text.Length);
        bool TargetIsCurrent() => !_windowClosed && _shell.Tabs.Contains(tab) && ReferenceEquals(_shell.ActiveTab, tab)
            && !tab.IsReadOnly && tab.Session.IsCurrent(snapshot.Revision);
        bool StandaloneResult() => Equals(scope.SelectedItem, "Instruction only") || action is "Summarize" or "Explain" or "Extract action items";
        // Selected text is always replaced after review; a whole-note summary is inserted so it cannot overwrite the note.
        bool InsertsResult() => StandaloneResult() && !Equals(scope.SelectedItem, "Selected text");
        Tern.App.AIRequest Message(string profileId, string question, string text) =>
            AIWorkspaceService.WritingRequest(question, _aiWorkspace.PrepareNoteText(profileId, text), StandaloneResult());
        void ReviewAtCurrentCursor(string result)
        {
            if (_windowClosed || _shown is not { IsReadOnly: false } target || !_shell.Tabs.Contains(target))
            {
                _shell.SetStatus("Open an editable note before inserting the AI result. Copy remains available.", StatusSeverity.Warning);
                return;
            }
            var currentSnapshot = target.Session.GetSnapshot();
            var offset = Math.Clamp(Editor.CaretOffset, 0, currentSnapshot.Text.Length);
            if (offset > 0 && offset < currentSnapshot.Text.Length && currentSnapshot.Text[offset - 1] == '\r' && currentSnapshot.Text[offset] == '\n')
                offset++;
            var text = result.ReplaceLineEndings(target.Session.Format.NewLineText);
            bool CurrentTarget() => !_windowClosed && _shell.Tabs.Contains(target) && ReferenceEquals(_shown, target)
                && !target.IsReadOnly && target.Session.IsCurrent(currentSnapshot.Revision);
            ShowWritingReview(new AIEditReview(currentSnapshot.Text, text, offset, edits =>
            {
                if (!CurrentTarget()) return false;
                ContextFor(target).Buffer.Apply(edits);
                _shell.SetStatus("AI result inserted. Undo to restore the previous draft.", StatusSeverity.Success);
                return true;
            }, HideOverlay, CopyAIResult, () => ShowAIWritingPrompt(),
                insertResult: true, copyText: text, canApply: CurrentTarget, reanchorInsert: () => ReviewAtCurrentCursor(result)), target);
            _shell.SetStatus($"Review insertion into {target.DisplayName} at the current cursor. Nothing has been applied.", StatusSeverity.Info);
        }
        AIPromptPanel? panel = null;
        panel = new AIPromptPanel(tab.DisplayName, _aiWorkspace, async (profileId, question, token) =>
        {
            if (!TargetIsCurrent())
                throw new InvalidOperationException("The note changed. Reopen Write with AI.");
            var range = Range();
            var original = snapshot.Text.Substring(range.Start, range.Length);
            var insert = InsertsResult();
            string response;
            var copilot = _aiWorkspace.Connections.Any(connection => connection.Id == profileId && connection.Kind == "copilot");
            var activity = panel?.CurrentActivity;
            try { response = await _aiWorkspace.SendWritingAsync(profileId, Message(profileId, question, original), TargetIsCurrent, token,
                new Progress<string>(phase => activity?.Report(phase))); }
            finally { if (!_windowClosed) RefreshAISummary(); }
            token.ThrowIfCancellationRequested();
            activity?.Report("Checking the returned Markdown before review");
            var newline = tab.Session.Format.NewLineText;
            var markdown = AIWorkspaceService.MarkdownResponse(copilot ? AIWorkspaceService.RestoreNoteText(response) : response).ReplaceLineEndings(newline);
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
            else
            {
                if (StandaloneResult()) markdown = markdown.TrimStart('\r', '\n').TrimEnd();
                if (range.Start + range.Length < snapshot.Text.Length && original.EndsWith('\n') && !markdown.EndsWith('\n'))
                    markdown += newline;
            }
            ShowWritingReview(new AIEditReview(original, markdown, offset, edits =>
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
            }, insert, copyText, canApply: TargetIsCurrent, reanchorInsert: () => ReviewAtCurrentCursor(copyText ?? markdown)), tab);
            return "Suggestion ready for review.";
        }, HideOverlay, () => ShowAIConnectionsAsync(this), SelectAIConnection, WritingShortcut(),
            (profileId, question) =>
            {
                if (!TargetIsCurrent()) return "The note changed. Reopen Write with AI.";
                var range = Range();
                return _aiWorkspace.WritingValidationError(profileId, Message(profileId, question, snapshot.Text.Substring(range.Start, range.Length)));
            }, scope: scope, initialAction: initialAction, changeAction: selected => action = selected, writingOnly: true,
            isEmptyDraft: snapshot.Text.Length == 0, initialInstruction: instruction, activities: _shell.Activities);
        panel.MaxHeight = Math.Max(250, Body.Bounds.Height - 40);
        ShowOverlay(panel);
    }

    private void ShowWritingReview(AIEditReview review, DocumentTab target)
    {
        HideOverlay();
        review.Activities = _shell.Activities;
        review.ActivityIdentity = target.DisplayName;
        void Changed(object? sender, EventArgs e) => review.RefreshTargetState();
        void ShellChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ShellViewModel.ActiveTab)) review.RefreshTargetState();
        }
        target.Session.StateChanged += Changed;
        _shell.PropertyChanged += ShellChanged;
        review.DetachedFromVisualTree += (_, _) =>
        {
            target.Session.StateChanged -= Changed;
            _shell.PropertyChanged -= ShellChanged;
        };
        ShowOverlay(review);
        review.RefreshTargetState();
    }

    private async void CopyAIResult(string text)
    {
        try
        {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        { _shell.StatusMessage = "Could not copy the AI result: " + _shell.PlainProblem(ex); }
    }

    private string WritingShortcut() => Gesture.Describe(_shell.Registry.GetGesture(CommandIds.AskAI)) is { Length: > 0 } shortcut ? shortcut : "Edit → Write with AI";

    private async Task<string> AnswerGuideQuestionAsync(string question, CancellationToken token)
    {
        if (!GuideQuestionPrompt.CanAnswer(_shell.Registry, question)) return GuideQuestionPrompt.ScopeMessage;
        if (_aiWorkspace.HelpSelectedConnection is not { } profile)
            return "Installed Tern guide · offline answer\n\n" + GuideQuestionPrompt.OfflineAnswer(_shell.Registry, question);
        var message = GuideQuestionPrompt.BuildRequest(_shell.Registry, question, forCopilot: profile.Kind == "copilot", _aiWorkspace.SendCharactersFor(profile.Id));
        try { return await _aiWorkspace.SendAsync(profile.Id, message, token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return "Provider timed out. Installed Tern guide · offline answer\n\n" + GuideQuestionPrompt.OfflineAnswer(_shell.Registry, question); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            token.ThrowIfCancellationRequested();
            return "AI unavailable. Recovered from the installed Tern guide · offline answer\n\n" + GuideQuestionPrompt.OfflineAnswer(_shell.Registry, question);
        }
    }
}
