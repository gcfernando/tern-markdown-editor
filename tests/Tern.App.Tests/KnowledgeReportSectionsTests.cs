using System.Text.Json;
using Tern.App.Knowledge;

namespace Tern.App.Tests;

public sealed class KnowledgeReportSectionsTests
{
    private static readonly KnowledgeSource A = new(1, @"C:\synthetic", @"C:\synthetic\A.md", "A.md", "A", null, 1, "", "", []);
    private static readonly KnowledgeSource B = A with { Id = 2, Path = @"C:\synthetic\B.md", RelativePath = "B.md", Title = "B" };
    private static readonly (KnowledgeSource Source, string Text)[] Notes =
        [(A, "# A\nAlpha is recorded.\n" + new string('a', 5_000)), (B, "# B\nBeta is recorded.\n" + new string('b', 5_000))];
    private static string Material => string.Concat(KnowledgeReportBuilder.Blocks(Notes));
    private static string Reply(int sourceId, string fact) => JsonSerializer.Serialize(new
    {
        title = "Recorded facts", headline = "What is recorded?", recommendation = fact,
        findings = new[] { new { title = "Recorded fact", kind = "Fact", detail = fact, evidenceIds = new[] { 1 } } },
        evidence = new[] { new { id = 1, sourceId, fact, quote = fact, line = 2 } },
    });
    private const string Executive = """{"title":"Recorded facts","headline":"What is recorded?","recommendation":"Alpha and Beta are recorded.","summaryNextAction":"Review the original notes."}""";

    [Fact]
    public async Task Duplicate_model_fields_are_rebuilt_from_original_sections_instead_of_guessing_their_meaning()
    {
        var malformed = Reply(1, "Alpha is recorded.").Replace("\"fact\":", "\"fact\":\"A different claim\",\"fact\":", StringComparison.Ordinal);
        var calls = 0;
        var result = await KnowledgeReportSections.ReadAsync(malformed, "What is recorded?", Material, Notes, "", DateTimeOffset.UnixEpoch,
            50_000, (request, _) =>
            {
                calls++;
                if (request.Instructions.Contains("concise executive brief", StringComparison.Ordinal)) return Task.FromResult(Executive);
                return Task.FromResult(request.CurrentInput.Contains("Beta is recorded.", StringComparison.Ordinal) ? Reply(2, "Beta is recorded.") : Reply(1, "Alpha is recorded."));
            }, CancellationToken.None);
        Assert.Equal(3, calls);
        Assert.Equal(2, result.Report.Findings.Count);
        Assert.DoesNotContain("A different claim", KnowledgeReportBuilder.ToMarkdown(result.Report));
        KnowledgeReportBuilder.ValidateEvidence(result.Report, Notes);
    }

    [Fact]
    public void Merging_preserves_section_qualifications_actions_and_final_assessments()
    {
        var report = KnowledgeReportBuilder.Read(Reply(1, "Alpha is recorded."), "What is recorded?", Notes, "", DateTimeOffset.UnixEpoch).Report;
        var section = report with { SummaryConditions = ["Subject to review"], SummaryRisks = ["Timing remains uncertain"],
            SummaryNextAction = "Confirm the timing", FinalAssessment = "Approval remains pending" };
        var merged = KnowledgeReportSections.Merge(report, [section]);
        Assert.Contains("Subject to review", merged.SummaryConditions);
        Assert.Contains("Timing remains uncertain", merged.SummaryRisks);
        Assert.Contains("Confirm the timing", merged.SummaryNextAction);
        Assert.Contains("Approval remains pending", merged.FinalAssessment);
    }

    [Fact]
    public void Repeated_identical_supported_findings_are_merged_once_without_dropping_distinct_claims()
    {
        var report = KnowledgeReportBuilder.Read(Reply(1, "Alpha is recorded."), "What is recorded?", Notes, "", DateTimeOffset.UnixEpoch).Report;
        var merged = KnowledgeReportSections.Merge(report, [report, report]);
        Assert.Single(merged.Findings);
        Assert.Single(merged.Evidence);
        Assert.Equal([1], merged.Findings[0].EvidenceIds);
        KnowledgeReportBuilder.ValidateEvidence(merged, Notes);
    }

    [Fact]
    public async Task A_complete_report_uses_no_extra_requests()
    {
        var calls = 0;
        var report = await KnowledgeReportSections.ReadAsync(Reply(1, "Alpha is recorded."), "What is recorded?", Material,
            Notes, "", DateTimeOffset.UnixEpoch, 50_000, (_, _) => { calls++; return Task.FromResult(Executive); }, CancellationToken.None);
        Assert.Equal(0, calls);
        Assert.Equal("Alpha is recorded.", report.Report.Evidence[0].Quote);
    }

