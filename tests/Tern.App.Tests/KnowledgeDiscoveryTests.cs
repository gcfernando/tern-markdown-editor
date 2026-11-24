using Tern.App.Knowledge;
using Tern.App.Shell;

namespace Tern.App.Tests;

/// <summary>Discovery, automatic selection, coverage and report filling for Explore your notes.</summary>
public sealed class KnowledgeDiscoveryTests
{
    private static async Task<ShellFixture> ProjectAsync()
    {
        var f = new ShellFixture();
        var filler = string.Join(' ', Enumerable.Repeat("Tern changes the way a team works and every change is tracked by Tern.", 6));
        for (var i = 0; i < 12; i++) await f.WriteAsync($"guide-{i}.md", $"# Guide {i}\n\nTern guide. {filler}\n");
        await f.WriteAsync("CHANGELOG.md", "# Changelog\n\n## 2.1.0\n\n### Search\n\n- Search now ranks notes by meaning.\n\n### Reports\n\n- Reports fill in their title.\n\n## 2.0.0 — March 3 2026\n\n- Old release item.\n\n"
            + string.Join('\n', Enumerable.Range(0, 400).Select(i => $"- Older Tern change number {i} with extra words.")));
        return f;
    }

    [Theory]
    [InlineData("What are the latest changes of Tern?")]
    [InlineData("What changed recently?")]
    [InlineData("What is new in the newest version?")]
    public async Task Change_questions_select_the_changelog_and_read_its_newest_release(string question)
    {
        using var f = await ProjectAsync();
        using var workspace = await WorkspaceSession.OpenAsync(f.Docs, f.Root.File("index"));
        var result = await new KnowledgeRetrieval().SearchAsync(workspace, question, new());
        var first = result.Sources[0];
        Assert.Equal("CHANGELOG.md", first.RelativePath);
        Assert.True(first.Recommended);
        Assert.Equal("2.1.0", first.Heading);
        Assert.Contains("ranks notes by meaning", first.Excerpt);
        Assert.Contains("fill in their title", first.Excerpt);
        Assert.DoesNotContain("Old release item", first.Excerpt);
    }

    [Fact]
    public async Task A_change_history_with_an_unusual_file_name_is_found_from_its_structure()
    {
        using var f = new ShellFixture();
        var filler = string.Join(' ', Enumerable.Repeat("Atlas project notes about how the team plans its week.", 8));
        for (var i = 0; i < 10; i++) await f.WriteAsync($"plan-{i}.md", $"# Plan {i}\n\nAtlas {filler}\n");
        await f.WriteAsync("team/shipping-log.md", "# Shipping log\n\n## Version 3.2 - 2026-05-01\n\n- Atlas added offline maps.\n\n## Version 3.1 - 2026-01-10\n\n- Atlas fixed sign in.\n");
        using var workspace = await WorkspaceSession.OpenAsync(f.Docs, f.Root.File("index"));
        var result = await new KnowledgeRetrieval().SearchAsync(workspace, "What is the latest version of Atlas?", new());
        var source = result.Sources[0];
        Assert.Equal("team/shipping-log.md", source.RelativePath);
        Assert.Equal("2026-05-01", source.DocumentedDate);
        Assert.Contains("offline maps", source.Excerpt);
        Assert.NotNull(source.FileSavedUtc);
    }

    [Fact]
    public void Newest_release_is_chosen_by_version_not_by_position()
    {
        var headings = new[] { "1.2.0", "1.10.0", "1.9.0" }.Select((text, index) => new Tern.Markdown.HeadingInfo(2, text, text, index + 1, default)).ToArray();
        Assert.Equal(1, KnowledgeRanking.NewestRelease(headings)!.Value.HeadingIndex);
    }

    [Fact]
    public async Task Content_is_found_when_the_file_name_gives_no_hint()
    {
        using var f = new ShellFixture();
        await f.WriteAsync("meeting-14.md", "# Thursday\n\nThe vendor refund deadline moved to 9 June.\n");
        await f.WriteAsync("other.md", "# Lunch\n\nSoup was served.\n");
        using var workspace = await WorkspaceSession.OpenAsync(f.Docs, f.Root.File("index"));
        var result = await new KnowledgeRetrieval().SearchAsync(workspace, "When is the refund deadline?", new());
        Assert.Equal("meeting-14.md", Assert.Single(result.Sources).RelativePath);
    }

