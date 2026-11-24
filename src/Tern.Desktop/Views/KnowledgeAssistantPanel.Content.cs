using Avalonia.Controls;
using Tern.App.Knowledge;
using Tern.App;
using Tern.App.Shell;
using Tern.Export;
using Tern.Desktop.Services;
using Tern.Workspaces;
using Tern.Rendering.Preview;

namespace Tern.Desktop.Views;

internal sealed partial class KnowledgeAssistantPanel
{
    private KnowledgeCachedNote[] _cachedNotes = [];
    private Dictionary<string, KnowledgeSourceState> _sourceStates = new(WorkspacePaths.Comparer);
    private KnowledgeCheckedContent? _checkedContent;
    private KnowledgeCachedCoverage? _cachedCoverage;
    private PdfExportResult? _cachedReportPdf;
    private ExploreReportCheckWitness? _checkWitness;
    private string _contentSourceKey = "";
    private readonly RadioButton[] _reportOutputs = Enumerable.Range(0, 4).Select(_ => new RadioButton { GroupName = "tern-report-output", Classes = { "tern-choice" } }).ToArray();
    private bool _syncingOutput;
    internal string CurrentReportMarkdown => _draft is null ? "" : KnowledgeReportBuilder.ToMarkdown(EditedReport());
    internal IReadOnlyList<string> CurrentReportIssues => Array.AsReadOnly(_reportIssues);
    internal bool CheckedContentReady => ContentReady;
    internal int ContentRenderCount { get; private set; }
    internal int NoteReadCount { get; private set; }
    internal int FullDigestChecks { get; private set; }

    private bool ContentReady => _checkedContent is not null && _cachedReportPdf is not null && _draft is not null && !_evidenceNeedsRefresh
        && _checkedContent.Key == KnowledgeCheckedContent.ReportKey(EditedReport()) && WitnessMatches(EditedReport()) && _reportIssues.Length == 0;

    private bool WitnessMatches(DecisionReport report) => _checkWitness is { } witness && witness.TaskId == _taskId
        && witness.PolicyVersion == KnowledgeCheckedContent.CheckPolicyVersion && witness.ReportKey == KnowledgeCheckedContent.ReportKey(report)
        && witness.SourceKey == _contentSourceKey && _contentSourceKey.Length > 0;

    private void InvalidateCheckedContent()
    {
        _checkedContent = null; _cachedReportPdf = null; _reportCheckedKey = null; _checkWitness = null;
    }

    private void SynchronizeReportOutputs()
    {
        if (_syncingOutput) return;
        _syncingOutput = true;
        try
        {
            var original = new[] { _shareCopy, _sharePdf, _shareNote, _sharePrint };
            for (var index = 0; index < original.Length; index++)
            {
                _reportOutputs[index].IsChecked = original[index].IsChecked;
                _reportOutputs[index].IsEnabled = _work is null && (index != 3 || _printerName is not null);
            }
        }
        finally { _syncingOutput = false; }
    }

    private Control BuildReportOutputs()
    {
        var original = new[] { _shareCopy, _sharePdf, _shareNote, _sharePrint };
        var labels = new[] { "Copy for email", "Save PDF", "Add to my note", "Print" };
        for (var index = 0; index < original.Length; index++)
        {
            var choice = index;
            _reportOutputs[index].Content = labels[index];
            NameControl(_reportOutputs[index], "Report output: " + labels[index]);
            _reportOutputs[index].IsCheckedChanged += (_, _) =>
            {
                if (!_syncingOutput && _reportOutputs[choice].IsChecked == true) original[choice].IsChecked = true;
            };
        }
        return new Avalonia.Controls.Primitives.UniformGrid { Columns = 2, Children = { _reportOutputs[0], _reportOutputs[1], _reportOutputs[2], _reportOutputs[3] } };
    }

    private Dictionary<string, KnowledgeSourceState> CaptureSourceStates(WorkspaceSession root, IEnumerable<KnowledgeSource> sources) =>
        sources.DistinctBy(source => source.Path, WorkspacePaths.Comparer).ToDictionary(source => source.Path, source => root.CaptureKnowledgeState(source.Path), WorkspacePaths.Comparer);

    private static bool SameSourceStates(IReadOnlyDictionary<string, KnowledgeSourceState> first, IReadOnlyDictionary<string, KnowledgeSourceState> second) =>
        first.Count == second.Count && first.All(item => second.TryGetValue(item.Key, out var value) && value == item.Value);

