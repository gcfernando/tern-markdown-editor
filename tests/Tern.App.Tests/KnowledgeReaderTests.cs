using Tern.App.Knowledge;
using Tern.App.Settings;

namespace Tern.App.Tests;

/// <summary>Explore reads all ticked notes within one connection's value, in parts when needed, and never silently cuts text.</summary>
public sealed class KnowledgeReaderTests
{
    private const string Root = @"C:\synthetic\notes";
    private const string Answer = """{"answer":"- The launch is in April [1].","interpretation":"","insufficientEvidence":false,"sources":[{"id":1,"reason":"Launch"}]}""";
    private const string NotFound = """{"answer":"Not stated.","insufficientEvidence":true,"sources":[]}""";

    private sealed class Rejected : Exception;

    private static KnowledgeSource Source(int id, string name = "") =>
        new(id, Root, Path.Combine(Root, (name.Length > 0 ? name : $"note{id}") + ".md"), (name.Length > 0 ? name : $"note{id}") + ".md", $"Note {id}", null, 1, $"Best section of note {id}", "digest", []);

    private static string Lines(string marker, int characters)
    {
        var line = marker + " holds one useful line of project detail.\n";
        return string.Concat(Enumerable.Repeat(line, characters / line.Length + 1));
    }

    private static (KnowledgeConversation Conversation, KnowledgeRequest Request) Start(string question, IReadOnlyList<KnowledgeSource> sources, KnowledgeLimits limits)
    {
        var conversation = new KnowledgeConversation();
        conversation.Start(Root);
        return (conversation, conversation.PrepareReading(question, "Answer question", sources, limits));
    }

    private static bool IsPart(string message) => message.StartsWith("TERN INSTRUCTIONS (ExtractFacts)\n", StringComparison.Ordinal);

