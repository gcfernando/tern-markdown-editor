using Tern.App.Knowledge;
using Tern.App.Shell;

namespace Tern.App.Tests;

public sealed class KnowledgeNaturalQuestionTests
{
    [Theory]
    [InlineData("What are the latest features talks abour in 1.0.6", "1.0.6")]
    [InlineData("Which improvements came with v3.17.42-rc.4?", "3.17.42-rc.4")]
    [InlineData("Describe the changes discussed for 12.8.29.", "12.8.29")]
    public async Task Natural_questions_keep_arbitrary_release_identifiers_without_becoming_phrase_filters(string question, string version)
    {
        using var f = new ShellFixture();
        await f.WriteAsync("shipping.md", $"# Shipping history\n\n## v{version}\n\nOffline diagrams and searchable bookmarks.\n");
        await f.WriteAsync("other.md", "# Lunch\n\nSoup was served.\n");
        using var workspace = await WorkspaceSession.OpenAsync(f.Docs, f.Root.File("index"));

        Assert.False(KnowledgeRetrieval.IsAdvancedQuery(question));
        var result = await new KnowledgeRetrieval().SearchAsync(workspace, question, new());

        var source = Assert.Single(result.Sources);
        Assert.Equal("shipping.md", source.RelativePath);
        Assert.Contains(version, source.Excerpt);
        Assert.Contains("Offline diagrams", source.Excerpt);
        Assert.NotEmpty(result.Shortlist);
        Assert.Null(result.Coverage.MissingSubject);
    }

    [Theory]
    [InlineData("Tell me about customer-facing support.", false)]
    [InlineData("What happened on 2027-02-14?", false)]
    [InlineData("Which notes mention example.test?", false)]
    [InlineData("\"offline diagrams\"", true)]
    [InlineData("heading:release", true)]
    [InlineData("title:shipping -archived", true)]
    [InlineData("bookmark*", true)]
    public void Advanced_search_requires_explicit_syntax(string question, bool expected) =>
        Assert.Equal(expected, KnowledgeRetrieval.IsAdvancedQuery(question));

    [Fact]
    public async Task Explicit_phrase_and_field_filters_still_exclude_nonmatching_notes()
    {
        using var f = new ShellFixture();
        await f.WriteAsync("shipping.md", "# Shipping history\n\nOffline diagrams are available.\n");
        await f.WriteAsync("other.md", "# Other notes\n\nDiagrams can also be used offline.\n");
        using var workspace = await WorkspaceSession.OpenAsync(f.Docs, f.Root.File("index"));

        var result = await new KnowledgeRetrieval().SearchAsync(workspace, "title:shipping \"offline diagrams\"", new());

        Assert.Equal("shipping.md", Assert.Single(result.Sources).RelativePath);
    }

    [Fact]
    public async Task Dynamic_meaning_terms_can_recover_an_unfamiliar_subject_without_question_specific_corrections()
    {
        using var f = new ShellFixture();
        await f.WriteAsync("operations.md", "# Equipment\n\nPortable generators require a monthly load test.\n");
        using var workspace = await WorkspaceSession.OpenAsync(f.Docs, f.Root.File("index"));
        var retrieval = new KnowledgeRetrieval();
        const string question = "How is the spare powr thing checked?";
        var first = await retrieval.SearchAsync(workspace, question, new());
        Assert.Empty(first.Sources);
        var meaning = KnowledgeConversation.ReadMeaningTerms("""{"terms":["generator","load","test"]}""");

        var wider = await retrieval.SearchAsync(workspace, question, new(), meaningTerms: meaning);

        Assert.Equal("operations.md", Assert.Single(wider.Sources).RelativePath);
    }

    [Fact]
    public async Task An_optional_interpreted_query_is_only_retrieval_data_and_cannot_create_advanced_filters()
    {
        using var f = new ShellFixture();
        await f.WriteAsync("operations.md", "# Maintenance\n\nPortable generators require a monthly load test.\n");
        using var workspace = await WorkspaceSession.OpenAsync(f.Docs, f.Root.File("index"));
        const string question = "How is the spare powr thing checked?";
        const string reply = """{"searchQuery":"title:generator -monthly","clarification":"","terms":["load"],"questions":["How is backup power tested?"]}""";
        var query = KnowledgeConversation.ReadMeaningQuery(reply);
        Assert.Equal("title:generator -monthly", query);
        Assert.Null(KnowledgeConversation.ReadMeaningClarification(reply));
        var result = await new KnowledgeRetrieval().SearchAsync(workspace, question, new(), meaningQuery: query);
        Assert.Equal("operations.md", Assert.Single(result.Sources).RelativePath);
        Assert.False(KnowledgeRetrieval.IsAdvancedQuery(question));
        Assert.Null(KnowledgeConversation.ReadMeaningQuery("""{"terms":["generator"]}"""));
        Assert.Equal("Which project?", KnowledgeConversation.ReadMeaningClarification("""{"clarification":"Which project?"}"""));
        using var requestData = System.Text.Json.JsonDocument.Parse(KnowledgeConversation.MeaningRequest(question).CurrentInput);
        Assert.Equal(question, requestData.RootElement.GetProperty("question").GetString());
    }

    [Fact]
    public async Task An_interpreted_query_does_not_replace_the_persons_explicit_search_filter()
    {
        using var f = new ShellFixture();
        await f.WriteAsync("shipping.md", "# Shipping\n\nOffline diagrams.\n");
        await f.WriteAsync("lunch.md", "# Lunch\n\nSoup was served.\n");
        using var workspace = await WorkspaceSession.OpenAsync(f.Docs, f.Root.File("index"));
        var result = await new KnowledgeRetrieval().SearchAsync(workspace, "title:shipping", new(), meaningQuery: "soup");
        Assert.Equal("shipping.md", Assert.Single(result.Sources).RelativePath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Unmatched subject")]
    public void Empty_search_does_not_claim_to_have_read_every_file_or_prove_the_subject_absent(string? subject)
    {
        var coverage = new KnowledgeCoverage { FilesInFolder = 83, MissingSubject = subject };
        var explanation = coverage.ExplainEmpty();
        Assert.DoesNotContain("83", explanation.Why);
        Assert.DoesNotContain("none", explanation.Why, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("checked the", explanation.Why, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("may have been missed", explanation.Why);
    }
}
