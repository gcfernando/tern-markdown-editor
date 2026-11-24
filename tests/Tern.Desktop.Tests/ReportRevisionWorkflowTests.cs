using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Tern.App;
using Tern.Desktop.Services;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed partial class AIWritingWorkflowTests
{
    [AvaloniaFact]
    public async Task A_failed_check_on_another_connection_invalidates_an_earlier_pass_before_switching_back()
    {
        var checkedModels = new List<string>();
        var pickerCalls = 0;
        const string issue = "An approval condition is missing from the recommendation.";
        using var f = Fixture(async (request, token) =>
        {
            var body = await request.Content!.ReadAsStringAsync(token);
            var text = SentText(body);
            if (HasPurpose(text, AIPurpose.ReportCheck))
            {
                using var wire = JsonDocument.Parse(body);
                checkedModels.Add(wire.RootElement.GetProperty("model").GetString()!);
                return Reply(JsonSerializer.Serialize(new { unsupported = checkedModels.Count == 1 ? Array.Empty<string>() : new[] { issue } }));
            }
            if (HasPurpose(text, AIPurpose.Report)) return Reply(ReportJson);
            if (HasPurpose(text, AIPurpose.Answer)) return Reply(OneSourceAnswer);
            return Reply("""{"terms":[],"questions":[],"notes":[]}""");
        }, seeSearchHelp: true);
        var path = await f.WriteAsync("report-cache/launch-plan.md", "# Launch plan\n\nWe decided the project launch is on 14 April.\nQA signs off first.");
        Assert.True(await f.Shell.PromoteFolderAsync(Path.GetDirectoryName(path)!));
        f.Shell.Settings.KnowledgeAllowRemote = true;
        f.Window.AIWorkspace.Select(FirstId);
        f.Window.ShowKnowledgeAssistant();
        var panel = Assert.Single(KnowledgePanels(f.Window));
        Find<TextBox>(panel, "Your question").Text = "What is the project launch decision?";
        Click(Find<Button>(panel, "Find my notes"));
        await Until(() => Find<Button>(panel, "Get my answer").IsVisible && !Find<Button>(panel, "Cancel").IsEnabled);
        await GetAnswer(panel);
        await PrepareReport(panel, "Save as PDF");
        panel.PickPdfPath = _ => { pickerCalls++; return Task.FromResult<string?>(null); };
        Find<CheckBox>(panel, "Report reviewed").IsChecked = true;
        Click(Find<Button>(panel, "Finish report"));
        await Until(() => checkedModels.Count == 1 && !Find<Button>(panel, "Cancel").IsEnabled);
        Assert.Equal(1, pickerCalls); // The first passed check reaches the synthetic cancelled save picker.

        f.Window.AIWorkspace.Select(SecondId);
        Find<TextBox>(panel, "Report title").Text = "Edited launch report";
        Click(Find<Button>(panel, "Finish report"));
        await Until(() => checkedModels.Count == 2 && !Find<Button>(panel, "Cancel").IsEnabled);
        Assert.Contains(issue, panel.CurrentReportIssues);
        Assert.Equal(1, pickerCalls);

        f.Window.AIWorkspace.Select(FirstId);
        Click(Find<Button>(panel, "Finish report"));
        await Until(() => checkedModels.Count == 3 && !Find<Button>(panel, "Cancel").IsEnabled);
        Assert.Equal(new[] { "model-one", "model-two", "model-one" }, checkedModels);
        Assert.Contains(issue, panel.CurrentReportIssues);
        Assert.Equal(1, pickerCalls);
    }

    [AvaloniaTheory]
    [InlineData(0)] // Checked replacement, persisted issues, and explicit re-review.
    [InlineData(1)] // Malformed correction keeps the user's draft.
    [InlineData(2)] // Remaining semantic issues keep the user's draft.
    [InlineData(3)] // Changed source rejects the in-flight correction.
    [InlineData(4)] // Newer user edits reject the in-flight correction.
    [InlineData(5)] // The complete correction request cannot bypass a lowered limit.
    public async Task Correcting_report_issues_requires_an_explicit_action_and_only_a_current_checked_draft_can_replace_user_edits(int outcome)
    {
        const string fact = "The budget could be reserved after approval.";
        const string unsupported = "The budget is reserved.";
        const string issue = "The notes say the budget could be reserved after approval, not that it is already reserved.";
        const string editedTitle = "My edited budget report";
        var reportCalls = 0; var checks = 0; var revisions = 0; var pickerCalls = 0;
        string Report(string recommendation, string title) => JsonSerializer.Serialize(new
        {
            title, hasDecision = false, headline = "What is the budget position?", recommendation,
            findings = new[] { new { title = "Conditional budget", kind = "Fact", detail = fact, evidenceIds = new[] { 1 } } },
            evidence = new[] { new { id = 1, sourceId = 1, fact, quote = fact } },
        });
        var revisionStarted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var revisionReply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var f = Fixture(async (request, token) =>
        {
            var body = await request.Content!.ReadAsStringAsync(token);
            var text = SentText(body);
            if (HasPurpose(text, AIPurpose.ReportRevision))
            {
                revisions++;
                using var wire = JsonDocument.Parse(body);
                Assert.DoesNotContain(wire.RootElement.GetProperty("messages").EnumerateArray(), item => item.GetProperty("role").GetString() == "assistant");
                revisionStarted.TrySetResult(text);
                return await revisionReply.Task;
            }
            if (HasPurpose(text, AIPurpose.ReportCheck))
            {
                checks++;
                return Reply(JsonSerializer.Serialize(new { unsupported = text.Contains(unsupported, StringComparison.Ordinal) ? new[] { issue } : [] }));
            }
            if (HasPurpose(text, AIPurpose.Report)) { reportCalls++; return Reply(Report(fact, "Budget report")); }
            if (HasPurpose(text, AIPurpose.Answer)) return Reply("""{"answer":"- The budget is conditional on approval [1].","sources":[{"id":1,"reason":"Budget condition"}]}""");
            return Reply("""{"terms":[],"questions":[],"notes":[]}""");
        }, seeSearchHelp: true);
        var path = await f.WriteAsync("report-revision/budget.md", "# Budget\n\n" + fact + (outcome == 5 ? "\n" + new string('x', 20_000) : ""));
        Assert.True(await f.Shell.PromoteFolderAsync(Path.GetDirectoryName(path)!));
        f.Shell.Settings.KnowledgeAllowRemote = true;
        f.Window.ShowKnowledgeAssistant();
        var panel = Assert.Single(KnowledgePanels(f.Window));
        Find<TextBox>(panel, "Your question").Text = "What is the project budget position?";
        Click(Find<Button>(panel, "Find my notes"));
        await Until(() => Find<Button>(panel, "Get my answer").IsVisible && !Find<Button>(panel, "Cancel").IsEnabled);
        await GetAnswer(panel);
        await PrepareReport(panel, "Save as PDF");
        Find<TextBox>(panel, "Report title").Text = editedTitle;
        Find<TextBox>(panel, "Report prepared by").Text = "Report owner";
        Find<TextBox>(panel, "Report recommendation").Text = unsupported;
        var output = Path.Combine(f.AppData, "must-not-export-during-correction.pdf");
        panel.PickPdfPath = _ => { pickerCalls++; return Task.FromResult<string?>(output); };
        Find<CheckBox>(panel, "Report reviewed").IsChecked = true;
        Click(Find<Button>(panel, "Finish report"));
        await Until(() => checks == 2 && !Find<Button>(panel, "Cancel").IsEnabled);
        Assert.Contains(issue, panel.CurrentReportIssues);
        Assert.True(Find<Button>(panel, "Ask AI to correct these issues").IsVisible);
        Assert.Equal(0, revisions);
        Assert.Equal(0, pickerCalls);

        if (outcome == 0)
        {
            Assert.Single(f.Window.OwnedWindows.OfType<KnowledgeAssistantWindow>()).Close();
            var callsBefore = reportCalls + checks + revisions;
            f.Window.ShowKnowledgeAssistant();
            panel = Assert.Single(KnowledgePanels(f.Window));
            Assert.Equal(editedTitle, Find<TextBox>(panel, "Report title").Text);
            Assert.Contains(issue, panel.CurrentReportIssues);
            Assert.True(Find<Button>(panel, "Ask AI to correct these issues").IsEnabled);
            Assert.Equal(callsBefore, reportCalls + checks + revisions);
            panel.PickPdfPath = _ => { pickerCalls++; return Task.FromResult<string?>(output); };
        }
        if (outcome == 5)
        {
            var profiles = new AIProfileStore(f.Shell.Paths.SettingsDirectory);
            profiles.Save(profiles.Load().Select(profile => profile with { SendCharacters = 8_000 }).ToArray());
            f.Window.AIWorkspace.Reload();
        }
        Click(Find<Button>(panel, "Ask AI to correct these issues"));
        if (outcome != 5)
        {
            var request = await revisionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Contains(editedTitle, request); Assert.Contains(issue, request); Assert.Contains(fact, request);
            Assert.DoesNotContain("EARLIER CONVERSATION", request, StringComparison.OrdinalIgnoreCase);
            Assert.False(Find<TextBox>(panel, "Report title").IsEnabled);
            if (outcome == 0)
            {
                // Main-window activation after closing the old Explore window follows this path.
                // An unchanged-settings refresh must keep the new, deliberately started operation alive.
                panel.RefreshSettings();
                Assert.True(Find<Button>(panel, "Cancel").IsEnabled);
            }
            if (outcome == 3) f.Shell.Workspace!.SetUnsavedText(path, "# Budget\n\nThe source changed during correction.");
            if (outcome == 4) Find<TextBox>(panel, "Report title").Text = "My newer title must survive";
            revisionReply.SetResult(Reply(outcome == 1 ? "{not valid JSON" : Report(outcome == 2 ? unsupported : fact, editedTitle)));
        }
        await Until(() => !Find<Button>(panel, "Cancel").IsEnabled);
        Assert.Equal(outcome == 5 ? 0 : 1, revisions);
        Assert.Equal(1, reportCalls);
        Assert.Equal(0, pickerCalls); Assert.False(File.Exists(output));
        Assert.True(Find<Button>(panel, "Finish report").IsVisible);
        Assert.Equal(outcome == 4 ? "My newer title must survive" : editedTitle, Find<TextBox>(panel, "Report title").Text);
        if (outcome == 0)
        {
            Assert.True(checks == 3, $"Expected initial, edited and corrected whole-report checks, got {checks}. Explore status: {Find<TextBlock>(panel, "Explore status").Text} | Report check: {Find<TextBlock>(panel, "AI report check").Text} | Report help: {Find<TextBlock>(panel, "Report help").Text}");
            Assert.Equal(fact, Find<TextBox>(panel, "Report recommendation").Text);
            Assert.False(Find<CheckBox>(panel, "Report reviewed").IsChecked);
            Assert.False(Find<Button>(panel, "Finish report").IsEnabled);
            Assert.False(Find<Button>(panel, "Ask AI to correct these issues").IsVisible);
        }
        else
        {
            Assert.Equal(unsupported, Find<TextBox>(panel, "Report recommendation").Text);
            Assert.Contains(issue, panel.CurrentReportIssues);
            Assert.Equal(outcome == 2 ? 3 : 2, checks);
            if (outcome is 3 or 4) Assert.False(Find<Button>(panel, "Ask AI to correct these issues").IsEnabled);
        }
    }
}
