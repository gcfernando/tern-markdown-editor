using System.Text.Json;
using System.Text.Json.Nodes;
using Tern.App.Knowledge;

namespace Tern.App.Tests;

public sealed class ManagementReportSelectionTests
{
    private static readonly KnowledgeSource First = new(1, @"C:\synthetic", @"C:\synthetic\First.md", "First.md", "First", null, 1, "", "", []);
    private static readonly KnowledgeSource Second = First with { Id = 2, Path = @"C:\synthetic\Second.md", RelativePath = "Second.md", Title = "Second" };
    private const string Basic = "## Budget\nCash is €2,400.\n";
    private static string Candidate(string quote = "Cash is EUR2,400.") => JsonSerializer.Serialize(new
    {
        title = "Proposal", headline = "Review the proposal", recommendation = "The sponsor approved the budget.",
        findings = new[] { new { title = "Budget", kind = "Fact", detail = "Cash is €2,400.", evidenceIds = new[] { 1 } } },
        evidence = new[] { new { id = 1, sourceId = 1, fact = "Requested cash", quote, line = 0 } },
    });
    private static string Plan(int first, int last, int evidenceId = 1, int sourceId = 1) => JsonSerializer.Serialize(new
    {
        replacements = new[] { new { evidenceId, sourceId, startLine = first, endLine = last } },
    });
    private static Task<KnowledgeReportDraft> Read(string candidate, IReadOnlyList<(KnowledgeSource Source, string Text)> notes,
        Func<AIRequest, CancellationToken, Task<string>> select) => KnowledgeReportRepair.ReadAsync(candidate, "Q", notes, "", DateTimeOffset.UnixEpoch,
            null, false, 50_000, select, CancellationToken.None);

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r")]
    public async Task Contiguous_selection_keeps_the_middle_paragraph_raw_line_endings_and_unicode(string ending)
    {
        var lines = new[] { "## Pilot", "People decide access.", "", "Cash is €2,400; café 😀.", "", "Target is six hours.", "" };
        var note = string.Join(ending, lines);
        var candidate = Candidate("People decide access. Target is six hours.");
        var draft = await Read(candidate, [(First, note)], (request, _) =>
        {
            using var input = JsonDocument.Parse(request.CurrentInput);
            var failed = Assert.Single(input.RootElement.GetProperty("failedEvidence").EnumerateArray());
            Assert.Equal(1, failed.GetProperty("evidenceId").GetInt32());
            Assert.Equal(1, failed.GetProperty("sourceId").GetInt32());
            var source = Assert.Single(input.RootElement.GetProperty("suppliedSources").EnumerateArray());
            Assert.Equal(note, string.Concat(source.GetProperty("lines").EnumerateArray().Select(row => row.GetProperty("text").GetString())));
            return Task.FromResult(Plan(2, 6));
        });
        var evidence = Assert.Single(draft.Report.Evidence);
        Assert.Equal(string.Join(ending, lines[1..6]), evidence.Quote);
        Assert.Contains("Cash is €2,400; café 😀.", evidence.Quote);
        Assert.Equal(2, evidence.Line);
        Assert.Equal("The sponsor approved the budget.", draft.Report.Recommendation); // Locator repair never blesses or rewrites this unsupported business claim.
        Assert.Equal("Cash is €2,400.", Assert.Single(draft.Report.Findings).Detail);
        var wholeCheck = KnowledgeReportBuilder.CheckRequest(draft.Report, note);
        Assert.Contains("The sponsor approved the budget.", wholeCheck.UserInput);
        Assert.Single(KnowledgeReportBuilder.ReadUnsupported("""{"unsupported":["Sponsor approval is not documented."]}"""));
        KnowledgeReportBuilder.ValidateEvidence(draft.Report, [(First, note)]);
    }

