using Tern.App.Knowledge;
using Tern.Export;

namespace Tern.App.Tests;

public sealed class KnowledgeReportTests
{
    private static readonly KnowledgeSource Source = new(1, @"C:\w", @"C:\w\Budget_Review.md", "Budget_Review.md", "Budget", "Options", 3, "x", "d", []);
    private static readonly (KnowledgeSource, string)[] Notes = [(Source, "A one-week delay costs $18,000. Launch is 14 April.")];

    [Fact]
    public void Report_keeps_only_facts_from_selected_notes_and_flags_unknown_numbers()
    {
        const string reply = """
            {"title":"Launch","hasDecision":true,"headline":"Delay the launch?","decisionBy":"Steering Committee","status":"Unknown","recommendation":"Delay for $99,000",
            "options":[{"name":"Option A: Keep 14 April","recommended":true}],"missing":[{"item":"Revenue.","whyItMatters":"Cost","whoCanConfirm":""}],
            "evidence":[{"fact":"Delay costs $18,000","sourceId":1}]}
            """;
        var draft = KnowledgeReportBuilder.Read(reply, "Should we delay?", Notes, "Sara", new DateTimeOffset(2027, 4, 2, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal("", draft.Report.DecisionBy);
        Assert.Equal("", draft.Report.Status);
        Assert.Equal("2 April 2027", draft.Report.Date);
        Assert.Equal("Keep 14 April", Assert.Single(draft.Report.Options).Name);
        Assert.Equal("Revenue", draft.Report.Missing[0].Item);
        Assert.Equal("Project owner", draft.Report.Missing[0].WhoCanConfirm);
        var fact = Assert.Single(draft.Report.Evidence);
        Assert.Equal("Budget_Review", fact.Note);
        Assert.Contains("99,000", draft.NumbersToCheck);
        Assert.DoesNotContain("18,000", draft.NumbersToCheck);
    }

    [Fact]
    public void A_report_without_a_decision_never_keeps_invented_options()
    {
        const string reply = """{"title":"Meeting","hasDecision":false,"headline":"What was discussed?","recommendation":"The team reviewed the launch.","options":[{"name":"Invented"}]}""";
        var report = KnowledgeReportBuilder.Read(reply, "What was discussed?", Notes, "", DateTimeOffset.UnixEpoch).Report;
        Assert.False(report.HasDecision);
        Assert.Empty(report.Options);
        var markdown = KnowledgeReportBuilder.ToMarkdown(report);
        Assert.Contains("## Main question", markdown);
        Assert.Contains("## Key finding", markdown);
        Assert.DoesNotContain("Recommendation", markdown);
    }

    [Fact]
    public void Citation_markers_are_removed_and_are_not_flagged_as_numbers()
    {
        const string reply = """{"title":"Launch","hasDecision":false,"headline":"When?","recommendation":"Launch is 14 April [1].","evidence":[{"fact":"Launch is 14 April [1]","sourceId":1}]}""";
        var draft = KnowledgeReportBuilder.Read(reply, "When?", Notes, "", DateTimeOffset.UnixEpoch);
        Assert.Equal("Launch is 14 April.", draft.Report.Recommendation);
        Assert.Empty(draft.NumbersToCheck);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"title\":\"Only a title\"}")]
    public void Unusable_ai_replies_give_a_plain_message(string reply)
    {
        var error = Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.Read(reply, "Q", Notes, "", DateTimeOffset.UnixEpoch));
        Assert.DoesNotContain("JSON", error.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{broken}")]
    [InlineData("{}")]
    [InlineData("{\"unsupported\":null}")]
    [InlineData("{\"unsupported\":\"none\"}")]
    [InlineData("{\"unsupported\":{}}")]
    [InlineData("{\"unsupported\":[17]}")]
    [InlineData("{\"unsupported\":[null]}")]
    [InlineData("{\"unsupported\":[\"\"]}")]
    [InlineData("{\"unsupported\":[\"A real issue\",false]}")]
    public void Invalid_report_check_replies_cannot_claim_a_successful_check(string reply)
    {
        var error = Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.ReadUnsupported(reply));
        Assert.Equal("AI could not check this report. Please try again.", error.Message);
    }

    [Fact]
    public void Only_a_valid_empty_report_check_list_means_no_unsupported_statements() =>
        Assert.Empty(KnowledgeReportBuilder.ReadUnsupported("""{"unsupported":[]}"""));

    [Fact]
    public void Valid_report_check_issues_remain_available_to_the_reader() =>
        Assert.Equal(["Sales doubled", "Launch is tomorrow"], KnowledgeReportBuilder.ReadUnsupported(
            """{"unsupported":["Sales doubled","Launch is tomorrow"]}"""));

    [Fact]
    public void Long_notes_are_split_into_parts_that_fit_one_ai_request()
    {
        var text = string.Join('\n', Enumerable.Repeat("A line of project detail for the report.", 300));
        var room = KnowledgeReportBuilder.MaterialRoom(new string('q', 1500), 8_000);
        var parts = KnowledgeReportBuilder.NoteParts([(Source, text)], room);
        Assert.True(parts.Count >= 2);
        Assert.All(parts, part => Assert.True(part.Length <= room));
        Assert.True(KnowledgeReportBuilder.ReportPrompt(new string('q', 1500), parts[0]).Length <= 8_000);
    }

    [Theory]
    [InlineData("Summarize the main project", true)]
    [InlineData("project", true)]
    [InlineData("Launch", false)]
    [InlineData("What are the main risks?", false)]
    public void Vague_questions_are_recognised(string question, bool vague) => Assert.Equal(vague, KnowledgeRetrieval.IsVague(question));

    [Fact]
    public void Decision_report_is_a4_and_flows_across_pages()
    {
        var report = new DecisionReport
        {
            Title = "Launch", Question = "Delay?", PreparedBy = "Sara", Date = "2 April 2027", DecisionBy = "7 April", Status = "At risk", HasDecision = true,
            Headline = "Delay the launch?", Recommendation = "Launch in two phases.", Metrics = [new("QA", "86%", "112 of 130")], Reasons = ["Payment risk"],
            Options = [new("Keep", "Fast", "High", "$0", "14 Apr", "All", false), new("Phase", "Balanced", "Medium", "$7,500", "21 Apr", "200", true)],
            Risks = Enumerable.Range(1, 10).Select(i => new ReportRisk($"Risk {i} with a longer description that wraps", "High", "Medium", "Fix it", "Owner", "6 Apr")).ToArray(),
            Actions = Enumerable.Range(1, 12).Select(i => new ReportAction($"Action {i}", "Owner", "7 Apr", "Planned")).ToArray(),
            Missing = [new("Revenue", "Changes the cost", "Finance")], Evidence = Enumerable.Range(1, 16).Select(i => new ReportEvidence($"Fact {i}", "Budget", "Costs")).ToArray(),
        };
        var result = DecisionReportPdf.Export(report);
        // Full-size body text and all supporting evidence may legitimately need more
        // pages than the former compact summary layout. The content is never cut.
        Assert.True(result.PageCount >= 3);
        using var stream = new MemoryStream(result.Data);
        using var pdf = PdfSharp.Pdf.IO.PdfReader.Open(stream, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        Assert.All(pdf.Pages.Cast<PdfSharp.Pdf.PdfPage>(), page =>
        {
            Assert.InRange(page.Width.Point, 594, 597);
            Assert.InRange(page.Height.Point, 840, 843);
        });
    }
}
