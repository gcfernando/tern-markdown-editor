using System.Text.Json;
using Tern.App.Knowledge;

namespace Tern.App.Tests;

public sealed class ManagementReportRepairTests
{
    private const string Quote = "The requested budget is €2,400.";
    private const string WrongQuote = "The requested budget is EUR2,400.";
    private const string Note = "## Budget — 2027-01-02\n" + Quote + "\n";
    private static readonly KnowledgeSource Source = new(1, @"C:\synthetic", @"C:\synthetic\Plan.md", "Plan.md", "Plan", null, 1, "", "", []);
    private static string Reply(string quote, int sourceId = 1, int line = 0) => JsonSerializer.Serialize(new
    {
        title = "Budget review", headline = "Consider the proposed budget", recommendation = "Review the proposed budget.",
        findings = new[] { new { title = "Requested budget", kind = "Fact", detail = Quote, evidenceIds = new[] { 1 } } },
        evidence = new[] { new { id = 1, fact = Quote, sourceId, quote, line } },
    });
    private static string Plan(int start = 2, int end = 2, int sourceId = 1, int evidenceId = 1) => JsonSerializer.Serialize(new
    {
        replacements = new[] { new { evidenceId, sourceId, startLine = start, endLine = end } },
    });

    [Fact]
    public async Task A_failed_anchor_uses_one_original_line_selection_and_keeps_the_finding_unchanged()
    {
        var payload = System.Text.Json.Nodes.JsonNode.Parse(Reply(WrongQuote))!;
        var item = payload["evidence"]![0]!.AsObject();
        item.Remove("quote"); item["quoteStart"] = WrongQuote; item["quoteEnd"] = WrongQuote;
        var calls = 0;
        var draft = await KnowledgeReportRepair.ReadAsync(payload.ToJsonString(), "Q", [(Source, Note)], "", DateTimeOffset.UnixEpoch,
            null, false, 50_000, (request, _) => { calls++; Assert.Equal(Tern.App.AIPurpose.ReportRepair, request.Purpose); return Task.FromResult(Plan()); }, CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.Equal(Quote, draft.Report.Findings[0].Detail);
        Assert.Equal(Quote, draft.Report.Evidence[0].Quote);
        KnowledgeReportBuilder.ValidateEvidence(draft.Report, [(Source, Note)]);
    }

    [Theory]
    [InlineData(50_000)]
    [InlineData(80_000)]
    public async Task Large_multiline_sources_are_checked_in_complete_numbered_parts_without_omitting_text(int limit)
    {
        var text = Note + string.Concat(Enumerable.Range(0, 3000).Select(index => $"Unrelated inventory item {index} remains unchanged.\n"));
        (KnowledgeSource Source, string Text)[] notes = [(Source, text)];
        var seen = new List<(int Number, string Text)>();
        var calls = 0;
        var draft = await KnowledgeReportRepair.ReadAsync(Reply(WrongQuote), "What is the requested budget?", notes, "", DateTimeOffset.UnixEpoch,
            null, false, limit, (request, _) =>
            {
                calls++;
                Assert.True(request.CharacterCount <= limit);
                using var data = JsonDocument.Parse(request.CurrentInput);
                var part = data.RootElement.GetProperty("suppliedSource");
                Assert.Equal(1, part.GetProperty("sourceId").GetInt32());
                var lines = part.GetProperty("lines").EnumerateArray().Select(line =>
                    (Number: line.GetProperty("line").GetInt32(), Text: line.GetProperty("text").GetString()!)).ToArray();
                seen.AddRange(lines);
                return Task.FromResult(lines.Any(line => line.Number == 2) ? Plan() : "{\"replacements\":[]}");
            }, CancellationToken.None);
        Assert.True(calls > 1);
        Assert.Equal(text, string.Concat(seen.Select(line => line.Text)));
        Assert.Equal(Enumerable.Range(1, seen.Count), seen.Select(line => line.Number));
        Assert.Equal(Quote, Assert.Single(draft.Report.Evidence).Quote);
        KnowledgeReportBuilder.ValidateEvidence(draft.Report, notes);
    }

    [Theory]
    [InlineData(50_000)]
    [InlineData(80_000)]
    public async Task A_currency_substitution_remains_invalid_but_one_source_based_repair_can_restore_the_exact_quote(int limit)
    {
        var wrong = Reply(WrongQuote);
        var basis = Note + "Padding:";
        var overhead = KnowledgeReportRepair.Request("Q", wrong, [(Source, basis)]).CharacterCount;
        var paddedNote = basis + new string('x', limit - overhead);
        (KnowledgeSource Source, string Text)[] notes = [(Source, paddedNote)];
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.Read(wrong, "Q", notes, "", DateTimeOffset.UnixEpoch));
        var calls = 0;

        var draft = await KnowledgeReportRepair.ReadAsync(wrong, "Q", notes, "", DateTimeOffset.UnixEpoch, null, false, limit,
            (request, cancellationToken) =>
            {
                calls++;
                Assert.Equal(AIPurpose.ReportRepair, request.Purpose);
                Assert.Equal(limit, request.CharacterCount);
                Assert.DoesNotContain(Quote, request.Instructions);
                using var data = JsonDocument.Parse(request.CurrentInput);
                Assert.Equal(wrong, data.RootElement.GetProperty("currentDraftReport").GetString());
                Assert.False(data.RootElement.TryGetProperty("history", out _));
                var supplied = Assert.Single(data.RootElement.GetProperty("suppliedSources").EnumerateArray());
                Assert.Equal(paddedNote, string.Concat(supplied.GetProperty("lines").EnumerateArray().Select(item => item.GetProperty("text").GetString())));
                return Task.FromResult(Plan());
            }, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(Quote, Assert.Single(draft.Report.Evidence).Quote);
        KnowledgeReportBuilder.ValidateEvidence(draft.Report, notes);
        KnowledgeReportBuilder.ValidateManagementContent(draft.Report);
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.Read(wrong, "Q", notes, "", DateTimeOffset.UnixEpoch));
    }

