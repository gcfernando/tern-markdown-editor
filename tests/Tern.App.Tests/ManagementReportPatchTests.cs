using System.Text.Json;
using System.Text.Json.Nodes;
using Tern.App.Knowledge;
using Tern.Export;

namespace Tern.App.Tests;

public sealed class ManagementReportPatchTests
{
    private const string OriginalText = "# Pilot\r\n\r\nThe budget could be reserved after approval: €2,400.\r\n\r\nThe response target is six hours; no achieved result is recorded.\r\n";
    private static readonly KnowledgeSource Source = new(3, @"C:\synthetic", @"C:\synthetic\pilot.md", "pilot.md", "Pilot", null, 1, "", "", []);
    private static readonly (KnowledgeSource Source, string Text)[] Notes = [(Source, OriginalText)];
    private static DecisionReport Original() => KnowledgeReportBuilder.Read("""
        {"title":"Current edited pilot report","headline":"Should the budget be reserved?","hasDecision":true,
        "recommendation":"The budget could be reserved after approval.","recommendationWhy":"Sponsor approval remains required.",
        "background":"KEEP_THE_PERSONS_UNAFFECTED_EDIT","metrics":[{"label":"Requested cash","value":"€2,400","note":"Subject to approval"}],
        "summaryConditions":["Approval remains required."],
        "findings":[{"title":"Budget","kind":"Fact","detail":"The budget could be reserved after approval.","keywords":["approval"],"evidenceIds":[1]}],
        "evidence":[{"id":1,"sourceId":3,"fact":"Budget","quote":"The budget could be reserved after approval: €2,400."}]}
        """, "What should management decide?", Notes, "Current author", DateTimeOffset.UnixEpoch).Report;

    private static KnowledgeReportDraft Read(string json) => KnowledgeReportBuilder.Read(json, "What should management decide?", Notes, "Current author", DateTimeOffset.UnixEpoch);

    [Fact]
    public void A_targeted_edit_preserves_unaffected_wording_numbers_quotes_metadata_and_the_original_object()
    {
        var original = Original(); var snapshot = JsonSerializer.Serialize(original);
        var reply = KnowledgeReportRevision.ApplyPatch("""{"edits":[{"path":"/findings/0/detail","value":"The requested budget remains subject to sponsor approval."}],"addEvidence":[],"addFindings":[],"removeEvidenceIds":[]}""", original, Notes);
        var patched = Read(reply).Report;
        Assert.Equal("The requested budget remains subject to sponsor approval.", patched.Findings[0].Detail);
        Assert.Equal(original.Recommendation, patched.Recommendation);
        Assert.Equal(original.Background, patched.Background);
        Assert.Equal(original.Metrics, patched.Metrics);
        Assert.Equal(original.SummaryConditions, patched.SummaryConditions);
        Assert.Equal(original.Evidence[0].Quote, patched.Evidence[0].Quote);
        Assert.Equal(snapshot, JsonSerializer.Serialize(original));
        using var payload = JsonDocument.Parse(reply);
        Assert.Equal(original.Date, payload.RootElement.GetProperty("date").GetString());
        Assert.Equal(original.PreparedBy, payload.RootElement.GetProperty("preparedBy").GetString());
    }

    [Fact]
    public void A_citation_fix_can_add_an_exact_original_span_and_relink_only_the_affected_finding()
    {
        var original = Original();
        var reply = KnowledgeReportRevision.ApplyPatch("""
            {"edits":[{"path":"/findings/0/evidenceIds","value":[2]}],
            "addEvidence":[{"id":2,"sourceId":3,"quoteStart":"The budget could be reserved","quoteEnd":"approval: €2,400."}],
            "addFindings":[],"removeEvidenceIds":[1]}
            """, original, Notes);
        var patched = Read(reply).Report;
        Assert.Equal([2], patched.Findings[0].EvidenceIds);
        Assert.Equal(original.Findings[0].Detail, patched.Findings[0].Detail);
        Assert.Equal(original.Evidence[0].Quote, Assert.Single(patched.Evidence).Quote);
        Assert.Equal(3, patched.Evidence[0].SourceId);
        KnowledgeReportBuilder.ValidateEvidence(patched, Notes);
        KnowledgeReportBuilder.ValidateManagementContent(patched);
    }

