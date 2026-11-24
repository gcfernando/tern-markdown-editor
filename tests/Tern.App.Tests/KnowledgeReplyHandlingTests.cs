using Tern.App.Knowledge;

namespace Tern.App.Tests;

public sealed class KnowledgeReplyHandlingTests
{
    private const string Root = @"C:\w";
    private static readonly KnowledgeSource First = new(1, Root, @"C:\w\launch.md", "launch.md", "Launch", "Plan", 1, "Launch is 14 April. Cost is $18,000.", "d", []);
    private static readonly KnowledgeSource Second = new(2, Root, @"C:\w\budget.md", "budget.md", "Budget", "Costs", 1, "Budget is 1.5 million.", "d", []);
    private static readonly (KnowledgeSource, string)[] Notes = [(First, "Launch is 14 April. Cost is $18,000. Growth was 1.5 percent.")];

    private static (KnowledgeConversation Conversation, KnowledgeRequest Request) Prepared()
    {
        var conversation = new KnowledgeConversation();
        conversation.Start(Root);
        return (conversation, conversation.Prepare("When is launch?", "Explain", [First, Second], new()));
    }

    private static KnowledgeAnswer Read(string reply) { var (conversation, request) = Prepared(); return conversation.ReadAnswer(reply, request); }

    [Fact]
    public void A_sentence_around_the_json_does_not_lose_the_answer()
    {
        var answer = Read("Here is the answer:\n{\"answer\":\"- Launch is 14 April [1]\",\"sources\":[{\"id\":1,\"reason\":\"Plan\"}]}\nHope that helps.");
        Assert.Equal("Launch is 14 April", Assert.Single(KnowledgeAnswerExport.Lines(answer)));
    }

    [Theory]
    [InlineData("{\"answer\":\"\",\"insufficientEvidence\":true,\"sources\":[]}")]
    [InlineData("{\"insufficientEvidence\":true}")]
    [InlineData("{\"answer\":\"Not there\",\"insufficientEvidence\":true,\"sources\":[{\"id\":99,\"reason\":\"x\"}]}")]
    public void Not_found_is_a_clear_result_even_with_an_empty_or_partial_reply(string reply)
    {
        var answer = Read(reply);
        Assert.True(answer.InsufficientEvidence);
        Assert.Empty(answer.Citations);
    }

    [Fact]
    public void Several_notes_in_one_marker_are_checked_and_hidden_from_the_reader()
    {
        var answer = Read("{\"answer\":\"- Launch and budget agreed [1, 2]\",\"sources\":[{\"id\":1,\"reason\":\"Plan\"}]}");
        Assert.Equal([1, 2], answer.Citations.Select(citation => citation.Source.Id).Order());
        Assert.Equal("Launch and budget agreed", Assert.Single(KnowledgeAnswerExport.Lines(answer)));
        Assert.Throws<InvalidOperationException>(() => Read("{\"answer\":\"Invented [1, 9]\",\"sources\":[{\"id\":1,\"reason\":\"Plan\"}]}"));
    }

