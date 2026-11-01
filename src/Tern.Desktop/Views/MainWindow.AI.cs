using Avalonia.Controls;
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
            AIConnectionSummary.Text = _aiWorkspace.SelectedConnection is { } profile
                ? profile.Summary
                : "Set up once in Settings, then ask from any writing pane.";
            foreach (var panel in OverlayLayer.Children.OfType<AIPromptPanel>()) panel.RefreshConnections(_aiWorkspace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        { _shell.StatusMessage = "AI connections could not be loaded: " + ex.Message; }
        finally { _refreshingAI = false; }
    }

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

    internal void ShowAIWritingPrompt()
    {
        if (_aiWorkspace.SelectedConnection is { Kind: "mcp" } tools) { _ = ShowMCPToolsAsync(tools.Id); return; }
        if (_shown is not { IsReadOnly: false } tab || !CanRunCommand(CommandIds.AskAI)) return;
        ReloadAIConnections();
        HideOverlay();
        var panel = new AIPromptPanel(tab.DisplayName, _aiWorkspace, async (profileId, question, token) =>
        {
            bool TargetIsCurrent() => !_windowClosed && _shell.Tabs.Contains(tab) && ReferenceEquals(_shell.ActiveTab, tab) && !tab.IsReadOnly;
            if (!TargetIsCurrent()) throw new InvalidOperationException("Return to the intended editable note before generating. Nothing has been sent.");
            var snapshot = tab.Session.GetSnapshot();
            if (snapshot.Text.Length + question.Length > AIConnectionClient.MaximumMessageCharacters)
                throw new InvalidOperationException("This note and question exceed the connection's 32,768-character request limit. Use a smaller note or a shorter question.");
            var message = AIWorkspaceService.WritingMessage(question, snapshot.Text);
            if (message.Length > AIConnectionClient.MaximumMessageCharacters)
                throw new InvalidOperationException("This note and question exceed the connection's 32,768-character request limit. Use a smaller note or a shorter question.");
            var response = await _aiWorkspace.SendWritingAsync(profileId, message,
                () => TargetIsCurrent() && tab.Session.IsCurrent(snapshot.Revision), token);
            token.ThrowIfCancellationRequested();
            if (_windowClosed || !_shell.Tabs.Contains(tab) || !ReferenceEquals(_shell.ActiveTab, tab)
                || tab.IsReadOnly || !tab.Session.IsCurrent(snapshot.Revision))
                throw new InvalidOperationException("The active note changed while AI was working. Its text has been kept. Return to the intended note and ask again.");
            var markdown = AIWorkspaceService.MarkdownResponse(response).ReplaceLineEndings(tab.Session.Format.NewLineText);
            if (string.IsNullOrWhiteSpace(markdown)) throw new InvalidOperationException("AI returned no Markdown. Your note has not changed.");
            if (markdown != snapshot.Text) ContextFor(tab).Buffer.Apply([new TextEdit(0, snapshot.Text.Length, markdown)]);
            _shell.StatusMessage = "AI updated “" + tab.DisplayName + "”. Undo to restore the previous draft; save when ready.";
            return _shell.StatusMessage;
        }, HideOverlay, () => ShowSettingsAsync("AI connections"), SelectAIConnection, WritingShortcut(),
            question => tab.CurrentText.Length + question.Length > AIConnectionClient.MaximumMessageCharacters
                ? "The note and question exceed this connection's request limit. A shorter note is needed; nothing has been sent."
                : AIWorkspaceService.WritingMessage(question, tab.CurrentText),
            (profileId, question) =>
            {
                if (_windowClosed || !_shell.Tabs.Contains(tab) || !ReferenceEquals(_shell.ActiveTab, tab) || tab.IsReadOnly)
                    return "Return to the intended editable note before generating.";
                if (tab.CurrentText.Length + question.Length > AIConnectionClient.MaximumMessageCharacters)
                    return "Shorten the note or question: the complete request exceeds 32,768 characters.";
                return _aiWorkspace.WritingValidationError(profileId, AIWorkspaceService.WritingMessage(question, tab.CurrentText));
            }, openTools: ShowMCPToolsAsync);
        panel.MaxHeight = Math.Max(250, Body.Bounds.Height - 40);
        OverlayLayer.Children.Add(panel);
        OverlayLayer.IsVisible = true;
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
