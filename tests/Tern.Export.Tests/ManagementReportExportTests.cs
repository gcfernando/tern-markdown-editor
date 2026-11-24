namespace Tern.Export.Tests;

public sealed class ManagementReportExportTests
{
    private static DecisionReport Report(bool decision = true) => new()
    {
        Title = "Service desk pilot", Question = "Should the pilot proceed?", Date = "23 November 2026", HasDecision = decision,
        Headline = decision ? "Reserve the budget conditionally" : "What is known about the pilot?",
        Recommendation = "Reserve up to €2,400, subject to sponsor approval. Do not authorize launch yet.",
        Metrics = [new("Cash", "€2,400", "Excludes staff time"), new("Staff effort", "40 hours", "24 available; 16 unallocated")],
        SummaryConditions = ["Resolve staffing, dates, privacy and training."], SummaryNextAction = "Ask the support lead to confirm the gap.",
        Findings = [new("Effort", "Fact", "Staff effort is separate from cash.") { Keywords = ["Staff effort"], EvidenceIds = [1] }],
        Evidence = [new("Staff effort is separate", "Plan", "Costs") { Id = 1, SourceId = 1, RelativePath = "Plan.md", Line = 4,
            Quote = "Budget €2,400; Ninety-six requests; café — approved?", QuoteVerified = true }],
        FinalAssessment = "Resolve the conditions before launch.",
    };

    [Fact]
    public void All_findings_evidence_and_a_multi_page_table_cell_survive_export()
    {
        var baseReport = Report();
        var longCell = string.Concat(Enumerable.Repeat("Long cost context that must remain visible. ", 400)) + "COST_TAIL";
        var report = baseReport with
        {
            Findings = Enumerable.Range(1, 30).Select(i => new ReportFinding($"Finding {i}", "Interpretation", $"Detail marker {i} remains in the report.")).ToArray(),
            Costs = [new("Staff", "40 hours", longCell)],
            Evidence = Enumerable.Range(1, 24).Select(i => new ReportEvidence($"Evidence claim {i}", "Plan", "Costs")
                { Id = i, SourceId = 1, Quote = $"Exact source passage marker {i}.", QuoteVerified = true, Line = i }).ToArray(),
        };
        var result = DecisionReportPdf.Export(report);
        var text = PdfText.AllText(result.Data);
        Assert.False(result.Truncated);
        Assert.Contains("Finding 30", text);
        Assert.Contains("COST_TAIL", text);
        Assert.Contains("Evidence claim 24", text);
        Assert.Contains("Exact source passage marker 24.", text);
        Assert.True(result.PageCount > 4);
        using var pdf = PdfText.Open(result.Data);
        Assert.All(pdf.Pages.Cast<PdfSharp.Pdf.PdfPage>(), p =>
        {
            Assert.InRange(p.Width.Point, 594, 597);
            Assert.InRange(p.Height.Point, 840, 843);
        });
    }

    [Fact]
    public void Executive_page_and_final_signature_form_have_distinct_professional_content()
    {
        var result = DecisionReportPdf.Export(Report());
        using var pdf = PdfText.Open(result.Data);
        var first = PdfText.PageText(pdf.Pages[0]);
        var last = PdfText.PageText(pdf.Pages[pdf.PageCount - 1]);
        Assert.Contains("Reserve", first);
        Assert.Contains("€2,400", first);
        Assert.Contains("40 hours", first);
        Assert.Contains("16 unallocated", first);
        Assert.DoesNotContain("Exact source", first);
        Assert.Contains("Review and decision", last);
        foreach (var label in new[] { "Signature", "Printed name", "Title or role", "Date", "Decision authorized by" }) Assert.Contains(label, last);
        var all = PdfText.AllText(result.Data);
        Assert.DoesNotContain("reviewed it before sharing", all);
        Assert.DoesNotContain("Decision record", all);
        Assert.DoesNotContain("01 /", all);
    }

    [Fact]
    public void Information_reports_do_not_invent_a_management_decision_or_approval_form()
    {
        var report = Report(false);
        var pdf = DecisionReportPdf.Export(report);
        var text = PdfText.AllText(pdf.Data);
        Assert.Contains("Main question", text);
        Assert.Contains("Key finding", text);
        Assert.Contains("Review and acknowledgement", text);
        Assert.DoesNotContain("Decision authorized by", text);
        Assert.DoesNotContain("Approve, subject", text);
        var markdown = ManagementReportMarkdown.Export(report);
        Assert.Contains("## Review and acknowledgement", markdown);
        Assert.DoesNotContain("Decision authorized by", markdown);
    }

