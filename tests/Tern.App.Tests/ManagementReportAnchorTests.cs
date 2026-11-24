using System.Text.Json;
using System.Text.Json.Nodes;
using Tern.App.Knowledge;

namespace Tern.App.Tests;

public sealed class ManagementReportAnchorTests
{
    private static readonly KnowledgeSource Source = new(13, @"C:\synthetic", @"C:\synthetic\plan.md", "plan.md", "Plan", null, 1, "", "", []);
    private const string Opening = "- Opening €20 — Café 😀.";
    private const string Closing = "- Closing ends here ✓.";
    private const string Note = "# Plan\r\n## Budget — 2027-03-04\r\n" + Opening +
        "\r\n- Permission remains conditional.\r\n  Wrapped continuation    preserves spaces.\r\n\r\n" + Closing + "\r\n";
    private static string Reply(string start = Opening, string end = Closing, int sourceId = 13) => JsonSerializer.Serialize(new
    {
        title = "Budget conditions", headline = "Review the proposed budget", recommendation = "Confirm permission before acting.",
        findings = new[] { new { title = "Budget condition", kind = "Fact", detail = "Permission remains conditional.", evidenceIds = new[] { 5 } } },
        evidence = new[] { new { id = 5, sourceId, quoteStart = start, quoteEnd = end } },
    });

    [Fact]
    public void Short_exact_anchors_copy_the_entire_original_span_with_currency_unicode_bullets_and_line_endings()
    {
        var draft = KnowledgeReportBuilder.Read(Reply(), "Which budget conditions?", [(Source, Note)], "", DateTimeOffset.UnixEpoch);
        var evidence = Assert.Single(draft.Report.Evidence);
        var start = Note.IndexOf(Opening, StringComparison.Ordinal);
        var end = Note.IndexOf(Closing, StringComparison.Ordinal) + Closing.Length;
        Assert.Equal(Note[start..end], evidence.Quote);
        Assert.Contains("\r\n- Permission remains conditional.\r\n  Wrapped continuation    preserves spaces.\r\n\r\n", evidence.Quote);
        Assert.Equal("Budget condition", evidence.Fact); // Existing finding title, not another AI-authored claim.
        Assert.True(evidence.QuoteVerified);
        Assert.Equal(3, evidence.Line);
        Assert.Equal("Budget — 2027-03-04", evidence.Section);
        Assert.Equal("2027-03-04", evidence.DocumentedDate);
        KnowledgeReportBuilder.ValidateEvidence(draft.Report, [(Source, Note)]);
        KnowledgeReportBuilder.ValidateManagementContent(draft.Report);
        using var response = JsonDocument.Parse(Reply());
        Assert.Equal(new[] { "id", "sourceId", "quoteStart", "quoteEnd" },
            Assert.Single(response.RootElement.GetProperty("evidence").EnumerateArray()).EnumerateObject().Select(property => property.Name));
    }

    [Theory]
    [InlineData("repeat-start")]
    [InlineData("repeat-end")]
    [InlineData("missing-start")]
    [InlineData("missing-end")]
    [InlineData("reversed")]
    public void Ambiguous_absent_or_reversed_anchors_fail_without_guessing_a_span(string failure)
    {
        var text = failure switch
        {
            "repeat-start" => Note + Opening,
            "repeat-end" => Note + Closing,
            "missing-start" => Note.Replace(Opening, "Different opening", StringComparison.Ordinal),
            "missing-end" => Note.Replace(Closing, "Different closing", StringComparison.Ordinal),
            _ => Closing + "\n" + Opening,
        };
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.Read(Reply(), "Q", [(Source, text)], "", DateTimeOffset.UnixEpoch));
    }

    [Theory]
    [InlineData("- Opening EUR20 — Café 😀.")]
    [InlineData("- Opening €20 — Café 😀.")]
    [InlineData("- opening €20 — Café 😀.")]
    [InlineData("- Opening €20 — Café 😀.\n- Permission remains conditional.")]
    public void Anchor_matching_does_not_replace_currency_normalize_unicode_case_or_line_endings(string changedOpening) =>
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.Read(Reply(changedOpening), "Q", [(Source, Note)], "", DateTimeOffset.UnixEpoch));

    [Fact]
    public void Anchors_cannot_resolve_against_a_different_supplied_note()
    {
        var other = Source with { Id = 14, RelativePath = "other.md" };
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.Read(Reply(sourceId: 14), "Q",
            [(Source, Note), (other, "An unrelated source without these anchors.")], "", DateTimeOffset.UnixEpoch));
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.Read(Reply(sourceId: 99), "Q", [(Source, Note)], "", DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void A_repaired_explicit_quote_remains_strict_and_overrides_stale_anchor_text()
    {
        var payload = JsonNode.Parse(Reply("Absent opening", "Absent closing"))!;
        payload["evidence"]![0]!["quote"] = Opening;
        payload["evidence"]![0]!["line"] = 3;
        var draft = KnowledgeReportBuilder.Read(payload.ToJsonString(), "Q", [(Source, Note)], "", DateTimeOffset.UnixEpoch);
        Assert.Equal(Opening, Assert.Single(draft.Report.Evidence).Quote);
        payload["evidence"]![0]!["quote"] = "Invented literal quotation";
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.Read(payload.ToJsonString(), "Q", [(Source, Note)], "", DateTimeOffset.UnixEpoch));
        payload["evidence"]![0]!["quote"] = Opening;
        payload["evidence"]![0]!["quoteStart"] = 42;
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.Read(payload.ToJsonString(), "Q", [(Source, Note)], "", DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Identical_or_overlapping_unique_anchors_are_unambiguous_but_incomplete_surrogates_are_invalid()
    {
        Assert.Equal(Opening, KnowledgeReportBuilder.ResolveEvidenceAnchors(Note, Opening, Opening));
        Assert.Equal("abcdef", KnowledgeReportBuilder.ResolveEvidenceAnchors("abcdef", "abcd", "cdef"));
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.ResolveEvidenceAnchors("abcdef", "abcdef", "cd"));
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.ResolveEvidenceAnchors(Note, "\ud83d", Closing));
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.ResolveEvidenceAnchors(Note, " ", Closing));
    }
}