    [Fact]
    public async Task Words_with_the_same_meaning_from_ai_find_a_note_the_question_does_not_match()
    {
        using var f = new ShellFixture();
        await f.WriteAsync("policy.md", "# Returns\n\nCustomers receive a reimbursement within 14 days.\n");
        await f.WriteAsync("other.md", "# Lunch\n\nSoup was served.\n");
        using var workspace = await WorkspaceSession.OpenAsync(f.Docs, f.Root.File("index"));
        var retrieval = new KnowledgeRetrieval();
        var plain = await retrieval.SearchAsync(workspace, "How do I get my money back?", new());
        Assert.Empty(plain.Sources);
        Assert.True(plain.Coverage.WeakMatch);
        var terms = KnowledgeConversation.ReadMeaningTerms("""{"terms":["reimbursement","refund"],"questions":["How do refunds work?"]}""");
        var wider = await retrieval.SearchAsync(workspace, "How do I get my money back?", new(), meaningTerms: terms);
        Assert.Equal("policy.md", Assert.Single(wider.Sources).RelativePath);
    }

    [Fact]
    public async Task Broad_ai_words_do_not_pull_the_answer_away_from_the_section_the_question_names()
    {
        using var f = new ShellFixture();
        var noise = string.Join(' ', Enumerable.Repeat("Options, color, style and design settings.", 12));
        await f.WriteAsync("guide.md", $"# Guide\n\n## Display options\n\n{noise}\n\n## Themes\n\nChoose Paper, Linen or Midnight.\n");
        using var workspace = await WorkspaceSession.OpenAsync(f.Docs, f.Root.File("index"));
        var result = await new KnowledgeRetrieval().SearchAsync(workspace, "Which themes can I choose?", new(ExcerptCharacters: 200), meaningTerms: ["options", "color", "style", "design"]);
        var source = Assert.Single(result.Sources);
        Assert.Equal("Themes", source.Heading);
        Assert.Contains("Paper, Linen or Midnight", source.Excerpt);
    }

    [Fact]
    public void Ai_search_help_sends_only_the_question()
    {
        var prompt = KnowledgeConversation.MeaningPrompt("Where is my invoice?");
        Assert.Contains("Where is my invoice?", prompt);
        Assert.Contains("Do not answer", prompt);
        Assert.Empty(KnowledgeConversation.ReadMeaningTerms("not json"));
    }

    [Fact]
    public void Ai_note_choice_sees_names_and_headings_only_and_cannot_invent_a_note()
    {
        KnowledgeShortlistNote[] shortlist = [new(1, @"C:\w\a.md", "a.md", "Alpha", ["Plan"]), new(2, @"C:\w\b.md", "b.md", "Beta", []), new(3, @"C:\w\c.md", "c.md", "Gamma", [])];
        var prompt = KnowledgeConversation.SelectionPrompt("Which plan?", shortlist, 6);
        Assert.Contains("a.md", prompt);
        Assert.Contains("Plan", prompt);
        var (chosen, also) = KnowledgeConversation.ReadSelection("""{"notes":[{"id":2,"why":"Holds the plan"},{"id":9,"why":"Invented"},{"id":2,"why":"Again"}],"alsoRead":[3,2,8]}""", shortlist, 6);
        Assert.Equal("b.md", Assert.Single(chosen).Note.RelativePath);
        Assert.Equal("Holds the plan", chosen[0].Why);
        Assert.Equal("c.md", Assert.Single(also).RelativePath);
        Assert.Empty(KnowledgeConversation.ReadSelection("not json", shortlist, 6).Chosen);
    }

    [Fact]
    public void Ai_email_cannot_add_a_number_the_checked_answer_does_not_have()
    {
        var answer = new KnowledgeAnswer("- Launch is 14 April [1].", "", [], false);
        Assert.Equal("The launch is on 14 April.", KnowledgeAnswerExport.ReadEmail("""{"email":"The launch is on 14 April."}""", answer));
        Assert.Null(KnowledgeAnswerExport.ReadEmail("""{"email":"The launch is on 15 April."}""", answer));
        Assert.Null(KnowledgeAnswerExport.ReadEmail("not json", answer));
        Assert.StartsWith("Hi,\n\nThe launch", KnowledgeAnswerExport.ForEmail(answer, "The launch is on 14 April."));
    }

