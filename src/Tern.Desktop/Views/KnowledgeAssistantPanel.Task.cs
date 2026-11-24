using Avalonia.Controls;
using Avalonia.Threading;
using Tern.App.Knowledge;
using Tern.App.Shell;
using Tern.Desktop.Services;
using Tern.Workspaces;

namespace Tern.Desktop.Views;

internal sealed partial class KnowledgeAssistantPanel
{
    private readonly ExploreTaskStore _taskStore;
    private WorkspaceSession? _taskRoot;
    private Guid _taskId = Guid.NewGuid();
    private bool _restoringTask, _checkpointQueued, _evidenceNeedsRefresh, _insertionPending;
    private string? _retryStep;
    private KnowledgeReportDraft? _retainedDraft;
    private readonly Button _done = new() { Content = "Done" };
    private readonly Button _retry = new() { Content = "Retry", IsVisible = false };
    private readonly Button _refreshNotes = new() { Content = "Refresh notes", IsVisible = false };
    private readonly MessageBar _taskBar = new();
    internal bool HasPendingTask => !string.IsNullOrWhiteSpace(QuestionText) || _choices.Count > 0 || _conversation.Turns.Count > 0 || _draft is not null || _work is not null || _retryStep is not null || _insertionPending;

    private void QueueCheckpoint()
    {
        if (_restoringTask || _disposed || _checkpointQueued) return;
        _checkpointQueued = true;
        Dispatcher.UIThread.Post(() => { _checkpointQueued = false; if (!_disposed) SaveCheckpoint(); }, DispatcherPriority.Background);
    }

    private void SaveCheckpoint(bool interrupted = false)
    {
        if (_restoringTask || _taskRoot is not { } root) return;
        if (!HasPendingTask) { _taskStore.Remove(root.Root); return; }
        CaptureReportEdits();
        _taskStore.Save(new ExploreTaskCheckpoint
        {
            TaskId = _taskId, WorkspaceRoot = root.Root, Question = _question.Text ?? "", Operation = _task, Stage = _stage,
            Choices = _choices.Select(choice => new ExploreNoteChoice(choice.Source, choice.Include.IsChecked == true)).ToArray(),
            AddedNotes = _addedNotes.ToArray(), Answer = _conversation.Turns.LastOrDefault(), AnswerSources = _answerSources.ToArray(),
            Draft = _draft, RetainedDraft = _retainedDraft, ReportMaterial = _reportMaterial, ReportCoverage = _reportCoverage,
            ReportIssues = _reportIssues.ToArray(), ReportIssuesKey = _reportIssuesKey,
            CachedNotes = _cachedNotes.ToArray(), SourceStates = new(_sourceStates, WorkspacePaths.Comparer), CheckedContentKey = _checkedContent?.Key,
            CachedCoverage = _cachedCoverage,
            CheckWitness = _checkWitness,
            Output = _sharePdf.IsChecked == true ? "pdf" : _shareNote.IsChecked == true ? "note" : _sharePrint.IsChecked == true ? "print" : "email",
            Status = _status.Text ?? "", NotFoundReason = _notFoundReason.Text ?? "", Interrupted = interrupted || _work is not null,
            RetryStep = _retryStep, InsertionPending = _insertionPending, NeedsRefresh = _evidenceNeedsRefresh,
        }, root.IsPrivate);
        if (_taskStore.Problem is { } problem) _taskBar.Show(MessageTone.Warning, problem);
        else if (root.IsPrivate) _taskBar.Show(MessageTone.Info, "Private workspace: your unfinished task is kept while Tern is open. Closing Tern will remove it.");
    }

    private void SwitchTask()
    {
        SaveCheckpoint(interrupted: _work is not null);
        _restoringTask = true;
        try
        {
            StartConversation();
            _taskRoot = Scope;
            _taskId = Guid.NewGuid(); _retryStep = null; _retainedDraft = null; _insertionPending = false; _evidenceNeedsRefresh = false;
            _taskBar.Clear();
            if (_taskRoot is not { } root) return;
            var saved = _taskStore.Load(root.Root, root.IsPrivate);
            if (saved is null)
            {
                if (_taskStore.Problem is { } problem) _taskBar.Show(MessageTone.Warning, problem);
                else if (root.IsPrivate) _taskBar.Show(MessageTone.Info, "Private workspace: your unfinished task is kept while Tern is open. Closing Tern will remove it.");
                return;
            }
            _taskId = saved.TaskId; _task = saved.Operation; _retryStep = saved.RetryStep;
            _insertionPending = saved.InsertionPending; _evidenceNeedsRefresh = saved.NeedsRefresh;
            _refreshing = true;
            _question.Text = saved.Question;
            _conversation.RestoreCurrent(root.Root, saved.Answer);
            _answerSources = saved.AnswerSources;
            foreach (var choice in saved.Choices)
            {
                var (row, include) = NoteRow(root, choice.Source, choice.Included);
                _choices.Add((choice.Source, include)); _noteRows.Children.Add(row);
                _userChoices[choice.Source.Path] = choice.Included;
            }
            _addedNotes.AddRange(saved.AddedNotes);
            _chooseTitle.Text = $"{_choices.Count} notes in this task"; _reviewQuestion.Text = saved.Question;
            if (saved.Answer is { } turn) RenderAnswer(root, turn.Answer);
            _draft = saved.Draft; _retainedDraft = saved.RetainedDraft; _draftTurn = saved.Draft is null ? null : _conversation.Turns.LastOrDefault();
            _reportMaterial = saved.ReportMaterial; _reportCoverage = saved.ReportCoverage;
            _cachedNotes = saved.CachedNotes; _cachedCoverage = saved.CachedCoverage; _sourceStates = new(saved.SourceStates, WorkspacePaths.Comparer);
            _contentSourceKey = KnowledgeCheckedContent.SourceKey(_answerSources, _cachedNotes); _checkWitness = saved.CheckWitness;
            if (_draft is not null) LoadReport(_draft);
            _reportIssues = saved.ReportIssues; _reportIssuesKey = saved.ReportIssuesKey;
            _shareCopy.IsChecked = saved.Output == "email"; _sharePdf.IsChecked = saved.Output == "pdf";
            _shareNote.IsChecked = saved.Output == "note"; _sharePrint.IsChecked = saved.Output == "print";
            _notFoundReason.Text = saved.NotFoundReason;
            _refreshing = false;
            ShowStage(saved.Stage); RefreshPrepared();
            ShowReportIssues();
            _status.Text = saved.Interrupted ? "This step was interrupted. Your task is kept. Press Retry when you are ready."
                : saved.InsertionPending ? "Your report is kept. Review Add to my note again before inserting it."
                : "Your unfinished task is restored. Nothing has been sent to AI.";
            if (saved.Interrupted && _retryStep is null) _retryStep = saved.Stage switch { ChooseStage => "answer", ReportStage => "finish", AnswerStage => "share", _ => "find" };
            if (_draft is not null && WitnessMatches(_draft.Report) && _reportIssues.Length == 0)
            {
                var restoredTask = _taskId;
                Dispatcher.UIThread.Post(async () =>
                {
                    if (!_disposed && _taskId == restoredTask && _work is null) await RunAsync(RestoreCheckedContentAsync);
                }, DispatcherPriority.Background);
            }
        }
        finally { _refreshing = false; _restoringTask = false; RefreshAvailability(); }
        _ = InspectRestoredEvidenceAsync();
    }

