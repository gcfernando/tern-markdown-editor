using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tern.App.Knowledge;
using Tern.Export;

namespace Tern.App.Tests;

public sealed class ManagementReportRevisionWriteTests
{
    private const string Condition = "The budget could be reserved, subject to approval: €2,400.";
    private static readonly KnowledgeSource Source = new(4, @"C:\synthetic", @"C:\synthetic\plan.md", "plan.md", "Plan", null, 1, "", "", []);
    private static DecisionReport Draft(string original) => KnowledgeReportBuilder.Read(JsonSerializer.Serialize(new
    {
        title = "Current edited report", headline = "Review the conditional budget", recommendation = "Seek approval; the budget could be reserved.",
        background = "UNAFFECTED_CURRENT_EDIT_CANARY", summaryConditions = new[] { "Subject to approval" },
        findings = new[] { new { title = "Budget", kind = "Fact", detail = Condition, evidenceIds = new[] { 9 } } },
        evidence = new[] { new { id = 9, sourceId = Source.Id, quote = original } },
    }), "Which budget conditions apply?", [(Source, original)], "Current author", DateTimeOffset.UnixEpoch).Report;

    [Theory]
    [InlineData(50_000)]
    [InlineData(80_000)]
    public async Task Oversized_originals_are_all_read_and_only_verified_quote_bytes_are_omitted_from_one_fitting_revision(int limit)
    {
        var original = "# Budget\r\n" + Condition + "\r\nVERIFIED_LONG_QUOTE_CANARY\r\n" + new string('z', limit) + "\r\n";
        var other = Source with { Id = 5, Path = @"C:\synthetic\context.md", RelativePath = "context.md", Title = "Context" };
        var otherText = "# Context\n" + string.Concat(Enumerable.Repeat("Unrelated archive detail.\n", limit / 12));
        (KnowledgeSource Source, string Text)[] notes = [(Source, original), (other, otherText)];
        var report = Draft(original);
        string[] issues = ["Retain the original approval condition and link it to the supporting budget passage.", "Preserve the current edited background."];
        Assert.True(KnowledgeReportRevision.Request(report, issues, notes).CharacterCount > limit);
        var before = JsonSerializer.Serialize(report);
        var received = new Dictionary<int, StringBuilder> { [4] = new(), [5] = new() };
        var purposes = new List<AIPurpose>();
        AIRequest? revision = null;
        var reader = new KnowledgeReader("Synthetic", limit, (_, _) => throw new InvalidOperationException("Typed request required."), _ => false,
            askRequest: (request, _) =>
            {
                purposes.Add(request.Purpose);
                Assert.InRange(request.CharacterCount, 1, limit);
                using var data = JsonDocument.Parse(request.CurrentInput);
                if (request.Purpose == AIPurpose.ExtractFacts)
                {
                    var focus = data.RootElement.GetProperty("question").GetString()!;
                    Assert.Contains(report.Question, focus);
                    Assert.All(issues, issue => Assert.Contains(issue, focus));
                    var part = data.RootElement.GetProperty("sourcePart");
                    var id = part.GetProperty("sourceId").GetInt32();
                    var rows = part.GetProperty("lines").EnumerateArray().ToArray();
                    foreach (var row in rows) received[id].Append(row.GetProperty("text").GetString());
                    var passages = rows.Where(row => row.GetProperty("text").GetString()!.Contains(Condition, StringComparison.Ordinal))
                        .Select(row => new { sourceId = id, startLine = row.GetProperty("line").GetInt32(), endLine = row.GetProperty("line").GetInt32() }).ToArray();
                    return Task.FromResult(JsonSerializer.Serialize(new { passages }));
                }
                Assert.Equal(AIPurpose.ReportRevision, request.Purpose);
                revision = request;
                return Task.FromResult("CURRENT_CORRECTION_REPLY");
            });

        var result = await KnowledgeReportRevision.WriteAsync(report, issues, notes, reader, CancellationToken.None);

        Assert.Equal("CURRENT_CORRECTION_REPLY", result.Reply);
        Assert.Equal(original, received[4].ToString());
        Assert.Equal(otherText, received[5].ToString());
        Assert.Equal(1, purposes.Count(purpose => purpose == AIPurpose.ReportRevision));
        Assert.True(purposes.Count(purpose => purpose == AIPurpose.ExtractFacts) > 1);
        Assert.NotNull(revision);
        using var sent = JsonDocument.Parse(revision.CurrentInput);
        Assert.Equal(report.Question, sent.RootElement.GetProperty("question").GetString());
        Assert.Equal(issues, sent.RootElement.GetProperty("checkIssues").EnumerateArray().Select(item => item.GetString()).ToArray());
        var expected = JsonSerializer.SerializeToNode(report)!.AsObject();
        foreach (var item in expected["Evidence"]!.AsArray())
        { item!.AsObject().Remove("Quote"); item["literalQuoteVerifiedAndOmittedByApp"] = true; }
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(sent.RootElement.GetProperty("currentReport").GetRawText())));
        Assert.DoesNotContain("VERIFIED_LONG_QUOTE_CANARY", sent.RootElement.GetProperty("currentReport").GetRawText());
        Assert.Contains(Condition, result.Material);
        Assert.DoesNotContain("VERIFIED_LONG_QUOTE_CANARY", result.Material);
        Assert.Contains("could be reserved", sent.RootElement.GetProperty("currentReport").GetProperty("Recommendation").GetString());
        Assert.Contains("UNAFFECTED_CURRENT_EDIT_CANARY", revision.UserInput);
        Assert.DoesNotContain("UNAFFECTED_CURRENT_EDIT_CANARY", revision.Instructions);
        Assert.Equal(before, JsonSerializer.Serialize(report));
    }

    [Fact]
    public async Task Short_sources_need_only_one_revision_and_no_extraction_call()
    {
        var original = "# Budget\n" + Condition + "\n";
        var report = Draft(original);
        var purposes = new List<AIPurpose>();
        var reader = new KnowledgeReader("Synthetic", 50_000, (_, _) => throw new InvalidOperationException(), _ => false,
            askRequest: (request, _) => { purposes.Add(request.Purpose); Assert.Contains(Condition, request.UserInput); return Task.FromResult("CURRENT_REPLY"); });

        var result = await KnowledgeReportRevision.WriteAsync(report, ["Preserve the approval condition."], [(Source, original)], reader, CancellationToken.None);

        Assert.Equal([AIPurpose.ReportRevision], purposes);
        Assert.Equal("CURRENT_REPLY", result.Reply);
        Assert.Contains(Condition, result.Material);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_or_unverified_quotations_are_never_omitted_or_sent(bool stale)
    {
        var original = "# Budget\n" + Condition + "\n";
        var report = Draft(original);
        report = report with { Evidence = report.Evidence.Select(item => stale ? item with { SourceDigest = new string('a', 64) } : item with { QuoteVerified = false }).ToArray() };
        var calls = 0;
        var reader = new KnowledgeReader("Synthetic", 50_000, (_, _) => { calls++; return Task.FromResult("Unexpected"); }, _ => false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => KnowledgeReportRevision.WriteAsync(report, ["Check the condition."], [(Source, original)], reader, CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Oversized_business_fields_are_not_clipped_and_cancellation_does_not_send()
    {
        var original = "# Budget\n" + Condition + "\n";
        var report = Draft(original) with { Background = new string('b', 60_000) };
        var calls = 0;
        var reader = new KnowledgeReader("Synthetic", 50_000, (_, _) => { calls++; return Task.FromResult("Unexpected"); }, _ => false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => KnowledgeReportRevision.WriteAsync(report, ["Check the condition."], [(Source, original)], reader, CancellationToken.None));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => KnowledgeReportRevision.WriteAsync(report, ["Check the condition."], [(Source, original)], reader, cancel.Token));
        Assert.Equal(60_000, report.Background.Length);
        Assert.Equal(0, calls);
    }
}