    private async Task CacheCurrentNotesAsync(WorkspaceSession root, IReadOnlyList<KnowledgeSource> sources, CancellationToken token)
    {
        var before = CaptureSourceStates(root, sources);
        NoteReadCount++;
        var notes = await _retrieval.ReadForReportAsync(root, sources, Limits, token);
        var after = CaptureSourceStates(root, sources);
        if (!SameSourceStates(before, after)) throw new InvalidOperationException("Your notes changed while being read. Refresh your notes.");
        var within = KnowledgeReader.Within(notes);
        _cachedNotes = within.Read.Select(note => new KnowledgeCachedNote(note.Source, note.Text)).ToArray();
        _cachedCoverage = new(_cachedNotes.Sum(note => note.Text.Length), within.Total, notes.Count, within.NotRead.ToArray());
        _sourceStates = after;
        _contentSourceKey = KnowledgeCheckedContent.SourceKey(sources, _cachedNotes);
    }

    private IReadOnlyList<(KnowledgeSource Source, string Text)> CachedNotes => _cachedNotes.Select(note => (note.Source, note.Text)).ToArray();

    private async Task RestoreCheckedContentAsync(CancellationToken token)
    {
        if (_draft is null || _cachedNotes.Length == 0 || _reportIssues.Length > 0 || !WitnessMatches(_draft.Report)) return;
        var task = _taskId; var revision = _reportEditRevision; var root = Scope;
        var witness = _checkWitness;
        bool Current() => !_disposed && _taskId == task && _reportEditRevision == revision && root is not null && ScopeCurrent(root)
            && ReferenceEquals(witness, _checkWitness) && _draft is not null && WitnessMatches(_draft.Report) && !token.IsCancellationRequested;
        await ValidateTaskEvidenceAsync(token);
        await PublishCheckedContentAsync(_draft, witness!, Current, token);
        _status.Text = "Report checked. Choose what to do.";
    }

    private KnowledgeAnswer AnswerFromCheckedReport(DecisionReport report)
    {
        var details = report.Findings.SelectMany(finding => new[]
        {
            string.IsNullOrWhiteSpace(finding.Title) ? finding.Detail : finding.Title + ": " + finding.Detail,
            finding.Derivation,
            string.IsNullOrWhiteSpace(finding.Limitations) ? "" : "Limitations: " + finding.Limitations,
        });
        var lines = new[] { report.Recommendation }.Concat(report.Reasons).Concat(details)
            .Where(line => !string.IsNullOrWhiteSpace(line)).Distinct(StringComparer.Ordinal).Select(line => "- " + line);
        var citations = report.Evidence.Select(evidence => _answerSources.First(source => source.Id == evidence.SourceId))
            .DistinctBy(source => source.Id).Select(source => new KnowledgeCitation(source, "Supports the checked report")).ToArray();
        return new(string.Join('\n', lines), "", citations, false);
    }

    private async Task PublishCheckedContentAsync(KnowledgeReportDraft draft, ExploreReportCheckWitness witness, Func<bool> current, CancellationToken token)
    {
        if (witness.TaskId != _taskId || witness.PolicyVersion != KnowledgeCheckedContent.CheckPolicyVersion || witness.ReportKey != KnowledgeCheckedContent.ReportKey(draft.Report)
            || witness.SourceKey != _contentSourceKey) throw new InvalidOperationException("Check this report before preparing its outputs.");
        var content = KnowledgeCheckedContent.Create(draft.Report, CachedNotes, []);
        _status.Text = "Preparing your report for saving…";
        ContentRenderCount++;
        var pdf = await Task.Run(() => DecisionReportPdf.Export(content.Report, token), token);
        await ValidateTaskEvidenceAsync(token);
        if (!current()) throw new InvalidOperationException("This task changed. Prepare the report again before sharing.");
        _checkWitness = witness; _checkedContent = content; _cachedReportPdf = pdf;
    }