    [Fact]
    public void Bracketed_wording_that_starts_with_a_number_is_not_a_failure()
    {
        var answer = Read("{\"answer\":\"- See the [2026 plan] for launch [1]\",\"sources\":[{\"id\":1,\"reason\":\"Plan\"}]}");
        Assert.Contains("[2026 plan]", Assert.Single(KnowledgeAnswerExport.Lines(answer)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_note_number_written_as_text_or_a_missing_reason_is_accepted()
    {
        var answer = Read("{\"answer\":\"- Launch [1]\",\"sources\":[{\"id\":\"1\"},{\"id\":1,\"reason\":\"again\"}]}");
        Assert.Equal(1, Assert.Single(answer.Citations).Source.Id);
    }

    [Theory]
    [InlineData("{\"answer\":\"[1]\",\"sources\":[{\"id\":1,\"reason\":\"Plan\"}]}")]
    [InlineData("{\"answer\":\"- [1, 2]\",\"sources\":[{\"id\":1,\"reason\":\"Plan\"}]}")]
    public void An_answer_with_only_markers_is_reported_as_empty(string reply) =>
        Assert.Contains("empty", Assert.Throws<InvalidOperationException>(() => Read(reply)).Message, StringComparison.Ordinal);

    [Fact]
    public void Accents_and_symbols_are_not_inflated_in_the_request()
    {
        var conversation = new KnowledgeConversation();
        conversation.Start(Root);
        var request = conversation.Prepare("R&D café?", "Explain", [First with { Excerpt = "Zoë’s R&D note +5%" }], new());
        Assert.Contains("Zoë’s R&D note +5%", request.Message, StringComparison.Ordinal);
        Assert.Contains("café", KnowledgeConversation.SuggestionPrompt("café"), StringComparison.Ordinal);
    }

    [Fact]
    public void Report_lists_never_show_source_markers()
    {
        const string reply = """{"headline":"When?","recommendation":"14 April","reasons":["Launch is 14 April [1]"],"onTrack":["Cost agreed [1, 2]"],"needsAttention":["Check date [1]"]}""";
        var report = KnowledgeReportBuilder.Read(reply, "When?", Notes, "", DateTimeOffset.UnixEpoch).Report;
        Assert.Equal("Launch is 14 April", Assert.Single(report.Reasons));
        Assert.Equal("Cost agreed", Assert.Single(report.OnTrack));
        Assert.Equal("Check date", Assert.Single(report.NeedsAttention));
    }

    [Theory]
    [InlineData("""{"headline":"Q","recommendation":"R","timeline":[{"when":"30 June","what":"Go live"}]}""", "30")]
    [InlineData("""{"headline":"Q","recommendation":"R","risks":[{"risk":"Lose $77,000"}]}""", "77,000")]
    [InlineData("""{"headline":"Q","recommendation":"R","actions":[{"action":"Hire 42 people"}]}""", "42")]
    [InlineData("""{"headline":"Q","recommendation":"R","needsAttention":["Down 63 percent"]}""", "63")]
    [InlineData("""{"headline":"Q","recommendation":"R","onTrack":["Saved 51 hours"]}""", "51")]
    [InlineData("""{"headline":"Q","recommendation":"R","decisionBy":"9 May"}""", "9")]
    [InlineData("""{"title":"Plan for 2031","headline":"Q","recommendation":"R"}""", "2031")]
    public void Invented_numbers_are_flagged_in_every_section(string reply, string number) =>
        Assert.Contains(number, KnowledgeReportBuilder.Read(reply, "Q", Notes, "", DateTimeOffset.UnixEpoch).NumbersToCheck);

    [Fact]
    public void A_number_is_only_trusted_when_the_whole_value_is_in_the_notes()
    {
        const string reply = """{"headline":"Growth was 1 percent","recommendation":"Spend $18000 by 14 April, then 1.5 more."}""";
        var flagged = KnowledgeReportBuilder.Read(reply, "Q", Notes, "", DateTimeOffset.UnixEpoch).NumbersToCheck;
        Assert.Equal(["1"], flagged);
    }

    [Fact]
    public void A_note_number_written_as_text_does_not_break_the_report()
    {
        const string reply = """{"headline":"When?","recommendation":"14 April","evidence":[{"fact":"Launch is 14 April","sourceId":"1"}]}""";
        Assert.Single(KnowledgeReportBuilder.Read(reply, "When?", Notes, "", DateTimeOffset.UnixEpoch).Report.Evidence);
    }

    [Fact]
    public void A_malformed_report_source_cannot_be_silently_discarded()
    {
        const string reply = """{"headline":"When?","recommendation":"14 April","evidence":[{"fact":"Other","sourceId":{"x":1}}]}""";
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.Read(reply, "When?", Notes, "", DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void A_report_says_when_long_notes_were_only_partly_read()
    {
        const string reply = """{"headline":"Q","recommendation":"R"}""";
        var longNote = string.Join('\n', Enumerable.Repeat("A line of project detail for the report.", 1000));
        var partial = KnowledgeReportBuilder.Read(reply, "Q", [(First, longNote)], "", DateTimeOffset.UnixEpoch, partlyRead: true).Report;
        Assert.Contains("partly read", partial.Missing[0].Item, StringComparison.Ordinal);
        Assert.Empty(KnowledgeReportBuilder.Read(reply, "Q", Notes, "", DateTimeOffset.UnixEpoch).Report.Missing);
    }

    [Fact]
    public void Splitting_long_notes_never_cuts_an_emoji_in_half()
    {
        var parts = KnowledgeReportBuilder.NoteParts([(First, string.Concat(Enumerable.Repeat("😀", 6000)))], 4_600);
        Assert.All(parts, part => Assert.False(char.IsHighSurrogate(part[^1])));
    }
}
