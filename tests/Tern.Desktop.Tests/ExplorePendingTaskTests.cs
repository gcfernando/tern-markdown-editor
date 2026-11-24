using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Tern.App.Knowledge;
using Tern.Desktop.Services;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed partial class AIWritingWorkflowTests
{
    [AvaloniaFact]
    public async Task Explore_reopens_typed_work_without_sending_and_Done_starts_clean()
    {
        var calls = 0;
        using var f = Fixture((_, _) => { calls++; return Task.FromResult(Reply("Never sent")); });
        var path = await f.WriteAsync("pending/note.md", "# Pending\nA useful note.");
        Assert.True(await f.Shell.PromoteFolderAsync(Path.GetDirectoryName(path)!));
        f.Window.ShowKnowledgeAssistant();
        var window = Assert.Single(f.Window.OwnedWindows.OfType<KnowledgeAssistantWindow>());
        Find<TextBox>(window.Assistant, "Your question").Text = "What is the current project decision?";
        window.Close();

        f.Window.ShowKnowledgeAssistant();
        var reopened = Assert.Single(f.Window.OwnedWindows.OfType<KnowledgeAssistantWindow>());
        Assert.Equal("What is the current project decision?", Find<TextBox>(reopened.Assistant, "Your question").Text);
        Assert.True(Find<Button>(reopened.Assistant, "Find my notes").IsVisible);
        Assert.Equal(0, calls);
        Click(Find<Button>(reopened.Assistant, "Done with this task"));
        reopened.Close();
        f.Window.ShowKnowledgeAssistant();
        Assert.Empty(Find<TextBox>(Assert.Single(KnowledgePanels(f.Window)), "Your question").Text ?? "");
        Assert.Equal(0, calls);
    }

    [AvaloniaFact]
    public async Task Report_edits_survive_Back_share_and_window_reopening_with_a_full_preview()
    {
        var (f, panel) = await OpenTwoNotes();
        using (f)
        {
            await GetAnswer(panel);
            await PrepareReport(panel, "Save as PDF");
            Find<TextBox>(panel, "Report recommendation").Text = "Keep the reviewed wording.";
            Find<TextBox>(panel, "Report conditions").Text = "QA must sign off first.";
            Find<TextBox>(panel, "Report next action").Text = "Ask QA to confirm readiness.";
            Find<TextBox>(panel, "Report key findings").Text = string.Join('\n', Enumerable.Range(1, 10).Select(index => "Reviewed finding " + index));
            Assert.Contains("Reviewed finding 10", panel.CurrentReportMarkdown);
            Assert.Contains("Keep the reviewed wording.", panel.CurrentReportMarkdown);
            Find<CheckBox>(panel, "Report reviewed").IsChecked = true;
            Find<TextBox>(panel, "Report final assessment").Text = "Readiness remains conditional.";
            Assert.False(Find<CheckBox>(panel, "Report reviewed").IsChecked);
            Click(Find<Button>(panel, "Back"));
            await PrepareReport(panel, "Save as PDF");
            Assert.Equal("Keep the reviewed wording.", Find<TextBox>(panel, "Report recommendation").Text);
            Assert.Equal("QA must sign off first.", Find<TextBox>(panel, "Report conditions").Text);
            Assert.Contains("Reviewed finding 10", Find<TextBox>(panel, "Report key findings").Text);

            Assert.Single(f.Window.OwnedWindows.OfType<KnowledgeAssistantWindow>()).Close();
            f.Window.ShowKnowledgeAssistant();
            var restored = Assert.Single(KnowledgePanels(f.Window));
            Assert.True(Find<Button>(restored, "Finish report").IsVisible);
            Assert.Equal("Keep the reviewed wording.", Find<TextBox>(restored, "Report recommendation").Text);
            Assert.Equal("Ask QA to confirm readiness.", Find<TextBox>(restored, "Report next action").Text);
            Assert.Contains("Readiness remains conditional.", restored.CurrentReportMarkdown);
            Assert.False(Find<CheckBox>(restored, "Report reviewed").IsChecked);
        }
    }

    [AvaloniaFact]
    public async Task Closing_an_inflight_answer_restores_interrupted_choices_without_accepting_a_late_reply()
    {
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var f = Fixture(async (request, token) =>
        {
            calls++;
            var text = SentText(await request.Content!.ReadAsStringAsync(token));
            if (HasPurpose(text, Tern.App.AIPurpose.Report)) { requested.TrySetResult(); return await pending.Task; }
            return Reply("""{"terms":[],"questions":[],"notes":[]}""");
        });
        var path = await f.WriteAsync("interrupted/launch.md", "# Launch\nThe project launch is in April.");
        Assert.True(await f.Shell.PromoteFolderAsync(Path.GetDirectoryName(path)!));
        f.Shell.Settings.KnowledgeAllowRemote = true;
        f.Window.ShowKnowledgeAssistant();
        var first = Assert.Single(KnowledgePanels(f.Window));
        Find<TextBox>(first, "Your question").Text = "When is the project launch?";
        Click(Find<Button>(first, "Find my notes"));
        await Until(() => Find<Button>(first, "Get my answer").IsVisible && !Find<Button>(first, "Cancel").IsEnabled);
        Click(Find<Button>(first, "Get my answer"));
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Single(f.Window.OwnedWindows.OfType<KnowledgeAssistantWindow>()).Close();
        var before = calls;
        f.Window.ShowKnowledgeAssistant();
        var restored = Assert.Single(KnowledgePanels(f.Window));
        Assert.True(Find<Button>(restored, "Get my answer").IsVisible);
        Assert.True(Find<Button>(restored, "Retry interrupted step").IsVisible);
        Assert.Equal(before, calls);
        pending.SetResult(Reply("""{"title":"Launch report","hasDecision":false,"headline":"When is the launch?","recommendation":"The documented project launch is in April.","findings":[{"title":"Launch date","kind":"Fact","detail":"The project launch is in April.","evidenceIds":[1]}],"evidence":[{"id":1,"sourceId":1,"fact":"The project launch is in April.","quote":"The project launch is in April."}]}"""));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Pump(f.Window);
        Assert.False(Find<Button>(restored, "Share this answer").IsVisible);
        Assert.Equal(before, calls);
    }

    [AvaloniaFact]
    public async Task Workspace_switches_restore_only_their_own_question_without_AI()
    {
        var calls = 0;
        using var f = Fixture((_, _) => { calls++; return Task.FromResult(Reply("Never sent")); });
        var first = Path.GetDirectoryName(await f.WriteAsync("scope-a/note.md", "# A"))!;
        var second = Path.GetDirectoryName(await f.WriteAsync("scope-b/note.md", "# B"))!;
        Assert.True(await f.Shell.PromoteFolderAsync(first));
        Assert.True(await f.Shell.PromoteFolderAsync(second));
        f.Window.ShowKnowledgeAssistant();
        var panel = Assert.Single(KnowledgePanels(f.Window));
        var scope = Find<ComboBox>(panel, "Search in");
        scope.SelectedItem = f.Shell.OpenWorkspaces.Single(root => root.Root == first);
        Find<TextBox>(panel, "Your question").Text = "Question for workspace A";
        scope.SelectedItem = f.Shell.OpenWorkspaces.Single(root => root.Root == second);
        Assert.Empty(Find<TextBox>(panel, "Your question").Text ?? "");
        Find<TextBox>(panel, "Your question").Text = "Question for workspace B";
        scope.SelectedItem = f.Shell.OpenWorkspaces.Single(root => root.Root == first);
        Assert.Equal("Question for workspace A", Find<TextBox>(panel, "Your question").Text);
        scope.SelectedItem = f.Shell.OpenWorkspaces.Single(root => root.Root == second);
        Assert.Equal("Question for workspace B", Find<TextBox>(panel, "Your question").Text);
        Assert.Equal(0, calls);
    }

    [AvaloniaFact]
    public async Task An_edit_during_the_full_report_check_prevents_saving_the_older_report()
    {
        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;
        using var f = Fixture(async (request, token) =>
        {
            var text = SentText(await request.Content!.ReadAsStringAsync(token));
            if (HasPurpose(text, Tern.App.AIPurpose.ReportCheck))
            {
                if (++checks == 1) return Reply("""{"unsupported":[]}""");
                checking.TrySetResult(); return await complete.Task;
            }
            return await AnswerOrReport(request, token, TwoSourceAnswer);
        });
        var path = await f.WriteAsync("editing/launch-plan.md", "# Launch plan\n\nWe decided the project launch is on 14 April.");
        await f.WriteAsync("editing/qa.md", "# QA\n\nThe project launch needs QA sign-off first.");
        Assert.True(await f.Shell.PromoteFolderAsync(Path.GetDirectoryName(path)!));
        f.Shell.Settings.KnowledgeAllowRemote = true;
        f.Window.ShowKnowledgeAssistant();
        var panel = Assert.Single(KnowledgePanels(f.Window));
        Find<TextBox>(panel, "Your question").Text = "What did we decide about the project launch?";
        Click(Find<Button>(panel, "Find my notes"));
        await Until(() => Find<Button>(panel, "Get my answer").IsVisible && !Find<Button>(panel, "Cancel").IsEnabled);
        await GetAnswer(panel);
        await PrepareReport(panel, "Save as PDF");
        var output = Path.Combine(f.AppData, "must-not-save.pdf");
        panel.PickPdfPath = _ => Task.FromResult<string?>(output);
        Find<TextBox>(panel, "Report recommendation").Text = "Review the documented launch date before proceeding.";
        Find<CheckBox>(panel, "Report reviewed").IsChecked = true;
        Click(Find<Button>(panel, "Finish report"));
        await checking.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var recommendation = Find<TextBox>(panel, "Report recommendation");
        Assert.False(recommendation.IsEnabled);
        // Programmatic edits must invalidate the captured report too; disabling the control alone is not a guard.
        recommendation.Text = "My newer reviewed wording must survive.";
        complete.SetResult(Reply("""{"unsupported":[]}"""));
        await Until(() => !Find<Button>(panel, "Cancel").IsEnabled);
        Assert.False(File.Exists(output));
        Assert.Equal("My newer reviewed wording must survive.", recommendation.Text);
        Assert.Contains("My newer reviewed wording must survive.", panel.CurrentReportMarkdown);
        Assert.False(Find<CheckBox>(panel, "Report reviewed").IsChecked);
        Assert.Equal(2, checks);
    }

    [Fact]
    public void Checkpoint_authentication_rejects_wrong_scope_outside_sources_and_invalid_identity_or_stage()
    {
        using var folder = new ReviewFolder();
        var task = new ExploreTaskCheckpoint { WorkspaceRoot = folder.Path, Question = "Keep my current question" };
        Assert.True(task.ValidFor(folder.Path));
        Assert.False((task with { TaskId = Guid.Empty }).ValidFor(folder.Path));
        Assert.False((task with { Stage = 99 }).ValidFor(folder.Path));
        Assert.False((task with { Choices = null! }).ValidFor(folder.Path));
        Assert.False((task with { Question = null! }).ValidFor(folder.Path));
        Assert.False((task with { Choices = [null!] }).ValidFor(folder.Path));
        Assert.False((task with { ReportIssues = null! }).ValidFor(folder.Path));
        var report = new Tern.Export.DecisionReport { Title = "T", Question = "Q", Date = "D", Headline = "H", Recommendation = "R" };
        Assert.True((task with { Draft = new(report, []) }).ValidFor(folder.Path));
        Assert.True((task with { Draft = new(report, []), ReportIssues = ["A specific check issue"], ReportIssuesKey = ExploreTaskCheckpoint.ReportKey(report) }).ValidFor(folder.Path));
        Assert.False((task with { Draft = new(report, []), ReportIssues = ["A specific check issue"], ReportIssuesKey = "invalid" }).ValidFor(folder.Path));
        Assert.False((task with { Draft = new(null!, []) }).ValidFor(folder.Path));
        Assert.False((task with { Draft = new(report with { Findings = null! }, []) }).ValidFor(folder.Path));
        Assert.False((task with { Draft = new(report with { Reasons = [null!] }, []) }).ValidFor(folder.Path));
        Assert.False((task with { Draft = new(report, null!) }).ValidFor(folder.Path));
        Assert.False((task with { WorkspaceRoot = Path.GetDirectoryName(folder.Path)! }).ValidFor(folder.Path));
        var source = new KnowledgeSource(1, folder.Path, Path.Combine(Path.GetDirectoryName(folder.Path)!, "outside.md"), "../outside.md", "Outside", null, 1, "", "digest", []);
        Assert.False((task with { Choices = [new(source, true)] }).ValidFor(folder.Path));
    }

    [Fact]
    public void Private_checkpoints_stay_in_memory_and_normal_checkpoints_never_fall_back_to_plaintext()
    {
        using var folder = new ReviewFolder();
        var privateStore = new ExploreTaskStore(folder.Path);
        var task = new ExploreTaskCheckpoint { WorkspaceRoot = folder.Path, Question = "Sensitive current task canary" };
        privateStore.Save(task, privateWorkspace: true);
        Assert.Equal(task.Question, privateStore.Load(folder.Path, true)?.Question);
        Assert.False(File.Exists(privateStore.FileFor(folder.Path)));
        Assert.Null(new ExploreTaskStore(folder.Path).Load(folder.Path, true));

        var normal = new ExploreTaskStore(folder.Path);
        normal.Save(task, privateWorkspace: false);
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(File.Exists(normal.FileFor(folder.Path)));
            Assert.Equal(task.Question, normal.Load(folder.Path, false)?.Question);
            Assert.Contains("protect", normal.Problem);
            return;
        }
        var bytes = File.ReadAllBytes(normal.FileFor(folder.Path));
        Assert.True(bytes.AsSpan().StartsWith("TERNDP01"u8));
        Assert.DoesNotContain(task.Question, Encoding.UTF8.GetString(bytes));
        Assert.Equal(task.Question, new ExploreTaskStore(folder.Path).Load(folder.Path, false)?.Question);
        var corrupted = bytes.ToArray(); corrupted[^1] ^= 1;
        File.WriteAllBytes(normal.FileFor(folder.Path), corrupted);
        Assert.Null(new ExploreTaskStore(folder.Path).Load(folder.Path, false));
        File.WriteAllText(normal.FileFor(folder.Path), JsonSerializer.Serialize(task));
        Assert.Null(new ExploreTaskStore(folder.Path).Load(folder.Path, false));
        normal.Save(task, false);
        Assert.True(File.Exists(normal.FileFor(folder.Path)));
        Assert.Equal(task.Question, normal.Load(folder.Path, true)?.Question);
        Assert.False(File.Exists(normal.FileFor(folder.Path)));
    }

    [Fact]
    public void A_failed_Done_cleanup_warns_and_does_not_resurrect_the_task_in_this_session()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var folder = new ReviewFolder();
        var store = new ExploreTaskStore(folder.Path);
        var task = new ExploreTaskCheckpoint { WorkspaceRoot = folder.Path, Question = "Keep this task" };
        store.Save(task, false);
        using var locked = new FileStream(store.FileFor(folder.Path), FileMode.Open, FileAccess.Read, FileShare.Read);
        store.Remove(folder.Path);
        Assert.Null(store.Load(folder.Path, false));
        Assert.Contains("may reappear after restarting", store.Problem);
    }

    [AvaloniaFact]
    public async Task Making_a_workspace_private_purges_its_checkpoint_even_when_Explore_is_closed()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("privacy/note.md", "# Private conversion\nSynthetic note.");
        Assert.True(await f.Shell.PromoteFolderAsync(Path.GetDirectoryName(path)!));
        var workspace = f.Shell.Workspace!;
        var store = ExploreTaskStore.For(f.Shell);
        var task = new ExploreTaskCheckpoint { WorkspaceRoot = workspace.Root, Question = "Keep this unfinished question in memory" };
        store.Save(task, false);
        var savedPath = store.FileFor(workspace.Root);
        Assert.True(File.Exists(savedPath));
        Assert.Empty(KnowledgePanels(f.Window));

        Assert.True(await f.Shell.SetWorkspacePrivateAsync(workspace, true));

        Assert.True(f.Shell.Workspace!.IsPrivate);
        // Check immediately after conversion, before opening Explore or calling private Load.
        Assert.False(File.Exists(savedPath));
        Assert.Equal(task.Question, store.Load(workspace.Root, true)?.Question);
        Assert.Empty(KnowledgePanels(f.Window));
    }

    [Fact]
    public void A_linked_checkpoint_is_not_followed_or_deleted()
    {
        using var folder = new ReviewFolder();
        var store = new ExploreTaskStore(folder.Path);
        var file = store.FileFor(folder.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var target = Path.Combine(folder.Path, "unrelated.bin");
        File.WriteAllText(target, "Keep unrelated data");
        try { File.CreateSymbolicLink(file, target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        { Assert.Skip("Creating a synthetic symbolic link is unavailable on this test host."); return; }
        Assert.Null(store.Load(folder.Path, false));
        store.Save(new() { WorkspaceRoot = folder.Path, Question = "A pending question" }, false);
        store.Remove(folder.Path);
        Assert.Equal("Keep unrelated data", File.ReadAllText(target));
        Assert.True((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0);
    }
}