    private async Task PrepareCurrentContentAsync(CancellationToken token, bool allowInitialRevision = false)
    {
        if (_draft is null || Model is not { } model || !CanTransmit(model) || _conversation.Turns.LastOrDefault() is not { } turn || Scope is not { } root)
            throw new InvalidOperationException("Choose an available AI connection to check this report.");
        CaptureReportEdits();
        var draft = _draft; var task = _taskId; var generation = _generation; var revision = _reportEditRevision;
        bool Current() => !_disposed && task == _taskId && generation == _generation && revision == _reportEditRevision && ScopeCurrent(root)
            && Model == model && CanTransmit(model) && !token.IsCancellationRequested;
        await ValidateTaskEvidenceAsync(token);
        if (_cachedNotes.Length == 0) await CacheCurrentNotesAsync(root, _answerSources, token);
        KnowledgeReportBuilder.ValidateManagementContent(draft.Report);
        KnowledgeReportBuilder.ValidateEvidence(draft.Report, CachedNotes);
        _status.Text = "Checking your report…";
        var (issues, _, witness) = await CheckReportAsync(draft.Report, turn.Question, _reportMaterial, model, Current, token);
        if (allowInitialRevision && issues.Count > 0)
        {
            var initialIssues = issues;
            async Task<string> Ask(AIRequest request, CancellationToken stepToken)
            {
                await ValidateTaskEvidenceAsync(stepToken);
                if (!Current()) throw new InvalidOperationException("This task changed. Your question is kept.");
                var prepared = _ai.PrepareNoteText(model.Id, request);
                if (_ai.WritingValidationError(model.Id, prepared) is { } error) throw new InvalidOperationException(error);
                var response = await _ai.SendWritingAsync(model.Id, prepared, Current, stepToken, skipCopilotCheck: true);
                await ValidateTaskEvidenceAsync(stepToken);
                if (!Current()) throw new InvalidOperationException("This task changed. Your question is kept.");
                return AIWorkspaceService.RestoreNoteText(response);
            }
            _status.Text = "Improving the answer before it is ready…";
            KnowledgeReportDraft candidate;
            var completeMaterial = string.Concat(KnowledgeReportBuilder.NoteParts(CachedNotes, KnowledgeLimits.MaximumQuestionCharacters));
            try
            {
                var revised = await KnowledgeReportRevision.WriteAsync(draft.Report, issues, CachedNotes, NewReader(model, Current), token);
                completeMaterial = revised.Material;
                candidate = await KnowledgeReportSections.ReadAsync(revised.Reply, turn.Question, completeMaterial, CachedNotes, draft.Report.PreparedBy,
                    DateTimeOffset.Now, _ai.SendCharactersFor(model.Id), Ask, token);
            }
            catch (AIReplyIncompleteException)
            {
                candidate = await KnowledgeReportSections.CompleteAsync(turn.Question, completeMaterial, CachedNotes, draft.Report.PreparedBy,
                    DateTimeOffset.Now, _ai.SendCharactersFor(model.Id), Ask, token);
            }
            candidate = candidate with { Report = candidate.Report with { Coverage = _reportCoverage } };
            if (KnowledgeCheckedContent.ReportKey(candidate.Report) != KnowledgeCheckedContent.ReportKey(draft.Report))
            {
                (issues, _, witness) = await CheckReportAsync(candidate.Report, turn.Question, completeMaterial, model, Current, token);
                if (issues.Count == 0) { draft = candidate; _draft = candidate; LoadReport(candidate); _reportMaterial = completeMaterial; }
                else { issues = initialIssues; witness = null; }
            }
        }
        await ValidateTaskEvidenceAsync(token);
        if (!Current()) throw new InvalidOperationException("The report changed. Your edits are kept.");
        if (issues.Count > 0) { SetReportIssues(draft.Report, issues); _status.Text = "Some statements need checking. Your report is kept."; return; }
        await PublishCheckedContentAsync(draft, witness!, Current, token);
        _reportIssues = []; _reportIssuesKey = null; _reportCheckedKey = ReportCheckKey(draft.Report, model);
        _reportIssueDetails.IsVisible = false;
        var answer = AnswerFromCheckedReport(draft.Report);
        _conversation.RestoreCurrent(root.Root, new KnowledgeTurn(turn.Question, answer));
        _draftTurn = _conversation.Turns[^1];
        RenderAnswer(root, answer, preserveReport: true);
        _reportCheck.Show(MessageTone.Success, "Report checked. Choose what to do.");
        _status.Text = "Report checked. Choose what to do.";
        RefreshReportAvailability(); QueueCheckpoint();
    }

    private async Task PreviewReportAsync()
    {
        if (_draft is null || _work is not null || TopLevel.GetTopLevel(this) is not Window owner) return;
        var task = _taskId; var revision = _reportEditRevision; var text = CurrentReportMarkdown;
        _reportPreview.IsEnabled = false;
        try
        {
            var document = await Task.Run(() => PreviewBuilder.Build(text, new PreviewOptions
            {
                MaxSourceLength = Math.Max(1, text.Length), MaxNodes = (int)Math.Min(int.MaxValue, Math.Max(250_000L, text.Length * 2L)),
                MaxBuildTime = TimeSpan.FromSeconds(15), Resolver = new() { AllowDataImages = false, ImageExtensions = new HashSet<string>() },
            }));
            if (_disposed || task != _taskId || revision != _reportEditRevision) return;
            var view = new Tern.PreviewView.PreviewView { Document = document, UseFullWidth = true };
            var window = new Window();
            DialogParts.Setup(window, "Report preview", 880, 760, DialogGuidance.Connections, guidance: false);
            var close = new Button { Content = "Close" }; close.Click += (_, _) => window.Close();
            var note = GuideParts.Caption(document.Truncated ? "This preview is shortened to keep Tern responsive. The saved PDF contains the full report." : "Read the full report before sharing it.");
            window.Content = GuideParts.Frame(GuideParts.Vertical(GuideParts.Title("Report preview"), note), view, GuideParts.Actions(close));
            await window.ShowDialog(owner);
        }
        finally { if (!_disposed) _reportPreview.IsEnabled = _draft is not null && _work is null; }
    }
}
