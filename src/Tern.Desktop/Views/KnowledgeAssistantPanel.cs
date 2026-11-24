using System.Collections.Specialized;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Tern.App.Knowledge;
using Tern.App.Shell;
using Tern.Desktop.Services;
using Tern.Workspaces;

namespace Tern.Desktop.Views;

/// <summary>Explore your notes: ask a question, keep the right notes, then get and share a checked answer.</summary>
internal sealed partial class KnowledgeAssistantPanel : Border, IDisposable
{
    private const string AnswerTask = "Answer question";
    private const int VisibleNotes = 3;
    private const string AiSuggestedNote = "AI suggests this for your question";
    private const string OfflineReason = "Explore your notes needs AI, and Offline mode is on. Press Open Settings and turn off Offline mode.";
    private readonly ShellViewModel _shell;
    private readonly AIWorkspaceService _ai;
    private ActivityScope? _operationActivity;
    private readonly Func<string, int, Task> _open;
    private readonly Action<KnowledgeAnswer, string?, Func<bool>, Func<CancellationToken, Task>> _review;
    private readonly Func<Task>? _openFolder;
    private readonly Func<Task>? _showSettings;
    private readonly KnowledgeRetrieval _retrieval = new();
    private readonly KnowledgeConversation _conversation = new();
    private readonly ComboBox _scope = new() { MinWidth = 160 };
    private readonly TextBox _question = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinLines = 10, MaxLines = 16, MaxLength = 1500, PlaceholderText = "For example: When is the project launch?" };
    private readonly TextBlock _status = GuideParts.Body("");
    private readonly MessageBar _statusBar;
    private readonly Button _find = new() { Content = "Find my notes" };
    private readonly Button _send = new() { Content = "Get my answer" };
    private readonly Button _cancel = new() { Content = "Cancel", IsEnabled = false };
    private readonly Button _configure = new() { Content = "Set up AI" };
    private readonly Button _openSettings = new() { Content = "Open Settings", IsVisible = false };
    private readonly List<(KnowledgeSource Source, CheckBox Include)> _choices = [];
    private readonly HashSet<WorkspaceSession> _observed = [];
    // The person's own ticks and added notes. A new search in this conversation never overrides them.
    private readonly Dictionary<string, bool> _userChoices = new(WorkspacePaths.Comparer);
    private readonly List<string> _addedNotes = [];
    private KnowledgeCoverage _coverage = KnowledgeCoverage.Unknown;
    private CancellationTokenSource? _work;
    private KnowledgeRequest? _request;
    private IReadOnlyList<KnowledgeSource> _answerSources = [];
    private IReadOnlyList<KnowledgeRelatedNote> _foundRelated = [];
    private string _task = AnswerTask;
    private string _starterPrefix = "";
    private string? _vagueChecked;
    private string? _connectionProblem;
    private bool _sending;
    private bool _operationUsesAI;
    private bool _disposed;
    private bool _refreshing;
    private bool _preparedRefreshQueued;
    private long _generation;
    private long _sourceRevision;

    public KnowledgeAssistantPanel(ShellViewModel shell, AIWorkspaceService ai, Func<string, int, Task> open,
        Action<KnowledgeAnswer, string?, Func<bool>, Func<CancellationToken, Task>> review, Func<Task> configure, Func<Task>? openFolder = null, Func<Task>? openSettings = null)
    {
        _shell = shell; _ai = ai; _open = open; _review = review; _openFolder = openFolder; _showSettings = openSettings;
        _taskStore = ExploreTaskStore.For(shell);
        this[!BackgroundProperty] = this.GetResourceObservable("Tern.Panel").ToBinding();
        this[!BorderBrushProperty] = this.GetResourceObservable("Tern.ControlBorder").ToBinding();
        AutomationProperties.SetName(this, "Explore your notes");
        NameControl(_scope, "Search in"); NameControl(_question, "Your question"); NameControl(_openSettings, "Open Settings");
        NameControl(_status, "Explore status"); NameControl(_find, "Find my notes");
        NameControl(_send, "Get my answer"); NameControl(_cancel, "Cancel");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _scope.ItemTemplate = new FuncDataTemplate<WorkspaceSession>((root, _) => new TextBlock
        { Text = string.IsNullOrWhiteSpace(root?.Name) ? root?.Root : root.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        _statusBar = new MessageBar(_status, followText: true);
        BuildLayout();
        _scope.SelectionChanged += (_, _) => { if (!_refreshing) SwitchTask(); };
        _question.PropertyChanged += (_, e) =>
        {
            if (_disposed || e.Property != TextBox.TextProperty || _refreshing) return;
            _generation++; _work?.Cancel(); _retryStep = null;
            if (_conversation.Turns.Count > 0 || _draft is not null)
            {
                ClearAnswer(); _retainedDraft = null; _insertionPending = false;
                if (Scope is { } root) _conversation.Start(root.Root);
            }
            if (_starterPrefix.Length == 0 || _question.Text?.StartsWith(_starterPrefix, StringComparison.Ordinal) != true) { _task = AnswerTask; _starterPrefix = ""; }
            _vagueChecked = null;
            ClearAskSuggestions();
            ClearSelection();
            if (_stage != AskStage) ShowStage(AskStage);
            RefreshAvailability();
            QueueCheckpoint();
        };
        _find.Click += async (_, _) => await RunAsync(FindAsync);
        _send.Click += async (_, _) => await RunAsync(SendAsync);
        _cancel.Click += (_, _) => { _work?.Cancel(); _status.Text = "Stopping…"; };
        _configure.Click += async (_, _) => await configure();
        _openSettings.Click += async (_, _) => { if (_showSettings is { } show) await show(); };
        _question.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control)
            { e.Handled = true; if (_find.IsEnabled) await RunAsync(FindAsync); }
            else if (e.Key == Key.Escape && _work is not null) { _work.Cancel(); e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _shell.OpenWorkspaces.CollectionChanged += OnWorkspacesChanged;
        _ai.Reloaded += OnConnectionsChanged;
        RefreshWorkspaces();
        RefreshConnections();
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => { if (!_disposed) _question.Focus(); });
    }

    private static void NameControl(Control control, string name) => AutomationProperties.SetName(control, name);
    private KnowledgeLimits Limits => new(SendCharacters: Model is { } connection ? _ai.SendCharactersFor(connection.Id) : KnowledgeLimits.DefaultSendCharacters);

    /// <summary>Progress runs on the UI thread in order, so a late update cannot replace the final result message.</summary>
    private sealed class ReadProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private KnowledgeReader NewReader(WorkspaceConnection model, Func<bool> current)
    {
        var activity = _operationActivity;
        return new KnowledgeReader(model.Name, _ai.SendCharactersFor(model.Id),
            (_, _) => throw new InvalidOperationException("This AI step requires a current-task request."),
            AIWorkspaceService.IsSizeRejection,
            new ReadProgress(text => { if (!_disposed && _work is not null) { _status.Text = text; activity?.Report(text.TrimEnd('…')); } }),
            askRequest: async (request, token) =>
            {
                await ValidateTaskEvidenceAsync(token);
                if (!current()) throw new InvalidOperationException("This Explore task changed. Review it and try again.");
                var reply = await _ai.SendWritingAsync(model.Id, _ai.PrepareNoteText(model.Id, request), current, token, skipCopilotCheck: true);
                token.ThrowIfCancellationRequested();
                if (!current()) throw new InvalidOperationException("This Explore task changed. Review it and try again.");
                return AIWorkspaceService.RestoreNoteText(reply);
            });
    }
    private WorkspaceSession? Scope => _scope.SelectedItem as WorkspaceSession;
    private bool ScopeCurrent(WorkspaceSession root) => !_disposed && _shell.Settings.KnowledgeAssistantEnabled && ReferenceEquals(Scope, root) && _shell.OpenWorkspaces.Contains(root);
    private bool CanTransmit(WorkspaceConnection model) => !_shell.Settings.OfflineMode && (model.Location == "Local" || _shell.Settings.KnowledgeAllowRemote);
    /// <summary>The AI chosen in Tern's one global AI selector.</summary>
    private WorkspaceConnection? Model => _ai.SelectedConnection is { CanWrite: true } model ? model : null;
    private string QuestionText => _question.Text?.Trim() ?? "";
    private bool HasPlaceholder => QuestionText.Contains('[') && QuestionText.IndexOf(']') > QuestionText.IndexOf('[');

    private void OnWorkspacesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshWorkspaces();
    private void OnConnectionsChanged(object? sender, EventArgs e) => RefreshConnections();
    private void OnSourcesChanged(object? sender, EventArgs e)
    {
        _sourceRevision++;
        if (_work is null && HasPendingTask && ReferenceEquals(sender, Scope)) _ = InspectRestoredEvidenceAsync();
        if (_preparedRefreshQueued || _stage != ChooseStage || _work is not null) return;
        // Indexing reports many changes at once. Each rebuild reads saved credentials, so do it once per batch.
        _preparedRefreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _preparedRefreshQueued = false;
            if (!_disposed && _stage == ChooseStage && _work is null) RefreshPrepared();
        }, DispatcherPriority.Background);
    }
    private void OnFilesChanged(object? sender, WorkspaceChangeSet e) => OnSourcesChanged(sender, EventArgs.Empty);
    private void OnContentStatusChanged(object? sender, EventArgs e) { if (!_disposed && ReferenceEquals(sender, Scope)) RefreshAvailability(); }

    private void RefreshWorkspaces()
    {
        var previous = Scope;
        foreach (var root in _observed.Where(root => !_shell.OpenWorkspaces.Contains(root)).ToArray())
        { root.FilesChanged -= OnFilesChanged; root.GraphChanged -= OnSourcesChanged; root.ContentStatusChanged -= OnContentStatusChanged; _observed.Remove(root); }
        foreach (var root in _shell.OpenWorkspaces.Where(root => !_observed.Contains(root)))
        { root.FilesChanged += OnFilesChanged; root.GraphChanged += OnSourcesChanged; root.ContentStatusChanged += OnContentStatusChanged; _observed.Add(root); }
        _refreshing = true;
        _scope.ItemsSource = _shell.OpenWorkspaces.ToArray();
        _scope.SelectedItem = previous is not null && _shell.OpenWorkspaces.Contains(previous) ? previous
            : _taskStore.ResumeWorkspace(_shell.OpenWorkspaces) ?? _shell.Workspace ?? _shell.OpenWorkspaces.FirstOrDefault();
        _refreshing = false;
        if (!ReferenceEquals(previous, Scope)) SwitchTask();
    }

    private void RefreshConnections() { CheckConnection(); RefreshPrepared(); }

    /// <summary>Finds AI setup problems when the screen opens, not after the user presses a button.</summary>
    private void CheckConnection() => _connectionProblem = Model is { } model && !_shell.Settings.OfflineMode
        && CannotSend(model.Id, "Check") ? "Your AI connection needs attention. Press Fix AI connection." : null;

    /// <summary>A provider's "wait a moment" is not a broken connection or too much text. Sending reports the real wait time.</summary>
    private bool CannotSend(string modelId, string message) => _ai.WritingValidationError(modelId, message) is not null && !_ai.IsWaitingToRetry(modelId);

    private void StartConversation()
    {
        _generation++; _sourceRevision++; _work?.Cancel();
        if (Scope is { } root) _conversation.Start(root.Root); else _conversation.Clear();
        _refreshing = true; _question.Text = ""; _refreshing = false;
        _task = AnswerTask; _starterPrefix = ""; _vagueChecked = null;
        _userChoices.Clear(); _addedNotes.Clear(); _coverage = KnowledgeCoverage.Unknown;
        ClearAskSuggestions();
        ClearAnswer();
        ClearSelection();
        _status.Text = "";
        ShowStage(AskStage);
    }

    private void ClearSelection()
    {
        _request = null; _choices.Clear(); _noteRows.Children.Clear(); _moreRows.Children.Clear(); _moreRows.IsVisible = false; _showMore.IsVisible = false;
        RefreshAvailability();
    }

    public void RefreshSettings()
    {
        RefreshMotion();
        CheckConnection();
        // Owner activation also refreshes settings. It must not cancel a current task
        // merely because an older Explore window closed or the person returned to Tern.
        if (_operationUsesAI && (!_shell.Settings.KnowledgeAssistantEnabled || _shell.Settings.OfflineMode || Model is not { } model || !CanTransmit(model)))
            _work?.Cancel();
        if (_work is null) RefreshPrepared();
        else RefreshAvailability(); // Keep the reviewed request and its identity during an active send.
    }

    private void RefreshPrepared()
    {
        _request = null;
        var model = Model;
        _usingAI.Text = model is null ? "No AI selected" : "Using " + model.Name;
        var selected = _choices.Where(choice => choice.Include.IsChecked == true).Select(choice => choice.Source).ToArray();
        if (selected.Length > 0 && Scope is not null && _stage == ChooseStage)
        {
            try
            {
                // Full notes are read in parts when needed, so the amount of text never blocks this step.
                _request = _conversation.PrepareReading(QuestionText, _task, selected, Limits);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { _request = null; }
        }
        RefreshAvailability();
    }

    private void RefreshAvailability()
    {
        var busy = _work is not null;
        var model = Model;
        _scope.IsEnabled = _question.IsEnabled = !busy;
        foreach (var starter in _starters) starter.IsEnabled = !busy;
        var root = Scope;
        var (askTone, askText, askBlocks) = AskMessage(root, model);
        _find.IsEnabled = !busy && !askBlocks;
        _question.Classes.Set("tern-warn", HasPlaceholder);
        ShowMessage(_askBar, !busy && _stage == AskStage, askTone, askText);
        var offline = _shell.Settings.OfflineMode;
        _configure.IsVisible = !offline && (model is null || _connectionProblem is not null);
        _openSettings.IsVisible = offline && _showSettings is not null;
        _openSettings.IsEnabled = !busy;
        _searchAnyway.IsEnabled = _find.IsEnabled;
        _configure.Content = model is null ? "Set up AI" : "Fix AI connection";
        _configure.IsEnabled = !busy;
        RefreshFolderHelp(root);

        var selected = _choices.Count(choice => choice.Include.IsChecked == true);
        foreach (var choice in _choices) choice.Include.IsEnabled = !busy;
        _selectedCount.Text = $"{selected} of {_choices.Count} {(_choices.Count == 1 ? "note" : "notes")} ticked";
        _selectAll.IsEnabled = !busy && selected < _choices.Count;
        _selectNone.IsEnabled = !busy && selected > 0;
        ToolTip.SetTip(_selectAll, selected == _choices.Count ? "All notes are already ticked." : "Tick every note.");
        ToolTip.SetTip(_selectNone, selected == 0 ? "No notes are ticked yet." : "Untick every note.");
        var online = model is not null && model.Location != "Local";
        var needsPermission = online && !_shell.Settings.OfflineMode && !_shell.Settings.KnowledgeAllowRemote;
        _allowOnline.IsVisible = needsPermission && _stage == ChooseStage;
        _allowOnline.IsEnabled = !busy;
        var sendBlock = _evidenceNeedsRefresh ? "Your notes changed. Press Refresh notes before using this task."
            : _shell.Settings.OfflineMode ? OfflineReason
            : model is null ? "Add an AI connection to continue."
            : _connectionProblem is { } connection ? connection
            : needsPermission ? "Tern needs your OK to send the ticked notes to " + model.Name + "."
            : selected == 0 ? "Tick at least one note to get an answer."
            : _request is null ? "Tern could not prepare these notes. Press Back, then Find my notes again."
            : null;
        _send.IsEnabled = !busy && _stage == ChooseStage && sendBlock is null && model is not null && CanTransmit(model);
        ShowMessage(_chooseBar, !busy && _stage == ChooseStage, _connectionProblem is not null ? MessageTone.Error : MessageTone.Warning, sendBlock);
        _cancel.IsEnabled = busy;
        _cancel.IsVisible = busy;
        _newQuestion.IsEnabled = _back.IsEnabled = _otherFolder.IsEnabled = !busy;
        _done.IsEnabled = !busy && HasPendingTask;
        _retry.IsVisible = _retryStep is not null;
        _retry.IsEnabled = !busy && !_shell.Settings.OfflineMode && Model is not null && !_evidenceNeedsRefresh;
        _refreshNotes.IsVisible = _evidenceNeedsRefresh;
        _refreshNotes.IsEnabled = !busy && !_shell.Settings.OfflineMode && Model is not null;
        foreach (var suggestion in _suggestionButtons) suggestion.IsEnabled = !busy;
        RefreshShare();
        RefreshReportAvailability();
    }

    private (MessageTone Tone, string? Text, bool Blocks) AskMessage(WorkspaceSession? root, WorkspaceConnection? model)
    {
        // AI is required, so Offline mode stops here, before the user types or chooses anything.
        if (_shell.Settings.OfflineMode) return (MessageTone.Warning, OfflineReason, true);
        if (root is null) return (MessageTone.Warning, "Open a folder of notes first.", true);
        if (!root.HasContentIndex) return (MessageTone.Warning, "This folder needs one quick setup step first.", true);
        if (model is null) return (MessageTone.Warning, "Add an AI connection to continue.", true);
        if (_connectionProblem is { } connection) return (MessageTone.Error, connection, true);
        if (QuestionText.Length == 0) return (MessageTone.Info, "Type a question or choose a suggestion below.", true);
        if (HasPlaceholder) return (MessageTone.Warning, "Replace the words in [brackets] with your own.", true);
        if (QuestionText.Length >= 1400) return (MessageTone.Warning, "Your question is nearly too long. Keep it short for the best answer.", false);
        return KnowledgeRetrieval.IsVague(QuestionText)
            ? (MessageTone.Warning, "This question is broad. Add a name, date or project for a better answer.", false)
            : (MessageTone.Info, null, false);
    }

    private static void ShowMessage(MessageBar bar, bool visible, MessageTone tone, string? text)
    {
        if (visible && !string.IsNullOrEmpty(text)) bar.Show(tone, text); else bar.Clear();
    }

    private void RefreshFolderHelp(WorkspaceSession? root)
    {
        if (root is null) SetFolderHelp("Open your notes first", "Choose the folder that holds your notes. Tern never moves them.", "Open a folder", NextAction.OpenFolder);
        else if (!root.HasContentIndex) SetFolderHelp("Make this folder ready", $"“{root.Name}” needs one quick setup so AI can read it. Your notes are not moved or changed. Tern adds one small file, {WorkspaceDefinition.FileName}.", "Make this folder ready", NextAction.Promote);
        else if (root.IsPreparingContent) SetFolderHelp("Getting your notes ready", (root.ContentProgress is { Total: > 0 } p ? $"{p.Processed:N0} of {p.Total:N0} notes ready. " : "") + "You can ask now, but some notes may not appear yet.", null, NextAction.None);
        else if (!root.IsContentReady) SetFolderHelp("Some notes are not ready", "Tern stopped before reading every note. Press Finish to include them.", "Finish", NextAction.Resume);
        else SetFolderHelp("", "", null, NextAction.None);
        _nextCard.IsVisible = _nextTitle.Text is { Length: > 0 };
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_disposed || _work is not null) return;
        using var operation = new CancellationTokenSource();
        _work = operation;
        _operationUsesAI = action.Method.Name is nameof(FindAsync) or nameof(SendAsync) or nameof(CorrectReportAsync) or nameof(PrepareCurrentContentAsync)
            || action.Method.Name == nameof(FinishReportAsync) && !ContentReady || action.Method.Name == nameof(ShareAsync) && _draft is null;
        _retryStep = action.Method.Name switch { nameof(FindAsync) => "find", nameof(SendAsync) => "answer", nameof(ShareAsync) => "share", nameof(FinishReportAsync) => "finish", nameof(CorrectReportAsync) => "revise", _ => null };
        SaveCheckpoint(interrupted: true);
        using var activity = _shell.Activities.Begin("Explore your notes", Scope?.Name, cancel: operation.Cancel);
        _operationActivity = activity;
        _loading.Content = new LoadingState(_shell.Activities, activity.Id);
        var generation = _generation;
        _status.Text = "";
        RefreshAvailability();
        try
        {
            await action(operation.Token);
            if (operation.IsCancellationRequested || _disposed || generation != _generation)
                activity.Cancel("Stopped. Your notes were not changed.");
            else activity.Complete();
            if (!operation.IsCancellationRequested && !_disposed && generation == _generation) _retryStep = null;
        }
        catch (OperationCanceledException)
        {
            if (operation.IsCancellationRequested) activity.Cancel("Stopped. Your notes were not changed.");
            else activity.Fail("This took too long. Your notes were not changed.");
            if (!_disposed && generation == _generation) _status.Text = operation.IsCancellationRequested
                ? "Cancelled. Your notes were not changed."
                : "Could not finish because it took too long. Check your internet connection, then try again.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException
            or JsonException or TimeoutException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            var problem = UserProblem.Explain(ex, _sending ? "Could not get your answer" : "Could not finish this step");
            activity.Fail(problem.Why + " " + problem.Next);
            if (!_disposed && generation == _generation) _status.Text = problem.Title + ". " + problem.Why + " Your question is still here, so you can try again.";
        }
        catch (Exception ex) { activity.Fail(ex.Message); throw; }
        finally
        {
            _work = null;
            _sending = false;
            _operationUsesAI = false;
            _operationActivity = null;
            _loading.Content = null;
            if (!_disposed) { RefreshAvailability(); SaveCheckpoint(); }
        }
    }

    private async Task FindAsync(CancellationToken token)
    {
        if (_shell.Settings.OfflineMode) return;
        if (Scope is not { } root) throw new InvalidOperationException("Open a folder of notes first.");
        var question = QuestionText;
        _evidenceNeedsRefresh = false;
        ClearSelection();
        ClearAnswer();
        var generation = _generation;
        var operation = _work;
        var activity = _operationActivity;
        if (KnowledgeRetrieval.IsVague(question) && _vagueChecked != question)
        {
            _vagueChecked = question;
            _status.Text = "Finding a clearer way to ask…";
            var clearer = await SuggestAsync(question, token);
            if (!ScopeCurrent(root) || generation != _generation) return;
            _status.Text = "";
            ShowAskSuggestions(clearer);
            return;
        }
        _conversation.Start(root.Root);
        var progress = new Progress<string>(_ =>
        {
            if (!_disposed && generation == _generation && ReferenceEquals(_work, operation))
            { _status.Text = "Looking through your notes…"; activity?.Report("Looking through your notes"); }
        });
        var result = await _retrieval.SearchAsync(root, question, Limits, token, progress);
        token.ThrowIfCancellationRequested();
        if (!ScopeCurrent(root) || generation != _generation) return;
        // AI works on every question: it adds words with the same meaning. It sees the question only, never note text or file names.
        IReadOnlyList<string>? ideas = null;
        var aiHelpFailed = false;
        var aiNeedsPermission = Model is { } online && !CanTransmit(online);
        IReadOnlyList<KnowledgeSource>? aiSources = null;
        IReadOnlyList<KnowledgeShortlistNote> alsoRead = [];
        if (Model is { } helper && CanTransmit(helper))
        {
            _status.Text = "AI is finding the right notes for your question…";
            activity?.Report("AI is finding the right notes");
            var (terms, questions, meaningQuery, clarification, failed) = await AskMeaningAsync(question, token);
            ideas = questions; aiHelpFailed = failed;
            token.ThrowIfCancellationRequested();
            if (!ScopeCurrent(root) || generation != _generation) return;
            if (terms.Count > 0 || meaningQuery is not null)
            {
                var wider = await _retrieval.SearchAsync(root, question, Limits, token, progress, terms, meaningQuery: meaningQuery);
                token.ThrowIfCancellationRequested();
                if (!ScopeCurrent(root) || generation != _generation) return;
                if (wider.Sources.Count > 0 || wider.Shortlist.Count > 0) result = wider;
            }
            if (clarification is not null && result.Sources.Count == 0 && result.Shortlist.Count == 0)
            {
                ShowStage(AskStage); _status.Text = clarification; ShowAskSuggestions(questions); return;
            }
            if (result.Coverage.MissingSubject is null && result.Shortlist.Count > 0)
            {
                _status.Text = "AI is choosing the notes that fit…";
                activity?.Report("AI is choosing the notes that fit");
                var (chosen, also, selectionFailed) = await AskSelectionAsync(question, result.Shortlist, token);
                aiHelpFailed |= selectionFailed;
                token.ThrowIfCancellationRequested();
                if (!ScopeCurrent(root) || generation != _generation) return;
                alsoRead = also;
                if (chosen.Count > 0)
                {
                    var merged = await MergeAiChoiceAsync(root, question, result.Sources, chosen, token);
                    token.ThrowIfCancellationRequested();
                    if (!ScopeCurrent(root) || generation != _generation) return;
                    if (merged.Any(source => source.Recommended)) aiSources = merged;
                }
            }
        }
        _status.Text = "";
        _coverage = result.Coverage;
        var sources = (aiSources ?? result.Sources).ToList();
        foreach (var path in _addedNotes.Where(path => sources.All(source => !WorkspacePaths.PathsEqual(source.Path, path))).ToArray())
        {
            try { sources.Add(await _retrieval.ReadChosenNoteAsync(root, path, question, sources.Count + 1, Limits, token)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { _addedNotes.Remove(path); }
        }
        if (sources.Count == 0)
        {
            var (title, why, next) = result.Coverage.ExplainEmpty();
            if (aiHelpFailed) why += " AI could not help look for other words this time.";
            await ShowNotFoundAsync(why, question, token, title, next, ideas, result.Coverage.EmptyReason == KnowledgeEmptyReason.IndexIncomplete, askAi: !aiHelpFailed && !aiNeedsPermission);
            activity?.Complete(outcome: title + ". No note text was sent.");
            return;
        }
        _foundRelated = alsoRead.Select(note => new KnowledgeRelatedNote(note.Path, note.RelativePath, note.Title, AiSuggestedNote))
            .Concat(result.Related).Where(note => !KnowledgeRetrieval.IsHiddenResult(note.RelativePath, question))
            .DistinctBy(note => note.Path, WorkspacePaths.Comparer).ToArray();
        var index = 0;
        var shownLater = 0;
        foreach (var source in sources)
        {
            var ticked = _userChoices.TryGetValue(source.Path, out var chosen) ? chosen : source.Recommended;
            var (row, include) = NoteRow(root, source, ticked);
            _choices.Add((source, include));
            // Every ticked note is always visible, so nothing is sent that the person cannot see.
            if (ticked || index < VisibleNotes) _noteRows.Children.Add(row);
            else { _moreRows.Children.Add(row); shownLater++; }
            index++;
        }
        _showMore.IsVisible = shownLater > 0;
        _showMore.Content = shownLater == 1 ? "Show 1 more note" : $"Show {shownLater} more notes";
        _chooseTitle.Text = sources.Count == 1 ? "We found 1 note" : $"We found {sources.Count} notes";
        _reviewQuestion.Text = question;
        var (warning, summary) = result.Coverage.Summary(_choices.Count(choice => choice.Include.IsChecked == true), aiSources is not null);
        if (aiHelpFailed) { warning = true; summary += " AI could not help look for other words this time."; }
        if (aiNeedsPermission) { warning = true; summary += " AI has not helped with this search yet. Press Yes, use online AI, then Back and Find my notes."; }
        _coverageBar.Show(warning ? MessageTone.Warning : MessageTone.Info, summary);
        ShowStage(ChooseStage);
        RefreshPrepared();
        activity?.Complete(outcome: "Notes found. No note text was sent.");
    }

    private async Task<(IReadOnlyList<string> Terms, IReadOnlyList<string> Questions, string? Query, string? Clarification, bool Failed)> AskMeaningAsync(string question, CancellationToken token)
    {
        if (Model is not { } model || !CanTransmit(model)) return ([], [], null, null, false);
        try
        {
            var reply = AIWorkspaceService.RestoreNoteText(await _ai.SendWritingAsync(model.Id, _ai.PrepareNoteText(model.Id, KnowledgeConversation.MeaningRequest(question)),
                () => !_disposed && Model?.Id == model.Id && CanTransmit(model), token, skipCopilotCheck: true));
            return (KnowledgeConversation.ReadMeaningTerms(reply), KnowledgeConversation.ReadSuggestions(reply), KnowledgeConversation.ReadMeaningQuery(reply), KnowledgeConversation.ReadMeaningClarification(reply), false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or JsonException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or AIServiceException)
        {
            // Local search has already run. A failed AI helper is reported, never shown as "nothing in your notes".
            return ([], [], null, null, true);
        }
    }

    private async Task<(IReadOnlyList<(KnowledgeShortlistNote Note, string Why)> Chosen, IReadOnlyList<KnowledgeShortlistNote> AlsoRead, bool Failed)> AskSelectionAsync(
        string question, IReadOnlyList<KnowledgeShortlistNote> shortlist, CancellationToken token)
    {
        if (Model is not { } model || !CanTransmit(model)) return ([], [], false);
        try
        {
            var reply = AIWorkspaceService.RestoreNoteText(await _ai.SendWritingAsync(model.Id, _ai.PrepareNoteText(model.Id, KnowledgeConversation.SelectionRequest(question, shortlist, Limits.Sources, Limits.SendCharacters)),
                () => !_disposed && Model?.Id == model.Id && CanTransmit(model), token, skipCopilotCheck: true));
            var (chosen, also) = KnowledgeConversation.ReadSelection(reply, shortlist, Limits.Sources);
            return (chosen, also, false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or JsonException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or AIServiceException)
        {
            // Tern's own ticks remain, and the screen says AI could not help.
            return ([], [], true);
        }
    }

    /// <summary>AI's notes come first and start ticked. Other matches stay available, unticked.</summary>
    private async Task<IReadOnlyList<KnowledgeSource>> MergeAiChoiceAsync(WorkspaceSession root, string question, IReadOnlyList<KnowledgeSource> local,
        IReadOnlyList<(KnowledgeShortlistNote Note, string Why)> chosen, CancellationToken token)
    {
        var merged = new List<KnowledgeSource>();
        foreach (var (note, why) in chosen)
        {
            var source = local.FirstOrDefault(item => WorkspacePaths.PathsEqual(item.Path, note.Path));
            try { source ??= await _retrieval.ReadChosenNoteAsync(root, note.Path, question, merged.Count + 1, Limits, token); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { continue; }
            merged.Add(source with { Recommended = true, WhyChosen = why.Length > 0 ? why : null });
        }
        foreach (var source in local)
            if (merged.Count < Limits.Sources && merged.All(item => !WorkspacePaths.PathsEqual(item.Path, source.Path)))
                merged.Add(source with { Recommended = false });
        return merged.Select((source, index) => source with { Id = index + 1 }).ToArray();
    }

    private async Task AddNoteAsync(CancellationToken token)
    {
        if (Scope is not { } root || _stage != ChooseStage) return;
        var path = PickNote is { } pick ? await pick(root.Root) : await PickNoteWithDialogAsync(root);
        if (path is null) return;
        var existing = _choices.FirstOrDefault(choice => WorkspacePaths.PathsEqual(choice.Source.Path, path));
        if (existing.Include is { } tick)
        {
            tick.IsChecked = true;
            _moreRows.IsVisible = _moreRows.Children.Count > 0;
            _showMore.IsVisible = false;
            return;
        }
        KnowledgeSource source;
        try { source = await _retrieval.ReadChosenNoteAsync(root, path, QuestionText, _choices.Max(choice => choice.Source.Id) + 1, Limits, token); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _status.Text = "Could not add this note. Choose a Markdown note inside this folder that is saved on this computer.";
            return;
        }
        token.ThrowIfCancellationRequested();
        _addedNotes.Add(source.Path);
        _userChoices[source.Path] = true;
        var (row, include) = NoteRow(root, source, true);
        _choices.Add((source, include));
        _noteRows.Children.Add(row);
        _chooseTitle.Text = $"We found {_choices.Count} notes";
        RefreshPrepared();
    }

    private async Task<string?> PickNoteWithDialogAsync(WorkspaceSession root)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage) throw new InvalidOperationException("Choosing a file is not available in this window.");
        var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Add a note", AllowMultiple = false,
            SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(new Uri(root.Root)),
            FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("Markdown notes") { Patterns = ["*.md", "*.markdown", "*.mdown", "*.mkd", "*.txt"] }],
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    private async Task SendAsync(CancellationToken token)
    {
        if (_stage != ChooseStage || _request is not { } request || Scope is not { } root || Model is not { } model || !CanTransmit(model))
            throw new InvalidOperationException("Tick at least one note, then press Get my answer.");
        _sending = true;
        var generation = _generation;
        var activity = _operationActivity;
        _status.Text = "Reading your ticked notes…";
        _answerSources = request.Sources;
        await CacheCurrentNotesAsync(root, request.Sources, token);
        token.ThrowIfCancellationRequested();
        if (!ScopeCurrent(root) || generation != _generation || Model != model) throw new InvalidOperationException("This task changed. Review your notes again.");
        // The current task holds an in-progress result, never a fabricated provider-history turn.
        var turn = new KnowledgeTurn(request.Question, new KnowledgeAnswer("Preparing your report.", "", [], false));
        _conversation.RestoreCurrent(root.Root, turn);
        _request = null;
        try
        {
            _draft = await BuildReportAsync(turn, token);
            _draftTurn = turn;
            LoadReport(_draft);
            await PrepareCurrentContentAsync(token, allowInitialRevision: true);
            if (!ContentReady)
            {
                ShowStage(AskStage);
                _statusBar.Show(MessageTone.Warning, "AI could not finish a checked answer. Your question is kept. Try making it more specific, or choose another AI connection.");
                return;
            }
            ShowStage(AnswerStage);
            _statusBar.Show(_cachedCoverage?.NotRead.Count > 0 || _coverage.IsPartial ? MessageTone.Warning : MessageTone.Success,
                "Your answer and report are checked. Choose what to do. " + _reportCoverage);
            activity?.Complete(outcome: "One checked result is ready for all formats. Your notes were not changed.");
        }
        catch (KnowledgeNoEvidenceException)
        {
            _conversation.RestoreCurrent(root.Root, null);
            await ShowNotFoundAsync("The ticked notes do not contain enough information to answer this question. " + _reportCoverage,
                request.Question, token, next: "Choose other notes or ask in other words.");
            activity?.Complete(outcome: "The selected notes did not contain an answer.");
        }
    }

    private async Task<IReadOnlyList<string>> SuggestAsync(string question, CancellationToken token)
    {
        if (Model is not { } model || !CanTransmit(model)) return [];
        try
        {
            var reply = await _ai.SendWritingAsync(model.Id, _ai.PrepareNoteText(model.Id, KnowledgeConversation.SuggestionRequest(question)), () => !_disposed && Model?.Id == model.Id && CanTransmit(model),
                token, skipCopilotCheck: true);
            return KnowledgeConversation.ReadSuggestions(AIWorkspaceService.RestoreNoteText(reply));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or JsonException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or AIServiceException)
        {
            // Suggestions are a convenience. The written tips remain when AI cannot provide them.
            return [];
        }
    }

    private async Task ShowNotFoundAsync(string reason, string question, CancellationToken token, string? title = null, string? next = null,
        IReadOnlyList<string>? knownIdeas = null, bool canFinishReading = false, bool askAi = true)
    {
        _notFoundReason.Text = reason;
        SetSuggestions(_notFoundSuggestions, []);
        _notFoundSuggestionsArea.IsVisible = false;
        _finishReading.IsVisible = canFinishReading;
        ShowStage(NotFoundStage);
        if (title is not null) _title.Text = title;
        if (next is not null) _tip.Text = "Next step: " + next;
        _notFoundAdvice.Text = ""; _notFoundAdvice.IsVisible = false;
        var ideas = knownIdeas ?? [];
        if (askAi && Model is { } model && CanTransmit(model))
        {
            _status.Text = "AI is working out what to try next…";
            var (advice, questions) = await AskRecoveryAsync(model, question, reason, token);
            _status.Text = "";
            if (_stage != NotFoundStage) return;
            if (advice is not null) { _notFoundAdvice.Text = "AI suggests: " + advice; _notFoundAdvice.IsVisible = true; }
            if (questions.Count > 0) ideas = questions;
        }
        if (_stage != NotFoundStage) return;
        SetSuggestions(_notFoundSuggestions, ideas);
        _notFoundSuggestionsArea.IsVisible = ideas.Count > 0;
    }

    private async Task<(string? Advice, IReadOnlyList<string> Questions)> AskRecoveryAsync(WorkspaceConnection model, string question, string reason, CancellationToken token)
    {
        try
        {
            var reply = AIWorkspaceService.RestoreNoteText(await _ai.SendWritingAsync(model.Id, _ai.PrepareNoteText(model.Id, KnowledgeConversation.RecoveryRequest(question, reason)),
                () => !_disposed && Model?.Id == model.Id && CanTransmit(model), token, skipCopilotCheck: true));
            return (KnowledgeConversation.ReadAdvice(reply), KnowledgeConversation.ReadSuggestions(reply));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or JsonException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or AIServiceException)
        {
            // Tern's own reason and written tips remain on screen.
            return (null, []);
        }
    }

    private void UseSuggestion(string question)
    {
        if (_work is not null) return;
        _refreshing = true; _question.Text = question; _refreshing = false;
        _task = AnswerTask; _starterPrefix = ""; _vagueChecked = question;
        ClearAskSuggestions();
        ClearSelection();
        ShowStage(AskStage);
        _question.Focus();
    }

    public void Dispose()
    {
        if (_disposed) return;
        SaveCheckpoint(interrupted: _work is not null);
        _disposed = true; _generation++; _work?.Cancel();
        _shell.OpenWorkspaces.CollectionChanged -= OnWorkspacesChanged;
        _ai.Reloaded -= OnConnectionsChanged;
        foreach (var root in _observed) { root.FilesChanged -= OnFilesChanged; root.GraphChanged -= OnSourcesChanged; root.ContentStatusChanged -= OnContentStatusChanged; }
        _observed.Clear(); _choices.Clear(); _request = null; _retainedDraft = null;
        ClearAnswer();
        _conversation.Clear();
        _noteRows.Children.Clear(); _moreRows.Children.Clear(); _answerLines.Children.Clear(); _relatedRows.Children.Clear(); _question.Text = "";
        GC.SuppressFinalize(this);
    }
}