    [Fact]
    public void A_requested_omission_adds_a_finding_without_rewriting_existing_findings_or_numeric_conditions()
    {
        var original = Original();
        var reply = KnowledgeReportRevision.ApplyPatch("""
            {"edits":[],"addEvidence":[{"id":2,"sourceId":3,"quoteStart":"The response target is six hours","quoteEnd":"no achieved result is recorded."}],
            "addFindings":[{"title":"Response target","kind":"Target","detail":"The response target is six hours; no achieved result is recorded.","keywords":["target"],"evidenceIds":[2],"derivation":"","limitations":"No achieved result is recorded."}],
            "removeEvidenceIds":[]}
            """, original, Notes);
        var patched = Read(reply).Report;
        Assert.Equal(2, patched.Findings.Count);
        Assert.Equal(JsonSerializer.Serialize(original.Findings[0]), JsonSerializer.Serialize(patched.Findings[0]));
        Assert.Equal(original.Metrics, patched.Metrics);
        Assert.Equal("Target", patched.Findings[1].Kind);
        Assert.Equal("The response target is six hours; no achieved result is recorded.", patched.Evidence[1].Quote);
    }

    [Theory]
    [InlineData("/question")]
    [InlineData("/preparedBy")]
    [InlineData("/date")]
    [InlineData("/coverage")]
    [InlineData("/evidence/0/sourceId")]
    [InlineData("/evidence/0/id")]
    [InlineData("/evidence/0/quote")]
    [InlineData("/evidence/0/line")]
    [InlineData("/findings")]
    [InlineData("/findings/0")]
    [InlineData("/findings/00/detail")]
    [InlineData("/findings/+0/detail")]
    [InlineData("/findings/-1/detail")]
    [InlineData("/findings/1/detail")]
    [InlineData("/unknown")]
    public void Unknown_protected_noncanonical_and_out_of_bounds_paths_are_rejected_without_mutation(string path)
    {
        var original = Original(); var snapshot = JsonSerializer.Serialize(original);
        var reply = JsonSerializer.Serialize(new { edits = new[] { new { path, value = "A different value" } } });
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportRevision.ApplyPatch(reply, original, Notes));
        Assert.Equal(snapshot, JsonSerializer.Serialize(original));
    }

    [Theory]
    [InlineData("""{"edits":[{"path":"/recommendation","value":"A"},{"path":"/recommendation","value":"B"}]}""")]
    [InlineData("""{"edits":[{"path":"/findings/0/evidenceIds","value":[1]},{"path":"/findings/0/evidenceIds/0","value":1}]}""")]
    [InlineData("""{"edits":[{"path":"/hasDecision","value":"true"}]}""")]
    [InlineData("""{"edits":null}""")]
    [InlineData("""{"edits":[],"unknown":[]}""")]
    [InlineData("""{"edits":[],"edits":[]}""")]
    [InlineData("""{"edits":[],"addEvidence":[{"id":2,"sourceId":99,"quoteStart":"The budget","quoteEnd":"€2,400."}]}""")]
    [InlineData("""{"edits":[],"addEvidence":[{"id":1,"sourceId":3,"quoteStart":"The budget","quoteEnd":"€2,400."}]}""")]
    [InlineData("""{"edits":[],"addEvidence":[{"id":2,"sourceId":3,"quoteStart":"Invented opening","quoteEnd":"€2,400."}]}""")]
    [InlineData("""{"edits":[],"removeEvidenceIds":[1]}""")]
    [InlineData("""{"edits":[{"path":"/findings/0/evidenceIds","value":[1,1]}]}""")]
    public void Malformed_conflicting_unproven_or_still_referenced_changes_are_rejected(string reply)
    {
        var original = Original(); var snapshot = JsonSerializer.Serialize(original);
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportRevision.ApplyPatch(reply, original, Notes));
        Assert.Equal(snapshot, JsonSerializer.Serialize(original));
    }

    [Fact]
    public void Legacy_full_report_replies_remain_on_the_callers_existing_strict_report_path()
    {
        const string legacy = """{"title":"A full candidate","headline":"Q","recommendation":"R"}""";
        Assert.Equal(legacy, KnowledgeReportRevision.ApplyPatch(legacy, Original(), Notes));
        Assert.Equal("CURRENT_REPLY", KnowledgeReportRevision.ApplyPatch("CURRENT_REPLY", Original(), Notes));
    }
}
