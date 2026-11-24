using System.Text;
using System.Text.Json;
using Tern.App.Knowledge;

namespace Tern.App.Tests;

public sealed class KnowledgeSourcePassagesTests
{
    private static readonly KnowledgeSource Source = new(7, @"C:\synthetic", @"C:\synthetic\shipping.md", "shipping.md", "Shipping", null, 1, "", "", []);
    private static readonly KnowledgeSource Other = Source with { Id = 12, Title = "Earlier note", RelativePath = "earlier.md" };
    private static string Select(int id, int first, int last) => JsonSerializer.Serialize(new { passages = new[] { new { sourceId = id, startLine = first, endLine = last } } });

    [Theory]
    [InlineData(8_000)]
    [InlineData(50_000)]
    [InlineData(80_000)]
    public async Task Every_source_character_is_read_but_a_part_without_relevant_facts_cannot_add_absence_or_conflict_prose(int limit)
    {
        const string question = "What features were documented for release 4.27.3?";
        const string fact = "DOCUMENTED_FEATURE: Improved existing bookmarks, retaining the €20 default.\r\n";
        var notes = new[]
        {
            (Source, "# Shipping\r\n## 4.27.3\r\n" + fact + string.Concat(Enumerable.Repeat("Unrelated archive detail.\r\n", limit / 12))),
            (Other, "# Earlier release\n## 4.27.2\n" + string.Concat(Enumerable.Repeat("Earlier unrelated detail.\n", limit / 12))),
        };
        var input = new Dictionary<int, StringBuilder> { [Source.Id] = new(), [Other.Id] = new() };
        var requests = new List<AIRequest>();
        var reader = new KnowledgeReader("Synthetic", limit, (_, _) => throw new InvalidOperationException("Typed request required."), _ => false,
            askRequest: (request, _) =>
            {
                requests.Add(request);
                Assert.Equal(AIPurpose.ExtractFacts, request.Purpose);
                Assert.InRange(request.CharacterCount, 1, limit);
                using var data = JsonDocument.Parse(request.CurrentInput);
                Assert.Equal(question, data.RootElement.GetProperty("question").GetString());
                var part = data.RootElement.GetProperty("sourcePart");
                var id = part.GetProperty("sourceId").GetInt32();
                var rows = part.GetProperty("lines").EnumerateArray().ToArray();
                foreach (var row in rows) input[id].Append(row.GetProperty("text").GetString());
                var useful = rows.Where(row => row.GetProperty("text").GetString()!.Contains("DOCUMENTED_FEATURE", StringComparison.Ordinal)).ToArray();
                return Task.FromResult(useful.Length == 0 ? "{\"passages\":[]}"
                    : Select(id, useful[0].GetProperty("line").GetInt32(), useful[0].GetProperty("line").GetInt32()));
            });

        var read = await reader.ReadAllAsync(question, notes, () => limit - 3000, CancellationToken.None);

        Assert.Equal(notes[0].Item2, input[Source.Id].ToString());
        Assert.Equal(notes[1].Item2, input[Other.Id].ToString());
        Assert.Equal(notes.Sum(note => note.Item2.Length), read.CharactersRead);
        Assert.Empty(read.NotRead);
        Assert.Contains(fact, read.Material);
        Assert.Contains("## 4.27.3", read.Material);
        Assert.DoesNotContain("Earlier unrelated detail", read.Material);
        Assert.DoesNotContain("conflict", read.Material, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not found", read.Material, StringComparison.OrdinalIgnoreCase);
        Assert.True(requests.Count > 2);
        Assert.Contains("100%", reader.Coverage(read));
    }

    [Fact]
    public async Task Authored_negative_extraction_prose_fails_before_it_can_be_used_as_report_material()
    {
        var calls = 0;
        var reader = new KnowledgeReader("Synthetic", 8_000, (_, _) => throw new InvalidOperationException(), _ => false,
            askRequest: (_, _) => { calls++; return Task.FromResult("No requested release appears in this part, so the notes conflict."); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAllAsync("Documented changes?",
            [(Source, new string('x', 20_000))], () => 3000, CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Source_backed_combination_also_rejects_unverified_prose_instead_of_losing_provenance()
    {
        var purposes = new List<AIPurpose>();
        var reader = new KnowledgeReader("Synthetic", 8_000, (_, _) => throw new InvalidOperationException(), _ => false,
            askRequest: (request, _) =>
            {
                purposes.Add(request.Purpose);
                Assert.Equal(AIPurpose.CombineFacts, request.Purpose);
                return Task.FromResult("[Note 7] The full corpus contains no requested feature.");
            });
        var read = await reader.ReadAllAsync("Which feature?", [(Source, new string('x', 4000))], () => 6000, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.WriteRequestAsync("Which feature?",
            material => KnowledgeReportBuilder.ReportRequest("Which feature?", material), read.Material, () => 500, CancellationToken.None));
        Assert.Equal([AIPurpose.CombineFacts], purposes);
    }

    [Fact]
    public async Task A_new_reader_can_adopt_current_sources_and_refit_by_original_ranges_without_trusting_prose()
    {
        const string fact = "The documented budget is €20.\n";
        var original = "# Plan\n" + fact + new string('x', 4000);
        var first = new KnowledgeReader("Synthetic", 8_000, (_, _) => throw new InvalidOperationException(), _ => false);
        var read = await first.ReadAllAsync("Which budget?", [(Source, original)], () => 6000, CancellationToken.None);
        var purposes = new List<AIPurpose>();
        var second = new KnowledgeReader("Synthetic", 8_000, (_, _) => throw new InvalidOperationException(), _ => false,
            askRequest: (request, _) =>
            {
                purposes.Add(request.Purpose);
                if (request.Purpose == AIPurpose.CombineFacts)
                {
                    using var data = JsonDocument.Parse(request.CurrentInput);
                    var part = data.RootElement.GetProperty("sourcePart");
                    var row = Assert.Single(part.GetProperty("lines").EnumerateArray().Where(item => item.GetProperty("text").GetString() == fact));
                    return Task.FromResult(Select(Source.Id, row.GetProperty("line").GetInt32(), row.GetProperty("line").GetInt32()));
                }
                Assert.Equal(AIPurpose.ReportCheck, request.Purpose);
                Assert.Contains(fact, request.UserInput);
                Assert.DoesNotContain(new string('x', 100), request.UserInput);
                return Task.FromResult("{\"unsupported\":[]}");
            });
        second.AdoptSourceMaterial(read.Material, [(Source, original)]);

        var (reply, material) = await second.WriteRequestAsync("Which budget?",
            text => AIRequest.Create(AIPurpose.ReportCheck, "Check this current material.", text), read.Material, () => 1000, CancellationToken.None);

        Assert.Equal([AIPurpose.CombineFacts, AIPurpose.ReportCheck], purposes);
        Assert.Contains(fact, material);
        Assert.Empty(KnowledgeReportBuilder.ReadUnsupported(reply));
    }

    [Theory]
    [InlineData("{\"passages\":[{\"sourceId\":99,\"startLine\":1,\"endLine\":1}]}")]
    [InlineData("{\"passages\":[{\"sourceId\":7,\"startLine\":0,\"endLine\":1}]}")]
    [InlineData("{\"passages\":[{\"sourceId\":7,\"startLine\":1,\"endLine\":99}]}")]
    [InlineData("{\"passages\":[{\"sourceId\":7,\"startLine\":2,\"endLine\":1}]}")]
    [InlineData("{\"passages\":[{\"sourceId\":7,\"startLine\":1.0,\"endLine\":1}]}")]
    [InlineData("{\"passages\":[{\"sourceId\":7,\"startLine\":1,\"endLine\":2},{\"sourceId\":7,\"startLine\":2,\"endLine\":3}]}")]
    [InlineData("{\"passages\":[],\"fact\":\"Global absence\"}")]
    public void Invalid_or_overlapping_selections_are_not_clamped_or_silently_ignored(string reply) =>
        Assert.Throws<InvalidOperationException>(() => KnowledgeSourcePassages.Read(reply, new(Source, "First\nSecond\nThird", 0, 18)));

    [Fact]
    public void Selected_text_remains_exact_and_heading_context_comes_from_real_markdown_structure()
    {
        const string text = "# Actual note\r\n## Actual release\r\n```md\r\n# Fake release inside code\r\n```\r\n  €20 😀 exact spacing.\r\n";
        var start = text.IndexOf("  €20", StringComparison.Ordinal);
        var part = new KnowledgeSourcePassages.Span(Source, text, start, text.Length - start);
        var request = KnowledgeReportBuilder.FactsRequest("Which amount?", part);
        using var data = JsonDocument.Parse(request.CurrentInput);
        var context = data.RootElement.GetProperty("sourcePart").GetProperty("headingContext").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Equal(["# Actual note", "## Actual release"], context);
        var selected = Assert.Single(KnowledgeSourcePassages.Read(Select(Source.Id, 1, 1), part));
        Assert.Equal("  €20 😀 exact spacing.\r\n", selected.Text);
        Assert.Equal(start, selected.Start);
        Assert.DoesNotContain("Fake release", KnowledgeSourcePassages.Render([selected]));
    }
}
