using System.Text.Json;
using Tern.App.Knowledge;

namespace Tern.App.Tests;

/// <summary>Verified quotes must retain their real displayed source identity and date after restoration.</summary>
public sealed class ManagementEvidenceProvenanceTests
{
    [Theory]
    [InlineData("path")]
    [InlineData("label")]
    [InlineData("date")]
    public void Restored_evidence_cannot_relabel_a_valid_quote_as_another_file_or_date(string changedField)
    {
        const string note = "# Plan\n## Pilot 2027-04-02\nStaff time is separate.\n";
        var source = new KnowledgeSource(1, @"C:\synthetic", @"C:\synthetic\Plan.md",
            "Plan.md", "Plan", null, 1, "Staff time is separate.", "search digest", []);
        var reply = JsonSerializer.Serialize(new
        {
            headline = "Pilot effort",
            recommendation = "Review the staffing commitment.",
            findings = new[] { new { title = "Staffing", kind = "Fact", detail = "Staff time is separate.", evidenceIds = new[] { 1 } } },
            evidence = new[] { new { id = 1, sourceId = 1, fact = "Staffing", quote = "Staff time is separate.", line = 3 } },
        });
        var report = KnowledgeReportBuilder.Read(reply, "What effort is needed?", [(source, note)], "", DateTimeOffset.UnixEpoch).Report;
        KnowledgeReportBuilder.ValidateManagementContent(report);
        KnowledgeReportBuilder.ValidateEvidence(report, [(source, note)]);
        var verified = Assert.Single(report.Evidence);
        var changed = changedField switch
        {
            "path" => verified with { RelativePath = "Different-plan.md" },
            "label" => verified with { Note = "Different plan" },
            "date" => verified with { DocumentedDate = "2030-12-31" },
            _ => throw new InvalidOperationException(),
        };
        var restored = report with { Evidence = [changed] };

        // Correct quotation text and digest cannot authenticate a different displayed locator/date.
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.ValidateEvidence(restored, [(source, note)]));
    }
}
