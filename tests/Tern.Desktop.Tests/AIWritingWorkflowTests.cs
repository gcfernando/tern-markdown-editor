using System.Net;
using System.Net.Http;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.Desktop.Services;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class AIWritingWorkflowTests
{
    private const string FirstId = "11111111111111111111111111111111";
    private const string SecondId = "22222222222222222222222222222222";
    [AvaloniaTheory]
    [InlineData("source")]
    [InlineData("preview")]
    [InlineData("reference")]
    public async Task Alt_space_updates_the_active_note_in_one_undoable_edit(string surface)
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
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space, KeyModifiers = KeyModifiers.Alt });
        Pump(f.Window);
        Assert.True(f.Window.IsOverlayOpen);
        var panel = Assert.Single(f.Window.OverlayLayer.Children.OfType<AIPromptPanel>());
        var question = Find<TextBox>(panel, "AI writing question");
        await Until(() => question.IsFocused);
        question.Text = "Make the draft clearer";
        if (surface == "source")
            question.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.Control });
        else Click(Find<Button>(panel, "Generate and update Markdown"));
        await Until(() => !f.Window.IsOverlayOpen);
        Assert.Equal("# Updated\r\nA clearer draft.", active.CurrentText);
        Assert.Equal("# Reference stays unchanged", reference.CurrentText);
        Assert.True(active.IsDirty);
        Assert.Null(active.FilePath);
        Assert.Contains("Make the draft clearer", Assert.Single(requests), StringComparison.Ordinal);
        Assert.Contains("# Original", requests[0], StringComparison.Ordinal);
        await f.Window.RunCommandAsync(CommandIds.Undo);
        Assert.Equal("# Original", active.CurrentText);
        await f.Window.RunCommandAsync(CommandIds.Redo);
        Assert.Equal("# Updated\r\nA clearer draft.", active.CurrentText);
    }

    [AvaloniaFact]
    public async Task A_changed_or_canceled_note_is_never_overwritten_by_a_late_reply()
    {
        var completion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var f = Fixture((_, _) => completion.Task);
        var tab = f.Shell.NewDocument("Original");
        f.Window.ShowAIWritingPrompt(); Pump(f.Window);
        var panel = Assert.Single(f.Window.OverlayLayer.Children.OfType<AIPromptPanel>());
        Find<TextBox>(panel, "AI writing question").Text = "Rewrite";
        Click(Find<Button>(panel, "Generate and update Markdown"));
        tab.ApplyEdits([new Tern.Domain.Text.TextEdit(0, 0, "My new edit: ")]);
        completion.SetResult(Reply("AI replacement"));
        await Until(() => Find<TextBlock>(panel, "AI writing status").Text?.Contains("changed", StringComparison.Ordinal) == true);
        Assert.Equal("My new edit: Original", tab.CurrentText);
        f.Window.HideOverlay();
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
        Click(Find<Button>(panel, "Generate and update Markdown"));
        f.Window.HideOverlay();
        completion.SetResult(Reply("Late replacement"));
        await Task.Delay(60, TestContext.Current.CancellationToken); Dispatcher.UIThread.RunJobs();
        Assert.Equal("Original", tab.CurrentText);
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
        f.Window.AIConnectionPicker.SelectedItem = f.Window.AIWorkspace.Profiles[1];
        Assert.Equal(SecondId, f.Window.AIWorkspace.SelectedId);
        var reopened = new AIWorkspaceService(f.Shell.Paths.SettingsDirectory, new EmptyVault());
        reopened.Reload();
        Assert.Equal(SecondId, reopened.SelectedId);
        f.Shell.NewDocument("Old text");
        f.Window.ShowAIWritingPrompt(); Pump(f.Window);
        var panel = Assert.Single(f.Window.OverlayLayer.Children.OfType<AIPromptPanel>());
        Find<TextBox>(panel, "AI writing question").Text = "Rewrite";
        Click(Find<Button>(panel, "Generate and update Markdown"));
        await Until(() => !f.Window.IsOverlayOpen);
        Assert.Equal("model-two", sentModel);
    }

    [AvaloniaFact]
    public async Task Guide_answers_arbitrary_questions_inline_without_attaching_the_note()
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
        Find<TextBox>(guide, "Search feature help").Text = "Explain photosynthesis simply";
        Click(Find<Button>(guide, "Ask question"));
        await Until(() => Find<SelectableTextBlock>(guide, "Guide AI answer").Text == "Plants use sunlight to make food.");
        Assert.Contains("photosynthesis", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE NOTE NOT INCLUDED", sent, StringComparison.Ordinal);
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
        Click(Find<Button>(panel, "Generate and update Markdown"));
        await Until(() => Find<TextBlock>(panel, "AI writing status").Text?.Contains("not changed", StringComparison.Ordinal) == true);
        Assert.Equal("Keep me", tab.CurrentText);
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
        Assert.True(question.Bounds.Height >= 240);
        question.Text = "Rewrite";
        var send = Find<Button>(panel, "Generate and update Markdown");
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
        var send = Find<Button>(panel, "Generate and update Markdown");
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
        Click(Find<Button>(panel, "Generate and update Markdown"));
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
        var send = Find<Button>(panel, "Generate and update Markdown");
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
        Click(Find<Button>(panel, "Generate and update Markdown"));
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
        var send = Find<Button>(panel, "Generate and update Markdown");
        Click(send);
        await Until(() => Find<TextBlock>(panel, "AI writing status").Text?.Contains("credits", StringComparison.Ordinal) == true);
        Assert.False(send.IsEnabled);
        question.Text = "Try another question";
        Click(send);
        Assert.Equal(1, posts);
        Assert.Equal("Keep me", tab.CurrentText);
    }

    private static DesktopFixture Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? check = null) => new(directory =>
    {
        new AIProfileStore(directory).Save([
            new(FirstId, "Writing", "chat-completions", "https://example.test/v1/chat/completions", "model-one", DiscoveryEndpoint: "https://example.test/v1/models"),
            new(SecondId, "Research", "chat-completions", "https://example.test/v1/chat/completions", "model-two", DiscoveryEndpoint: "https://example.test/v1/models"),
        ]);
        return new AIWorkspaceService(directory, new EmptyVault(), () => new AIConnectionClient(new Handler((request, token) =>
            request.Method == HttpMethod.Get ? check?.Invoke(request, token) ?? Task.FromResult(Models()) : send(request, token))));
    });

    private static HttpResponseMessage Reply(string text, string finishReason = "stop") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = text }, finish_reason = finishReason } } })),
    };
    private static T Find<T>(Visual root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetName(c) == name);
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
