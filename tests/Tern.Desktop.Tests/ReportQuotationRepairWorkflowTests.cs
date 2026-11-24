using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Tern.App;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed partial class AIWritingWorkflowTests
{
    [AvaloniaTheory]
    [InlineData(0)] // The single correction selects the exact original source line.
    [InlineData(1)] // An invalid source range must remain a failure.
    [InlineData(2)] // Sources change while the correction is in flight.
    [InlineData(3)] // A literal correction must not approve an unsupported business claim.
    public async Task A_literal_quote_mismatch_gets_one_current_task_correction_and_never_accepts_stale_sources(int outcome)
    {
        const string sourceQuote = "The budget is €90,000.";
        const string alteredQuote = "The budget is EUR 90,000.";
        const string unsupportedClaim = "The project has already saved €90,000.";
        const string unsupportedIssue = "The notes record a budget, not achieved savings.";
        string Report(string quote) => JsonSerializer.Serialize(new
        {
            title = "Launch budget report", hasDecision = false, headline = "What is the documented launch budget?",
            recommendation = outcome == 3 ? unsupportedClaim : "The documented budget is €90,000.",
            findings = new[] { new { title = "Recorded budget", kind = "Fact", detail = "The plan records a budget of €90,000.", evidenceIds = new[] { 1 } } },
            evidence = new[] { new { id = 1, sourceId = 1, fact = "The documented budget is €90,000.", quote } },
        });
        var reportCalls = 0;
        var correctionCalls = 0;
        var reportChecks = 0;
        var revisions = 0;
        var correcting = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var correction = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var f = Fixture(async (request, token) =>
        {
            var body = await request.Content!.ReadAsStringAsync(token);
            var text = SentText(body);
            if (HasPurpose(text, AIPurpose.ReportRepair))
            {
                correctionCalls++;
                using var wire = JsonDocument.Parse(body);
                Assert.DoesNotContain(wire.RootElement.GetProperty("messages").EnumerateArray(), message => message.GetProperty("role").GetString() == "assistant");
                correcting.TrySetResult(text);
                return await correction.Task;
            }
            if (HasPurpose(text, AIPurpose.Report)) { reportCalls++; return Reply(Report(alteredQuote)); }
            if (HasPurpose(text, AIPurpose.ReportRevision))
            {
                revisions++;
                Assert.Equal(3, outcome);
                return Reply(Report(sourceQuote)); // Literal correction cannot make the unchanged savings claim supported.
            }
            if (HasPurpose(text, AIPurpose.ReportCheck))
            {
                reportChecks++;
                if (outcome == 3)
                {
                    Assert.Contains(unsupportedClaim, text);
                    return Reply(JsonSerializer.Serialize(new { unsupported = new[] { unsupportedIssue } }));
                }
                return Reply("""{"unsupported":[]}""");
            }
            Assert.False(HasPurpose(text, AIPurpose.Answer));
            return Reply("""{"terms":[],"questions":[],"notes":[]}""");
        }, seeSearchHelp: true);
        var path = await f.WriteAsync("quote-repair/launch-plan.md", "# Launch plan\n\n" + sourceQuote);
        Assert.True(await f.Shell.PromoteFolderAsync(Path.GetDirectoryName(path)!));
        f.Shell.Settings.KnowledgeAllowRemote = true;
        f.Window.ShowKnowledgeAssistant();
        var panel = Assert.Single(KnowledgePanels(f.Window));
        Find<TextBox>(panel, "Your question").Text = "What is the project launch budget?";
        Click(Find<Button>(panel, "Find my notes"));
        await Until(() => Find<Button>(panel, "Get my answer").IsVisible && !Find<Button>(panel, "Cancel").IsEnabled);
        var pickerCalls = 0;
        var output = Path.Combine(f.AppData, "unsupported-report-must-not-save.pdf");
        panel.PickPdfPath = _ => { pickerCalls++; return Task.FromResult<string?>(output); };
        Click(Find<Button>(panel, "Get my answer"));
        var repairText = await correcting.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Contains("correcting the source quotations", Find<TextBlock>(panel, "Explore status").Text);
        Assert.Contains("EUR 90,000", repairText);
        // The exact original passage is supplied as current data, including its currency symbol.
        Assert.True(repairText.Contains(sourceQuote, StringComparison.Ordinal)
            || repairText.Contains("The budget is \\u20AC90,000.", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("EARLIER CONVERSATION", repairText, StringComparison.OrdinalIgnoreCase);
        if (outcome == 2) f.Shell.Workspace!.SetUnsavedText(path, "# Launch plan\n\nThe budget changed after the request.");
        correction.SetResult(Reply(JsonSerializer.Serialize(new
        {
            replacements = new[] { new { evidenceId = 1, sourceId = 1, startLine = 3, endLine = outcome == 1 ? 99 : 3 } },
        })));
        await Until(() => !Find<Button>(panel, "Cancel").IsEnabled);

        Assert.Equal(1, reportCalls);
        Assert.Equal(1, correctionCalls);
        if (outcome is 0 or 3)
        {
            if (outcome == 0)
            {
                Assert.True(Find<Button>(panel, "Share this answer").IsVisible);
                await PrepareReport(panel, "Save as PDF");
            }
            Assert.Equal(outcome == 0, Find<Button>(panel, "Finish report").IsVisible);
            Assert.Equal("Launch budget report", Find<TextBox>(panel, "Report title").Text);
            Assert.Equal(outcome == 3 ? unsupportedClaim : "The documented budget is €90,000.", Find<TextBox>(panel, "Report recommendation").Text);
            Assert.Contains(sourceQuote, panel.CurrentReportMarkdown);
            Assert.DoesNotContain(alteredQuote, panel.CurrentReportMarkdown);
            if (outcome == 3)
            {
                Assert.Contains(unsupportedIssue, panel.CurrentReportIssues);
                Assert.False(Find<Button>(panel, "Share this answer").IsVisible);
                Assert.Equal(1, revisions);
                Assert.Equal(0, pickerCalls);
                Assert.False(File.Exists(output));
                Assert.True(Find<Button>(panel, "Find my notes").IsVisible); // Failed initial correction returns to the current question.
                Assert.False(panel.CheckedContentReady);
                Assert.Equal(unsupportedClaim, Find<TextBox>(panel, "Report recommendation").Text);
                Assert.Equal(1, reportCalls);
                Assert.Equal(1, correctionCalls);
            }
        }
        else
        {
            Assert.False(Find<Button>(panel, "Finish report").IsVisible);
            Assert.False(Find<Button>(panel, "Share this answer").IsVisible);
            Assert.False(panel.CheckedContentReady);
            if (outcome == 2) Assert.True(Find<Button>(panel, "Refresh notes").IsVisible);
        }
        Assert.Equal(outcome is 0 or 3 ? 1 : 0, reportChecks);
        Assert.Equal(outcome == 3 ? 1 : 0, revisions);
        Assert.Equal(0, pickerCalls);
        Assert.False(File.Exists(output));
    }
}