    [Theory]
    [InlineData(50_000)]
    [InlineData(80_000)]
    public async Task An_incomplete_reply_is_completed_in_independent_parts_and_locally_remaps_duplicate_local_ids(int limit)
    {
        var calls = new List<AIRequest>();
        var report = await KnowledgeReportSections.ReadAsync("{\"title\":\"unfinished", "What is recorded?", Material,
            Notes, "Author", DateTimeOffset.UnixEpoch, limit, (request, _) =>
            {
                calls.Add(request);
                Assert.InRange(request.CharacterCount, 1, limit);
                Assert.DoesNotContain("EARLIER CONVERSATION", request.Text);
                if (request.Instructions.Contains("concise executive brief", StringComparison.Ordinal)) return Task.FromResult(Executive);
                return Task.FromResult(request.CurrentInput.Contains("Beta is recorded.", StringComparison.Ordinal)
                    ? Reply(2, "Beta is recorded.") : Reply(1, "Alpha is recorded."));
            }, CancellationToken.None);
        Assert.Equal(3, calls.Count);
        Assert.Equal(new[] { 1, 2 }, report.Report.Evidence.Select(item => item.Id));
        Assert.Equal(new[] { 1, 2 }, report.Report.Evidence.Select(item => item.SourceId));
        Assert.Equal(new[] { 1, 2 }, report.Report.Findings.SelectMany(item => item.EvidenceIds));
        Assert.Equal("Alpha and Beta are recorded.", report.Report.Recommendation);
        KnowledgeReportBuilder.ValidateEvidence(report.Report, Notes);
        KnowledgeReportBuilder.ValidateManagementContent(report.Report);
    }

    [Fact]
    public async Task A_complete_but_invalid_reply_does_not_trigger_a_favourable_output_retry()
    {
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => KnowledgeReportSections.ReadAsync(Reply(99, "Alpha is recorded."),
            "What is recorded?", Material, Notes, "", DateTimeOffset.UnixEpoch, 50_000,
            (_, _) => { calls++; return Task.FromResult(Executive); }, CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task An_explicit_no_evidence_result_never_becomes_a_fabricated_report()
    {
        var calls = 0;
        var error = await Assert.ThrowsAsync<KnowledgeNoEvidenceException>(() => KnowledgeReportSections.ReadAsync(
            """{"insufficientEvidence":true,"reason":"The selected notes do not record the requested date."}""", "What is the date?",
            Material, Notes, "", DateTimeOffset.UnixEpoch, 50_000,
            (_, _) => { calls++; return Task.FromResult(Executive); }, CancellationToken.None));
        Assert.Contains("selected notes", error.Message);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task A_failed_section_cannot_be_silently_omitted_from_a_completed_report()
    {
        var executiveCalls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => KnowledgeReportSections.ReadAsync("{\"title\":", "What is recorded?",
            Material, Notes, "", DateTimeOffset.UnixEpoch, 50_000, (request, _) =>
            {
                if (request.Instructions.Contains("concise executive brief", StringComparison.Ordinal)) { executiveCalls++; return Task.FromResult(Executive); }
                return Task.FromResult(request.CurrentInput.Contains("Beta is recorded.", StringComparison.Ordinal)
                    ? Reply(99, "Beta is recorded.") : Reply(1, "Alpha is recorded."));
            }, CancellationToken.None));
        Assert.Equal(0, executiveCalls);
    }

    [Fact]
    public async Task Transport_confirmed_truncation_splits_only_the_incomplete_section()
    {
        var incomplete = 0;
        var result = await KnowledgeReportSections.CompleteAsync("What is recorded?", Material, Notes, "", DateTimeOffset.UnixEpoch,
            50_000, (request, _) =>
            {
                if (request.Instructions.Contains("concise executive brief", StringComparison.Ordinal)) return Task.FromResult(Executive);
                var alpha = request.CurrentInput.Contains("Alpha is recorded.", StringComparison.Ordinal);
                var beta = request.CurrentInput.Contains("Beta is recorded.", StringComparison.Ordinal);
                if (alpha && incomplete++ == 0) throw new AIReplyIncompleteException();
                return Task.FromResult(beta ? Reply(2, "Beta is recorded.") : alpha ? Reply(1, "Alpha is recorded.") : "{\"sectionHasRelevantFacts\":false}");
            }, CancellationToken.None);
        Assert.True(incomplete >= 1);
        Assert.Equal(2, result.Report.Findings.Count);
        Assert.Contains(result.Report.Evidence, item => item.Quote == "Alpha is recorded.");
        Assert.Contains(result.Report.Evidence, item => item.Quote == "Beta is recorded.");
        KnowledgeReportBuilder.ValidateEvidence(result.Report, Notes);
    }

    [Fact]
    public async Task An_invalid_section_cancels_a_waiting_sibling_instead_of_waiting_for_another_provider_timeout()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var siblingCancelled = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => KnowledgeReportSections.CompleteAsync("What is recorded?", Material,
            Notes, "", DateTimeOffset.UnixEpoch, 50_000, async (request, cancellation) =>
            {
                if (request.CurrentInput.Contains("Beta is recorded.", StringComparison.Ordinal)) return Reply(99, "Beta is recorded.");
                try { await Task.Delay(Timeout.Infinite, cancellation); }
                catch (OperationCanceledException) { siblingCancelled = true; throw; }
                return Reply(1, "Alpha is recorded.");
            }, budget.Token));
        Assert.True(siblingCancelled);
        Assert.False(budget.IsCancellationRequested);
    }

    [Theory]
    [InlineData("{\"value\":\"a } brace within a string\"}", false)]
    [InlineData("{\"value\":\"a \\\" quote", true)]
    [InlineData("{\"findings\":[", true)]
    [InlineData("{\"value\":1,}", false)]
    public void Incompleteness_is_structural_not_a_generic_parse_failure(string value, bool incomplete) =>
        Assert.Equal(incomplete, KnowledgeReportSections.IsIncomplete(value));
}
