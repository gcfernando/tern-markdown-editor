using Avalonia.LogicalTree;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.App.Settings;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed partial class AIWritingWorkflowTests
{
    [AvaloniaTheory]
    [InlineData(ThemeChoice.Paper, false)]
    [InlineData(ThemeChoice.Midnight, true)]
    public async Task Knowledge_guided_window_has_real_four_stage_navigation_shared_styles_and_followup_review(ThemeChoice theme, bool reduced)
    {
        var calls = 0;
        using var f = Fixture(async (request, token) =>
        {
            calls++;
            var text = SentText(await request.Content!.ReadAsStringAsync(token));
            if (HasPurpose(text, Tern.App.AIPurpose.ReportCheck)) return Reply("""{"unsupported":[]}""");
            Assert.True(HasPurpose(text, Tern.App.AIPurpose.Report));
            return Reply("""{"title":"Launch report","hasDecision":false,"headline":"Launch requirements","recommendation":"Complete the documented quality checks before release.","findings":[{"title":"Quality checks","kind":"Fact","detail":"The launch requires quality checks before release.","evidenceIds":[1]}],"evidence":[{"id":1,"sourceId":1,"fact":"Quality checks are required before release.","quote":"The launch requires quality checks before release."}]}""");
        });
        var path = await f.WriteAsync("notes/launch.md", "# Launch\n\nThe launch requires quality checks before release.");
        Assert.True(await f.Shell.PromoteFolderAsync(Path.GetDirectoryName(path)!));
        var target = f.Shell.NewDocument("Keep this draft unchanged");
        f.Shell.Settings.Theme = theme;
        f.Shell.Settings.ReduceMotion = reduced;
        f.Shell.Settings.KnowledgeAllowRemote = true;
        f.Window.ApplySettings(); f.Window.ApplyTheme();
        f.Window.ShowKnowledgeAssistant();
        var window = Assert.Single(f.Window.OwnedWindows.OfType<KnowledgeAssistantWindow>());
        Pump(window);
        var panel = window.Assistant;
        Assert.True(window.CanResize && !window.CanMaximize);
        Assert.False(f.Window.IsOverlayOpen);
        Assert.Equal(0, f.Window.Body.RowDefinitions[1].ActualHeight);
        var question = Find<TextBox>(panel, "Your question");
        question.Text = "What does the launch require?";
        Assert.True(question.Bounds.Height >= 70);
        var find = Find<Button>(panel, "Find my notes");
        Assert.True(find.IsEffectivelyVisible && find.IsDefault && find.Classes.Contains("accent"));
        Assert.Equal(window.FontSize, find.FontSize);
        Assert.StartsWith("Tip:", Find<TextBlock>(panel, "Tip").Text);
        await CaptureKnowledgeWindow(window, $"01-ask-{theme}");
        Click(find);
        await Until(() => Find<Button>(panel, "Get my answer").IsEffectivelyVisible && !Find<Button>(panel, "Cancel").IsEnabled);
        Pump(window);
        var send = Find<Button>(panel, "Get my answer");
        Assert.True(send.IsEffectivelyVisible);
        Assert.False(find.IsVisible);
        Assert.True(send.IsEnabled);
        Assert.Equal(0, calls);
        Assert.Contains(panel.GetVisualDescendants().OfType<TextBlock>(), text =>
            text.Text?.Contains("Only the ticked notes", StringComparison.Ordinal) == true);
        await CaptureKnowledgeWindow(window, $"02-sources-{theme}");
       
        Click(send);
        await Until(() => !Find<Button>(panel, "Cancel").IsEnabled);
        Pump(window);
        var share = Find<Button>(panel, "Share this answer");
        Assert.True(share.IsEffectivelyVisible);
        Assert.Contains(panel.GetVisualDescendants().OfType<Button>(), button =>
            AutomationProperties.GetName(button)?.StartsWith("Open note from answer", StringComparison.Ordinal) == true);
        Assert.Equal("Keep this draft unchanged", f.Shell.ActiveTab!.CurrentText);
        await CaptureKnowledgeWindow(window, $"03-answer-{theme}");
        Assert.Same(target, f.Shell.ActiveTab);
        Click(panel.GetVisualDescendants().OfType<Button>().Single(button =>
            AutomationProperties.GetName(button)?.StartsWith("Open note from answer", StringComparison.Ordinal) == true));
        await Until(() => !Find<Button>(panel, "Cancel").IsEnabled);
        Assert.Equal(WindowState.Minimized, window.WindowState);
        f.Window.ShowKnowledgeAssistant(); Pump(window);
        Assert.Same(window, Assert.Single(f.Window.OwnedWindows.OfType<KnowledgeAssistantWindow>()));
        Assert.Equal(WindowState.Normal, window.WindowState);
        Assert.True(share.IsEffectivelyVisible);
        Click(Find<Button>(panel, "Ask a new question"));
        Assert.True(find.IsVisible);
        Assert.Empty(question.Text ?? "");
        Assert.Equal(2, calls); // One report and its whole-content check; no separate answer generation.
        window.Close();
        Assert.Empty(KnowledgePanels(f.Window));
        f.Window.ShowKnowledgeAssistant(); Pump(f.Window);
        var reopened = Assert.Single(KnowledgePanels(f.Window));
        Assert.Empty(Find<TextBox>(reopened, "Your question").Text ?? "");
        Assert.DoesNotContain(reopened.GetVisualDescendants().OfType<Button>(), button =>
            AutomationProperties.GetName(button)?.StartsWith("Open note from answer", StringComparison.Ordinal) == true);
    }

    private static async Task CaptureKnowledgeWindow(Window window, string name)
    {
        await Task.Delay(160);
        Pump(window);
        var folder = Path.Combine(Screenshots.Folder, "knowledge-assistant-redesign");
        Directory.CreateDirectory(folder);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var output = File.Create(Path.Combine(folder, name + ".png"));
        frame.Save(output, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    [AvaloniaFact]
    public async Task Knowledge_review_validation_is_single_flight_and_closing_it_cancels_without_applying()
    {
        var started = 0;
        var applied = 0;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new Window();
        try
        {
            var review = new AIEditReview("", "A reviewed result", 0, _ => { applied++; return true; }, window.Close, _ => { }, window.Close,
                insertResult: true, validateApply: async token =>
                {
                    started++;
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return true; }
                    finally { finished.TrySetResult(); }
                });
            window.Content = review; window.Show(); Pump(window);
            var insert = ButtonNamed(review, "Insert result");
            Click(insert); Click(insert);
            Assert.Equal(1, started);
            Assert.False(insert.IsEnabled);
            window.Close();
            await Until(() => finished.Task.IsCompleted);
            Assert.Equal(0, applied);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Knowledge_entry_uses_the_effective_registered_shortcut()
    {
        using var f = Fixture((_, _) => Task.FromResult(Models()));
        f.Shell.NewDocument("A draft");
        void Press(Key key) => f.Window.RaiseEvent(new KeyEventArgs
        { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = KeyModifiers.Control | KeyModifiers.Alt });
        Press(Key.F2); Pump(f.Window);
        var panel = Assert.Single(KnowledgePanels(f.Window));
        Assert.Single(f.Window.OwnedWindows.OfType<KnowledgeAssistantWindow>()).Close();
        Assert.True(f.Shell.Registry.SetBinding(CommandIds.AskKnowledge, "Ctrl+Alt+F3").Succeeded);
        f.Window.ApplySettings();
        Press(Key.F2); Pump(f.Window);
        Assert.Empty(KnowledgePanels(f.Window));
        Press(Key.F3); Pump(f.Window);
        Assert.Single(KnowledgePanels(f.Window));
    }

    [AvaloniaTheory]
    [InlineData(ThemeChoice.Paper, 1280, 800, 1)]
    [InlineData(ThemeChoice.Midnight, 1280, 800, 1)]
    [InlineData(ThemeChoice.Paper, 960, 640, 1.5)]
    [InlineData(ThemeChoice.Midnight, 720, 520, 2)]
    public async Task Knowledge_composer_remains_reachable_at_compact_and_scaled_sizes(ThemeChoice theme, int width, int height, double scale)
    {
        using var f = Fixture((_, _) => Task.FromResult(Models()));
        try
        {
            var path = await f.WriteAsync("notes/launch.md", "# Launch\n\n## Risks\nSupply delays threaten rollout.");
            Assert.True(await f.Shell.PromoteFolderAsync(Path.GetDirectoryName(path)!));
            f.Shell.NewDocument("# A working draft\n\nEditing stays available beside the assistant.");
            f.Shell.Settings.Theme = theme; f.Shell.Settings.UiScale = scale;
            f.Window.Width = width; f.Window.Height = height;
            f.Window.ApplySettings(); f.Window.ApplyTheme();
            f.Window.ShowKnowledgeAssistant(); Pump(f.Window);
            var panel = Assert.Single(KnowledgePanels(f.Window));
            var assistantWindow = Assert.Single(f.Window.OwnedWindows.OfType<KnowledgeAssistantWindow>());
            assistantWindow.Width = Math.Min(width, assistantWindow.MaxWidth);
            assistantWindow.Height = Math.Min(height, assistantWindow.MaxHeight);
            Pump(assistantWindow);
            var question = Find<TextBox>(panel, "Your question");
            question.Text = "What are the rollout risks?";
            question.Focus(); question.BringIntoView(); Pump(f.Window);
            var origin = question.TranslatePoint(default, panel);
            Assert.NotNull(origin);
            Assert.True(origin.Value.Y >= -1 && origin.Value.Y + question.Bounds.Height <= panel.Bounds.Height + 1,
                $"Question outside panel: {origin.Value.Y} + {question.Bounds.Height} in {panel.Bounds.Height}");
            Assert.True(f.Window.Editor.TextArea.TextView.Bounds.Height >= f.Window.Editor.FontSize,
                $"Body={f.Window.Body.Bounds}, rows={f.Window.Body.RowDefinitions[0].ActualHeight}/{f.Window.Body.RowDefinitions[1].ActualHeight}, editorHost={f.Window.EditorHost.Bounds}, editor={f.Window.Editor.Bounds}");
            foreach (var control in new Control[] { question, Find<Button>(panel, "Find my notes") })
            {
                if (scale == 1)
                {
                    var initial = control.TranslatePoint(default, panel);
                    Assert.NotNull(initial);
                    Assert.True(initial.Value.Y >= -1 && initial.Value.Y + control.Bounds.Height <= panel.Bounds.Height + 1,
                        "Normal-size composer actions must be visible without scrolling.");
                }
                control.BringIntoView(); Pump(assistantWindow);
                var position = control.TranslatePoint(default, panel);
                Assert.NotNull(position);
                Assert.True(position.Value.Y >= -1 && position.Value.Y + control.Bounds.Height <= panel.Bounds.Height + 1,
                    $"Composer control outside panel: {position.Value.Y} + {control.Bounds.Height} in {panel.Bounds.Height}");
                Assert.True(control.Bounds.Width <= panel.Bounds.Width);
            }
            question.BringIntoView(); Pump(assistantWindow);
            if (scale == 1 && panel.Child is ScrollViewer viewport) { viewport.Offset = default; Pump(assistantWindow); }
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Tern.slnx"))) root = root.Parent;
            Assert.NotNull(root);
            var folder = Tern.Tests.Shared.TestOutput.Folder("screenshots", "knowledge-assistant");
            Directory.CreateDirectory(folder);
            Dispatcher.UIThread.RunJobs();
            using var bitmap = assistantWindow.CaptureRenderedFrame(); Assert.NotNull(bitmap);
            using var output = File.Create(Path.Combine(folder, $"{theme}-{width}x{height}-{scale:0.0}.png"));
            bitmap.Save(output, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        finally
        {
            f.Shell.Settings.UiScale = 1;
            f.Window.ApplySettings();
        }
    }

    [AvaloniaFact]
    public async Task Knowledge_remote_sharing_is_opt_in_and_question_changes_invalidate_consent_synchronously()
    {
        var calls = 0;
        using var f = Fixture((_, _) => { calls++; return Task.FromResult(Reply("Never sent")); });
        var path = await f.WriteAsync("notes/launch.md", "# Launch\nLaunch facts.");
        Assert.True(await f.Shell.PromoteFolderAsync(Path.GetDirectoryName(path)!));
        f.Window.ShowKnowledgeAssistant(); Pump(f.Window);
        var panel = Assert.Single(KnowledgePanels(f.Window));
        var question = Find<TextBox>(panel, "Your question");
        question.Text = "Launch";
        Click(Find<Button>(panel, "Find my notes"));
        await Until(() => Find<Button>(panel, "Get my answer").IsEffectivelyVisible && !Find<Button>(panel, "Cancel").IsEnabled);
       
        var send = Find<Button>(panel, "Get my answer");
        Assert.False(send.IsEnabled);
        Click(send);
        Assert.Equal(0, calls);
        f.Shell.Settings.KnowledgeAllowRemote = true;
        panel.RefreshSettings();
       
        Assert.True(send.IsEnabled);
        question.Text = "A different private question";
        Assert.False(send.IsEnabled);
        Assert.Equal(0, calls);
        f.Shell.Settings.KnowledgeAssistantEnabled = false;
        f.Window.ApplySettings();
        Assert.Empty(KnowledgePanels(f.Window));
    }

    [AvaloniaTheory]
    [InlineData(ThemeChoice.Paper, false)]
    [InlineData(ThemeChoice.Midnight, true)]
    public async Task Offline_mode_stops_on_the_first_screen_with_the_reason_and_fix_and_stays_nonmodal(ThemeChoice theme, bool reduced)
    {
        using var f = Fixture((_, _) => Task.FromResult(Models()));
        var path = await f.WriteAsync("notes/launch.md", "# Launch\n\n## Risks\nSupply delays threaten rollout.");
        Assert.True(await f.Shell.PromoteFolderAsync(Path.GetDirectoryName(path)!));
        var tab = f.Shell.NewDocument("Editing continues");
        f.Shell.Settings.OfflineMode = true; f.Shell.Settings.Theme = theme; f.Shell.Settings.ReduceMotion = reduced;
        f.Window.ApplySettings(); f.Window.ApplyTheme();
        await f.Window.RunCommandAsync(CommandIds.AskKnowledge);
        Pump(f.Window);
        var panel = Assert.Single(KnowledgePanels(f.Window));
        Assert.Single(f.Window.OwnedWindows.OfType<KnowledgeAssistantWindow>());
        var editorHeight = f.Window.Editor.TextArea.TextView.Bounds.Height;
        f.Window.ShowKnowledgeAssistant();
        Assert.Single(KnowledgePanels(f.Window));
        Assert.False(f.Window.IsOverlayOpen);
        var input = Find<TextBox>(panel, "Your question");
        input.Text = "What are the rollout risks?";
        Assert.True(input.Focus());
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.Control });
        Pump(f.Window);
        Assert.False(Find<Button>(panel, "Find my notes").IsEnabled);
        Assert.True(Find<Button>(panel, "Find my notes").IsEffectivelyVisible);
        Assert.False(Find<Button>(panel, "Get my answer").IsVisible);
        Assert.Contains("turn off Offline mode", Find<TextBlock>(panel, "Help for your question").Text);
        Assert.True(Find<Button>(panel, "Open Settings").IsVisible);
        Assert.DoesNotContain(panel.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text?.Contains("Supply delays", StringComparison.Ordinal) == true);
        Assert.True(f.Window.Editor.TextArea.Focus());
        tab.ApplyEdits([new Tern.Domain.Text.TextEdit(tab.CurrentText.Length, 0, " safely")]);
        Assert.Equal("Editing continues safely", tab.CurrentText);
        Assert.Equal(editorHeight, f.Window.Editor.TextArea.TextView.Bounds.Height);
        Assert.True(AutomationProperties.GetLiveSetting(Find<TextBlock>(panel, "Explore status")) == AutomationLiveSetting.Polite);
        Assert.Single(f.Window.OwnedWindows.OfType<KnowledgeAssistantWindow>()).Close();
        Assert.Empty(KnowledgePanels(f.Window));
        Assert.True(f.Window.Editor.TextArea.IsKeyboardFocusWithin);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Knowledge_review_rejects_target_or_source_changes_before_the_debounced_graph_update(bool changeSource)
    {
        using var f = Fixture((request, token) => AnswerOrReport(request, token, """{"answer":"Launch facts [1].","sources":[{"id":1,"reason":"Excerpt"}]}"""));
        var path = await f.WriteAsync("notes/launch.md", "# Launch\nLaunch facts.");
        Assert.True(await f.Shell.PromoteFolderAsync(Path.GetDirectoryName(path)!));
        var tab = f.Shell.NewDocument("Keep this draft");
        f.Shell.Settings.KnowledgeAllowRemote = true;
        f.Window.ShowKnowledgeAssistant(); Pump(f.Window);
        var panel = Assert.Single(KnowledgePanels(f.Window));
        Find<TextBox>(panel, "Your question").Text = "Launch";
        Click(Find<Button>(panel, "Find my notes"));
        await Until(() => Find<Button>(panel, "Get my answer").IsEffectivelyVisible && !Find<Button>(panel, "Cancel").IsEnabled);
        
        Click(Find<Button>(panel, "Get my answer"));
        await Until(() => !Find<Button>(panel, "Cancel").IsEnabled);
        await ShareToNoteAsync(panel);
        await Until(() => f.Window.OverlayLayer.Children.OfType<AIEditReview>().Any());
        var review = Assert.Single(f.Window.OverlayLayer.Children.OfType<AIEditReview>());
        if (changeSource) f.Shell.OpenWorkspaces[0].SetUnsavedText(path, "# Launch\nChanged source facts.");
        else tab.ApplyEdits([new Tern.Domain.Text.TextEdit(0, 0, "My new edit. ")]);
        var expected = tab.CurrentText;
        var insert = ButtonNamed(review, "Insert result");
        Click(insert);
        await Until(() => !f.Window.OverlayLayer.Children.OfType<AIEditReview>().Any()
            || review.GetVisualDescendants().OfType<TextBlock>().Any(text =>
                text.Text?.Contains("out of date", StringComparison.OrdinalIgnoreCase) == true
                || text.Text?.Contains("changed", StringComparison.OrdinalIgnoreCase) == true));
        Assert.Equal(expected, tab.CurrentText);
        Assert.Contains(review.GetVisualDescendants().OfType<TextBlock>(), text =>
            text.Text?.Contains("out of date", StringComparison.OrdinalIgnoreCase) == true
            || text.Text?.Contains("changed", StringComparison.OrdinalIgnoreCase) == true);
    }

    [AvaloniaTheory]
    [InlineData("invented")]
    [InlineData("provider")]
    [InlineData("network")]
    [InlineData("timeout")]
    [InlineData("cancel")]
    [InlineData("stale")]
    public async Task Knowledge_failures_never_present_a_late_or_invented_answer_or_modify_a_note(string failure)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<System.Net.Http.HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var f = Fixture(async (_, _) =>
        {
            started.TrySetResult();
            if (failure == "provider") throw new InvalidOperationException("Synthetic provider unavailable");
            if (failure == "network") throw new System.Net.Http.HttpRequestException("Synthetic unreachable endpoint");
            if (failure == "timeout") throw new TaskCanceledException("Synthetic provider timeout");
            return failure == "invented" ? Reply("""{"title":"Launch report","hasDecision":false,"headline":"Launch facts","recommendation":"Review the launch facts.","findings":[{"title":"Invented source","kind":"Fact","detail":"Launch facts.","evidenceIds":[1]}],"evidence":[{"id":1,"sourceId":99,"fact":"Launch facts.","quote":"Launch facts."}]}""") : await completion.Task;
        });
        var path = await f.WriteAsync("notes/launch.md", "# Launch\nLaunch facts.");
        Assert.True(await f.Shell.PromoteFolderAsync(Path.GetDirectoryName(path)!));
        var tab = f.Shell.NewDocument("Keep my unsaved draft");
        f.Shell.Settings.KnowledgeAllowRemote = true;
        f.Window.ShowKnowledgeAssistant(); Pump(f.Window);
        var panel = Assert.Single(KnowledgePanels(f.Window));
        Find<TextBox>(panel, "Your question").Text = "Launch";
        Click(Find<Button>(panel, "Find my notes"));
        await Until(() => Find<Button>(panel, "Get my answer").IsEffectivelyVisible && !Find<Button>(panel, "Cancel").IsEnabled);
        
        Click(Find<Button>(panel, "Get my answer"));
        await Until(() => started.Task.IsCompleted);
        if (failure == "cancel") Click(Find<Button>(panel, "Cancel"));
        if (failure == "stale") f.Shell.OpenWorkspaces[0].SetUnsavedText(path, "# Launch\nChanged facts.");
        completion.TrySetResult(Reply("""{"title":"Launch report","hasDecision":false,"headline":"Launch facts","recommendation":"Review the launch facts.","findings":[{"title":"Launch facts","kind":"Fact","detail":"Launch facts.","evidenceIds":[1]}],"evidence":[{"id":1,"sourceId":1,"fact":"Launch facts.","quote":"Launch facts."}]}"""));
        await Until(() => !Find<Button>(panel, "Cancel").IsEnabled);
        var status = Find<TextBlock>(panel, "Explore status").Text;
        Assert.True(status?.StartsWith("Could not", StringComparison.Ordinal) == true || status?.StartsWith("Cancelled", StringComparison.Ordinal) == true, status);
        Assert.DoesNotContain(panel.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetName(button)?.StartsWith("Open note from answer", StringComparison.Ordinal) == true);
        Assert.Equal("Keep my unsaved draft", tab.CurrentText);
    }

    [AvaloniaFact]
    public async Task Each_question_starts_fresh_and_a_report_is_added_to_a_note_only_after_review_and_can_be_undone()
    {
        var requests = new List<string>();
        using var f = Fixture(async (request, token) =>
        {
            var body = await request.Content!.ReadAsStringAsync(token);
            requests.Add(body);
            var text = SentText(body);
            if (HasPurpose(text, Tern.App.AIPurpose.ReportCheck)) return Reply("""{"unsupported":[]}""");
            if (HasPurpose(text, Tern.App.AIPurpose.Report))
            {
                Assert.Contains("Launch facts and risks.", text);
                return Reply(System.Text.Json.JsonSerializer.Serialize(new
                {
                    title = "Launch report", hasDecision = false, headline = "What do the launch notes say?",
                    recommendation = body.Contains("Explain Launch", StringComparison.Ordinal)
                        ? "Review these launch facts and risks in the first discussion." : "Review the documented launch facts.",
                    findings = new[] { new { title = "Launch facts", kind = "Fact", detail = "The note records launch facts and risks for review.", evidenceIds = new[] { 1 } } },
                    evidence = new[] { new { id = 1, sourceId = 1, fact = "The note records launch facts and risks.", quote = "Launch facts and risks." } },
                }));
            }
            return Reply("""{"terms":["launch"],"questions":[]}""");
        });
        var path = await f.WriteAsync("notes/launch.md", "# Launch\nLaunch facts and risks.");
        Assert.True(await f.Shell.PromoteFolderAsync(Path.GetDirectoryName(path)!));
        var tab = f.Shell.NewDocument("Original draft");
        f.Shell.Settings.KnowledgeAllowRemote = true;
        f.Window.ShowKnowledgeAssistant(); Pump(f.Window);
        var panel = Assert.Single(KnowledgePanels(f.Window));
        async Task Ask(string question)
        {
            Find<TextBox>(panel, "Your question").Text = question;
            Click(Find<Button>(panel, "Find my notes"));
            await Until(() => Find<Button>(panel, "Get my answer").IsEffectivelyVisible && !Find<Button>(panel, "Cancel").IsEnabled);
            Click(Find<Button>(panel, "Get my answer"));
            await Until(() => Find<Button>(panel, "Share this answer").IsEffectivelyVisible && !Find<Button>(panel, "Cancel").IsEnabled);
        }
        await Ask("Explain Launch");
        Assert.Equal("Original draft", tab.CurrentText);
        Click(Find<Button>(panel, "Ask a new question"));
        await Ask("What are the launch risks?");
        var reportRequests = requests.Where(body => HasPurpose(SentText(body), Tern.App.AIPurpose.Report)).ToArray();
        Assert.Equal(2, reportRequests.Length);
        Assert.DoesNotContain("Explain Launch", reportRequests[1]);
        Assert.DoesNotContain("in the first discussion", reportRequests[1]);
        Assert.DoesNotContain(requests, body => HasPurpose(SentText(body), Tern.App.AIPurpose.Answer));
        Assert.Contains("What are the launch risks?", reportRequests[1]);
        Assert.Contains("Launch facts", reportRequests[1]);
        await ShareToNoteAsync(panel);
        await Until(() => f.Window.OverlayLayer.Children.OfType<AIEditReview>().Any());
        Assert.Equal("Original draft", tab.CurrentText);
        var review = Assert.Single(f.Window.OverlayLayer.Children.OfType<AIEditReview>());
        Click(ButtonNamed(review, "Insert result"));
        await Until(() => !f.Window.OverlayLayer.Children.OfType<AIEditReview>().Any());
        Assert.Contains("# Launch report", tab.CurrentText);
        Assert.Contains("Review the documented launch facts.", tab.CurrentText);
        Assert.Contains("Launch facts and risks.", tab.CurrentText);
        await f.Window.RunCommandAsync(CommandIds.Undo);
        Assert.Equal("Original draft", tab.CurrentText);
        Click(Find<Button>(panel, "Ask a new question"));
        Assert.DoesNotContain(panel.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetName(button)?.StartsWith("Open note from answer", StringComparison.Ordinal) == true);
        await Ask("Launch new topic");
        reportRequests = requests.Where(body => HasPurpose(SentText(body), Tern.App.AIPurpose.Report)).ToArray();
        Assert.Equal(3, reportRequests.Length);
        Assert.Contains("Launch new topic", reportRequests[2]);
        Assert.DoesNotContain("What are the launch risks?", reportRequests[2]);
        Assert.DoesNotContain("Explain Launch", reportRequests[2]);
        Assert.DoesNotContain("in the first discussion", reportRequests[2]);
        Assert.DoesNotContain(requests, body => HasPurpose(SentText(body), Tern.App.AIPurpose.Answer));
    }

    private static IEnumerable<KnowledgeAssistantPanel> KnowledgePanels(MainWindow window) =>
        window.OwnedWindows.OfType<KnowledgeAssistantWindow>().Select(assistant => assistant.Assistant);
}
