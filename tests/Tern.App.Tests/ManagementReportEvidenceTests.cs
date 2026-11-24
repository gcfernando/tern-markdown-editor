using System.Text.Json;
using Tern.App.Knowledge;
using Tern.Export;

namespace Tern.App.Tests;

public sealed class ManagementReportEvidenceTests
{
    private static readonly KnowledgeSource Source = new(1, @"C:\w", @"C:\w\Plan.md", "Plan.md", "Plan", "Wrong search heading", 200, "old excerpt", "old digest", [])
        { DocumentedDate = "1999-01-01" };
    private static readonly DateTimeOffset Date = DateTimeOffset.UnixEpoch;
    private static string Reply(string quote, int sourceId = 1, int line = 0) => JsonSerializer.Serialize(new
    {
        title = "Pilot", headline = "What is known?", recommendation = "The pilot is proposed.",
        evidence = new[] { new { id = 1, fact = "Budget and staffing", sourceId, quote, line } },
        findings = new[] { new { title = "Effort", kind = "Fact", detail = "Staff time is separate.", keywords = new[] { "Staff time" }, evidenceIds = new[] { 1 } } },
    });

    [Fact]
    public void Proof_uses_the_actual_heading_line_date_and_text_not_the_search_hit()
    {
        const string note = "# Plan\n## Pilot 2027-04-02\n```md\n# Fake code heading\n```\nStaff time is separate.\n";
        var report = KnowledgeReportBuilder.Read(Reply("Staff time is separate."), "Q", [(Source, note)], "", Date).Report;
        var evidence = Assert.Single(report.Evidence);
        Assert.True(evidence.QuoteVerified);
        Assert.Equal("Pilot 2027-04-02", evidence.Section);
        Assert.Equal(6, evidence.Line);
        Assert.Equal("2027-04-02", evidence.DocumentedDate);
        Assert.Equal("Staff time is separate.", evidence.Quote);
        Assert.NotEqual("old digest", evidence.SourceDigest);
        KnowledgeReportBuilder.ValidateEvidence(report, [(Source, note)]);
    }

    [Theory]
    [InlineData("Budget is €9,000.")]
    [InlineData("Budget is €2,400!")]
    [InlineData("Budget  is €2,400.")]
    public void A_quotation_cannot_change_numbers_punctuation_or_internal_spacing(string invented)
    {
        var error = Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.Read(Reply(invented), "Q", [(Source, "Budget is €2,400.")], "", Date));
        Assert.Contains("quotation", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_source_that_was_not_supplied_is_rejected_instead_of_silently_dropped() =>
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.Read(Reply("Budget is €2,400.", 9), "Q", [(Source, "Budget is €2,400.")], "", Date));