    [Fact]
    public async Task Repeated_passages_use_the_selected_occurrence_and_actual_heading()
    {
        const string note = "## Old\nApproved.\n## Current\nApproved.\n";
        var draft = await Read(Candidate("Not a literal passage."), [(First, note)], (_, _) => Task.FromResult(Plan(4, 4)));
        Assert.Equal("Approved.", draft.Report.Evidence[0].Quote);
        Assert.Equal(4, draft.Report.Evidence[0].Line);
        Assert.Equal("Current", draft.Report.Evidence[0].Section);
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("reverse")]
    [InlineData("outside")]
    [InlineData("blank-last")]
    [InlineData("source-reassignment")]
    [InlineData("unknown-evidence")]
    [InlineData("string-number")]
    [InlineData("fraction")]
    [InlineData("max-integer")]
    [InlineData("overflow")]
    [InlineData("extra-field")]
    [InlineData("duplicate-key")]
    [InlineData("escaped-duplicate-key")]
    public async Task Invalid_plans_are_rejected_once_without_clamping_or_prose_fallback(string invalid)
    {
        var plan = JsonNode.Parse(Plan(2, 2))!;
        var replacement = plan["replacements"]![0]!;
        var text = invalid switch
        {
            "duplicate-key" => """{"replacements":[{"evidenceId":1,"sourceId":1,"startLine":2,"startLine":2,"endLine":2}]}""",
            "escaped-duplicate-key" => """{"replacements":[{"evidenceId":1,"sourceId":1,"startLine":2,"\u0073tartLine":2,"endLine":2}]}""",
            _ => "",
        };
        if (text.Length == 0)
        {
            switch (invalid)
            {
                case "zero": replacement["startLine"] = 0; break;
                case "negative": replacement["startLine"] = -1; break;
                case "reverse": replacement["startLine"] = 2; replacement["endLine"] = 1; break;
                case "outside": replacement["endLine"] = 99; break;
                case "blank-last": replacement["startLine"] = 3; replacement["endLine"] = 3; break;
                case "source-reassignment": replacement["sourceId"] = 2; break;
                case "unknown-evidence": replacement["evidenceId"] = 99; break;
                case "string-number": replacement["startLine"] = "2"; break;
                case "fraction": replacement["startLine"] = 2.5; break;
                case "max-integer": replacement["endLine"] = int.MaxValue; break;
                case "overflow": replacement["endLine"] = (long)int.MaxValue + 1; break;
                case "extra-field": replacement["quote"] = "A fabricated replacement."; break;
            }
            text = plan.ToJsonString();
        }
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(Candidate(), [(First, Basic), (Second, Basic)], (_, _) =>
        { calls++; return Task.FromResult(text); }));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("locator")]
    [InlineData("ambiguous")]
    [InlineData("shape")]
    [InlineData("reference")]
    [InlineData("duplicate-key")]
    public async Task An_earlier_failed_quote_cannot_hide_a_later_non_quote_failure(string error)
    {
        var candidate = JsonNode.Parse(Candidate())!;
        var second = candidate["evidence"]![0]!.DeepClone();
        second["id"] = 2;
        second["quote"] = "Cash is €2,400.";
        candidate["evidence"]!.AsArray().Add(second);
        var note = Basic;
        switch (error)
        {
            case "source": second["sourceId"] = 99; break;
            case "locator": second["line"] = 1; break;
            case "ambiguous": note += "Cash is €2,400.\n"; break;
            case "shape": candidate["risks"] = "not an array"; break;
            case "reference": candidate["findings"]![0]!["evidenceIds"] = new JsonArray(99); break;
        }
        var text = candidate.ToJsonString();
        if (error == "duplicate-key") text = text.Replace("\"headline\":", "\"headline\":\"Duplicate\",\"headline\":", StringComparison.Ordinal);
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(text, [(First, note)], (_, _) =>
        { calls++; return Task.FromResult(Plan(2, 2)); }));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    public async Task Every_failed_evidence_id_must_be_selected_exactly_once(string kind)
    {
        var candidate = JsonNode.Parse(Candidate())!;
        var other = candidate["evidence"]![0]!.DeepClone();
        other["id"] = 2; other["sourceId"] = 2;
        candidate["evidence"]!.AsArray().Add(other);
        var selections = new JsonArray();
        selections.Add(JsonNode.Parse(Plan(2, 2))!["replacements"]![0]!.DeepClone());
        if (kind != "missing") selections.Add(JsonNode.Parse(Plan(2, 2, evidenceId: kind == "duplicate" ? 1 : 2, sourceId: kind == "duplicate" ? 1 : 2))!["replacements"]![0]!.DeepClone());
        if (kind == "extra") selections.Add(JsonNode.Parse(Plan(2, 2, evidenceId: 99))!["replacements"]![0]!.DeepClone());
        var plan = new JsonObject { ["replacements"] = selections }.ToJsonString();
        var calls = 0;
        Task<KnowledgeReportDraft> Run() => Read(candidate.ToJsonString(), [(First, Basic), (Second, Basic)], (request, _) =>
        {
            calls++;
            using var input = JsonDocument.Parse(request.CurrentInput);
            Assert.Equal(2, input.RootElement.GetProperty("failedEvidence").GetArrayLength());
            return Task.FromResult(plan);
        });
        if (kind == "complete")
        {
            var draft = await Run();
            Assert.Equal([1, 2], draft.Report.Evidence.Select(item => item.Id));
            Assert.All(draft.Report.Evidence, item => Assert.Equal("Cash is €2,400.", item.Quote));
        }
        else await Assert.ThrowsAsync<InvalidOperationException>(Run);
        Assert.Equal(1, calls);
    }
}
