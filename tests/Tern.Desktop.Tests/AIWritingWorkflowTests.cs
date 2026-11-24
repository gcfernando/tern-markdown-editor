using System.Net;
using System.Net.Http;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App;
using Tern.App.Commands;
using Tern.App.Shell;
using Tern.Desktop.Services;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed partial class AIWritingWorkflowTests
{
    private const string FirstId = "11111111111111111111111111111111";
    private const string SecondId = "22222222222222222222222222222222";
    [AvaloniaTheory]
    [InlineData("source")]
    [InlineData("preview")]
    [InlineData("reference")]
    public async Task Alt_space_reviews_the_active_note_then_applies_one_undoable_edit(string surface)
    {
        var requests = new List<string>();
        using var f = Fixture(async (request, token) =>
        {
            requests.Add(await request.Content!.ReadAsStringAsync(token));
            return Reply("```markdown\n# Updated\nA clearer draft.\n```");
        });
        var reference = f.Shell.NewDocument("# Reference stays unchanged");
        var active = f.Shell.NewDocument("# Original");
        InputElement target = f.Window.Editor.TextArea;
        if (surface == "preview") { await f.Window.RunCommandAsync(CommandIds.ViewReading); target = f.Window.PreviewControl; }
        if (surface == "reference")
        {
            f.Window.ToggleReferencePane();
            f.Window.ReferenceTabs.SelectedItem = reference;
            target = f.Window.ReferenceEditor.TextArea;
        }
        Pump(f.Window);
        target.Focus();
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space, KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift });
        Pump(f.Window);
        Assert.True(f.Window.IsOverlayOpen);
        var panel = Assert.Single(f.Window.OverlayLayer.Children.OfType<AIPromptPanel>());
        Assert.Equal("Entire document", Find<ComboBox>(panel, "AI text scope").SelectedItem);
        var question = Find<TextBox>(panel, "AI writing question");
        await Until(() => question.IsFocused);
        question.Text = "Make the draft clearer";
        if (surface == "source")
            question.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.Control });
        else Click(Find<Button>(panel, "Generate AI suggestion"));
        var review = await WaitForReview(f);
        Assert.Equal("# Original", active.CurrentText);
        var generation = Assert.Single(f.Shell.Activities.Items, item => item.Kind == "AI generation");
        Assert.Equal(ActivityState.Completed, generation.State);
        Assert.Equal("Suggestion ready for review; not applied", generation.Phase);
        Click(review.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Apply changes")));
        Assert.False(f.Window.IsOverlayOpen);
        Assert.Equal("# Updated\r\nA clearer draft.", active.CurrentText);
        Assert.Equal("# Reference stays unchanged", reference.CurrentText);
        Assert.True(active.IsDirty);
        Assert.Null(active.FilePath);
        Assert.Equal("Suggestion applied to buffer; not saved",
            Assert.Single(f.Shell.Activities.Items, item => item.Kind == "Applying approved AI suggestion").Phase);
        Assert.Contains("Make the draft clearer", Assert.Single(requests), StringComparison.Ordinal);
        Assert.Contains("# Original", requests[0], StringComparison.Ordinal);
        await f.Window.RunCommandAsync(CommandIds.Undo);
        Assert.Equal("# Original", active.CurrentText);
        await f.Window.RunCommandAsync(CommandIds.Redo);
        Assert.Equal("# Updated\r\nA clearer draft.", active.CurrentText);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_late_reply_remains_copyable_and_requires_reanchored_review_after_edit_or_tab_switch(bool switchTab)
    {
        var completion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var f = Fixture((_, _) => { requested.TrySetResult(); return completion.Task; });
        var tab = f.Shell.NewDocument("Original");
        f.Window.ShowAIWritingPrompt(); Pump(f.Window);
        var panel = Assert.Single(f.Window.OverlayLayer.Children.OfType<AIPromptPanel>());
        Find<TextBox>(panel, "AI writing question").Text = "Rewrite";
        Click(Find<Button>(panel, "Generate AI suggestion"));
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var target = switchTab ? f.Shell.NewDocument("Other note") : tab;
        if (!switchTab) tab.ApplyEdits([new Tern.Domain.Text.TextEdit(0, 0, "My new edit: ")]);
        var original = target.CurrentText;
        completion.SetResult(Reply("AI replacement"));
        var review = await WaitForReview(f);
        var apply = ButtonNamed(review, "Apply changes");
        Assert.False(apply.IsEnabled);
        var copy = ButtonNamed(review, "Copy result");
        Assert.True(copy.IsEnabled);
        Click(copy);
        Assert.Equal("AI replacement", await f.Window.Clipboard!.TryGetTextAsync());
        Assert.Equal(original, target.CurrentText);
        f.Window.Editor.CaretOffset = 2;
        Click(ButtonNamed(review, "Insert at cursor"));
        var insertion = await WaitForReview(f);
        Assert.NotSame(review, insertion);
        Assert.Equal(original, target.CurrentText);
        Click(ButtonNamed(insertion, "Insert result"));
        Assert.Equal(original.Insert(2, "AI replacement"), target.CurrentText);
        if (switchTab) Assert.Equal("Original", tab.CurrentText);
        await f.Window.RunCommandAsync(CommandIds.Undo);
        Assert.Equal(original, target.CurrentText);
    }

    [AvaloniaFact]
    public async Task Closing_the_prompt_cancels_without_applying_a_reply()
    {
        var completion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var f = Fixture((_, _) => completion.Task);
        var tab = f.Shell.NewDocument("Original");
        f.Window.ShowAIWritingPrompt(); Pump(f.Window);
        var panel = Assert.Single(f.Window.OverlayLayer.Children.OfType<AIPromptPanel>());
        Find<TextBox>(panel, "AI writing question").Text = "Rewrite";
        Click(Find<Button>(panel, "Generate AI suggestion"));
        f.Window.HideOverlay();
        completion.SetResult(Reply("Late replacement"));
        await Task.Delay(60, TestContext.Current.CancellationToken); Dispatcher.UIThread.RunJobs();
        Assert.Equal("Original", tab.CurrentText);
        await Until(() => f.Shell.Activities.Items.Any(item => item.Kind == "AI generation" && item.State == ActivityState.Canceled));
        Assert.DoesNotContain(f.Shell.Activities.Items, item => item.Kind == "AI generation" && item.IsActive);
    }

    [AvaloniaFact]
    public async Task Connection_switching_is_remembered_and_sends_to_the_selected_model()
    {
        string? sentModel = null;
        using var f = Fixture(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            sentModel = json.RootElement.GetProperty("model").GetString();
            return Reply("New text");
        });
        f.Window.AIConnectionPicker.SelectedItem = f.Window.AIWorkspace.Connections[1];
        Assert.Equal(SecondId, f.Window.AIWorkspace.SelectedId);
        var reopened = new AIWorkspaceService(f.Shell.Paths.SettingsDirectory, new EmptyVault());
        reopened.Reload();
        Assert.Equal(SecondId, reopened.SelectedId);
        f.Shell.NewDocument("Old text");
        f.Window.ShowAIWritingPrompt(); Pump(f.Window);
        var panel = Assert.Single(f.Window.OverlayLayer.Children.OfType<AIPromptPanel>());
        Find<TextBox>(panel, "AI writing question").Text = "Rewrite";
        Click(Find<Button>(panel, "Generate AI suggestion"));
        await WaitForReview(f);
        Assert.Equal("model-two", sentModel);
    }

    [AvaloniaFact]
    public async Task Selection_shares_only_selected_text_and_review_cannot_overwrite_a_newer_edit()
    {
        string? sent = null;
        using var f = Fixture(async (request, token) =>
        {
            sent = await request.Content!.ReadAsStringAsync(token);
            return Reply("Updated");
        });
        var note = f.Shell.NewDocument("Private text\nPublic text\nMore private text");
        Pump(f.Window);
        f.Window.Editor.Select("Private text\n".Length, "Public text".Length);
        f.Window.Editor.TextArea.Focus();
        f.Window.Editor.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space, KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift });
        Pump(f.Window);
        var prompt = Assert.Single(f.Window.OverlayLayer.Children.OfType<AIPromptPanel>());
        Assert.Equal("Selected text", Find<ComboBox>(prompt, "AI text scope").SelectedItem);
        Assert.Contains(prompt.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "What would you like me to do with this text?");
        Assert.Contains("starter", Find<TextBlock>(prompt, "How to write an AI instruction").Text);
        Assert.Contains("only the text you highlighted", Find<TextBlock>(prompt, "What the selected text scope shares").Text);
        var instruction = Find<TextBox>(prompt, "AI writing question");
        Assert.True(instruction.Bounds.Height >= 260);
        await Until(() => instruction.IsFocused);
        Click(ButtonNamed(prompt, "Improve selected text"));
        Assert.Contains("Rewrite this for clarity", instruction.Text, StringComparison.Ordinal);
        instruction.Text = "Improve this";
        Click(Find<Button>(prompt, "Generate AI suggestion"));
        var review = await WaitForReview(f);
        Assert.Contains("Public text", sent, StringComparison.Ordinal);
        Assert.Contains("Improve this", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("Private text", sent, StringComparison.Ordinal);
        Click(Find<Button>(review, "Back to AI instruction"));
        Pump(f.Window);
        var reopened = Assert.Single(f.Window.OverlayLayer.Children.OfType<AIPromptPanel>());
        Assert.Equal("Improve this", Find<TextBox>(reopened, "AI writing question").Text);
        Assert.Equal("Selected text", Find<ComboBox>(reopened, "AI text scope").SelectedItem);
        Assert.Equal("Private text\nPublic text\nMore private text", note.CurrentText);
        Click(Find<Button>(reopened, "Generate AI suggestion"));
        review = await WaitForReview(f);
        note.ApplyEdits([Tern.Domain.Text.TextEdit.Insert(0, "New edit\n")]);
        Click(review.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Apply changes")));
        Assert.StartsWith("New edit\nPrivate text", note.CurrentText, StringComparison.Ordinal);
        Assert.True(f.Window.IsOverlayOpen);
    }

    [Fact]
    public void Review_groups_separate_changes_and_reconstructs_the_proposal()
    {
        const string original = "old one\r\nunchanged\r\nold two";
        const string proposed = "new one\r\nunchanged\r\nnew two";
        var edits = AIEditReview.Changes(original, proposed, 0);
        Assert.Equal(2, edits.Count);
        Assert.Equal(proposed, Tern.Domain.Text.TextEdit.ApplyAll(original, edits));
        Assert.Equal("new one\r\nunchanged\r\nold two", Tern.Domain.Text.TextEdit.ApplyAll(original, [edits[0]]));
    }

    [AvaloniaFact]
    public async Task Workspace_answer_sends_reviewed_passages_only_and_opens_a_cited_file()
    {
        string? sent = null;
        var currentTaskSends = new List<string>();
        using var f = Fixture(async (request, token) =>
        {
            sent = await request.Content!.ReadAsStringAsync(token);
            var text = SentText(sent); currentTaskSends.Add(text);
            if (HasPurpose(text, Tern.App.AIPurpose.ReportCheck)) return Reply("""{"unsupported":[]}""");
            Assert.True(HasPurpose(text, AIPurpose.Report));
            var sourceId = int.Parse(System.Text.RegularExpressions.Regex.Match(text, @"\[Note (\d+):").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            return Reply(System.Text.Json.JsonSerializer.Serialize(new
            {
                title = "Recorded decision", hasDecision = false, headline = "Which option was selected?", recommendation = "Beta was selected.",
                findings = new[] { new { title = "Recorded selection", kind = "Fact", detail = "Beta was selected.", evidenceIds = new[] { 1 } } },
                evidence = new[] { new { id = 1, sourceId, fact = "Beta was selected.", quote = "Beta was selected." } },
            }));
        });
        var selected = await f.WriteAsync("first/decision.md", "# Decision\nBeta was selected.");
        await f.WriteAsync("first/excluded.md", "# Decision\nBeta is EXCLUDED PRIVATE.");
        await f.WriteAsync("second/secret.md", "# Decision\nBeta is PRIVATE.");
        Assert.True(await f.Shell.PromoteFolderAsync(Path.GetDirectoryName(selected)!));
        Assert.True(await f.Shell.OpenFolderAsync(Path.Combine(f.Docs, "second")));
        var first = Assert.Single(f.Shell.OpenWorkspaces, root => root.Root == Path.GetDirectoryName(selected));
        Click(f.Window.FindControl<Button>("AIEntryButton")!); Pump(f.Window);
        Click(Find<Button>(f.Window, "Explore your notes"));
        Pump(f.Window);
        var assistantWindow = Assert.Single(f.Window.OwnedWindows.OfType<KnowledgeAssistantWindow>());
        Pump(assistantWindow);
        Assert.False(f.Window.IsOverlayOpen);
        var window = Assert.Single(KnowledgePanels(f.Window));
        Find<ComboBox>(window, "Search in").SelectedItem = first;
        Find<TextBox>(window, "Your question").Text = "Beta";
        f.Shell.Settings.KnowledgeAllowRemote = true;
        var send = Find<Button>(window, "Get my answer");
        Click(Find<Button>(window, "Find my notes"));
        await Until(() => Find<Button>(window, "Get my answer").IsEffectivelyVisible && !Find<Button>(window, "Cancel").IsEnabled);
        Assert.True(send.IsEnabled);
        var includes = window.GetVisualDescendants().OfType<CheckBox>()
            .Where(c => AutomationProperties.GetName(c)?.StartsWith("Include note: ", StringComparison.Ordinal) == true).ToArray();
        Assert.Equal(2, includes.Length);
        var excluded = includes.Single(c => AutomationProperties.GetName(c)!.EndsWith(": excluded.md", StringComparison.Ordinal));
        excluded.IsChecked = false;
        var included = includes.Single(c => !ReferenceEquals(c, excluded));
        included.IsChecked = false; Assert.False(send.IsEnabled);
        included.IsChecked = true; Assert.True(send.IsEnabled);
        
        Assert.True(send.IsEnabled);
        Assert.Null(sent);
        Click(send);
        await Until(() => window.GetVisualDescendants().OfType<SelectableTextBlock>().Any(t => t.Text == "• Beta was selected."));
        Assert.Contains("Beta was selected", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", sent, StringComparison.Ordinal);
        Assert.Equal(2, currentTaskSends.Count);
        Assert.All(currentTaskSends, text => Assert.DoesNotContain("PRIVATE", text, StringComparison.Ordinal));
        var source = Find<Button>(window, "Open note from answer: decision.md");
        Click(source);
        await Until(() => string.Equals(f.Shell.ActiveTab?.FilePath, Path.GetFullPath(selected), StringComparison.OrdinalIgnoreCase));
    }

    private static Button ButtonNamed(Visual root, string text) =>
        root.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, text));

    [AvaloniaTheory]
    [InlineData(50_000)]
    [InlineData(80_000)]
    public async Task Related_notes_only_insert_selected_links(int limit)
    {
        var sent = new List<string>();
        using var f = new DesktopFixture(directory => TwoLimits(directory, _ => Reply(
            """{"answer":"","sources":[{"id":1,"reason":"Same rollout decision"}]}"""), sent));
        f.Window.AIWorkspace.Select(limit == 50_000 ? FirstId : SecondId);
        var source = await f.WriteAsync("docs/Beta plan.md", "# Beta rollout");
        await f.WriteAsync("docs/Beta decision.md", "# Decision\nBeta was selected.");
        Assert.True(await f.Shell.PromoteFolderAsync(Path.GetDirectoryName(source)!));
        var note = (await f.Shell.OpenFileAsync(source))!;
        f.Window.InspectorContent.SelectedIndex = 1;
        Pump(f.Window);
        Click(ButtonNamed(f.Window, "Suggest related documents…"));
        var window = Assert.Single(f.Window.OwnedWindows);
        Pump(window);
        Find<TextBox>(window, "Related note topics").Text = "Beta";
        var send = ButtonNamed(window, "Send reviewed request");
        Click(ButtonNamed(window, "Find passages"));
        await Until(() => send.IsEnabled);
        Click(send);
        await Until(() => window.GetVisualDescendants().OfType<CheckBox>().Any(box => Equals(box.Content, "Include link")));
        var include = Assert.Single(window.GetVisualDescendants().OfType<CheckBox>(), box => Equals(box.Content, "Include link"));
        var insert = ButtonNamed(window, "Insert selected links");
        Click(insert);
        Assert.Equal("# Beta rollout", note.CurrentText);
        include.IsChecked = true;
        Click(insert);
        Assert.Contains("[Beta decision](Beta%20decision.md)", note.CurrentText, StringComparison.Ordinal);
        await f.Window.RunCommandAsync(CommandIds.Undo);
        Assert.Equal("# Beta rollout", note.CurrentText);
        Assert.NotEmpty(sent);
        Assert.All(sent, message => Assert.InRange(message.Length, 1, limit));
    }

    [AvaloniaFact]
    public async Task Summary_of_selected_text_replaces_only_that_selection()
    {
        using var f = Fixture((_, _) => Task.FromResult(Reply("\n\nShort summary")));
        var note = f.Shell.NewDocument("# First\nDetails\n\n# Second\nKeep");
        f.Window.Editor.CaretOffset = "# First\n".Length;
        f.Window.Editor.Select("# First\n".Length, "Details".Length);
        f.Window.ShowAIWritingPrompt("Summarize");
        Pump(f.Window);
        var panel = Assert.Single(f.Window.OverlayLayer.Children.OfType<AIPromptPanel>());
        var scope = Find<ComboBox>(panel, "AI text scope");
        Assert.Equal(new[] { "Selected text", "Instruction only" }, scope.Items.OfType<string>());
        Click(Find<Button>(panel, "Generate AI suggestion"));
        var review = await WaitForReview(f);
        Pump(f.Window);
        var original = Find<TextBox>(review, "Original Markdown");
        var result = Find<TextBox>(review, "Proposed Markdown");
        Assert.Equal("Details", original.Text);
        Assert.Equal("Short summary", result.Text);
        Assert.Equal(original.Bounds.Height, result.Bounds.Height);
        var close = ButtonNamed(review, "Close");
        var back = ButtonNamed(review, "Back");
        var apply = ButtonNamed(review, "Apply changes");
        Assert.Same(close.GetVisualAncestors().OfType<DialogActions>().Single(),
            back.GetVisualAncestors().OfType<DialogActions>().Single());
        Assert.True(back.TranslatePoint(default, review)!.Value.X < apply.TranslatePoint(default, review)!.Value.X);
        Assert.True(apply.TranslatePoint(default, review)!.Value.X < close.TranslatePoint(default, review)!.Value.X);
        Assert.DoesNotContain("Short summary", note.CurrentText, StringComparison.Ordinal);
        Click(apply);
        Assert.Equal("# First\nShort summary\n\n# Second\nKeep", note.CurrentText);
        await f.Window.RunCommandAsync(CommandIds.Undo);
        Assert.Equal("# First\nDetails\n\n# Second\nKeep", note.CurrentText);
    }

    [AvaloniaFact]
    public async Task Generation_shows_progress_until_the_request_finishes()
    {
        var release = new TaskCompletionSource();
        using var f = Fixture(async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return Reply("Updated");
        });
        f.Shell.NewDocument("Original text");
        f.Window.ShowAIWritingPrompt();
        Pump(f.Window);
        var panel = Assert.Single(f.Window.OverlayLayer.Children.OfType<AIPromptPanel>());
        Find<TextBox>(panel, "AI writing question").Text = "Improve";
        var send = Find<Button>(panel, "Generate AI suggestion");
        Click(send);
        await Until(() => panel.GetVisualDescendants().OfType<LoadingState>().Any());
        Assert.Equal("Generating…", send.Content);
        release.SetResult();
        await WaitForReview(f);
    }

    [AvaloniaFact]
    public async Task Summary_of_the_entire_note_is_inserted_without_replacing_it()
    {
        using var f = Fixture((_, _) => Task.FromResult(Reply("\n\nShort summary")));
        var note = f.Shell.NewDocument("# First\nDetails");
        f.Window.ShowAIWritingPrompt("Summarize");
        Pump(f.Window);
        var panel = Assert.Single(f.Window.OverlayLayer.Children.OfType<AIPromptPanel>());
        Assert.Equal(new[] { "Entire document", "Instruction only" }, Find<ComboBox>(panel, "AI text scope").Items.OfType<string>());
        Click(Find<Button>(panel, "Generate AI suggestion"));
        var review = await WaitForReview(f);
        Pump(f.Window);
        Click(ButtonNamed(review, "Insert result"));
        Assert.Contains("# First\nDetails", note.CurrentText, StringComparison.Ordinal);
        Assert.Contains("Short summary", note.CurrentText, StringComparison.Ordinal);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_aligns_boxes_and_applies_all_changes_or_discards_on_escape(bool escape)
    {
        using var f = Fixture((_, _) => Task.FromResult(Reply("new\nextra line\nanchor\nnew last")));
        var note = f.Shell.NewDocument("old\nanchor\nlast");
        f.Window.ShowAIWritingPrompt();
        Pump(f.Window);
        var composer = Assert.Single(f.Window.OverlayLayer.Children.OfType<AIPromptPanel>());
        Find<TextBox>(composer, "AI writing question").Text = "Improve";
        Click(Find<Button>(composer, "Generate AI suggestion"));
        var review = await WaitForReview(f);
        Pump(f.Window);
        Assert.Equal(new[] { "Apply changes", "Back", "Close", "Copy result" },
            review.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible && button is not CheckBox)
                .Select(button => button.Content).OfType<string>().OrderBy(label => label, StringComparer.Ordinal));
        var before = review.GetVisualDescendants().OfType<TextBox>().First(box => AutomationProperties.GetName(box) == "Original Markdown");
        var after = review.GetVisualDescendants().OfType<TextBox>().First(box => AutomationProperties.GetName(box) == "Proposed Markdown");
        Assert.True(before.Bounds.Height > 0);
        Assert.Equal(before.Bounds.Height, after.Bounds.Height);
        Assert.Empty(review.GetVisualDescendants().OfType<CheckBox>());
        var apply = ButtonNamed(review, "Apply changes");
        Assert.True(apply.IsEnabled);
        if (escape)
        {
            var close = ButtonNamed(review, "Close");
            Assert.True(before.IsKeyboardFocusWithin);
            var comparisons = review.GetVisualDescendants().OfType<TextBox>().ToArray();
            Assert.Equal(2, comparisons.Length); // One complete before/after comparison.
            Control[] order = [after,
                ButtonNamed(review, "Back"), ButtonNamed(review, "Copy result"), apply, close];
            foreach (var control in order)
            {
                f.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
                Assert.True(control.IsKeyboardFocusWithin, $"Expected focus on {AutomationProperties.GetName(control) ?? (control as Button)?.Content}.");
            }
            f.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            Assert.True(before.IsKeyboardFocusWithin);
            f.Window.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
            Assert.True(close.IsKeyboardFocusWithin);
            f.Window.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
            Assert.True(apply.IsKeyboardFocusWithin);
            Assert.True(after.Focus());
            after.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Assert.Equal("old\nanchor\nlast", note.CurrentText);
        }
        else
        {
            Click(apply);
            Assert.Equal("new\nextra line\nanchor\nnew last", note.CurrentText.ReplaceLineEndings("\n"));
        }
        Assert.False(f.Window.IsOverlayOpen);
    }

    [AvaloniaFact]
    public void Escape_from_a_picker_result_restores_the_invoking_control()
    {
        using var f = new DesktopFixture();
        Assert.True(f.Window.SearchBox.Focus());
        f.Window.ShowPalette();
        Pump(f.Window);
        var row = f.Window.Picker!.Results.GetVisualDescendants().OfType<ListBoxItem>().First();
        Assert.True(row.Focus());
        f.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(f.Window.IsOverlayOpen);
        Assert.True(f.Window.SearchBox.IsKeyboardFocusWithin);
    }

    [AvaloniaFact]
    public void Tab_and_shift_tab_cycle_inside_a_nested_dialog_and_skip_disabled_actions()
    {
        using var f = new DesktopFixture();
        var settings = new SettingsWindow(f.Shell.Settings, f.Shell.Registry);
        settings.Show(f.Window);
        try
        {
            var table = new InsertTableWindow();
            _ = table.ShowDialog<TableDimensions?>(settings);
            try
            {
                Pump(table);
                Assert.True(table.RowsInput.IsKeyboardFocusWithin);
                table.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
                Assert.True(table.ColumnsInput.IsKeyboardFocusWithin);
                table.ColumnsInput.Text = "0";
                Assert.False(table.InsertButton.IsEnabled);
                table.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
                Assert.True(ButtonNamed(table, "Cancel").IsKeyboardFocusWithin);
                table.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
                Assert.True(Find<Button>(table, "Learn more about this screen").IsKeyboardFocusWithin);
                table.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
                Assert.True(table.RowsInput.IsKeyboardFocusWithin);
                table.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
                Assert.True(Find<Button>(table, "Learn more about this screen").IsKeyboardFocusWithin);
                table.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
                Assert.True(ButtonNamed(table, "Cancel").IsKeyboardFocusWithin);
            }
            finally { table.Close(); }
        }
        finally { settings.Close(); }
    }

    private static async Task<AIEditReview> WaitForReview(DesktopFixture f)
    {
        await Until(() => f.Window.OverlayLayer.Children.OfType<AIEditReview>().Any());
        return Assert.Single(f.Window.OverlayLayer.Children.OfType<AIEditReview>());
    }

    [AvaloniaFact]
    public async Task Instruction_only_shares_no_note_text_and_inserts_one_undoable_result()
    {
        string? sent = null;
        using var f = Fixture(async (request, token) => { sent = await request.Content!.ReadAsStringAsync(token); return Reply("New paragraph"); });
        var tab = f.Shell.NewDocument("PRIVATE EXISTING NOTE");
        f.Window.Editor.CaretOffset = tab.CurrentText.Length;
        f.Window.ShowAIWritingPrompt(preferredScope: "Instruction only"); Pump(f.Window);
        var prompt = Assert.Single(f.Window.OverlayLayer.Children.OfType<AIPromptPanel>());
        Find<TextBox>(prompt, "AI writing question").Text = "Create a paragraph";
        Click(Find<Button>(prompt, "Generate AI suggestion"));
        var review = await WaitForReview(f);
        Assert.DoesNotContain("PRIVATE EXISTING NOTE", sent, StringComparison.Ordinal);
        Assert.Contains("Create a paragraph", sent, StringComparison.Ordinal);
        Click(ButtonNamed(review, "Insert result"));
        Assert.Equal("PRIVATE EXISTING NOTE\r\n\r\nNew paragraph", tab.CurrentText);
        await f.Window.RunCommandAsync(CommandIds.Undo);
        Assert.Equal("PRIVATE EXISTING NOTE", tab.CurrentText);
    }

    [AvaloniaFact]
    public async Task Tutor_requests_are_independent_and_never_attach_previous_answers_or_document_selections()
    {
        var requests = new List<string>();
        using var f = Fixture(async (request, token) => { requests.Add(await request.Content!.ReadAsStringAsync(token)); return Reply("A grounded explanation"); });
        f.Shell.NewDocument("PRIVATE BEFORE\nSelected excerpt\nPRIVATE AFTER");
        f.Window.Editor.Select("PRIVATE BEFORE\n".Length, "Selected excerpt".Length);
        using var tutor = new HelpTutorView(f.Window.AIWorkspace, f.Shell.Registry, () => Task.CompletedTask, null, null, _ => { }, null,
            () => f.Window.Editor.SelectedText);
        var window = new Window { Content = tutor }; window.Show(f.Window); Pump(window);
        tutor.Connections.SelectedIndex = 0;
        tutor.Question.Text = "Explain headings OLD_QUESTION_CANARY"; Click(tutor.SendButton);
        await Until(() => tutor.SendButton.IsEnabled && tutor.Transcript.Text?.Contains("A grounded explanation", StringComparison.Ordinal) == true);
        Assert.DoesNotContain("Selected excerpt", requests[0], StringComparison.Ordinal);
        Assert.DoesNotContain(tutor.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Use selected text"));
        tutor.Question.Text = "How do I search a note?"; Click(tutor.SendButton);
        await Until(() => requests.Count == 2 && tutor.SendButton.IsEnabled);
        Assert.DoesNotContain("EARLIER CONVERSATION", requests[1], StringComparison.Ordinal);
        Assert.DoesNotContain("Explain headings", requests[1], StringComparison.Ordinal);
        Assert.DoesNotContain("OLD_QUESTION_CANARY", requests[1], StringComparison.Ordinal);
        Assert.DoesNotContain("A grounded explanation", requests[1], StringComparison.Ordinal);
        Assert.Contains("How do I search a note?", requests[1], StringComparison.Ordinal);
        Assert.DoesNotContain("Selected excerpt", requests[1], StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE BEFORE", requests[1], StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE AFTER", requests[1], StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Failed_provider_automatically_recovers_with_installed_export_steps()
    {
        using var f = Fixture((_, _) => throw new InvalidOperationException("Synthetic provider unavailable"));
        using var tutor = new HelpTutorView(f.Window.AIWorkspace, f.Shell.Registry, () => Task.CompletedTask, null, null, _ => { }, null);
        var window = new Window { Content = tutor }; window.Show(f.Window); Pump(window);
        try
        {
            tutor.Connections.SelectedIndex = 0;
            tutor.Question.Text = "How do I export a PDF?"; Click(tutor.SendButton);
            await Until(() => tutor.SendButton.IsEnabled && tutor.Transcript.IsVisible);
            Assert.Contains("offline answer", tutor.Transcript.Text);
            Assert.Contains("Export As", tutor.Transcript.Text);
            Assert.Contains("recovered", Find<TextBlock>(tutor, "Tern help status").Text);
            Assert.Equal("How do I export a PDF?", tutor.Question.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Guide_rejects_unrelated_questions_before_any_provider_request()
    {
        string? sent = null;
        using var f = Fixture(async (request, token) =>
        {
            sent = await request.Content!.ReadAsStringAsync(token);
            return Reply("Plants use sunlight to make food.");
        });
        f.Shell.NewDocument("PRIVATE NOTE NOT INCLUDED");
        await f.Window.RunCommandAsync(CommandIds.Help);
        var guide = Assert.Single(f.Window.OwnedWindows.OfType<HelpWindow>());
        Pump(guide);
        var sections = Assert.Single(guide.GetLogicalDescendants().OfType<TabControl>(), t => t.Items.OfType<TabItem>().Any(i => Equals(i.Header, "Ask & learn")));
        sections.SelectedIndex = 1; Pump(guide);
        var tutor = Assert.Single(guide.GetLogicalDescendants().OfType<HelpTutorView>().Distinct());
        Assert.Equal(f.Window.AIWorkspace.SelectedId, Assert.IsType<WorkspaceConnection>(tutor.Connections.SelectedItem).Id);
        tutor.Connections.SelectedIndex = 0;
        tutor.Question.Text = "Explain photosynthesis simply";
        Click(tutor.SendButton);
        Pump(guide);
        Assert.Null(sent);
        Assert.False(tutor.Transcript.IsVisible);
        Assert.Contains("Ask about using Tern", Find<TextBlock>(tutor, "Tern help status").Text);
        Assert.Equal("Explain photosynthesis simply", tutor.Question.Text);
        Assert.Empty(guide.OwnedWindows);
        guide.Close();
    }

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("truncated")]
    public async Task Empty_and_truncated_replies_keep_the_original_note(string response)
    {
        using var f = Fixture((_, _) => Task.FromResult(Reply(response, response == "truncated" ? "length" : "stop")));
        var tab = f.Shell.NewDocument("Keep me");
        f.Window.ShowAIWritingPrompt(); Pump(f.Window);
        var panel = Assert.Single(f.Window.OverlayLayer.Children.OfType<AIPromptPanel>());
        Find<TextBox>(panel, "AI writing question").Text = "Rewrite";
        Click(Find<Button>(panel, "Generate AI suggestion"));
        await Until(() => Find<TextBlock>(panel, "AI writing status").Text?.Contains("not changed", StringComparison.Ordinal) == true);
        Assert.Equal("Keep me", tab.CurrentText);
        var activity = Assert.Single(f.Shell.Activities.Items, item => item.Kind == "AI generation");
        Assert.Equal(ActivityState.Failed, activity.State);
        Assert.NotNull(activity.Finished);
        f.Window.HideOverlay();
    }

    [AvaloniaTheory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    [InlineData(200)]
    public async Task Failed_validation_never_executes_generation_or_changes_the_note(int status)
    {
        var posts = 0;
        using var f = Fixture((_, _) => { posts++; return Task.FromResult(Reply("Must not apply")); },
            (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent(status == 200 ? "{\"data\":[{\"id\":\"different-model\"}]}" : "{\"error\":{\"message\":\"Denied\"}}") }));
        var tab = f.Shell.NewDocument("Keep me");
        f.Window.ShowAIWritingPrompt(); Pump(f.Window);
        var panel = Assert.Single(f.Window.OverlayLayer.Children.OfType<AIPromptPanel>());
        var question = Find<TextBox>(panel, "AI writing question");
        Assert.InRange(question.Bounds.Height, 260, 380);
        question.Text = "Rewrite";
        var send = Find<Button>(panel, "Generate AI suggestion");
        Assert.True(send.IsEnabled);
        Click(send);
        await Until(() => Find<TextBlock>(panel, "AI writing validation").Text?.StartsWith("Generation stopped", StringComparison.Ordinal) == true);
        Assert.False(send.IsEnabled);
        Click(send);
        question.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.Control });
        Assert.Equal(0, posts);
        Assert.Equal("Keep me", tab.CurrentText);
    }

    [AvaloniaFact]
    public async Task Invalid_local_inputs_and_stale_targets_block_click_and_keyboard_submission()
    {
        var requests = 0;
        using var f = Fixture((_, _) => { requests++; return Task.FromResult(Reply("Never")); });
        var tab = f.Shell.NewDocument("Keep me");
        f.Window.ShowAIWritingPrompt(); Pump(f.Window);
        var panel = Assert.Single(f.Window.OverlayLayer.Children.OfType<AIPromptPanel>());
        var question = Find<TextBox>(panel, "AI writing question");
        var send = Find<Button>(panel, "Generate AI suggestion");
        foreach (var text in new[] { "", "   " })
        {
            question.Text = text;
            Assert.False(send.IsEnabled);
            Click(send);
        }
        question.Text = "Rewrite";
        tab.ApplyEdits([Tern.Domain.Text.TextEdit.Insert(0, new string('x', 33000))]);
        Click(send); // Revalidates even if the document changed after the last text-box event.
        Assert.False(send.IsEnabled);
        await f.Window.RunCommandAsync(CommandIds.Undo);
        f.Shell.NewDocument("Other note");
        question.Text = "Rewrite again";
        Click(send);
        Assert.False(send.IsEnabled);
        Assert.Equal(0, requests);
        Assert.DoesNotContain("Never", tab.CurrentText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Document_changes_during_model_validation_prevent_the_generation_request()
    {
        var check = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var posts = 0;
        using var f = Fixture((_, _) => { posts++; return Task.FromResult(Reply("Never")); }, (_, _) => check.Task);
        var tab = f.Shell.NewDocument("Original");
        f.Window.ShowAIWritingPrompt(); Pump(f.Window);
        var panel = Assert.Single(f.Window.OverlayLayer.Children.OfType<AIPromptPanel>());
        Find<TextBox>(panel, "AI writing question").Text = "Rewrite";
        Click(Find<Button>(panel, "Generate AI suggestion"));
        tab.ApplyEdits([Tern.Domain.Text.TextEdit.Insert(0, "My edit ")]);
        check.SetResult(Models());
        await Until(() => Find<TextBlock>(panel, "AI writing status").Text?.Contains("changed during validation", StringComparison.Ordinal) == true);
        Assert.Equal(0, posts);
        Assert.Equal("My edit Original", tab.CurrentText);
    }

    private static HttpResponseMessage Models() => new(HttpStatusCode.OK)
    { Content = new StringContent("{\"data\":[{\"id\":\"model-one\"},{\"id\":\"model-two\"}]}") };

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Missing_key_or_model_list_address_blocks_every_submission_path(bool missingKey)
    {
        var calls = 0;
        Task<HttpResponseMessage> Unexpected(HttpRequestMessage _, CancellationToken __)
        { calls++; return Task.FromResult(Reply("Never")); }
        using var f = Fixture(Unexpected, Unexpected);
        var store = new AIProfileStore(f.Shell.Paths.SettingsDirectory);
        var profile = store.Load()[0];
        store.Save([missingKey
            ? profile with { ServiceId = "openai", Endpoint = "https://api.openai.com/v1/chat/completions", DiscoveryEndpoint = null }
            : profile with { DiscoveryEndpoint = null }]);
        var tab = f.Shell.NewDocument("Keep me");
        f.Window.ShowAIWritingPrompt(); Pump(f.Window);
        var panel = Assert.Single(f.Window.OverlayLayer.Children.OfType<AIPromptPanel>());
        var question = Find<TextBox>(panel, "AI writing question");
        question.Text = "Rewrite";
        var send = Find<Button>(panel, "Generate AI suggestion");
        Assert.False(send.IsEnabled);
        Assert.True(send.Opacity < 1);
        Click(send);
        question.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.Control });
        Assert.Equal(0, calls);
        Assert.Equal("Keep me", tab.CurrentText);
    }

    [AvaloniaFact]
    public async Task Connection_edits_during_validation_block_generation()
    {
        var check = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var posts = 0;
        using var f = Fixture((_, _) => { posts++; return Task.FromResult(Reply("Never")); }, (_, _) => check.Task);
        f.Shell.NewDocument("Original");
        f.Window.ShowAIWritingPrompt(); Pump(f.Window);
        var panel = Assert.Single(f.Window.OverlayLayer.Children.OfType<AIPromptPanel>());
        Find<TextBox>(panel, "AI writing question").Text = "Rewrite";
        Click(Find<Button>(panel, "Generate AI suggestion"));
        var store = new AIProfileStore(f.Shell.Paths.SettingsDirectory);
        store.Save(store.Load().Select(p => p with { Model = "edited-model" }).ToArray());
        check.SetResult(Models());
        await Until(() => Find<TextBlock>(panel, "AI writing status").Text?.Contains("connection changed", StringComparison.Ordinal) == true);
        Assert.Equal(0, posts);
    }

    [AvaloniaFact]
    public async Task Billing_failure_keeps_the_note_and_blocks_repeated_generation_in_the_panel()
    {
        var posts = 0;
        using var f = Fixture((_, _) =>
        {
            posts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            { Content = new StringContent("{\"error\":{\"type\":\"invalid_request_error\",\"message\":\"Your credit balance is too low\"}}") });
        });
        var tab = f.Shell.NewDocument("Keep me");
        f.Window.ShowAIWritingPrompt(); Pump(f.Window);
        var panel = Assert.Single(f.Window.OverlayLayer.Children.OfType<AIPromptPanel>());
        var question = Find<TextBox>(panel, "AI writing question");
        question.Text = "Rewrite";
        var send = Find<Button>(panel, "Generate AI suggestion");
        Click(send);
        await Until(() => Find<TextBlock>(panel, "AI writing status").Text?.Contains("credits", StringComparison.Ordinal) == true);
        Assert.False(send.IsEnabled);
        question.Text = "Try another question";
        Click(send);
        Assert.Equal(1, posts);
        Assert.Equal("Keep me", tab.CurrentText);
    }

    [AvaloniaFact]
    public async Task Explicit_retry_after_a_provider_failure_rechecks_the_request_and_opens_review_without_applying()
    {
        var posts = 0;
        using var f = Fixture((_, _) =>
        {
            posts++;
            return Task.FromResult(posts == 1
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                    { Content = new StringContent("{\"error\":{\"message\":\"Synthetic provider failure\"}}") }
                : Reply("Review this suggestion"));
        });
        var tab = f.Shell.NewDocument("Keep my original");
        f.Window.ShowAIWritingPrompt();
        Pump(f.Window);
        var panel = Assert.Single(f.Window.OverlayLayer.Children.OfType<AIPromptPanel>());
        Find<TextBox>(panel, "AI writing question").Text = "Rewrite clearly";
        Click(Find<Button>(panel, "Generate AI suggestion"));
        await Until(() => panel.GetVisualDescendants().OfType<Button>().Any(button => Equals(button.Content, "Retry generation") && button.IsVisible));
        Assert.Equal(1, posts);
        var failed = Assert.Single(f.Shell.Activities.Items, item => item.Phase.StartsWith("AI generation failed."));
        Assert.Equal(["Retry", "Manage connections"], failed.Actions.Select(action => action.Label));
        failed.Actions[0].Invoke();
        await WaitForReview(f);
        Assert.Equal(2, posts);
        Assert.Equal("Keep my original", tab.CurrentText);
    }

    private static DesktopFixture Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? check = null, bool seeSearchHelp = false) => new(directory =>
    {
        new AIProfileStore(directory).Save([
            new(FirstId, "Writing", "chat-completions", "https://example.test/v1/chat/completions", "model-one", DiscoveryEndpoint: "https://example.test/v1/models"),
            new(SecondId, "Research", "chat-completions", "https://example.test/v1/chat/completions", "model-two", DiscoveryEndpoint: "https://example.test/v1/models"),
        ]);
        return new AIWorkspaceService(directory, new EmptyVault(), () => new AIConnectionClient(new Handler((request, token) =>
            request.Method == HttpMethod.Get ? check?.Invoke(request, token) ?? Task.FromResult(Models())
            : seeSearchHelp ? send(request, token) : SkipSearchHelp(request, token, send))));
    });

    // AI's search help runs before the answer. Tests about the answer step script only the answer request.
    private static async Task<HttpResponseMessage> SkipSearchHelp(HttpRequestMessage request, CancellationToken token,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    {
        var body = await request.Content!.ReadAsStringAsync(token);
        return body.Contains("Choose which notes", StringComparison.Ordinal) || body.Contains("List up to 8 single words", StringComparison.Ordinal)
            || body.Contains("Check this report against", StringComparison.Ordinal) || body.Contains("Rewrite this checked answer", StringComparison.Ordinal)
            ? Reply("{}") : await send(request, token);
    }

    [AvaloniaFact]
    public async Task Changed_credentials_invalidate_validation_status_and_cached_checks()
    {
        var vault = new MutableVault(); var checks = 0;
        using var f = new DesktopFixture(directory =>
        {
            new AIProfileStore(directory).Save([new(FirstId, "Writing", "chat-completions", "https://example.test/v1/chat/completions", "model-one", DiscoveryEndpoint: "https://example.test/v1/models")]);
            return new AIWorkspaceService(directory, vault, () => new AIConnectionClient(new Handler((request, _) =>
            {
                if (request.Method == HttpMethod.Get) { checks++; return Task.FromResult(Models()); }
                return Task.FromResult(Reply("Answer"));
            })));
        });
        vault.Save(FirstId, "synthetic-secret-one");
        var workspace = f.Window.AIWorkspace;
        await workspace.SendWritingAsync(FirstId, "An explicit test question", () => true, TestContext.Current.CancellationToken);
        var profile = Assert.Single(workspace.Connections);
        Assert.NotNull(workspace.Summary(profile).TestedAt); Assert.Equal(1, checks);
        vault.Save(FirstId, "synthetic-secret-two"); workspace.Reload();
        Assert.Equal("Not checked this session", workspace.Summary(profile).State);
        Assert.Null(workspace.Summary(profile).TestedAt);
        Assert.DoesNotContain("synthetic-secret", workspace.Summary(profile).Description, StringComparison.Ordinal);
        await workspace.SendWritingAsync(FirstId, "Another explicit question", () => true, TestContext.Current.CancellationToken);
        Assert.Equal(2, checks);
    }

    private sealed class MutableVault : IAICredentialStore
    {
        private readonly Dictionary<string, string> _values = [];
        public string? Read(string profileId) => _values.GetValueOrDefault(profileId);
        public void Save(string profileId, string secret) => _values[profileId] = secret;
        public void Remove(string profileId) => _values.Remove(profileId);
    }

    private static HttpResponseMessage Reply(string text, string finishReason = "stop") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = text }, finish_reason = finishReason } } })),
    };
    private static T Find<T>(Visual root, string name) where T : Control =>
        (root is KnowledgeAssistantPanel panel ? panel.GetLogicalDescendants().OfType<T>() : root.GetVisualDescendants().OfType<T>())
        .Single(c => AutomationProperties.GetName(c) == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Pump(Window window) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> complete)
    {
        for (var i = 0; i < 150 && !complete(); i++) { await Task.Delay(20, TestContext.Current.CancellationToken); Dispatcher.UIThread.RunJobs(); }
        Assert.True(complete());
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class EmptyVault : IAICredentialStore
    {
        public string? Read(string profileId) => null;
        public void Save(string profileId, string secret) { }
        public void Remove(string profileId) { }
    }
}