    [Fact]
    public void Repeated_quotes_need_a_real_line_and_never_pick_the_first_release_blindly()
    {
        const string note = "## 1.0\nApproved.\n## 2.0\nApproved.\n";
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.Read(Reply("Approved."), "Q", [(Source, note)], "", Date));
        var report = KnowledgeReportBuilder.Read(Reply("Approved.", line: 4), "Q", [(Source, note)], "", Date).Report;
        Assert.Equal("2.0", report.Evidence[0].Section);
        Assert.Equal(4, report.Evidence[0].Line);
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.Read(Reply("Approved.", line: 3), "Q", [(Source, note)], "", Date));
    }

    [Fact]
    public void The_quote_is_restored_to_the_actual_source_line_endings()
    {
        const string note = "## Details\r\nFirst line.\r\nSecond line.\r\n";
        var report = KnowledgeReportBuilder.Read(Reply("First line.\nSecond line."), "Q", [(Source, note)], "", Date).Report;
        Assert.Equal("First line.\r\nSecond line.", report.Evidence[0].Quote);
        Assert.Equal(2, report.Evidence[0].Line);
    }

    [Fact]
    public void Supplied_prefixes_cannot_validate_a_quote_from_the_unread_tail() =>
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.Read(Reply("Unread tail"), "Q", [(Source, "Admitted prefix")], "", Date));

    [Fact]
    public void Source_fingerprints_and_locators_are_rechecked_after_a_report_is_edited_or_restored()
    {
        const string note = "## Budget\nBudget is €2,400.\n";
        var report = KnowledgeReportBuilder.Read(Reply("Budget is €2,400."), "Q", [(Source, note)], "", Date).Report;
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.ValidateEvidence(report, [(Source, note + "New context.")]));
        var forged = report with { Evidence = [report.Evidence[0] with { Section = "Different section" }] };
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.ValidateEvidence(forged, [(Source, note)]));
    }

    [Fact]
    public void The_answer_citation_order_is_never_used_to_invent_report_evidence()
    {
        var answer = new KnowledgeAnswer("A finding.", "", [new KnowledgeCitation(Source, "Used")], false);
        var report = KnowledgeReportBuilder.Read("""{"headline":"Q","recommendation":"R","evidence":null,"findings":null}""", "Q", [(Source, "Text")], "", Date, answer).Report;
        Assert.Empty(report.Evidence);
        Assert.Empty(report.Findings);
        Assert.Contains("No source-specific evidence", KnowledgeReportBuilder.ToMarkdown(report));
    }

    [Fact]
    public void A_paraphrase_without_a_verbatim_passage_stays_explicitly_unverified()
    {
        const string reply = """{"headline":"Q","recommendation":"R","evidence":[{"sourceId":1,"fact":"A cheaper pilot"}]}""";
        var report = KnowledgeReportBuilder.Read(reply, "Q", [(Source, "The budget is €2,400.")], "", Date).Report;
        Assert.False(report.Evidence[0].QuoteVerified);
        Assert.Empty(report.Evidence[0].Quote);
        Assert.Empty(report.Evidence[0].Section);
        Assert.Contains("No exact quotation was verified", KnowledgeReportBuilder.ToMarkdown(report));
    }

    [Fact]
    public void Full_findings_and_lists_are_not_cut_at_the_old_600_character_or_item_caps()
    {
        var detail = new string('x', 1_300) + "END-OF-DETAIL";
        var payload = JsonSerializer.Serialize(new
        {
            headline = "Q", recommendation = "R",
            findings = Enumerable.Range(1, 30).Select(i => new { title = $"Finding {i}", kind = "Interpretation", detail }),
            actions = Enumerable.Range(1, 30).Select(i => new { action = $"Action {i}" }),
        });
        var report = KnowledgeReportBuilder.Read(payload, "Q", [(Source, "Text")], "", Date).Report;
        Assert.Equal(30, report.Findings.Count);
        Assert.Equal(30, report.Actions.Count);
        Assert.All(report.Findings, f => Assert.Equal(detail, f.Detail));
        var markdown = KnowledgeReportBuilder.ToMarkdown(report);
        Assert.Contains("Finding 30", markdown);
        Assert.Contains("END-OF-DETAIL", markdown);
        Assert.Contains("Action 30", markdown);
    }

    [Fact]
    public void Whole_report_checks_include_detail_costs_actions_quotes_conditions_and_final_assessment()
    {
        var report = new DecisionReport
        {
            Title = "T", Question = "Q", Date = "D", Headline = "H", Recommendation = "R",
            Findings = [new("Finding", "Target", "DETAIL_SENTINEL") { Derivation = "INPUT_SENTINEL", Limitations = "LIMIT_SENTINEL" }],
            Costs = [new("Cash", "COST_SENTINEL", "Cost note")], Actions = [new("ACTION_SENTINEL", "Owner", "Due", "Planned")],
            SummaryConditions = ["CONDITION_SENTINEL"], FinalAssessment = "FINAL_SENTINEL",
            Evidence = [new("Fact", "Note", "Section") { Quote = "QUOTE_SENTINEL" }],
        };
        var request = KnowledgeReportBuilder.CheckRequest(report, "SOURCE_SENTINEL");
        Assert.Equal(AIPurpose.ReportCheck, request.Purpose);
        foreach (var value in new[] { "DETAIL_SENTINEL", "INPUT_SENTINEL", "LIMIT_SENTINEL", "COST_SENTINEL", "ACTION_SENTINEL", "CONDITION_SENTINEL", "FINAL_SENTINEL", "QUOTE_SENTINEL", "SOURCE_SENTINEL" })
        {
            Assert.Contains(value, request.UserInput);
            Assert.DoesNotContain(value, request.Instructions);
        }
    }

    [Fact]
    public void All_unsupported_issues_remain_visible_instead_of_stopping_after_four()
    {
        var issues = Enumerable.Range(1, 12).Select(i => $"Issue {i}: " + new string('x', 170)).ToArray();
        Assert.Equal(issues, KnowledgeReportBuilder.ReadUnsupported(JsonSerializer.Serialize(new { unsupported = issues })));
    }

    [Fact]
    public void Large_complete_report_and_check_replies_preserve_their_final_content_without_a_fixed_character_ceiling()
    {
        var detail = new string('x', 600_000) + "FINAL_SOURCE_CONTENT";
        var reply = JsonSerializer.Serialize(new
        {
            headline = "Source review", recommendation = "Read the source detail.",
            findings = new[] { new { title = "Detail", kind = "Fact", detail, evidenceIds = new[] { 1 } } },
            evidence = new[] { new { id = 1, sourceId = Source.Id, fact = "Detail", quote = detail } },
        });
        var report = KnowledgeReportBuilder.Read(reply, "Q", [(Source, detail)], "", Date).Report;
        Assert.Equal(detail, Assert.Single(report.Findings).Detail);
        Assert.Equal(detail, Assert.Single(report.Evidence).Quote);
        KnowledgeReportBuilder.ValidateEvidence(report, [(Source, detail)]);
        KnowledgeReportBuilder.ValidateManagementContent(report);
        Assert.Equal(detail, Assert.Single(KnowledgeReportBuilder.ReadUnsupported(JsonSerializer.Serialize(new { unsupported = new[] { detail } }))));
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.Read(new string('x', 600_000), "Q", [(Source, detail)], "", Date));
    }

    private static DecisionReport GroundedReport()
    {
        const string note = "96 of 240 requests concern password resets.";
        const string reply = """
            {"headline":"Review demand","recommendation":"Consider a focused pilot.",
             "findings":[{"title":"Demand","kind":"Fact","detail":"96 of 240 requests concern password resets.","evidenceIds":[1]}],
             "evidence":[{"id":1,"sourceId":1,"fact":"Demand counts","quote":"96 of 240 requests concern password resets."}]}
            """;
        return KnowledgeReportBuilder.Read(reply, "Q", [(Source, note)], "", Date).Report;
    }

    [Fact]
    public void Legacy_thin_reports_can_load_but_cannot_pass_the_production_sharing_gate()
    {
        var report = KnowledgeReportBuilder.Read("""{"headline":"Q","recommendation":"R"}""", "Q", [(Source, "Text")], "", Date).Report;
        Assert.Equal("R", report.Recommendation);
        var error = Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.ValidateManagementContent(report));
        Assert.Contains("detailed findings", error.Message);
        Assert.Contains("again", error.Message);
    }

    [Theory]
    [InlineData("Fact")]
    [InlineData("Target")]
    [InlineData("Calculation")]
    public void Every_source_supported_finding_requires_its_own_verified_evidence_links(string kind)
    {
        var report = GroundedReport();
        var finding = report.Findings[0] with { Kind = kind, EvidenceIds = [], Derivation = "96 / 240 × 100 = 40%." };
        var error = Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.ValidateManagementContent(report with { Findings = [finding] }));
        Assert.Contains("supporting passage", error.Message);
    }

    [Fact]
    public void Grounded_details_pass_but_unverified_extra_evidence_or_forged_provenance_does_not()
    {
        var report = GroundedReport();
        KnowledgeReportBuilder.ValidateManagementContent(report);
        var unverified = report with { Evidence = [report.Evidence[0], new ReportEvidence("Unproven", "Other", "") { Id = 2, SourceId = 1 }] };
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.ValidateManagementContent(unverified));
        var forged = report with { Evidence = [report.Evidence[0] with { SourceDigest = "" }] };
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.ValidateManagementContent(forged));
        var unknownLink = report with { Findings = [report.Findings[0] with { EvidenceIds = [99] }] };
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.ValidateManagementContent(unknownLink));
    }

    [Fact]
    public void Calculations_require_an_explicit_method_as_well_as_source_inputs()
    {
        var report = GroundedReport();
        var calculation = report.Findings[0] with { Kind = "Calculation", Detail = "Password-reset requests represent 40%." };
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.ValidateManagementContent(report with { Findings = [calculation] }));
        KnowledgeReportBuilder.ValidateManagementContent(report with { Findings = [calculation with { Derivation = "96 / 240 × 100 = 40%." }] });
    }

    [Theory]
    [InlineData("Interpretation")]
    [InlineData("Recommendation")]
    public void An_unbased_interpretation_needs_an_explicit_matching_missing_information_record(string kind)
    {
        var report = GroundedReport();
        var speculative = new ReportFinding("Possible benefit", kind, "A queue might help response times.")
            { Limitations = "The effect has not been measured; no supporting evidence is supplied." };
        var draft = report with { Findings = [report.Findings[0], speculative] };
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.ValidateManagementContent(draft));
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.ValidateManagementContent(draft with
            { Missing = [new("Unrelated item", "A gap", "Owner")] }));
        var explicitGap = draft with { Missing = [new("Possible benefit", "The proposed benefit has no measured support.", "Support lead")] };
        KnowledgeReportBuilder.ValidateManagementContent(explicitGap);
        var markdown = KnowledgeReportBuilder.ToMarkdown(explicitGap);
        Assert.Contains("No source-specific evidence", markdown);
        Assert.Contains("The proposed benefit has no measured support.", markdown);
    }

    [Fact]
    public void Strict_content_validation_does_not_add_an_arbitrary_word_minimum()
    {
        var report = KnowledgeReportBuilder.Read(Reply("Pending."), "Q", [(Source, "Pending.")], "", Date).Report;
        report = report with { Findings = [new("Status", "Fact", "Pending.") { EvidenceIds = [1] }], Evidence = [report.Evidence[0] with { Fact = "The status is pending." }] };
        KnowledgeReportBuilder.ValidateManagementContent(report);
    }
}