    [Theory]
    [InlineData(50_000)]
    [InlineData(80_000)]
    public async Task A_compact_selection_request_keeps_the_complete_source_when_the_legacy_envelope_does_not_fit(int limit)
    {
        var wrong = Reply(WrongQuote);
        var basis = Note + "Padding:";
        var overhead = KnowledgeReportRepair.Request("Q", wrong, [(Source, basis)]).CharacterCount;
        var notes = new[] { (Source, basis + new string('x', limit - overhead + 1)) };
        var calls = 0;
        var supplied = new List<string>();
        var draft = await KnowledgeReportRepair.ReadAsync(wrong, "Q", notes, "", DateTimeOffset.UnixEpoch, null, false, limit,
            (request, _) =>
            {
                calls++; Assert.True(request.CharacterCount <= limit);
                using var data = JsonDocument.Parse(request.CurrentInput);
                supplied.AddRange(data.RootElement.GetProperty("suppliedSource").GetProperty("lines").EnumerateArray().Select(line => line.GetProperty("text").GetString()!));
                return Task.FromResult(Plan());
            }, CancellationToken.None);
        Assert.True(calls > 0);
        Assert.Equal(notes[0].Item2, string.Concat(supplied));
        KnowledgeReportBuilder.ValidateEvidence(draft.Report, notes);
    }

