using System.Text.Json;
using Avalonia.Controls;
using Tern.App;
using Tern.App.Knowledge;
using Tern.Desktop.Services;
using Tern.Export;

namespace Tern.Desktop.Views;

internal sealed partial class KnowledgeAssistantPanel
{
    private readonly Button _reportCorrect = new() { Content = "Ask AI to correct these issues", IsVisible = false };
    private string[] _reportIssues = [];
    private string? _reportIssuesKey;

    private static string ReportCheckKey(DecisionReport report, WorkspaceConnection model) =>
        JsonSerializer.Serialize(new { report, model.Id, model.Model, model.Destination, model.SendCharacters });

    private bool IssuesMatchReport() => _draft is not null && _reportIssues.Length > 0
        && _reportIssuesKey == ExploreTaskCheckpoint.ReportKey(EditedReport());

    private void ShowReportIssues()
    {
        if (_reportIssues.Length == 0) return;
        _reportCheck.Show(MessageTone.Warning, IssuesMatchReport()
            ? "Some report statements need checking. Ask AI to correct them, or edit the report. Nothing has been shared."
            : "The report has changed. Check your edits again before sharing. Nothing has been shared.");
        var details = GuideParts.Vertical();
        foreach (var issue in _reportIssues.Take(3))
        {
            var plain = System.Text.RegularExpressions.Regex.Replace(issue, @"\[(?:E|S)\d+[^\]]*\]|\b(?:E|S)\d+\b", "", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            plain = string.Join(' ', plain.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            details.Children.Add(GuideParts.Body(plain.Length <= 240 ? plain : plain[..240].TrimEnd() + "…"));
        }
        if (_reportIssues.Length > 3) details.Children.Add(GuideParts.Caption($"{_reportIssues.Length - 3} more issues will also be included when AI corrects the report."));
        _reportIssueDetails.Content = details; _reportIssueDetails.IsVisible = true; NameControl(_reportIssueDetails, "What needs checking");
    }

    private void SetReportIssues(DecisionReport report, IReadOnlyList<string> issues)
    {
        InvalidateCheckedContent();
        _reportIssues = issues.ToArray();
        _reportIssuesKey = ExploreTaskCheckpoint.ReportKey(report);
        ShowReportIssues(); RefreshReportAvailability(); QueueCheckpoint();
    }

    private async Task<(IReadOnlyList<string> Issues, string? Advice, ExploreReportCheckWitness? Witness)> CheckReportAsync(DecisionReport report, string question,
        string material, WorkspaceConnection model, Func<bool> current, CancellationToken token)
    {
        var reader = NewReader(model, current);
        reader.AdoptSourceMaterial(material, CachedNotes);
        var issues = new List<string>();
        foreach (var prompt in KnowledgeReportChecks.Plan(report, reader.Limit))
        {
            int Room() => reader.Limit - prompt("").CharacterCount;
            var (reply, _) = await reader.WriteRequestAsync(question, prompt, material, Room, token);
            token.ThrowIfCancellationRequested();
            if (!current()) throw new InvalidOperationException("The report or its notes changed. Your existing draft is kept.");
            issues.AddRange(KnowledgeReportBuilder.ReadUnsupported(AIWorkspaceService.RestoreNoteText(reply)));
        }
        var remaining = issues.Distinct(StringComparer.Ordinal).ToArray();
        var witness = remaining.Length == 0 ? new ExploreReportCheckWitness(_taskId, KnowledgeCheckedContent.ReportKey(report), _contentSourceKey, KnowledgeCheckedContent.CheckPolicyVersion) : null;
        return (remaining, reader.LowerLimitAdvice, witness);
    }

    private async Task CorrectReportAsync(CancellationToken token)
    {
        if (_draft is null || !IssuesMatchReport() || Scope is not { } root || Model is not { } model || !CanTransmit(model)
            || _conversation.Turns.LastOrDefault() is not { } turn)
            throw new InvalidOperationException("Check the current report first so AI can correct its exact issues.");
        CaptureReportEdits();
        var original = _draft;
        var issues = _reportIssues.ToArray();
        var taskId = _taskId; var generation = _generation; var revision = _reportEditRevision;
        bool SameDraft() => !_disposed && _taskId == taskId && _generation == generation && revision == _reportEditRevision && ScopeCurrent(root)
            && ReferenceEquals(_conversation.Turns.LastOrDefault(), turn);
        bool Current() => SameDraft() && Model == model && CanTransmit(model) && !token.IsCancellationRequested;
        void RequireCurrent()
        {
            token.ThrowIfCancellationRequested();
            if (!Current()) throw new InvalidOperationException("The report, notes or AI connection changed. Your existing draft is kept. Review it before correcting again.");
        }
        async Task<string> Ask(AIRequest request, CancellationToken stepToken)
        {
            await ValidateTaskEvidenceAsync(stepToken);
            RequireCurrent();
            var prepared = _ai.PrepareNoteText(model.Id, request);
            if (_ai.WritingValidationError(model.Id, prepared) is { } error) throw new InvalidOperationException(error);
            var reply = await _ai.SendWritingAsync(model.Id, prepared, Current, stepToken, skipCopilotCheck: true);
            await ValidateTaskEvidenceAsync(stepToken);
            RequireCurrent();
            return AIWorkspaceService.RestoreNoteText(reply);
        }
        try
        {
            await ValidateTaskEvidenceAsync(token);
            RequireCurrent();
            if (_cachedNotes.Length == 0) await CacheCurrentNotesAsync(root, _answerSources, token);
            var admitted = CachedNotes;
            RequireCurrent();
            _status.Text = "AI is correcting the report using the issues you chose…";
            var material = string.Concat(KnowledgeReportBuilder.NoteParts(admitted, KnowledgeLimits.MaximumQuestionCharacters));
            KnowledgeReportDraft candidate;
            try
            {
                var revised = await KnowledgeReportRevision.WriteAsync(original.Report, issues, admitted, NewReader(model, Current), token);
                material = revised.Material;
                candidate = await KnowledgeReportSections.ReadAsync(revised.Reply, turn.Question, material, admitted, original.Report.PreparedBy,
                    DateTimeOffset.Now, _ai.SendCharactersFor(model.Id), Ask, token);
            }
            catch (AIReplyIncompleteException)
            {
                candidate = await KnowledgeReportSections.CompleteAsync(turn.Question, material, admitted, original.Report.PreparedBy,
                    DateTimeOffset.Now, _ai.SendCharactersFor(model.Id), Ask, token);
            }
            candidate = candidate with { Report = candidate.Report with { Coverage = _reportCoverage,
                Missing = candidate.Report.Missing.Concat(original.Report.Missing.Where(item => item.Item == "Some long notes were only partly read"
                    && !candidate.Report.Missing.Any(existing => existing.Item == item.Item))).ToArray() } };
            await ValidateTaskEvidenceAsync(token);
            RequireCurrent();
            _status.Text = "AI is checking the corrected report against your notes…";
            var (remaining, advice, witness) = await CheckReportAsync(candidate.Report, turn.Question, material, model, Current, token);
            await ValidateTaskEvidenceAsync(token);
            RequireCurrent();
            if (remaining.Count > 0)
            {
                _status.Text = "AI could not make a checked correction. Your existing report and edits are kept.";
                _reportCheck.Show(MessageTone.Warning, "The correction still needs checking. Your existing report and edits are kept. Try correcting again or edit the report.");
                return;
            }
            // Commit only after every check. Nothing above changes the user's current draft or its issues.
            await PublishCheckedContentAsync(candidate, witness!, Current, token);
            _draft = candidate; _draftTurn = turn; _reportMaterial = material; _reportEditRevision++;
            _reportIssues = []; _reportIssuesKey = null;
            _reportIssueDetails.IsVisible = false;
            LoadReport(candidate);
            var answer = AnswerFromCheckedReport(candidate.Report);
            _conversation.RestoreCurrent(root.Root, new KnowledgeTurn(turn.Question, answer));
            _draftTurn = _conversation.Turns[^1];
            _shareResult = null;
            RenderAnswer(root, answer, preserveReport: true);
            _reportCheckedKey = ReportCheckKey(candidate.Report, model);
            _reportCheck.Show(advice is null ? MessageTone.Success : MessageTone.Warning,
                "AI prepared a corrected draft and checked it against your notes. Read the full report and tick I reviewed this report again. Nothing has been shared."
                + (advice is null ? "" : " " + advice));
            _status.Text = "Your corrected draft is ready for review. Nothing has been shared.";
            ShowStage(ReportStage); QueueCheckpoint();
        }
        catch
        {
            if (SameDraft())
                _reportCheck.Show(MessageTone.Warning, "AI could not finish the correction. Your existing report, edits and issues are kept. Try again when you are ready.");
            throw;
        }
    }
}
