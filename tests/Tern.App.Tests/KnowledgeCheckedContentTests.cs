using System.Security.Cryptography;
using System.Text;
using Tern.App.Knowledge;

namespace Tern.App.Tests;

public sealed class KnowledgeCheckedContentTests
{
    private static (KnowledgeReportDraft Draft, (KnowledgeSource Source, string Text)[] Notes) Current()
    {
        var root = Path.Combine(Path.GetTempPath(), "tern-checked-content");
        const string text = "# Budget\n\nThe budget could be reserved after approval.";
        var source = new KnowledgeSource(1, root, Path.Combine(root, "budget.md"), "budget.md", "Budget", "Budget", 3, text,
            Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(text))), []);
        (KnowledgeSource Source, string Text)[] notes = [(source, text)];
        const string reply = """{"title":"Budget position","hasDecision":false,"headline":"What is the budget position?","recommendation":"The budget could be reserved after approval.","findings":[{"title":"Conditional budget","kind":"Fact","detail":"The budget could be reserved after approval.","evidenceIds":[1]}],"evidence":[{"id":1,"sourceId":1,"fact":"The budget remains conditional.","quote":"The budget could be reserved after approval."}]}""";
        return (KnowledgeReportBuilder.Read(reply, "What is the budget position?", notes, "", DateTimeOffset.UnixEpoch), notes);
    }

    [Fact]
    public void Local_formats_use_the_same_immutable_checked_report_and_keep_conditions()
    {
        var (draft, notes) = Current();
        var reasons = new List<string> { "Approval is still required." };
        var report = draft.Report with { Reasons = reasons };
        var content = KnowledgeCheckedContent.Create(report, notes, []);
        reasons[0] = "Approval was already granted.";
        Assert.Contains("could be reserved after approval", content.Email);
        Assert.DoesNotContain("already granted", content.Email);
        Assert.Contains("Approval is still required", content.Markdown);
        Assert.Equal(KnowledgeReportEmail.Format(content.Report), content.Email);
        Assert.Equal(KnowledgeReportBuilder.ToMarkdown(content.Report), content.Markdown);
        var exposed = content.Report;
        ((IList<Tern.Export.ReportFinding>)exposed.Findings)[0] = exposed.Findings[0] with { Detail = "An unrelated mutated claim." };
        Assert.DoesNotContain("mutated", content.Report.Findings[0].Detail);
    }

    [Fact]
    public void Unsupported_claims_or_changed_source_text_cannot_create_checked_output_content()
    {
        var (draft, notes) = Current();
        Assert.Throws<InvalidOperationException>(() => KnowledgeCheckedContent.Create(draft.Report, notes, ["Unsupported approval claim"]));
        notes[0] = (notes[0].Source, "# Budget\n\nThe source changed.");
        Assert.Throws<InvalidOperationException>(() => KnowledgeCheckedContent.Create(draft.Report, notes, []));
    }

    [Fact]
    public void An_indivisible_statement_is_not_cut_apart_to_fit_a_semantic_check()
    {
        var (draft, _) = Current();
        var report = draft.Report with { Recommendation = "Approval is conditional " + new string('x', 60_000) + " unless the funding review fails." };
        var error = Assert.Throws<InvalidOperationException>(() => KnowledgeReportChecks.Plan(report, 50_000));
        Assert.Contains("complete report statement", error.Message);
    }
}