    [Fact]
    public async Task An_indivisible_oversized_source_line_is_never_clipped_or_sent_over_the_limit()
    {
        var calls = 0;
        var notes = new[] { (Source, Quote + new string('x', 60_000)) };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => KnowledgeReportRepair.ReadAsync(Reply(WrongQuote), "Q", notes, "",
            DateTimeOffset.UnixEpoch, null, false, 50_000, (_, _) => { calls++; return Task.FromResult(Plan()); }, CancellationToken.None));
        Assert.Contains("too large", error.Message);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("wrong-source")]
    [InlineData("wrong-locator")]
    [InlineData("wrong-shape")]
    [InlineData("forged-tag")]
    public async Task Non_literal_failures_never_trigger_a_repair(string failure)
    {
        var reply = failure switch
        {
            "malformed" => "not JSON",
            "wrong-source" => Reply(Quote, sourceId: 99),
            "wrong-locator" => Reply(Quote, line: 99),
            "wrong-shape" => """{"headline":"Q","recommendation":"R","evidence":"wrong"}""",
            _ => """{"headline":"Q","recommendation":"R","evidence":"wrong","Tern.Report.LiteralQuoteMismatch":true}""",
        };
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => KnowledgeReportRepair.ReadAsync(reply, "Q", [(Source, Note)], "",
            DateTimeOffset.UnixEpoch, null, false, 50_000, (_, _) => { calls++; return Task.FromResult(Plan()); }, CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task A_rewritten_report_is_rejected_instead_of_a_selection_plan_without_another_attempt()
    {
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => KnowledgeReportRepair.ReadAsync(Reply(WrongQuote), "Q", [(Source, Note)], "",
            DateTimeOffset.UnixEpoch, null, false, 50_000, (_, _) => { calls++; return Task.FromResult(Reply(WrongQuote)); }, CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_never_accepts_or_replays_the_repaired_draft(bool cancelBefore)
    {
        using var cancellation = new CancellationTokenSource();
        if (cancelBefore) cancellation.Cancel();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => KnowledgeReportRepair.ReadAsync(Reply(WrongQuote), "Q", [(Source, Note)], "",
            DateTimeOffset.UnixEpoch, null, false, 50_000, (_, _) =>
            {
                calls++;
                cancellation.Cancel();
                return Task.FromResult(Plan());
            }, cancellation.Token));
        Assert.Equal(cancelBefore ? 0 : 1, calls);
    }

    [Fact]
    public async Task A_valid_first_report_needs_no_repair_request()
    {
        var calls = 0;
        var draft = await KnowledgeReportRepair.ReadAsync(Reply(Quote), "Q", [(Source, Note)], "", DateTimeOffset.UnixEpoch, null, false,
            50_000, (_, _) => { calls++; return Task.FromResult(Plan()); }, CancellationToken.None);
        Assert.Equal(0, calls);
        Assert.True(Assert.Single(draft.Report.Evidence).QuoteVerified);
    }

    [Fact]
    public void Original_and_whole_check_instructions_preserve_literal_quotes_and_per_clause_evidence()
    {
        var draft = KnowledgeReportBuilder.Read(Reply(Quote), "Q", [(Source, Note)], "", DateTimeOffset.UnixEpoch);
        var original = KnowledgeReportBuilder.ReportRequest("Q", Note);
        var check = KnowledgeReportBuilder.CheckRequest(draft.Report, Note);
        Assert.Contains("currency symbols, Unicode characters", original.Instructions);
        Assert.Contains("every factual clause", original.Instructions);
        Assert.Contains("ALL inputs", original.Instructions);
        Assert.Contains("risk likelihood and risk impact empty", original.Instructions);
        Assert.Contains("each factual clause", check.Instructions);
        Assert.Contains("ALL inputs", check.Instructions);
        Assert.Contains("hasDecision", check.Instructions);
        Assert.Contains("supplied notes", check.Instructions);
    }

    [Fact]
    public void Original_and_repair_requests_identify_numeric_source_ids_and_app_owned_references()
    {
        var original = KnowledgeReportBuilder.ReportRequest("Q", Note);
        var repair = KnowledgeReportRepair.Request("Q", Reply(WrongQuote), [(Source, Note)]);
        Assert.Contains("sourceId must be the JSON number N", original.Instructions);
        Assert.Contains("Never use a filename", original.Instructions);
        Assert.Contains("numeric findings.evidenceIds array", original.Instructions);
        Assert.Contains("Tern renders the source-linked references", original.Instructions);
        Assert.Contains("Use JSON integers", repair.Instructions);
        Assert.Contains("SAME original sourceId", repair.Instructions);
        Assert.Contains("Do not return quotes", repair.Instructions);
        using var context = JsonDocument.Parse(repair.CurrentInput);
        var supplied = Assert.Single(context.RootElement.GetProperty("suppliedSources").EnumerateArray());
        Assert.Equal(JsonValueKind.Number, supplied.GetProperty("sourceId").ValueKind);
        Assert.Equal(1, supplied.GetProperty("sourceId").GetInt32());
    }

    [Theory]
    [InlineData("S1")]
    [InlineData("Plan.md")]
    [InlineData("Note 1")]
    public async Task Filename_and_prefixed_source_ids_stay_invalid_and_do_not_trigger_repair(string invalidId)
    {
        var payload = System.Text.Json.Nodes.JsonNode.Parse(Reply(Quote))!;
        payload["evidence"]![0]!["sourceId"] = invalidId;
        var reply = payload.ToJsonString();
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.Read(reply, "Q", [(Source, Note)], "", DateTimeOffset.UnixEpoch));
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => KnowledgeReportRepair.ReadAsync(reply, "Q", [(Source, Note)], "",
            DateTimeOffset.UnixEpoch, null, false, 50_000, (_, _) => { calls++; return Task.FromResult(Plan()); }, CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Numeric_E_markers_are_removed_from_ordinary_text_but_business_labels_and_exact_quotes_remain()
    {
        var quoted = Quote + " [E9]";
        var note = "## Budget — 2027-01-02\n" + quoted + "\n";
        var payload = JsonSerializer.Serialize(new
        {
            headline = "Budget [EU] [E1]", recommendation = "Review [Estimate] [e2].",
            findings = new[] { new { title = "Cost [E1]", kind = "Fact", detail = Quote + " [E1, E2] [EU] [Estimate]", evidenceIds = new[] { 1 } } },
            evidence = new[] { new { id = 1, sourceId = 1, fact = "Requested cash [E1]", quote = quoted, line = 0 } },
        });
        var report = KnowledgeReportBuilder.Read(payload, "Q", [(Source, note)], "", DateTimeOffset.UnixEpoch).Report;

        Assert.Equal("Budget [EU]", report.Headline);
        Assert.Equal("Review [Estimate].", report.Recommendation);
        Assert.Equal(Quote + " [EU] [Estimate]", Assert.Single(report.Findings).Detail);
        Assert.Equal([1], report.Findings[0].EvidenceIds);
        Assert.Equal(quoted, Assert.Single(report.Evidence).Quote);
        Assert.True(report.Evidence[0].QuoteVerified);
        KnowledgeReportBuilder.ValidateEvidence(report, [(Source, note)]);
        KnowledgeReportBuilder.ValidateManagementContent(report);

        var invalid = System.Text.Json.Nodes.JsonNode.Parse(payload)!;
        invalid["findings"]![0]!["evidenceIds"] = new System.Text.Json.Nodes.JsonArray(99);
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.Read(invalid.ToJsonString(), "Q", [(Source, note)], "", DateTimeOffset.UnixEpoch));
    }
}