    [Fact]
    public void Verbatim_quotation_characters_and_meaningful_markdown_keywords_are_preserved()
    {
        var report = Report();
        var text = PdfText.AllText(DecisionReportPdf.Export(report).Data);
        Assert.Contains("Budget €2,400; Ninety-six requests; café — approved?", text);
        var markdown = ManagementReportMarkdown.Export(report);
        Assert.Contains("**Staff effort** is separate from cash.", markdown);
        Assert.Contains("> Budget €2,400; Ninety-six requests; café — approved?", markdown);
        Assert.Contains("Plan.md", markdown);
        Assert.Contains("**Line:** 4", markdown);
        Assert.Contains("## Final assessment", markdown);
        Assert.Contains("Title or role:", markdown);
        Assert.DoesNotContain("A person reviewed it", markdown);
    }

    [Fact]
    public void Short_evidence_keeps_its_title_locator_date_and_quotation_on_the_same_page()
    {
        var report = Report() with
        {
            Evidence = Enumerable.Range(1, 30).Select(i => new ReportEvidence($"PROOFTITLE{i}END", $"Note {i}", "Relevant section")
            {
                Id = i, SourceId = i, RelativePath = $"LOCATOR_{i}.md", Line = i, DocumentedDate = "2026-11-23",
                Quote = $"QUOTATION{i}END is the exact supporting passage, with sufficient context to explain this short finding.", QuoteVerified = true,
            }).ToArray(),
        };
        using var pdf = PdfText.Open(DecisionReportPdf.Export(report).Data);
        var pages = pdf.Pages.Cast<PdfSharp.Pdf.PdfPage>().Select(PdfText.PageText).ToArray();
        for (var i = 1; i <= 30; i++)
        {
            var headingPage = Array.FindIndex(pages, p => p.Contains($"PROOFTITLE{i}END", StringComparison.Ordinal));
            var locatorPage = Array.FindIndex(pages, p => p.Contains($"LOCATOR_{i}.md", StringComparison.Ordinal));
            var quotePage = Array.FindIndex(pages, p => p.Contains($"QUOTATION{i}END", StringComparison.Ordinal));
            Assert.True(headingPage >= 0, $"Missing heading {i}");
            Assert.Equal(headingPage, locatorPage);
            Assert.Equal(headingPage, quotePage);
        }
    }

