using System.Net;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed partial class AIWritingWorkflowTests
{
    private const string OneSourceAnswer = """{"answer":"- We launch on 14 April [1].","sources":[{"id":1,"reason":"Launch date"}]}""";

    private static async Task<(DesktopFixture Fixture, KnowledgeAssistantPanel Panel, List<string> Sent, string Extra)> OpenSelection(string answer = OneSourceAnswer)
    {
        var sent = new List<string>();
        var f = Fixture(async (request, token) =>
        {
            var body = await request.Content!.ReadAsStringAsync(token);
            lock (sent) sent.Add(body);
            var text = SentText(body);
            // The AI cites the first note it was actually given, whichever number it has.
            var id = System.Text.RegularExpressions.Regex.Match(body, @"evidence.{0,24}id.{0,8}:(\d+)").Groups[1].Value;
            if (body.Contains("Check EVERY content field", StringComparison.Ordinal))
                return Reply(body.Contains("Sales doubled", StringComparison.Ordinal)
                    ? """{"unsupported":["Sales doubled"]}""" : """{"unsupported":[]}""");
            if (HasPurpose(text, Tern.App.AIPurpose.Report))
            {
                // Search order can put QA first. The exact launch quotation must retain
                // the source ID of launch-plan, rather than assuming it was numbered 1.
                var launch = System.Text.RegularExpressions.Regex.Match(text, @"\[Note (\d+): launch-plan\]\r?\n");
                Assert.True(launch.Success, "The report fixture must receive the actual launch-plan note.");
                var report = System.Text.Json.Nodes.JsonNode.Parse("""{"title":"Launch report","hasDecision":false,"headline":"Documented launch date","recommendation":"Review the documented launch date.","reasons":["The launch plan records 14 April."],"findings":[{"title":"Launch date","kind":"Fact","detail":"The project launch is on 14 April.","evidenceIds":[1]}],"evidence":[{"id":1,"sourceId":1,"fact":"The project launch is on 14 April.","quote":"We decided the project launch is on 14 April."}]}""")!;
                report["evidence"]![0]!["sourceId"] = int.Parse(launch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                return Reply(report.ToJsonString());
            }
            return Reply(id.Length == 0 ? answer // Search help carries no numbered notes.
                : answer.Replace("[1]", $"[{id}]", StringComparison.Ordinal).Replace("\"id\":1", $"\"id\":{id}", StringComparison.Ordinal));
        }, seeSearchHelp: true);
        var path = await f.WriteAsync("notes/launch-plan.md", "# Launch plan\n\nWe decided the project launch is on 14 April.");
        await f.WriteAsync("notes/qa.md", "# QA launch decision\n\nWe decided the project launch needs QA sign-off first.");
        var extra = await f.WriteAsync("notes/lunch.md", "# Lunch\n\nThe soup menu is unrelated.");
        Assert.True(await f.Shell.PromoteFolderAsync(Path.GetDirectoryName(path)!));
        f.Shell.Settings.KnowledgeAllowRemote = true;
        f.Window.ShowKnowledgeAssistant();
        var panel = Assert.Single(KnowledgePanels(f.Window));
        Pump(f.Window);
        Find<TextBox>(panel, "Your question").Text = "What did we decide about the project launch?";
        Click(Find<Button>(panel, "Find my notes"));
        await Until(() => Find<Button>(panel, "Get my answer").IsEffectivelyVisible && !Find<Button>(panel, "Cancel").IsEnabled);
        return (f, panel, sent, extra);
    }

    [AvaloniaFact]
    public async Task An_unticked_note_is_never_sent_and_stays_unticked_after_searching_again()
    {
        var (f, panel, sent, _) = await OpenSelection();
        using (f)
        {
            var qa = Find<CheckBox>(panel, "Include note: qa.md");
            Assert.True(qa.IsChecked);
            qa.IsChecked = false;
            Click(Find<Button>(panel, "Back"));
            Click(Find<Button>(panel, "Find my notes"));
            await Until(() => Find<Button>(panel, "Get my answer").IsEffectivelyVisible && !Find<Button>(panel, "Cancel").IsEnabled);
            Assert.False(Find<CheckBox>(panel, "Include note: qa.md").IsChecked);
            await GetAnswer(panel);
            Assert.DoesNotContain(sent, body => body.Contains("QA sign-off", StringComparison.Ordinal));
            Assert.Contains(sent, body => body.Contains("14 April", StringComparison.Ordinal));
            await PrepareReport(panel, "Save as PDF");
            Assert.DoesNotContain(sent, body => body.Contains("QA sign-off", StringComparison.Ordinal));
        }
    }

    [AvaloniaFact]
    public async Task A_note_the_person_adds_is_ticked_visible_and_sent()
    {
        var (f, panel, sent, extra) = await OpenSelection();
        using (f)
        {
            panel.PickNote = _ => Task.FromResult<string?>(extra);
            Click(Find<Button>(panel, "Add a note"));
            await Until(() => panel.GetLogicalDescendants().OfType<CheckBox>().Any(box => Avalonia.Automation.AutomationProperties.GetName(box) == "Include note: lunch.md") && !Find<Button>(panel, "Cancel").IsEnabled);
            var added = Find<CheckBox>(panel, "Include note: lunch.md");
            Assert.True(added.IsChecked);
            Assert.True(added.IsEffectivelyVisible);
            await GetAnswer(panel);
            Assert.Contains(sent, body => body.Contains("soup menu", StringComparison.Ordinal));
        }
    }

    [AvaloniaFact]
    public async Task The_screen_says_what_was_searched_without_promising_everything_was_found()
    {
        var (f, panel, _, _) = await OpenSelection();
        using (f)
        {
            var coverage = Find<TextBlock>(panel, "What Tern searched").Text!;
            Assert.Contains("3 notes", coverage);
            Assert.Contains("may be missed", coverage);
        }
    }

    [AvaloniaFact]
    public async Task Report_fields_are_filled_from_the_checked_report_and_remain_editable()
    {
        var (f, panel, _, _) = await OpenSelection();
        using (f)
        {
            await GetAnswer(panel);
            await PrepareReport(panel, "Save as PDF");
            Assert.Equal("Launch report", Find<TextBox>(panel, "Report title").Text);
            Assert.False(string.IsNullOrWhiteSpace(Find<TextBox>(panel, "Report headline").Text));
            Assert.Equal("Review the documented launch date.", Find<TextBox>(panel, "Report recommendation").Text);
            var findings = Find<TextBox>(panel, "Report key findings");
            Assert.Contains("14 April", findings.Text);
            Assert.False(findings.IsReadOnly);
            findings.Text = "My own finding";
            Assert.Equal("My own finding", findings.Text);
        }
    }

    [AvaloniaFact]
    public async Task Ai_chooses_the_notes_says_why_and_the_person_can_still_overrule_it()
    {
        var sent = new List<string>();
        using var f = Fixture(async (request, token) =>
        {
            var body = await request.Content!.ReadAsStringAsync(token);
            lock (sent) sent.Add(body);
            var text = SentText(body);
            if (HasPurpose(text, Tern.App.AIPurpose.SearchMeaning))
                return Reply("""{"terms":["launch"],"questions":[]}""");
            if (HasPurpose(text, Tern.App.AIPurpose.ReportCheck)) return Reply("""{"unsupported":[]}""");
            if (HasPurpose(text, Tern.App.AIPurpose.Report))
            {
                var party = System.Text.RegularExpressions.Regex.Match(text, @"\[Note (\d+): launch-party\]\r?\n");
                Assert.True(party.Success, "The person selected the launch-party note, not the roadmap.");
                return Reply(System.Text.Json.JsonSerializer.Serialize(new
                {
                    title = "Launch party", hasDecision = false, headline = "What the party note records", recommendation = "Review the documented launch-party plan.",
                    findings = new[] { new { title = "Launch party", kind = "Fact", detail = "The launch-party note records cake for the launch.", evidenceIds = new[] { 1 } } },
                    evidence = new[] { new { id = 1, sourceId = int.Parse(party.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), fact = "Cake is planned for the launch.", quote = "Cake for the launch." } },
                }));
            }
            Assert.Contains("Choose which notes", body);
            var id = System.Text.RegularExpressions.Regex.Match(body, @"id.{0,8}:(\d+).{0,20}file.{0,10}:.{0,8}roadmap").Groups[1].Value;
            return Reply("{\"notes\":[{\"id\":" + id + ",\"why\":\"It holds the plan\"}],\"alsoRead\":[]}");
        }, seeSearchHelp: true);
        var path = await f.WriteAsync("notes/roadmap.md", "# Roadmap\n\nThe launch moves to June.");
        await f.WriteAsync("notes/launch-party.md", "# Launch party\n\nCake for the launch.");
        Assert.True(await f.Shell.PromoteFolderAsync(Path.GetDirectoryName(path)!));
        f.Shell.Settings.KnowledgeAllowRemote = true;
        f.Window.ShowKnowledgeAssistant();
        var panel = Assert.Single(KnowledgePanels(f.Window));
        Pump(f.Window);
        Find<TextBox>(panel, "Your question").Text = "When is the launch?";
        Click(Find<Button>(panel, "Find my notes"));
        await Until(() => Find<Button>(panel, "Get my answer").IsEffectivelyVisible && !Find<Button>(panel, "Cancel").IsEnabled);
        Assert.True(Find<CheckBox>(panel, "Include note: roadmap.md").IsChecked);
        Assert.False(Find<CheckBox>(panel, "Include note: launch-party.md").IsChecked);
        Assert.Contains(panel.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == "Why AI chose it: It holds the plan");
        Assert.DoesNotContain(sent, body => body.Contains("moves to June", StringComparison.Ordinal));
        Find<CheckBox>(panel, "Include note: launch-party.md").IsChecked = true;
        Find<CheckBox>(panel, "Include note: roadmap.md").IsChecked = false;
        await GetAnswer(panel);
        Assert.Contains(sent, body => body.Contains("Cake for the launch", StringComparison.Ordinal));
        Assert.DoesNotContain(sent, body => body.Contains("moves to June", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Ai_warns_about_an_unsupported_edit_before_the_report_is_saved()
    {
        var (f, panel, sent, _) = await OpenSelection();
        using (f)
        {
            var pdfPath = Path.Combine(f.AppData, "checked.pdf");
            panel.PickPdfPath = _ => Task.FromResult<string?>(pdfPath);
            await GetAnswer(panel);
            await PrepareReport(panel, "Save as PDF");
            Find<TextBox>(panel, "Report key findings").Text = "Sales doubled";
            Find<CheckBox>(panel, "Report reviewed").IsChecked = true;
            Assert.Equal("Check report", Find<Button>(panel, "Finish report").Content);
            Click(Find<Button>(panel, "Finish report"));
            await Until(() => panel.CurrentReportIssues.Contains("Sales doubled") && !Find<Button>(panel, "Cancel").IsEnabled);
            Assert.False(File.Exists(pdfPath));
            var checksBefore = sent.Count(body => body.Contains("Check EVERY content field", StringComparison.Ordinal));
            Click(Find<Button>(panel, "Finish report"));
            await Until(() => sent.Count(body => body.Contains("Check EVERY content field", StringComparison.Ordinal)) > checksBefore
                && !Find<Button>(panel, "Cancel").IsEnabled);
            Assert.False(File.Exists(pdfPath)); // Repeating the same unsupported report never bypasses checking.
            Assert.Contains("Sales doubled", panel.CurrentReportIssues);
            Find<TextBox>(panel, "Report key findings").Text = "We decided the project launch is on 14 April.";
            Find<CheckBox>(panel, "Report reviewed").IsChecked = true;
            Click(Find<Button>(panel, "Finish report"));
            await Until(() => Find<Button>(panel, "Finish report").Content?.ToString() == "Save PDF"
                && !Find<Button>(panel, "Cancel").IsEnabled);
            Assert.False(File.Exists(pdfPath)); // Checking an edit prepares it; output still needs a separate action.
            Find<CheckBox>(panel, "Report reviewed").IsChecked = true;
            Click(Find<Button>(panel, "Finish report"));
            await Until(() => File.Exists(pdfPath) && !Find<Button>(panel, "Cancel").IsEnabled);
        }
    }

    [AvaloniaFact]
    public async Task A_failed_ai_helper_is_not_shown_as_nothing_in_the_notes()
    {
        using var f = Fixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") }), seeSearchHelp: true);
        var path = await f.WriteAsync("notes/lunch.md", "# Lunch\n\nThe soup menu.");
        Assert.True(await f.Shell.PromoteFolderAsync(Path.GetDirectoryName(path)!));
        f.Shell.Settings.KnowledgeAllowRemote = true;
        f.Window.ShowKnowledgeAssistant();
        var panel = Assert.Single(KnowledgePanels(f.Window));
        Pump(f.Window);
        Find<TextBox>(panel, "Your question").Text = "where is the invoice total";
        Click(Find<Button>(panel, "Find my notes"));
        await Until(() => Find<TextBlock>(panel, "Why we could not find it").IsEffectivelyVisible && !Find<Button>(panel, "Cancel").IsEnabled);
        Assert.Contains("AI could not help", Find<TextBlock>(panel, "Why we could not find it").Text);
    }
}
