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
    public async Task Every_requested_finding_is_readable_before_export_and_checked_formats_reuse_local_content_offline()
    {
        var requests = new List<string>();
        var facts = Enumerable.Range(1, 12).Select(index => $"Feature {index:00} supports its documented workflow.").ToArray();
        using var f = Fixture(async (request, token) =>
        {
            var text = SentText(await request.Content!.ReadAsStringAsync(token)); requests.Add(text);
            if (HasPurpose(text, AIPurpose.ReportCheck)) return Reply("""{"unsupported":[]}""");
            if (HasPurpose(text, AIPurpose.Report)) return Reply(JsonSerializer.Serialize(new
            {
                title = "Feature report", hasDecision = false, headline = "Documented features", recommendation = "The notes describe twelve features.",
                findings = facts.Select((fact, index) => new { title = $"Feature {index + 1:00}", kind = "Fact", detail = fact, evidenceIds = new[] { index + 1 } }),
                evidence = facts.Select((fact, index) => new { id = index + 1, sourceId = 1, fact, quote = fact }),
            }));
            return Reply("""{"terms":[],"questions":[],"notes":[]}""");
        });
        var path = await f.WriteAsync("checked/features.md", "# Features\n\n" + string.Join('\n', facts));
        Assert.True(await f.Shell.PromoteFolderAsync(Path.GetDirectoryName(path)!));
        var target = f.Shell.NewDocument("Keep this note.\n");
        f.Shell.Settings.KnowledgeAllowRemote = true;
        f.Window.ShowKnowledgeAssistant(); var panel = Assert.Single(KnowledgePanels(f.Window));
        panel.DefaultPrinterName = () => "Synthetic printer";
        Find<TextBox>(panel, "Your question").Text = "Explain all twelve documented features.";
        Click(Find<Button>(panel, "Find my notes"));
        await Until(() => Find<Button>(panel, "Get my answer").IsVisible && !Find<Button>(panel, "Cancel").IsEnabled);
        await GetAnswer(panel);
        Assert.True(panel.CheckedContentReady);
        var answer = string.Join('\n', Find<StackPanel>(panel, "Answer").Children.OfType<SelectableTextBlock>().Select(line => line.Text));
        Assert.All(facts, fact => Assert.Contains(fact, answer));
        Assert.Single(requests.Where(text => HasPurpose(text, AIPurpose.Report)));
        Assert.Single(requests.Where(text => HasPurpose(text, AIPurpose.ReportCheck)));
        Assert.DoesNotContain(requests, text => HasPurpose(text, AIPurpose.Answer) || HasPurpose(text, AIPurpose.Email));
        var calls = requests.Count; var reads = panel.NoteReadCount; var renders = panel.ContentRenderCount;
        var output = Path.Combine(f.AppData, "kept-feature-report.pdf");
        string? email = null; byte[]? print = null; var pickedReady = false;
        panel.CopyText = value => { email = value; return Task.CompletedTask; };
        panel.PickPdfPath = _ => { pickedReady = panel.CheckedContentReady && panel.ContentRenderCount == renders; return Task.FromResult<string?>(output); };
        panel.PrintPdf = (_, bytes, _) => { print = bytes; return Task.CompletedTask; };
        f.Shell.Settings.OfflineMode = true; panel.RefreshSettings();
        await PrepareReport(panel, "Save as PDF");
        Find<CheckBox>(panel, "Report reviewed").IsChecked = true;
        Click(Find<Button>(panel, "Finish report"));
        await Until(() => File.Exists(output) && !Find<Button>(panel, "Cancel").IsEnabled);
        Assert.True(pickedReady);
        foreach (var option in new[] { "Copy for email", "Print", "Add to my note" })
        {
            Find<RadioButton>(panel, option).IsChecked = true;
            Assert.True(Find<CheckBox>(panel, "Report reviewed").IsChecked);
            Assert.True(Find<Button>(panel, "Finish report").IsEnabled);
            Click(Find<Button>(panel, "Finish report"));
            await Until(() => !Find<Button>(panel, "Cancel").IsEnabled);
        }
        Assert.NotNull(email); Assert.Contains("Subject: Feature report", email);
        Assert.NotNull(print); Assert.Equal(File.ReadAllBytes(output), print);
        Assert.Single(f.Window.OverlayLayer.Children.OfType<AIEditReview>());
        Assert.Equal("Keep this note.\n", target.CurrentText);
        Assert.Equal(calls, requests.Count); Assert.Equal(reads, panel.NoteReadCount); Assert.Equal(renders, panel.ContentRenderCount);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_checkpoint_without_a_matching_successful_check_witness_cannot_become_ready_or_export(bool keepOldWitness)
    {
        var (f, panel) = await OpenTwoNotes();
        using (f)
        {
            await GetAnswer(panel); await PrepareReport(panel, "Save as PDF");
            var root = f.Shell.OpenWorkspaces.Single();
            Assert.Single(f.Window.OwnedWindows.OfType<KnowledgeAssistantWindow>()).Close();
            var store = ExploreTaskStore.For(f.Shell);
            var saved = Assert.IsType<ExploreTaskCheckpoint>(store.Load(root.Root, root.IsPrivate));
            var forgedDraft = saved.Draft! with { Report = saved.Draft!.Report with { Recommendation = "The launch budget is approved and reserved." } };
            store.Save(saved with { Draft = forgedDraft, ReportIssues = [], ReportIssuesKey = null,
                CheckedContentKey = null, CheckWitness = keepOldWitness ? saved.CheckWitness : null }, root.IsPrivate);
            f.Window.ShowKnowledgeAssistant(); panel = Assert.Single(KnowledgePanels(f.Window)); Pump(f.Window);
            await Until(() => !Find<Button>(panel, "Cancel").IsEnabled);
            Assert.False(panel.CheckedContentReady); Assert.Equal(0, panel.ContentRenderCount);
            Assert.Equal("Check report", Find<Button>(panel, "Finish report").Content);
            var picks = 0; panel.PickPdfPath = _ => { picks++; return Task.FromResult<string?>(null); };
            Assert.Equal(0, picks);
            Assert.Contains("approved and reserved", Find<TextBox>(panel, "Report recommendation").Text);
        }
    }

    [AvaloniaFact]
    public async Task Completing_a_task_removes_its_checkpoint_and_cached_content_but_keeps_saved_outputs_and_source_notes()
    {
        var (f, panel) = await OpenTwoNotes();
        using (f)
        {
            await GetAnswer(panel); await PrepareReport(panel, "Save as PDF");
            var source = Path.Combine(f.Docs, "notes", "launch-plan.md"); var original = File.ReadAllText(source);
            var output = Path.Combine(f.AppData, "saved-report-must-remain.pdf");
            panel.PickPdfPath = _ => Task.FromResult<string?>(output);
            Find<CheckBox>(panel, "Report reviewed").IsChecked = true; Click(Find<Button>(panel, "Finish report"));
            await Until(() => File.Exists(output) && !Find<Button>(panel, "Cancel").IsEnabled);
            var bytes = File.ReadAllBytes(output); var root = f.Shell.OpenWorkspaces.Single();
            Pump(f.Window);
            var store = ExploreTaskStore.For(f.Shell); Assert.NotNull(store.Load(root.Root, root.IsPrivate));
            var staleTemporary = store.FileFor(root.Root) + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var unrelated = store.FileFor(root.Root) + ".user-output.tmp";
            File.WriteAllText(staleTemporary, "orphaned task write"); File.WriteAllText(unrelated, "keep unrelated file");
            Click(Find<Button>(panel, "Done with this task")); Pump(f.Window);
            Assert.Null(store.Load(root.Root, root.IsPrivate));
            Assert.False(File.Exists(store.FileFor(root.Root))); Assert.False(panel.CheckedContentReady);
            Assert.False(File.Exists(staleTemporary)); Assert.True(File.Exists(unrelated));
            Assert.Empty(panel.CurrentReportMarkdown); Assert.Empty(Find<TextBox>(panel, "Report recommendation").Text ?? "");
            Assert.Equal(bytes, File.ReadAllBytes(output)); Assert.Equal(original, File.ReadAllText(source));
            Assert.Single(f.Window.OwnedWindows.OfType<KnowledgeAssistantWindow>()).Close();
            f.Window.ShowKnowledgeAssistant();
            Assert.Empty(Find<TextBox>(Assert.Single(KnowledgePanels(f.Window)), "Your question").Text ?? "");
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_same_length_source_edit_with_restored_saved_time_or_atomic_replacement_blocks_cached_output(bool replace)
    {
        var (f, panel) = await OpenTwoNotes();
        using (f)
        {
            await GetAnswer(panel); await PrepareReport(panel, "Save as PDF");
            var source = Path.Combine(f.Docs, "notes", "launch-plan.md");
            var savedTime = File.GetLastWriteTimeUtc(source); var original = File.ReadAllText(source);
            var changed = original.Replace("14 April", "15 April", StringComparison.Ordinal);
            Assert.NotEqual(original, changed); Assert.Equal(original.Length, changed.Length);
            if (replace)
            {
                var temporary = source + ".replacement"; File.WriteAllText(temporary, changed);
                File.SetLastWriteTimeUtc(temporary, savedTime); File.Move(temporary, source, overwrite: true);
            }
            else { File.WriteAllText(source, changed); File.SetLastWriteTimeUtc(source, savedTime); }
            var output = Path.Combine(f.AppData, "stale-cache-must-not-save.pdf"); var picks = 0;
            panel.PickPdfPath = _ => { picks++; return Task.FromResult<string?>(output); };
            Find<CheckBox>(panel, "Report reviewed").IsChecked = true;
            Click(Find<Button>(panel, "Finish report"));
            await Until(() => !Find<Button>(panel, "Cancel").IsEnabled);
            Assert.Equal(1, picks); // Ready Save opens its picker before checking local freshness.
            Assert.False(File.Exists(output)); Assert.False(panel.CheckedContentReady);
            Assert.True(Find<Button>(panel, "Refresh notes").IsVisible);
        }
    }

    [AvaloniaFact]
    public async Task Editing_immediately_after_reopening_invalidates_the_queued_checked_content_restore()
    {
        var (f, panel) = await OpenTwoNotes();
        using (f)
        {
            await GetAnswer(panel); await PrepareReport(panel, "Save as PDF");
            Assert.Single(f.Window.OwnedWindows.OfType<KnowledgeAssistantWindow>()).Close();
            f.Window.ShowKnowledgeAssistant(); panel = Assert.Single(KnowledgePanels(f.Window));
            Find<TextBox>(panel, "Report recommendation").Text = "My new wording needs checking.";
            Pump(f.Window); await Until(() => !Find<Button>(panel, "Cancel").IsEnabled);
            Assert.False(panel.CheckedContentReady);
            Assert.Equal("Check report", Find<Button>(panel, "Finish report").Content);
            Assert.Equal("My new wording needs checking.", Find<TextBox>(panel, "Report recommendation").Text);
        }
    }

    [AvaloniaFact]
    public async Task An_overlay_counter_reused_in_a_new_workspace_session_requires_a_digest_and_rejects_changed_content()
    {
        using var f = Fixture((request, token) => AnswerOrReport(request, token, TwoSourceAnswer));
        const string text = "# Launch plan\n\nWe decided the project launch is on 14 April.";
        var path = await f.WriteAsync("session-overlay/launch-plan.md", text);
        var folder = Path.GetDirectoryName(path)!;
        Assert.True(await f.Shell.PromoteFolderAsync(folder));
        var first = f.Shell.OpenWorkspaces.Single(); first.SetUnsavedText(path, text);
        f.Shell.Settings.KnowledgeAllowRemote = true; f.Window.ShowKnowledgeAssistant();
        var panel = Assert.Single(KnowledgePanels(f.Window));
        Find<TextBox>(panel, "Your question").Text = "When is the project launch?";
        Click(Find<Button>(panel, "Find my notes"));
        await Until(() => Find<Button>(panel, "Get my answer").IsVisible && !Find<Button>(panel, "Cancel").IsEnabled);
        await GetAnswer(panel); await PrepareReport(panel, "Save as PDF");
        var previous = first.CaptureKnowledgeState(path);
        Assert.Single(f.Window.OwnedWindows.OfType<KnowledgeAssistantWindow>()).Close();
        Assert.True(await f.Shell.CloseWorkspaceAsync(first));
        Assert.True(await f.Shell.OpenWorkspaceAsync(folder));
        var second = f.Shell.OpenWorkspaces.Single(); second.SetUnsavedText(path, text.Replace("14 April", "15 April", StringComparison.Ordinal));
        var current = second.CaptureKnowledgeState(path);
        Assert.Equal(previous.OverlayRevision, current.OverlayRevision);
        Assert.NotEqual(previous.SessionId, current.SessionId);
        Assert.Equal(previous.Disk, current.Disk);
        f.Window.ShowKnowledgeAssistant(); panel = Assert.Single(KnowledgePanels(f.Window)); Pump(f.Window);
        await Until(() => !Find<Button>(panel, "Cancel").IsEnabled);
        Assert.True(panel.FullDigestChecks > 0);
        Assert.False(panel.CheckedContentReady);
        Assert.True(Find<Button>(panel, "Refresh notes").IsVisible);
    }
}