    [Fact]
    public void Ai_report_check_and_advice_are_read_safely()
    {
        Assert.Equal(["Sales doubled"], KnowledgeReportBuilder.ReadUnsupported("""{"unsupported":["Sales doubled"]}"""));
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportBuilder.ReadUnsupported("not json"));
        Assert.Empty(KnowledgeReportBuilder.ReadUnsupported("""{"unsupported":[]}"""));
        Assert.Equal("Try the project name.", KnowledgeConversation.ReadAdvice("""{"advice":"Try the project name.","questions":[]}"""));
        Assert.Null(KnowledgeConversation.ReadAdvice("not json"));
        var prompt = KnowledgeConversation.RecoveryPrompt("Where is Mars?", "No note mentions Mars");
        Assert.Contains("do not change it", prompt);
    }

    [Fact]
    public async Task A_named_subject_in_no_note_is_explained_instead_of_returning_unrelated_notes()
    {
        using var f = new ShellFixture();
        await f.WriteAsync("budget.md", "# Budget\n\nThe budget for Venus was approved.\n");
        using var workspace = await WorkspaceSession.OpenAsync(f.Docs, f.Root.File("index"));
        var result = await new KnowledgeRetrieval().SearchAsync(workspace, "Who approved the budget for Mars?", new());
        Assert.Empty(result.Sources);
        Assert.Equal(KnowledgeEmptyReason.NamedSubjectMissing, result.Coverage.EmptyReason);
        Assert.Contains("Mars", result.Coverage.ExplainEmpty().Title);
    }

    [Fact]
    public async Task Unsupported_files_are_named_as_the_reason_nothing_was_found()
    {
        using var f = new ShellFixture();
        await f.WriteAsync("other.md", "# Lunch\n\nSoup was served.\n");
        await File.WriteAllBytesAsync(Path.Combine(f.Docs, "invoice-march.pdf"), [1, 2, 3]);
        using var workspace = await WorkspaceSession.OpenAsync(f.Docs, f.Root.File("index"));
        var result = await new KnowledgeRetrieval().SearchAsync(workspace, "What does the invoice say?", new());
        Assert.Empty(result.Sources);
        Assert.Equal(KnowledgeEmptyReason.UnsupportedFiles, result.Coverage.EmptyReason);
        Assert.Contains("invoice-march.pdf", result.Coverage.ExplainEmpty().Why);
    }

    [Fact]
    public async Task Ignored_files_are_reported_as_left_out()
    {
        using var f = new ShellFixture();
        await f.WriteAsync("other.md", "# Lunch\n\nSoup was served.\n");
        await f.WriteAsync("salary.md", "# Salary\n\nSalary bands.\n");
        await File.WriteAllTextAsync(Path.Combine(f.Docs, ".ternignore"), "salary.md\n");
        using var workspace = await WorkspaceSession.OpenAsync(f.Docs, f.Root.File("index"));
        var result = await new KnowledgeRetrieval().SearchAsync(workspace, "What are the salary bands?", new());
        Assert.Empty(result.Sources);
        Assert.Equal(KnowledgeEmptyReason.ExcludedFiles, result.Coverage.EmptyReason);
    }

    [Theory]
    [InlineData(false, true, 0, KnowledgeEmptyReason.IndexIncomplete)]
    [InlineData(true, true, 0, KnowledgeEmptyReason.UnreadableNotes)]
    [InlineData(true, false, 1, KnowledgeEmptyReason.UnreadableNotes)]
    [InlineData(true, false, 0, KnowledgeEmptyReason.NoMatch)]
    public void Each_empty_result_has_its_own_reason_and_next_step(bool indexed, bool unreadable, int online, KnowledgeEmptyReason expected)
    {
        var coverage = new KnowledgeCoverage { FilesInFolder = 5, IndexComplete = indexed, UnreadableNotes = unreadable ? ["a.md"] : [], OnlineOnlyNotes = online };
        Assert.Equal(expected, coverage.EmptyReason);
        var (title, why, next) = coverage.ExplainEmpty();
        Assert.All(new[] { title, why, next }, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        Assert.Equal(!indexed || unreadable || online > 0, coverage.IsPartial);
        Assert.Equal(coverage.IsPartial, coverage.Summary(2).Warning);
    }

    [Fact]
    public void A_full_search_never_promises_that_every_note_was_found()
    {
        var text = new KnowledgeCoverage { FilesInFolder = 33 }.Summary(4).Text;
        Assert.Contains("33 notes", text);
        Assert.Contains("may be missed", text);
    }

    [Fact]
    public void Ticked_notes_are_never_shortened_to_fit_and_none_are_dropped()
    {
        using var f = new ShellFixture();
        var conversation = new KnowledgeConversation();
        conversation.Start(f.Docs);
        var evidence = string.Join('\n', Enumerable.Repeat("A line of useful evidence for the answer.", 60));
        var sources = Enumerable.Range(1, 4).Select(id => new KnowledgeSource(id, f.Docs, Path.Combine(f.Docs, $"n{id}.md"), $"n{id}.md", "T", "H", 1, evidence, "d", [])).ToArray();
        var request = conversation.PrepareReading("Question?", "Answer question", sources, new(SendCharacters: 8_000));
        Assert.Equal(4, request.Sources.Count);
        Assert.All(request.Sources, source => Assert.Equal(evidence, source.Excerpt));
        // Too large for one request: the reader sends the full notes in parts instead of a shortened message.
        Assert.Empty(request.Message);
    }

    [Fact]
    public void Prompt_separates_written_dates_from_file_saved_dates()
    {
        using var f = new ShellFixture();
        var conversation = new KnowledgeConversation();
        conversation.Start(f.Docs);
        var source = new KnowledgeSource(1, f.Docs, Path.Combine(f.Docs, "n.md"), "n.md", "T", "1.0", 1, "text", "d", [])
        { DocumentedDate = "March 3 2026", FileSavedUtc = new DateTime(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc) };
        var message = conversation.Prepare("What changed?", "Answer question", [source], new()).Message;
        Assert.Contains("\"documentedDate\":\"March 3 2026\"", message);
        Assert.Contains("\"fileLastSaved\":\"2026-11-23\"", message);
        Assert.Contains("never present it as a release date", message);
    }

    [Fact]
    public void A_long_note_cannot_crowd_the_other_selected_notes_out_of_a_report()
    {
        var longNote = new KnowledgeSource(1, @"C:\w", @"C:\w\long.md", "long.md", "L", null, 1, "x", "d", []);
        var shortNote = new KnowledgeSource(2, @"C:\w", @"C:\w\short.md", "short.md", "S", null, 1, "x", "d", []);
        var parts = KnowledgeReportBuilder.NoteParts([(longNote, string.Join('\n', Enumerable.Repeat("Long note detail line for the report.", 3000))), (shortNote, "The short note holds the launch date.")], 8_000);
        Assert.Contains("The short note holds the launch date.", string.Concat(parts));
        Assert.All(parts, part => Assert.True(part.Length <= 8_000));
    }

    [Fact]
    public void A_report_keeps_the_asked_question_and_fills_empty_fields_from_the_checked_answer()
    {
        var source = new KnowledgeSource(1, @"C:\w", @"C:\w\CHANGELOG.md", "CHANGELOG.md", "Changelog", "2.1.0", 3, "x", "d", []);
        var answer = new KnowledgeAnswer("- Search ranks notes by meaning [1]\n- Reports fill in their title [1]", "", [new(source, "Used")], false);
        var report = KnowledgeReportBuilder.Read("""{"title":"","hasDecision":false,"headline":"Something else","recommendation":""}""",
            "What are the latest changes of Tern?", [(source, "Search ranks notes by meaning. Reports fill in their title.")], "", DateTimeOffset.UnixEpoch, answer).Report;
        Assert.Equal("What are the latest changes of Tern?", report.Headline);
        Assert.Equal("Report: What are the latest changes of Tern", report.Title);
        Assert.StartsWith("Search ranks notes by meaning", report.Recommendation);
        Assert.Contains(report.Reasons, reason => reason.StartsWith("Reports fill in their title", StringComparison.Ordinal));
        // A checked answer can fill summary wording, but its citation-list order
        // cannot establish which exact passage supports each report statement.
        Assert.Empty(report.Evidence);
    }
}
