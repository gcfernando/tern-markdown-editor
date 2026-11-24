using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Tern.App.Knowledge;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed partial class AIWritingWorkflowTests
{
    private const string TwoSourceAnswer = """{"answer":"- We launch on 14 April [1].\n- QA signs off first [2].","sources":[{"id":1,"reason":"Launch date"},{"id":2,"reason":"QA"}]}""";
    private const string ReportJson = """{"title":"Launch report","hasDecision":true,"headline":"Keep the launch date?","decisionBy":"Steering Committee","status":"At risk","recommendation":"Keep 14 April","recommendationWhy":"QA signs off first.","reasons":["QA signs off first"],"findings":[{"title":"Documented launch date","kind":"Fact","detail":"The launch plan records the project launch on 14 April.","keywords":["launch date"],"evidenceIds":[1]}],"options":[{"name":"Option A: Keep 14 April","benefit":"On time","risk":"QA","cost":"","timing":"14 April","customers":"","recommended":true}],"missing":[{"item":"Cost.","whyItMatters":"Budget","whoCanConfirm":"Finance"}],"evidence":[{"id":1,"fact":"Launch is 14 April","sourceId":1,"quote":"We decided the project launch is on 14 April."}]}""";

    private static async Task<HttpResponseMessage> AnswerOrReport(HttpRequestMessage request, CancellationToken token, string answer)
    {
        var text = SentText(await request.Content!.ReadAsStringAsync(token));
        if (HasPurpose(text, Tern.App.AIPurpose.ReportCheck)) return Reply("""{"unsupported":[]}""");
        if (HasPurpose(text, Tern.App.AIPurpose.Email)) return Reply("""{"subject":"Launch update","body":"Keep the launch on 14 April, subject to QA sign-off."}""");
        if (!HasPurpose(text, Tern.App.AIPurpose.Report)) return Reply(answer);
        if (text.Contains("Launch facts.", StringComparison.Ordinal) || text.Contains("Launch facts and risks.", StringComparison.Ordinal))
        {
            var quote = text.Contains("Launch facts and risks.", StringComparison.Ordinal) ? "Launch facts and risks." : "Launch facts.";
            return Reply(System.Text.Json.JsonSerializer.Serialize(new
            {
                title = "Launch report", hasDecision = false, headline = "What do the launch notes say?", recommendation = "Review the documented launch facts.",
                findings = new[] { new { title = "Launch facts", kind = "Fact", detail = "The note records launch facts for review.", evidenceIds = new[] { 1 } } },
                evidence = new[] { new { id = 1, sourceId = 1, fact = "The note records launch facts.", quote } },
            }));
        }
        return Reply(ReportJson);
    }

    private static async Task<(DesktopFixture Fixture, KnowledgeAssistantPanel Panel)> OpenTwoNotes()
    {
        var f = Fixture((request, token) => AnswerOrReport(request, token, TwoSourceAnswer));
        var path = await f.WriteAsync("notes/launch-plan.md", "# Launch plan\n\nWe decided the project launch is on 14 April.");
        await f.WriteAsync("notes/qa.md", "# QA\n\nThe project launch needs QA sign-off first.");
        Assert.True(await f.Shell.PromoteFolderAsync(Path.GetDirectoryName(path)!));
        f.Shell.Settings.KnowledgeAllowRemote = true;
        f.Window.ShowKnowledgeAssistant();
        var panel = Assert.Single(KnowledgePanels(f.Window));
        Pump(f.Window);
        Find<TextBox>(panel, "Your question").Text = "What did we decide about the project launch?";
        Click(Find<Button>(panel, "Find my notes"));
        await Until(() => Find<Button>(panel, "Get my answer").IsEffectivelyVisible && !Find<Button>(panel, "Cancel").IsEnabled);
        return (f, panel);
    }

    private static async Task GetAnswer(KnowledgeAssistantPanel panel)
    {
        Click(Find<Button>(panel, "Get my answer"));
        await Until(() => Find<Button>(panel, "Share this answer").IsEffectivelyVisible && !Find<Button>(panel, "Cancel").IsEnabled);
    }

    private static async Task PrepareReport(KnowledgeAssistantPanel panel, string option)
    {
        Find<RadioButton>(panel, option).IsChecked = true;
        if (!Find<Button>(panel, "Finish report").IsEffectivelyVisible) Click(Find<Button>(panel, "Share this answer"));
        await Until(() => Find<Button>(panel, "Finish report").IsEffectivelyVisible && !Find<Button>(panel, "Cancel").IsEnabled);
    }

    private static async Task ShareToNoteAsync(KnowledgeAssistantPanel panel)
    {
        await PrepareReport(panel, "Add to my note");
        Find<CheckBox>(panel, "Report reviewed").IsChecked = true;
        Click(Find<Button>(panel, "Finish report"));
    }

    [AvaloniaFact]
    public async Task Select_none_explains_the_block_and_select_all_restores_the_answer_button()
    {
        var (f, panel) = await OpenTwoNotes();
        using (f)
        {
            var send = Find<Button>(panel, "Get my answer");
            var all = Find<Button>(panel, "Select all");
            var none = Find<Button>(panel, "Select none");
            Assert.True(send.IsEnabled); Assert.False(all.IsEnabled); Assert.True(none.IsEnabled);
            Click(none);
            Assert.False(send.IsEnabled); Assert.True(all.IsEnabled); Assert.False(none.IsEnabled);
            Assert.Equal("Tick at least one note to get an answer.", Find<TextBlock>(panel, "Help for choosing notes").Text);
            Click(all);
            Assert.True(send.IsEnabled);
            Assert.False(Find<TextBlock>(panel, "Help for choosing notes").IsVisible);
            Assert.Contains("2 of 2 notes ticked", panel.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text));
        }
    }

    [AvaloniaFact]
    public async Task Answer_is_clean_and_each_share_option_runs_alone()
    {
        var (f, panel) = await OpenTwoNotes();
        using (f)
        {
            string? copied = null, printedTitle = null; byte[]? printed = null;
            var pdfPath = Path.Combine(f.AppData, "report.pdf");
            panel.CopyText = text => { copied = text; return Task.CompletedTask; };
            panel.PickPdfPath = _ => Task.FromResult<string?>(pdfPath);
            panel.DefaultPrinterName = () => "Office_Printer";
            panel.PrintPdf = (title, pdf, _) => { printedTitle = title; printed = pdf; return Task.CompletedTask; };
            await GetAnswer(panel);
            var lines = Find<StackPanel>(panel, "Answer").Children.OfType<SelectableTextBlock>().Select(line => line.Text).ToArray();
            Assert.Contains("• Keep 14 April", lines);
            Assert.Contains("• QA signs off first", lines);
            Assert.Contains("• Documented launch date: The launch plan records the project launch on 14 April.", lines);
            var print = Find<RadioButton>(panel, "Print");
            Assert.Contains(print.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == "Print on Office_Printer");
            var run = Find<Button>(panel, "Share this answer");
            Assert.True(run.IsEnabled);
            Assert.Equal("Use this checked report", run.Content);
            Click(run);
            await Until(() => Find<Button>(panel, "Finish report").IsVisible && !Find<Button>(panel, "Cancel").IsEnabled);
            Assert.Null(copied);
            Find<CheckBox>(panel, "Report reviewed").IsChecked = true;
            Click(Find<Button>(panel, "Finish report"));
            await Until(() => copied is not null && !Find<Button>(panel, "Cancel").IsEnabled);
            Assert.StartsWith("Subject: Launch report", copied);
            Assert.DoesNotContain("[1]", copied);
            Assert.DoesNotContain(f.Docs, copied);
            Assert.Contains("QA signs off first", copied);
            Assert.DoesNotContain("attached", copied, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(pdfPath)); Assert.Null(printed);

            await PrepareReport(panel, "Save as PDF");
            var finish = Find<Button>(panel, "Finish report");
            Find<CheckBox>(panel, "Report reviewed").IsChecked = false;
            Assert.False(finish.IsEnabled);
            Assert.Contains("I reviewed this report", Find<TextBlock>(panel, "Report help").Text);
            Find<CheckBox>(panel, "Report reviewed").IsChecked = true;
            Assert.Equal("Save PDF", finish.Content);
            Click(finish);
            await Until(() => File.Exists(pdfPath) && !Find<Button>(panel, "Cancel").IsEnabled);
            Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(pdfPath), 0, 4));
            Assert.Null(printed);

            await PrepareReport(panel, "Print");
            Find<CheckBox>(panel, "Report reviewed").IsChecked = true;
            Assert.Equal("Print now", finish.Content);
            Click(finish);
            await Until(() => printed is not null && !Find<Button>(panel, "Cancel").IsEnabled);
            Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(printed!, 0, 4));
            Assert.Equal("Launch report", printedTitle);
            Assert.Contains("Sent to Office_Printer", Find<TextBlock>(panel, "Share status").Text);
        }
    }

    [AvaloniaFact]
    public async Task Answer_page_asks_windows_for_the_printer_once_per_visit()
    {
        var (f, panel) = await OpenTwoNotes();
        using (f)
        {
            var calls = 0;
            panel.DefaultPrinterName = () => { calls++; return "Office_Printer"; };
            await GetAnswer(panel);
            var onArrival = calls;
            Assert.True(onArrival >= 1);
            foreach (var option in new[] { "Save as PDF", "Add to my note", "Copy for email" })
                Find<RadioButton>(panel, option).IsChecked = true;
            Assert.Equal(onArrival, calls);
        }
    }

    [AvaloniaFact]
    public async Task Print_explains_itself_when_no_printer_is_installed()
    {
        var (f, panel) = await OpenTwoNotes();
        using (f)
        {
            await GetAnswer(panel);
            panel.DefaultPrinterName = () => null;
            panel.RefreshSettings();
            Assert.False(Find<RadioButton>(panel, "Print").IsEnabled);
            Assert.Contains(panel.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == "No printer found. Add one in Windows Settings.");
        }
    }

    [AvaloniaFact]
    public async Task No_answer_shows_a_clear_screen_with_ai_suggestions_and_no_share_options()
    {
        using var f = Fixture(async (request, token) => (await request.Content!.ReadAsStringAsync(token)).Contains("Explain what to do next", StringComparison.Ordinal)
            ? Reply("""{"questions":["What is the launch date?","Who owns the launch?"]}""")
            : Reply("""{"insufficientEvidence":true,"reason":"The selected notes do not state a launch budget."}"""));
        var path = await f.WriteAsync("notes/launch.md", "# Launch\n\nThe launch team meets weekly.");
        Assert.True(await f.Shell.PromoteFolderAsync(Path.GetDirectoryName(path)!));
        f.Shell.Settings.KnowledgeAllowRemote = true;
        f.Window.ShowKnowledgeAssistant();
        var panel = Assert.Single(KnowledgePanels(f.Window));
        Pump(f.Window);
        Find<TextBox>(panel, "Your question").Text = "What did we decide about the launch budget?";
        Click(Find<Button>(panel, "Find my notes"));
        await Until(() => Find<Button>(panel, "Get my answer").IsEffectivelyVisible && !Find<Button>(panel, "Cancel").IsEnabled);
        Click(Find<Button>(panel, "Get my answer"));
        await Until(() => Find<TextBlock>(panel, "Why we could not find it").IsEffectivelyVisible && !Find<Button>(panel, "Cancel").IsEnabled);
        Assert.Equal("We could not find this in your notes", Find<TextBlock>(panel, "Screen title").Text);
        Assert.False(Find<Button>(panel, "Share this answer").IsVisible);
        var suggestion = Find<Button>(panel, "Use this question: What is the launch date?");
        Click(suggestion);
        Assert.Equal("What is the launch date?", Find<TextBox>(panel, "Your question").Text);
        Assert.True(Find<Button>(panel, "Find my notes").IsVisible);
    }

    [AvaloniaFact]
    public async Task Suggestion_keeps_the_user_on_task_until_the_bracket_is_replaced()
    {
        var (f, panel) = await OpenTwoNotes();
        using (f)
        {
            Click(Find<Button>(panel, "Back"));
            Click(Find<Button>(panel, "Suggestion: Main risks"));
            Assert.Equal("What are the main risks for [project name]?", Find<TextBox>(panel, "Your question").Text);
            Assert.False(Find<Button>(panel, "Find my notes").IsEnabled);
            Assert.Equal("Replace the words in [brackets] with your own.", Find<TextBlock>(panel, "Help for your question").Text);
            Assert.Equal(12, panel.GetLogicalDescendants().OfType<Button>().Count(button => AutomationProperties.GetName(button)?.StartsWith("Suggestion: ", StringComparison.Ordinal) == true));
            Find<TextBox>(panel, "Your question").Text = "What are the main risks for Atlas?";
            Assert.True(Find<Button>(panel, "Find my notes").IsEnabled);
        }
    }

    [Fact]
    public void Clean_sentence_removes_markup_and_prefers_the_question_topic()
    {
        var text = KnowledgeAssistantPanel.BestSentence("## Heading\n![badge](https://x/y.svg) <a id=\"x\"></a>\nUnrelated opening line here.\nThe **launch** risk is a [payment](a.md) defect.", "What is the launch risk?");
        Assert.Equal("The launch risk is a payment defect.", text);
    }

    [Fact]
    public void Document_export_names_sources_without_paths()
    {
        var source = new KnowledgeSource(1, @"C:\w", @"C:\w\team\plan.md", "team/plan.md", "Plan", "Decision", 3, "x", "d", []);
        var markdown = KnowledgeAnswerExport.ForDocument("When?", new KnowledgeAnswer("On 14 April [1].", "", [new KnowledgeCitation(source, "Date")], false));
        Assert.StartsWith("# When?", markdown);
        Assert.Contains("On 14 April.", markdown);
        Assert.Contains("- plan.md (Decision)", markdown);
        Assert.DoesNotContain("team/", markdown);
    }
}