    [Fact]
    public void A_page_sized_final_assessment_keeps_its_actions_and_limitations_together()
    {
        var report = Report() with
        {
            Evidence = Enumerable.Range(1, 18).Select(i => new ReportEvidence($"Earlier evidence {i}", "Plan", "Sources")
                { Id = i, SourceId = 1, Quote = "A short supporting passage for this finding.", QuoteVerified = true }).ToArray(),
            FinalAssessment = "ASSESSMENT_MARKER: reserve the cash but do not authorize launch yet.",
            Actions = [new("ACTION_MARKER: resolve staffing.", "Support lead", "Before launch", "Needed")],
            Missing = [new("LIMIT_MARKER", "The effect has not been measured.", "Support lead")],
        };
        using var pdf = PdfText.Open(DecisionReportPdf.Export(report).Data);
        var pages = pdf.Pages.Cast<PdfSharp.Pdf.PdfPage>().Select(PdfText.PageText).ToArray();
        var assessment = Array.FindIndex(pages, p => p.Contains("ASSESSMENT_MARKER", StringComparison.Ordinal));
        Assert.True(assessment >= 0);
        Assert.Equal(assessment, Array.FindIndex(pages, p => p.Contains("ACTION_MARKER", StringComparison.Ordinal)));
        Assert.Equal(assessment, Array.FindIndex(pages, p => p.Contains("LIMIT_MARKER", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("Proposed cash maximum", "Proposed response target", "Target, not a measured result")]
    [InlineData("Requested amount", "Proposed response measure", "Target, not a measured result")]
    public void The_cover_keeps_cash_target_and_comparable_baseline_when_they_are_late_in_the_metric_list(string cashLabel, string targetLabel, string targetNote)
    {
        var report = Report() with
        {
            Metrics =
            [
                new("Requests in four-week review", "240", "Review-volume context"),
                new("Median first response", "9 working hours", "Measured during review"),
                new("Requests waiting over 16 working hours", "48", "Long-wait context"),
                new(targetLabel, "6 working hours or less", targetNote),
                new(cashLabel, "€2,400", "€1,600 setup and €800 training; subject to sponsor approval"),
                new("Proposed staff effort", "Up to 40 hours", "Separate from cash; 16 hours remain unallocated"),
            ],
            Costs = [new("Staff effort", "Up to 40 hours", "24 available; 16 unallocated; separate from cash")],
        };
        using var pdf = PdfText.Open(DecisionReportPdf.Export(report).Data);
        var pages = pdf.Pages.Cast<PdfSharp.Pdf.PdfPage>().Select(PdfText.PageText).ToArray();
        var cover = pages[0];
        // PdfText decodes glyph strings but does not interpret Td/Tm line positions.
        // A correctly drawn two-line card can therefore read "workinghours". The
        // product-cover-render5 geometry/preview confirms the real values and labels.
        // Normalize whitespace only: all digits, currency, qualifiers and labels remain required.
        static string FlowText(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
        var coverFlow = FlowText(cover);
        foreach (var value in new[] { "9 working hours", "6 working hours or less", "Target, not a measured result", "€2,400", "Up to 40 hours", "16 unallocated", "Median first response", targetLabel, cashLabel })
            Assert.Contains(FlowText(value), coverFlow);
        Assert.DoesNotContain(FlowText("Review-volume context"), coverFlow);
        Assert.DoesNotContain(FlowText("Long-wait context"), coverFlow);
        // Both early unselected facts and their original values survive on later pages.
        var details = FlowText(string.Join(" ", pages.Skip(1)));
        Assert.Contains(FlowText("Requests in four-week review"), details);
        Assert.Contains("240", details);
        Assert.Contains(FlowText("Requests waiting over 16 working hours"), details);
        Assert.Contains("48", details);
    }

    [Fact]
    public void Clipped_cover_text_is_marked_and_its_full_content_has_a_meaningful_detail_heading()
    {
        var report = Report() with
        {
            Question = "QUESTIONHEAD " + string.Concat(Enumerable.Repeat("extended question context ", 30)) + "QUESTIONTAIL",
            RecommendationWhy = "WHYHEAD " + string.Concat(Enumerable.Repeat("additional rationale ", 30)) + "WHYTAIL",
            SummaryConditions = [string.Concat(Enumerable.Repeat("Resolve the documented condition. ", 25)) + "CONDITIONTAIL"],
            SummaryRisks = [string.Concat(Enumerable.Repeat("This risk needs confirmation. ", 25)) + "RISKTAIL"],
            Metrics =
            [
                new("Long metric", "VALUEHEAD " + string.Concat(Enumerable.Repeat("extended value ", 20)) + "VALUETAIL",
                    "CARDNOTE " + string.Concat(Enumerable.Repeat("supporting context ", 20)) + "NOTETAIL"),
                new("Baseline", "9 working hours", "Observed"), new("Cash", "€2,400", "Proposed"),
            ],
        };
        using var pdf = PdfText.Open(DecisionReportPdf.Export(report).Data);
        var pages = pdf.Pages.Cast<PdfSharp.Pdf.PdfPage>().Select(PdfText.PageText).ToArray();
        static string Flow(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
        var cover = Flow(pages[0]);
        var question = cover.IndexOf("QUESTIONHEAD", StringComparison.Ordinal);
        var panel = cover.IndexOf("DECISIONREQUESTED", StringComparison.Ordinal);
        Assert.True(question >= 0 && panel > question);
        Assert.Contains("…", cover[question..panel]);
        var value = cover.IndexOf("VALUEHEAD", StringComparison.Ordinal);
        var label = cover.IndexOf("Longmetric", StringComparison.Ordinal);
        var note = cover.IndexOf("CARDNOTE", StringComparison.Ordinal);
        var nextCard = cover.IndexOf("9workinghours", StringComparison.Ordinal);
        Assert.True(value >= 0 && label > value && note > label && nextCard > note);
        Assert.Contains("…", cover[value..label]);
        Assert.Contains("…", cover[note..nextCard]);
        var details = Flow(string.Join(" ", pages.Skip(1)));
        Assert.Contains("Reportquestion—fulltext", details);
        foreach (var tail in new[] { "QUESTIONTAIL", "WHYTAIL", "CONDITIONTAIL", "RISKTAIL", "VALUETAIL", "NOTETAIL" }) Assert.Contains(tail, details);
    }

    [Fact]
    public void A_table_row_that_fits_a_fresh_page_is_not_split_between_pages()
    {
        var report = Report() with
        {
            Actions = Enumerable.Range(1, 12).Select(i => new ReportAction($"ROWSTART{i}X "
                + string.Concat(Enumerable.Repeat("Detailed action context for the responsible person. ", 12))
                + $"ROWEND{i}X", $"OWNER{i}X", "Before launch", "Needed")).ToArray(),
        };
        using var pdf = PdfText.Open(DecisionReportPdf.Export(report).Data);
        var pages = pdf.Pages.Cast<PdfSharp.Pdf.PdfPage>().Select(PdfText.PageText).ToArray();
        for (var i = 1; i <= 12; i++)
        {
            var start = Array.FindIndex(pages, p => p.Contains($"ROWSTART{i}X", StringComparison.Ordinal));
            Assert.True(start >= 0);
            Assert.Equal(start, Array.FindIndex(pages, p => p.Contains($"ROWEND{i}X", StringComparison.Ordinal)));
            Assert.Equal(start, Array.FindIndex(pages, p => p.Contains($"OWNER{i}X", StringComparison.Ordinal)));
        }
    }
}