    private void DoneTask()
    {
        if (_work is not null) return;
        if (_taskRoot is { } root) _taskStore.Remove(root.Root);
        _restoringTask = true;
        try
        {
            _taskId = Guid.NewGuid(); _retryStep = null; _retainedDraft = null; _insertionPending = false; _evidenceNeedsRefresh = false; StartConversation();
            if (_taskStore.Problem is { } problem) _taskBar.Show(MessageTone.Warning, problem); else _taskBar.Clear();
        }
        finally { _restoringTask = false; RefreshAvailability(); }
    }

    private async Task RetryTaskAsync()
    {
        switch (_retryStep)
        {
            case "find": await RunAsync(FindAsync); break;
            case "answer": ShowStage(ChooseStage); RefreshPrepared(); await RunAsync(SendAsync); break;
            case "share": await RunAsync(ShareAsync); break;
            case "finish": ShowStage(ReportStage); await RunAsync(FinishReportAsync); break;
            case "revise": ShowStage(ReportStage); await RunAsync(CorrectReportAsync); break;
        }
    }

    private async Task RefreshTaskNotesAsync(CancellationToken token)
    {
        CaptureReportEdits();
        var retained = _draft ?? _retainedDraft;
        _retainedDraft = retained;
        _evidenceNeedsRefresh = false;
        await FindAsync(token);
        _retainedDraft = retained;
        if (retained is not null) _taskBar.Show(MessageTone.Info, "Your report wording is kept. Get a fresh answer, then create the report to check it against the refreshed notes.");
    }

    private async Task InspectRestoredEvidenceAsync()
    {
        try { await ValidateTaskEvidenceAsync(CancellationToken.None); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException) { }
    }

    private async Task ValidateTaskEvidenceAsync(CancellationToken token)
    {
        if (_taskRoot is not { } root || !ScopeCurrent(root)) throw new InvalidOperationException("Reopen this task's folder before continuing.");
        if (_evidenceNeedsRefresh) throw new InvalidOperationException("Your notes changed. Press Refresh notes before using this task.");
        var id = _taskId; var generation = _generation;
        var sources = _answerSources.Count > 0 ? _answerSources : _choices.Where(choice => choice.Include.IsChecked == true).Select(choice => choice.Source).ToArray();
        try
        {
            var observed = CaptureSourceStates(root, sources);
            var same = _sourceStates.Count > 0 && SameSourceStates(_sourceStates, observed);
            var newSession = _sourceStates.Count == observed.Count && observed.Count > 0
                && observed.All(item => _sourceStates.TryGetValue(item.Key, out var saved) && saved.SessionId != item.Value.SessionId);
            if (_sourceStates.Count > 0 && !same && !newSession) throw new InvalidOperationException("Your source notes changed. Refresh your notes.");
            // Strong Windows change time + file identity and overlay revision allow reuse without
            // rereading content. Other filesystems retain the original full digest check.
            if (!same || observed.Values.Any(state => state.Disk is null))
            {
                FullDigestChecks++;
                await _retrieval.ValidateAsync(root, sources, Limits, token);
                var after = CaptureSourceStates(root, sources);
                if (!SameSourceStates(observed, after)) throw new InvalidOperationException("Your source notes changed while being checked. Refresh your notes.");
                _sourceStates = after;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (!_disposed && id == _taskId && generation == _generation)
            {
                _evidenceNeedsRefresh = true; _reportReviewed.IsChecked = false; InvalidateCheckedContent();
                _taskBar.Show(MessageTone.Warning, "Your notes changed or are unavailable. Your wording is kept. Press Refresh notes before using this task.");
                RefreshAvailability(); QueueCheckpoint();
            }
            throw;
        }
        token.ThrowIfCancellationRequested();
        if (_disposed || id != _taskId || generation != _generation || !ScopeCurrent(root)) throw new OperationCanceledException("The Explore task changed.", token);
    }

    internal void MarkInsertionComplete() { _insertionPending = false; QueueCheckpoint(); }
}