    private static string SelectSourceRows(string message, Func<string, bool>? include = null, bool firstOnly = true)
    {
        using var data = SourceData(message);
        var part = data.RootElement.GetProperty("sourcePart");
        var sourceId = part.GetProperty("sourceId").GetInt32();
        var rows = part.GetProperty("lines").EnumerateArray()
            .Where(row => (include ?? (text => text.Contains("useful line of project detail.", StringComparison.Ordinal)))(row.GetProperty("text").GetString()!));
        if (firstOnly) rows = rows.Take(1);
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            passages = rows.Select(row => new { sourceId, startLine = row.GetProperty("line").GetInt32(), endLine = row.GetProperty("line").GetInt32() }).ToArray(),
        });
    }

    private static System.Text.Json.JsonDocument SourceData(string message)
    {
        const string marker = "CURRENT TASK AND DATA\n";
        var start = message.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        return System.Text.Json.JsonDocument.Parse(message[(start + marker.Length)..]);
    }

    [Theory]
    [InlineData(50_000, false)]
    [InlineData(80_000, false)]
    [InlineData(50_000, true)]
    [InlineData(80_000, true)]
    public async Task Version_range_answers_are_independently_corrected_from_current_evidence_within_the_total_limit(int limit, bool split)
    {
        const string question = "Explain all changes from version 3.0.0 to latest";
        const string sourceFacts = "## 3.0.1\n- Improve existing cited answers and selection prompts.\n## 3.0.0\n- Add an editor.\n";
        var wrong = System.Text.Json.JsonSerializer.Serialize(new
        {
            answer = "- 3.0.0: Added an editor [1].\n" + string.Concat(Enumerable.Repeat("- 3.0.1: Added cited answers and selection prompts [1].\n", 30)),
            interpretation = "", insufficientEvidence = false, sources = new[] { new { id = 1, reason = "Changes" } },
        });
        const string corrected = """{"answer":"- 3.0.0: Changes include an added editor [1].\n- 3.0.1: Changes include improved existing cited answers and selection prompts [1].","interpretation":"","insufficientEvidence":false,"sources":[{"id":1,"reason":"The release headings distinguish added from improved."}]}""";
        var source = Source(1);
        var limits = new KnowledgeLimits(SendCharacters: limit);
        var conversation = new KnowledgeConversation();
        conversation.RestoreCurrent(Root, new KnowledgeTurn("OLD_QUESTION_CANARY", new KnowledgeAnswer("OLD_REPLY_CANARY", "", [], false)));
        var request = conversation.PrepareReading(question, "Answer", [source], limits);
        var baseWhole = conversation.PrepareWhole(request, [(source, sourceFacts)], limits, limit)!;
        var note = sourceFacts + new string('x', split ? limit + 1000 : limit - baseWhole.Prompt!.CharacterCount);
        var sent = new List<AIRequest>();
        var reader = new KnowledgeReader("Server A", limit, (_, _) => throw new InvalidOperationException("Typed requests required."), _ => false,
            askRequest: (prompt, _) =>
            {
                sent.Add(prompt);
                return Task.FromResult(prompt.Purpose switch
                {
                    AIPurpose.ExtractFacts or AIPurpose.CombineFacts => SelectSourceRows(prompt.Text,
                        text => text.Contains("Improve existing cited answers", StringComparison.Ordinal) || text.Contains("Add an editor", StringComparison.Ordinal), firstOnly: false),
                    AIPurpose.Answer => wrong,
                    AIPurpose.AnswerReview => corrected,
                    _ => throw new InvalidOperationException("Unexpected request purpose."),
                });
            });

        var result = await reader.AnswerAsync(conversation, request, [(source, note)], limits, CancellationToken.None);

        var review = Assert.Single(sent.Where(prompt => prompt.Purpose == AIPurpose.AnswerReview));
        Assert.Equal(AIPurpose.AnswerReview, result.Request.Prompt!.Purpose);
        Assert.Contains("improved existing cited answers", result.Answer.Answer);
        Assert.DoesNotContain("Added cited answers", result.Answer.Answer);
        Assert.Contains("Added cited answers", review.UserInput); // The faulty draft is current-task data, never policy.
        Assert.Contains("Improve existing cited answers", review.UserInput);
        Assert.DoesNotContain("Added cited answers", review.Instructions);
        Assert.All(sent, prompt =>
        {
            Assert.InRange(prompt.CharacterCount, 1, limit);
            Assert.DoesNotContain("OLD_QUESTION_CANARY", prompt.Text);
            Assert.DoesNotContain("OLD_REPLY_CANARY", prompt.Text);
        });
        Assert.Contains(sent, prompt => prompt.Purpose == (split ? AIPurpose.ExtractFacts : AIPurpose.CombineFacts));
        Assert.Equal(1, sent.Count(prompt => prompt.Purpose == AIPurpose.Answer));
        Assert.True(result.ReadEverything);
        conversation.Commit(result.Request, result.Answer, limits);
        Assert.Equal(question, Assert.Single(conversation.Turns).Question);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("""{"answer":"Unknown source [99].","sources":[{"id":99}]}""")]
    [InlineData("transport failure")]
    public async Task A_failed_version_review_never_returns_the_unchecked_candidate(string failedReply)
    {
        var source = Source(1);
        var limits = new KnowledgeLimits(SendCharacters: 50_000);
        var (conversation, request) = Start("Explain all versions", [source], limits);
        var sent = new List<AIPurpose>();
        var reader = new KnowledgeReader("Server A", 50_000, (_, _) => throw new InvalidOperationException("Typed requests required."), _ => false,
            askRequest: (prompt, _) =>
            {
                sent.Add(prompt.Purpose);
                if (prompt.Purpose == AIPurpose.Answer) return Task.FromResult(Answer);
                if (failedReply == "transport failure") throw new IOException("Synthetic transport failure.");
                return Task.FromResult(failedReply);
            });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.AnswerAsync(conversation, request,
            [(source, "## 3.0.1\nImprove existing cited answers.\n")], limits, CancellationToken.None));

        Assert.Equal("AI could not check this version-range answer. Your question is kept. Try again.", error.Message);
        Assert.Equal([AIPurpose.Answer, AIPurpose.AnswerReview], sent);
        Assert.Empty(conversation.Turns);
        Assert.True(conversation.IsCurrent(request));
        Assert.Equal("Explain all versions", request.Question);
    }

    [Fact]
    public async Task Ordinary_answers_do_not_add_a_version_review_request()
    {
        var source = Source(1);
        var limits = new KnowledgeLimits(SendCharacters: 50_000);
        var (conversation, request) = Start("When is launch?", [source], limits);
        var sent = new List<AIPurpose>();
        var reader = new KnowledgeReader("Server A", 50_000, (_, _) => throw new InvalidOperationException("Typed requests required."), _ => false,
            askRequest: (prompt, _) => { sent.Add(prompt.Purpose); return Task.FromResult(Answer); });

        var result = await reader.AnswerAsync(conversation, request, [(source, "Launch is in April.\n")], limits, CancellationToken.None);

        Assert.Equal([AIPurpose.Answer], sent);
        Assert.False(result.Answer.InsufficientEvidence);
    }

    [Theory]
    [InlineData(50_000)]
    [InlineData(80_000)]
    public async Task Every_ticked_note_is_read_in_parts_that_fit_this_connection(int limit)
    {
        var sources = new[] { Source(1), Source(2) };
        (KnowledgeSource, string)[] notes = [(sources[0], Lines("ALPHA", 100_000)), (sources[1], Lines("OMEGA", 61_000))];
        var limits = new KnowledgeLimits(SendCharacters: limit);
        var (conversation, request) = Start("Summarize the whole project", sources, limits);
        var sent = new List<string>();
        var reader = new KnowledgeReader("Server A", limit, (message, _) =>
        {
            sent.Add(message);
            return Task.FromResult(IsPart(message) ? SelectSourceRows(message) : Answer);
        }, _ => false);

        var result = await reader.AnswerAsync(conversation, request, notes, limits, CancellationToken.None);

        Assert.All(sent, message => Assert.True(message.Length <= limit, $"A request of {message.Length} went over {limit}."));
        var parts = sent.Where(IsPart).ToArray();
        Assert.True(parts.Length >= 161_000 / limit);
        foreach (var (source, text) in notes)
        {
            var restored = new System.Text.StringBuilder();
            foreach (var part in parts)
            {
                using var data = SourceData(part);
                var input = data.RootElement.GetProperty("sourcePart");
                if (input.GetProperty("sourceId").GetInt32() == source.Id)
                    foreach (var row in input.GetProperty("lines").EnumerateArray()) restored.Append(row.GetProperty("text").GetString());
            }
            Assert.Equal(text, restored.ToString());
        }
        Assert.Contains("AI read 100% of your 2 ticked notes.", result.Coverage);
        Assert.True(result.ReadEverything);
        Assert.False(result.Answer.InsufficientEvidence);
    }

    [Fact]
    public async Task A_size_rejection_retries_smaller_finishes_and_advises_without_going_below_the_minimum()
    {
        var source = Source(1);
        var note = Lines("ALPHA", 120_000);
        var limits = new KnowledgeLimits(SendCharacters: 100_000);
        var (conversation, request) = Start("Summarize the whole project", [source], limits);
        var sent = new List<int>();
        var reader = new KnowledgeReader("Server A", 100_000, (message, _) =>
        {
            sent.Add(message.Length);
            if (message.Length > 50_000) throw new Rejected();
            return Task.FromResult(IsPart(message) ? SelectSourceRows(message) : Answer);
        }, error => error is Rejected);

        var result = await reader.AnswerAsync(conversation, request, [(source, note)], limits, CancellationToken.None);

        Assert.False(result.Answer.InsufficientEvidence);
        var advice = System.Text.RegularExpressions.Regex.Match(result.Coverage, @"Server A accepted less than your limit\. Try ([\d,]+) characters\.");
        Assert.True(advice.Success, result.Coverage);
        var suggested = int.Parse(advice.Groups[1].Value, System.Globalization.NumberStyles.AllowThousands, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(suggested, KnowledgeLimits.MinimumSendCharacters, 50_000); // Never a size that was just refused.
        Assert.Equal(1, sent.Count(length => length > 50_000));
        Assert.Contains("AI read 100%", result.Coverage);
        Assert.All(sent, length => Assert.True(length <= 100_000));
        Assert.Equal(100_000, limits.SendCharacters);
    }

    [Fact]
    public async Task A_rejection_at_the_minimum_is_reported_and_not_retried_forever()
    {
        var source = Source(1);
        var limits = new KnowledgeLimits(SendCharacters: 8_000);
        var (conversation, request) = Start("Summarize the whole project", [source], limits);
        var calls = 0;
        var reader = new KnowledgeReader("Server A", 8_000, (_, _) => { calls++; throw new Rejected(); }, error => error is Rejected);

        await Assert.ThrowsAsync<Rejected>(() => reader.AnswerAsync(conversation, request, [(source, Lines("ALPHA", 40_000))], limits, CancellationToken.None));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_one_fact_question_reads_everything_before_saying_not_found()
    {
        var source = Source(1);
        var note = Lines("ALPHA", 30_000) + "The launch date is 14 April.\n" + Lines("OMEGA", 30_000);
        var limits = new KnowledgeLimits(SendCharacters: 20_000);
        var (conversation, request) = Start("When is the launch date?", [source], limits);
        Assert.NotEmpty(request.Message);
        var sent = new List<string>();
        var reader = new KnowledgeReader("Server A", 20_000, (message, _) =>
        {
            sent.Add(message);
            return Task.FromResult(IsPart(message) ? SelectSourceRows(message, text => text.Contains("14 April", StringComparison.Ordinal)) : sent.Count == 1 ? NotFound : Answer);
        }, _ => false);

        var result = await reader.AnswerAsync(conversation, request, [(source, note)], limits, CancellationToken.None);

        Assert.Equal(request.Message, sent[0]);
        Assert.Contains(sent.Where(IsPart), part => part.Contains("14 April", StringComparison.Ordinal));
        Assert.False(result.Answer.InsufficientEvidence);
        Assert.Contains("100%", result.Coverage);
    }

    [Fact]
    public void Text_above_one_million_characters_is_named_exactly()
    {
        KnowledgeSource first = Source(1, "plan"), second = Source(2, "budget"), third = Source(3, "extra");
        var (read, notRead, total) = KnowledgeReader.Within([(first, new string('a', 900_000)), (second, new string('b', 150_000)), (third, "c")]);
        Assert.Equal(1_000_000, read.Sum(note => note.Text.Length));
        Assert.Equal(1_050_001, total);
        Assert.Equal(["budget.md (the last 50,000 characters)", "extra.md"], notRead);
    }

    [Theory]
    [InlineData("Explain all super features from version 1 to latest", true)]
    [InlineData("What changed from v1.2 through v2.0?", true)]
    [InlineData("List every release", true)]
    [InlineData("Compare versions 3 to 5", true)]
    [InlineData("What changed in the latest version?", false)]
    [InlineData("What is new in this release?", false)]
    [InlineData("What changed since version 3?", true)]
    [InlineData("What changed from 2 to the latest version?", true)]
    public void A_version_range_is_not_treated_as_newest_only(string question, bool range)
    {
        Assert.Equal(range, KnowledgeRanking.AsksForVersionRange(question));
        if (range) Assert.False(KnowledgeRanking.AsksForChanges(question));
        else Assert.True(KnowledgeRanking.AsksForChanges(question));
        if (range) Assert.False(KnowledgeReader.IsOneFactQuestion(question));
    }

    [Theory]
    [InlineData("from 1 to 10 how risky is it?")]
    [InlineData("What happened from 2020 to 2024?")]
    public void Plain_number_spans_are_not_version_ranges(string question) => Assert.False(KnowledgeRanking.AsksForVersionRange(question));

    [Fact]
    public void Release_history_rules_reach_extraction_combination_and_the_final_answer()
    {
        const string question = "Explain all super features from version 1 to latest";
        const string material = "[Note 1]\n## 1.0.6\n- Saved connection sizes.\n## 1.0.0-rc.2\n- Review drafts.\n" +
            "[Note 2]\n# Tern 1.0.5\n- Starter templates.\n";
        var limits = new KnowledgeLimits(SendCharacters: 50_000);
        var (conversation, request) = Start(question, [Source(1), Source(2)], limits);
        var final = conversation.PrepareFromFacts(request, material, limits, limits.SendCharacters);
        Assert.NotNull(final);
        var prompts = new[] { KnowledgeReportBuilder.FactsPrompt(question, material), KnowledgeReportBuilder.CombinePrompt(question, material), final.Message };

        // This is a prompt contract: real provider acceptance separately checks how AI follows it.
        foreach (var prompt in prompts)
        {
            Assert.Contains("highest explicitly documented release number", prompt);
            Assert.Contains("comparing numeric components rather than text order", prompt);
            Assert.Contains("historical wording", prompt);
            Assert.Contains("Preserve every documented prerelease ID and suffix exactly; never infer an undocumented stable release", prompt);
            Assert.Contains("Keep every documented release ID paired with its own features and source number", prompt);
            Assert.Contains("Preserve whether each change was added, improved, fixed, or removed", prompt);
            Assert.Contains("Keep conditions and defaults exact", prompt);
            Assert.Contains("Do not infer first introduction from an improvement or fix", prompt);
            Assert.Contains("a changed default does not establish a new choice or control", prompt);
            Assert.Contains("Use neutral 'Changes include:' for release summaries", prompt);
            Assert.Contains("Cite the note ID that actually states each detail", prompt);
            Assert.Contains("A release link alone does not support the linked note's details", prompt);
            Assert.Contains("Do not assign unversioned sections to the nearest preceding release", prompt);
            Assert.Contains("still cover the supported releases", prompt);
            Assert.True(prompt.Length <= limits.SendCharacters);
        }
        Assert.EndsWith(material, prompts[0]);
        Assert.EndsWith(material, prompts[1]);
        var payloadStart = final.Message.LastIndexOf("\n{", StringComparison.Ordinal);
        Assert.True(payloadStart >= 0);
        using var payload = System.Text.Json.JsonDocument.Parse(final.Message[(payloadStart + 1)..]);
        Assert.Equal(material, payload.RootElement.GetProperty("factsFromNotes").GetString());
    }

    [Theory]
    [InlineData("Which risks are open?")]
    [InlineData("Who are the owners?")]
    [InlineData("How many customers complained?")]
    [InlineData("How much did we spend in total?")]
    [InlineData("Which notes mention payment?")]
    [InlineData("What is the count of overdue tasks?")]
    [InlineData("What is the number of customers?")]
    public void List_and_count_questions_read_everything(string question) => Assert.False(KnowledgeReader.IsOneFactQuestion(question));

    [Theory]
    [InlineData("What is the count of overdue tasks?")]
    [InlineData("What is the number of customers?")]
    public async Task Count_questions_read_all_note_parts_before_answering(string question)
    {
        var source = Source(1);
        var limits = new KnowledgeLimits(SendCharacters: 20_000);
        var (conversation, request) = Start(question, [source], limits);
        var sent = new List<string>();
        var reader = new KnowledgeReader("Server A", 20_000, (message, _) =>
        {
            sent.Add(message);
            return Task.FromResult(IsPart(message) ? SelectSourceRows(message, text => text.Contains("END_OF_COUNT_EVIDENCE", StringComparison.Ordinal)) :
                """{"answer":"- There are 14 customers and 3 overdue tasks [1].","sources":[{"id":1}]}""");
        }, _ => false);
        var note = Lines("START", 60_000) + "END_OF_COUNT_EVIDENCE: 14 customers and 3 overdue tasks.\n";

        var result = await reader.AnswerAsync(conversation, request, [(source, note)], limits, CancellationToken.None);

        Assert.True(IsPart(sent[0]));
        Assert.Contains(sent.Where(IsPart), message => message.Contains("END_OF_COUNT_EVIDENCE", StringComparison.Ordinal));
        Assert.DoesNotContain(request.Message, sent);
        Assert.True(result.ReadEverything);
        Assert.Contains("AI read 100% of your 1 ticked note.", result.Coverage);
    }

    [Theory]
    [InlineData("When is the launch date?")]
    [InlineData("Who is the project owner?")]
    [InlineData("Which date is the launch?")]
    public void Single_fact_questions_may_start_from_the_best_sections(string question) => Assert.True(KnowledgeReader.IsOneFactQuestion(question));

    [Fact]
    public void Note_markers_are_checked_as_citations_and_never_shown()
    {
        var limits = new KnowledgeLimits();
        var (conversation, request) = Start("When is launch?", [Source(1), Source(2)], limits);
        var answer = conversation.ReadAnswer("""{"answer":"- Launch is in April [Note 1].\n- QA is first [Note 2, continued].","sources":[]}""", request);
        Assert.Equal([1, 2], answer.Citations.Select(citation => citation.Source.Id).Order());
        Assert.DoesNotContain("Note", string.Join(' ', KnowledgeAnswerExport.Lines(answer)));
        Assert.Throws<InvalidOperationException>(() => conversation.ReadAnswer("""{"answer":"- Launch [Note 9].","sources":[]}""", request));
    }

    [Fact]
    public void Report_note_markers_are_removed_before_the_number_check()
    {
        const string reply = """{"headline":"Launch [Note 12]","recommendation":"Keep April [Note 12]","evidence":[{"fact":"Launch is in April [Note 12]","sourceId":12}]}""";
        var draft = KnowledgeReportBuilder.Read(reply, "When?", [(Source(12), "The launch is in April.")], "", DateTimeOffset.UnixEpoch);
        Assert.DoesNotContain("Note", draft.Report.Recommendation);
        Assert.Empty(draft.NumbersToCheck);
    }

    [Fact]
    public async Task A_selected_note_beyond_the_total_cannot_be_cited_as_if_it_was_supplied()
    {
        KnowledgeSource first = Source(1), unread = Source(2);
        var limits = new KnowledgeLimits(SendCharacters: 80_000);
        var (conversation, request) = Start("Summarize everything", [first, unread], limits);
        var reader = new KnowledgeReader("Server B", 80_000, (message, _) => Task.FromResult(
            IsPart(message) ? SelectSourceRows(message, _ => false) :
            """{"answer":"- An invented claim [Note 2].","sources":[{"id":2}]}"""), _ => false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.AnswerAsync(conversation, request,
            [(first, new string('a', 1_000_000)), (unread, "This note was not supplied.")], limits, CancellationToken.None));
    }

    [Fact]
    public async Task Best_sections_never_claim_the_full_text_was_read()
    {
        KnowledgeSource first = Source(1), second = Source(2, "extra");
        var limits = new KnowledgeLimits(SendCharacters: 20_000);
        var (conversation, request) = Start("When is the launch date?", [first, second], limits);
        var sent = new List<string>();
        var reader = new KnowledgeReader("Server A", 20_000, (message, _) => { sent.Add(message); return Task.FromResult(Answer); }, _ => false);
        var result = await reader.AnswerAsync(conversation, request, [(first, new string('a', 60_000)), (second, "Extra text.")], limits, CancellationToken.None);
        Assert.Equal(request.Message, Assert.Single(sent));
        Assert.StartsWith("AI read the best sections of your 2 ticked notes.", result.Coverage);
        Assert.DoesNotContain("100%", result.Coverage);
        Assert.DoesNotContain("Not read:", result.Coverage);
        Assert.False(result.ReadEverything);
    }

    [Fact]
    public async Task A_one_fact_question_never_sends_excerpts_from_beyond_the_total_allowance()
    {
        const string tail = "EXCLUDED_TAIL_EVIDENCE";
        const string excluded = "EXCLUDED_NOTE_EVIDENCE";
        var first = Source(1) with { Excerpt = tail + ": Launch is in April." };
        var second = Source(2, "extra") with { Excerpt = excluded };
        var limits = new KnowledgeLimits(SendCharacters: 80_000);
        var (conversation, request) = Start("When is the launch date?", [first, second], limits);
        Assert.Contains(tail, request.Message);
        Assert.Contains(excluded, request.Message);
        var sent = new List<string>();
        var reader = new KnowledgeReader("Server A", 80_000, (message, _) =>
        {
            sent.Add(message);
            return Task.FromResult(IsPart(message) ? SelectSourceRows(message, _ => false) : Answer);
        }, _ => false);

        var result = await reader.AnswerAsync(conversation, request,
            [(first, new string('a', 1_000_000) + tail), (second, excluded)], limits, CancellationToken.None);

        Assert.True(IsPart(sent[0]));
        Assert.All(sent, message => { Assert.DoesNotContain(tail, message); Assert.DoesNotContain(excluded, message); });
        Assert.Equal([1], result.Request.Sources.Select(source => source.Id));
        Assert.StartsWith("AI read 1,000,000 characters of your 2 ticked notes.", result.Coverage);
        Assert.Contains($"note1.md (the last {tail.Length} characters)", result.Coverage);
        Assert.Contains("extra.md", result.Coverage);
        Assert.DoesNotContain("best sections", result.Coverage);
        Assert.False(result.ReadEverything);
    }

    [Fact]
    public void Splitting_keeps_every_line_and_labels_continued_notes()
    {
        var text = Lines("ALPHA", 30_000);
        var parts = KnowledgeReportBuilder.NoteParts([(Source(7), text)], 8_000);
        Assert.True(parts.Count > 3);
        Assert.All(parts, part => { Assert.True(part.Length <= 8_000); Assert.StartsWith("[Note 7", part); });
        Assert.Equal(text.Trim().Split('\n').Length, parts.Sum(part => part.Split('\n').Count(line => line.StartsWith("ALPHA", StringComparison.Ordinal))));
        var (_, second) = KnowledgeReportBuilder.SplitInHalf(parts[0]);
        Assert.StartsWith("[Note 7: note7, continued]", second);
    }

    [Theory]
    [InlineData(50_000)]
    [InlineData(80_000)]
    public void Release_continuations_keep_their_heading_and_preserve_every_body_character(int capacity)
    {
        var changes = "## 1.0.4\n" + Lines("FEATURE_104", capacity * 2) +
            "### .NET 10.0.15\n### Dependencies 9.0\n### 2026-11-23\n" + Lines("FEATURE_104", capacity) +
            "## 1.0.3\n" + Lines("FEATURE_103", capacity);
        var olderNote = "# Tern 1.0.5\n" + Lines("FEATURE_105", capacity * 3 / 2);
        var parts = KnowledgeReportBuilder.NoteParts([(Source(1), changes), (Source(2), olderNote)], capacity);
        var seen = new HashSet<string>();

        Assert.All(parts, part => Assert.True(part.Length <= capacity));
        Assert.Equal(changes + olderNote, WithoutNoteLabels(string.Concat(parts)));
        foreach (var part in parts)
        {
            var labels = System.Text.RegularExpressions.Regex.Matches(part, @"^\[Note \d+[^\]\r\n]*\]\n", System.Text.RegularExpressions.RegexOptions.Multiline);
            for (var index = 0; index < labels.Count; index++)
            {
                var label = labels[index];
                if (!label.Value.Contains(", continued", StringComparison.Ordinal)) continue;
                var end = index + 1 < labels.Count ? labels[index + 1].Index : part.Length;
                var body = part[(label.Index + label.Length)..end];
                var feature = body.Split('\n').First(line => line.StartsWith("FEATURE_", StringComparison.Ordinal));
                var release = feature.StartsWith("FEATURE_104", StringComparison.Ordinal) ? "1.0.4"
                    : feature.StartsWith("FEATURE_103", StringComparison.Ordinal) ? "1.0.3" : "1.0.5";
                Assert.Contains(", release context: " + release + ", release level: " + (release == "1.0.5" ? "1" : "2") + "]", label.Value);
                Assert.StartsWith(release == "1.0.5" ? "[Note 2" : "[Note 1", label.Value);
                seen.Add(release);
            }
        }
        Assert.Equal(["1.0.3", "1.0.4", "1.0.5"], seen.Order());
    }

    [Theory]
    [InlineData(50_000)]
    [InlineData(80_000)]
    public void Size_retry_halves_update_the_release_after_a_new_heading(int capacity)
    {
        var body = Lines("FEATURE_104", capacity / 4) + "## 1.0.3\n" + Lines("FEATURE_103", capacity / 2);
        var part = "[Note 1: changes, continued, release context: 1.0.4, release level: 2]\n" + body;
        Assert.True(part.Length < capacity);

        var (first, second) = KnowledgeReportBuilder.SplitInHalf(part);
        var (againFirst, againSecond) = KnowledgeReportBuilder.SplitInHalf(second);

        Assert.StartsWith("[Note 1: changes, continued, release context: 1.0.3, release level: 2]\n", second);
        Assert.StartsWith("[Note 1: changes, continued, release context: 1.0.3, release level: 2]\n", againSecond);
        Assert.Equal(body, WithoutNoteLabels(first + againFirst + againSecond));
    }

    [Theory]
    [InlineData(50_000, true)]
    [InlineData(80_000, true)]
    [InlineData(50_000, false)]
    [InlineData(80_000, false)]
    public void Size_retry_release_context_never_leaks_from_another_note(int capacity, bool secondHasRelease)
    {
        var firstBody = "## 1.0.4\n" + Lines("FEATURE_104", capacity / 10);
        var secondBody = (secondHasRelease ? "# Tern 1.0.5\n" : "### .NET 10.0.15\n### Dependencies 9.0\n### 2026-11-23\n") +
            Lines("FEATURE_OTHER", capacity * 7 / 10);
        var part = "[Note 1: changes]\n" + firstBody + "[Note 2: older]\n" + secondBody;
        Assert.True(part.Length < capacity);

        var (first, second) = KnowledgeReportBuilder.SplitInHalf(part);

        Assert.StartsWith(secondHasRelease ? "[Note 2: older, continued, release context: 1.0.5, release level: 1]\n" : "[Note 2: older, continued]\n", second);
        Assert.DoesNotContain("release context: 1.0.4", second);
        Assert.Equal(firstBody + secondBody, WithoutNoteLabels(first + second));
    }

    [Theory]
    [InlineData(50_000, "1.0.0-rc.1")]
    [InlineData(80_000, "1.0.0-rc.2")]
    public void Release_context_preserves_prerelease_suffixes(int capacity, string release)
    {
        var body = $"## {release} (not stable)\n" + Lines("PRERELEASE_FEATURE", capacity * 2);
        var parts = KnowledgeReportBuilder.NoteParts([(Source(1), body)], capacity);
        Assert.True(parts.Count > 1);
        Assert.All(parts.Skip(1), part => Assert.StartsWith($"[Note 1: note1, continued, release context: {release}, release level: 2]\n", part));
        Assert.Equal(body, WithoutNoteLabels(string.Concat(parts)));
    }

    [Theory]
    [InlineData(50_000)]
    [InlineData(80_000)]
    public void Unversioned_sibling_sections_end_release_context_in_continued_parts(int capacity)
    {
        var body = "## 4.0.1\n" + Lines("RELEASE_DETAIL", capacity) +
            "## Historical review\n" + Lines("UNVERSIONED_DETAIL", capacity * 2) +
            "## 4.0.0\n" + Lines("OLDER_RELEASE_DETAIL", capacity);
        var parts = KnowledgeReportBuilder.NoteParts([(Source(1), body)], capacity);
        var unversionedParts = parts.Where(part => WithoutNoteLabels(part).StartsWith("UNVERSIONED_DETAIL", StringComparison.Ordinal)).ToArray();

        Assert.NotEmpty(unversionedParts);
        Assert.All(unversionedParts, part =>
        {
            Assert.StartsWith("[Note 1: note1, continued, release context: unversioned]\n", part);
            Assert.DoesNotContain("release context: 4.0.1", part);
        });
        Assert.Contains(parts, part => part.StartsWith("[Note 1: note1, continued, release context: 4.0.0, release level: 2]\n", StringComparison.Ordinal));
        Assert.All(parts, part => Assert.True(part.Length <= capacity));
        Assert.Equal(body, WithoutNoteLabels(string.Concat(parts)));
    }

    [Theory]
    [InlineData(50_000)]
    [InlineData(80_000)]
    public void Unversioned_sibling_sections_end_inherited_release_context_in_retry_halves(int capacity)
    {
        var body = Lines("RELEASE_DETAIL", capacity / 4) + "## Historical review\n" + Lines("UNVERSIONED_DETAIL", capacity / 2);
        var part = "[Note 1: changes, continued, release context: 4.0.1, release level: 2]\n" + body;

        var (first, second) = KnowledgeReportBuilder.SplitInHalf(part);
        var (againFirst, againSecond) = KnowledgeReportBuilder.SplitInHalf(second);

        Assert.StartsWith("[Note 1: changes, continued, release context: unversioned]\n", second);
        Assert.StartsWith("[Note 1: changes, continued, release context: unversioned]\n", againSecond);
        Assert.Equal(body, WithoutNoteLabels(first + againFirst + againSecond));
    }

    [Theory]
    [InlineData(50_000)]
    [InlineData(80_000)]
    public void A_sibling_section_with_an_explicit_release_link_keeps_that_release_context(int capacity)
    {
        var body = "## 4.0.1\n" + Lines("RELEASE_DETAIL", capacity / 4) +
            "## Historical review (still 4.0.1)\n" + Lines("LINKED_DETAIL", capacity * 2);
        var parts = KnowledgeReportBuilder.NoteParts([(Source(1), body)], capacity);

        Assert.All(parts.Skip(1), part => Assert.StartsWith("[Note 1: note1, continued, release context: 4.0.1, release level: 2]\n", part));
        Assert.Equal(body, WithoutNoteLabels(string.Concat(parts)));
    }

    private static string WithoutNoteLabels(string text) => System.Text.RegularExpressions.Regex.Replace(text,
        @"^\[Note \d+[^\]\r\n]*\]\n", "", System.Text.RegularExpressions.RegexOptions.Multiline);

    [Fact]
    public void Old_settings_with_removed_limits_load_safely()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.Path);
        store.Save(new AppSettings { KnowledgeAllowRemote = true });
        var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(store.FilePath))!.AsObject();
        saved["KnowledgeSourceLimit"] = 12;
        saved["KnowledgeContextLimit"] = 28000;
        File.WriteAllText(store.FilePath, saved.ToJsonString());
        var reopened = new SettingsStore(dir.Path);
        var loaded = reopened.Load();
        Assert.Null(reopened.LastLoadProblem);
        Assert.True(loaded.KnowledgeAllowRemote);
        Assert.Null(typeof(AppSettings).GetProperty("KnowledgeSourceLimit"));
        Assert.Null(typeof(AppSettings).GetProperty("KnowledgeContextLimit"));
    }

    [Fact]
    public void Parts_preserve_indentation_blank_lines_and_non_English_text()
    {
        var text = "\n    café 漢字 😀\n\n" + string.Concat(Enumerable.Repeat("    keep this indentation\n", 1_000)) + "  \n";
        var parts = KnowledgeReportBuilder.NoteParts([(Source(1), text)], 8_000);
        var restored = string.Concat(parts.Select(part => System.Text.RegularExpressions.Regex.Replace(part, @"^\[Note 1[^\]\r\n]*\]\n", "", System.Text.RegularExpressions.RegexOptions.Multiline)));
        Assert.Equal(text, restored);
    }
}
