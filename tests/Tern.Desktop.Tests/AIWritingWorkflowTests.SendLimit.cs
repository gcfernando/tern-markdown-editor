using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Tern.App.Commands;
using Tern.App;
using Tern.App.Knowledge;
using Tern.Desktop.Services;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

/// <summary>Every AI request is sized by the selected connection's saved value.</summary>
public sealed partial class AIWritingWorkflowTests
{
    private const string ChatEndpoint = "https://example.test/v1/chat/completions", ModelsEndpoint = "https://example.test/v1/models";
    private static string ValidationText => AIRequest.ConnectionTest(AIConnectionClient.ValidationMessage).Text;
    private static bool HasPurpose(string text, AIPurpose purpose) => text.StartsWith("TERN INSTRUCTIONS (" + purpose + ")\n", StringComparison.Ordinal);

    private static AIWorkspaceService TwoLimits(string folder, Func<string, HttpResponseMessage>? reply = null, List<string>? sent = null)
    {
        new AIProfileStore(folder).Save([
            new(FirstId, "Server A", "chat-completions", ChatEndpoint, "model-one", DiscoveryEndpoint: ModelsEndpoint, SendCharacters: 50_000),
            new(SecondId, "Server B", "chat-completions", ChatEndpoint, "model-two", DiscoveryEndpoint: ModelsEndpoint, SendCharacters: 80_000),
        ]);
        var service = new AIWorkspaceService(folder, new CountingVault(), () => new AIConnectionClient(new Handler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get) return Models();
            var text = SentText(await request.Content!.ReadAsStringAsync(token));
            sent?.Add(text);
            return reply?.Invoke(text) ?? Reply("Answer");
        })));
        service.Reload();
        return service;
    }

    private static string SentText(string body)
    {
        using var json = JsonDocument.Parse(body);
        return AIWire.Text(json.RootElement);
    }

    private static (AIWorkspaceService Service, IntegrationProfileStore Store) TwoCopilotLimits(string folder, List<string> sent, Func<string, string>? reply = null)
    {
        var executable = Path.Combine(folder, "native", "copilot.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "Synthetic fixture; the injected process runner never starts this file.");
        var vault = new SendLimitVault();
        var store = new IntegrationProfileStore(folder, vault);
        store.Save(new(FirstId, "copilot", "Server A", executable, SendCharacters: 50_000), "synthetic-copilot-key");
        store.Save(new(SecondId, "copilot", "Server B", executable, SendCharacters: 80_000), "synthetic-copilot-key");
        var service = new AIWorkspaceService(folder, vault, createCopilotClient: () => new CopilotTextClient((start, input, _) =>
        {
            Assert.Equal(executable, start.FileName);
            Assert.True(start.RedirectStandardInput);
            Assert.DoesNotContain(input, start.ArgumentList);
            sent.Add(input);
            return Task.FromResult(new CopilotProcessResult(0, reply?.Invoke(input) ?? "Answer"));
        }));
        service.Reload();
        return (service, store);
    }

    private sealed class SendLimitVault : IAICredentialStore
    {
        private readonly Dictionary<string, string> _values = [];
        public string? Read(string id) => _values.GetValueOrDefault(id);
        public void Save(string id, string secret) => _values[id] = secret;
        public void Remove(string id) => _values.Remove(id);
    }

    private sealed class SendLimitProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    [AvaloniaFact]
    public async Task Copilot_writing_keeps_note_email_addresses_and_still_blocks_instruction_mentions()
    {
        var sent = new List<string>();
        using var f = new DesktopFixture(directory => TwoCopilotLimits(directory, sent,
            _ => "Contact qa＠example.test. Improved.").Service);
        var note = f.Shell.NewDocument("Contact qa@example.test.");
        Pump(f.Window);
        f.Window.ShowAIWritingPrompt(); Pump(f.Window);
        var panel = Assert.Single(f.Window.OverlayLayer.Children.OfType<AIPromptPanel>());
        var instruction = Find<TextBox>(panel, "AI writing question");
        var send = Find<Button>(panel, "Generate AI suggestion");
        instruction.Text = "Ask @someone"; Pump(f.Window);
        Assert.False(send.IsEnabled);
        Assert.Empty(sent);

        instruction.Text = "Proofread"; Pump(f.Window);
        Assert.True(send.IsEnabled);
        Click(send);
        var review = await WaitForReview(f);
        var message = Assert.Single(sent.Where(text => text != ValidationText));
        Assert.DoesNotContain('@', message);
        Assert.Contains("qa＠example.test", message);
        Click(ButtonNamed(review, "Apply changes"));
        Assert.Equal("Contact qa@example.test. Improved.", note.CurrentText);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Lowering_the_saved_limit_during_progress_never_sends_an_oversized_request(bool copilot, bool finalCallback)
    {
        using var folder = new ReviewFolder();
        var sent = new List<string>();
        AIWorkspaceService service;
        Action lowerLimit;
        if (copilot)
        {
            var fixture = TwoCopilotLimits(folder.Path, sent);
            service = fixture.Service;
            lowerLimit = () => fixture.Store.Save(fixture.Store.Load().Single(profile => profile.Id == SecondId) with { SendCharacters = 50_000 }, "synthetic-copilot-key");
        }
        else
        {
            service = TwoLimits(folder.Path, sent: sent);
            var store = new AIProfileStore(folder.Path);
            lowerLimit = () => store.Save(store.Load().Select(profile => profile.Id == SecondId ? profile with { SendCharacters = 50_000 } : profile).ToArray());
        }
        var changed = false;
        var progress = new SendLimitProgress(phase =>
        {
            if (phase.StartsWith(finalCallback ? "Contacting" : "Validating", StringComparison.Ordinal))
            {
                lowerLimit();
                changed = true;
            }
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendWritingAsync(SecondId,
            new string('x', 60_000), () => true, TestContext.Current.CancellationToken, progress));

        Assert.True(changed);
        Assert.Equal(AIWorkspaceService.TooMuchText(AIRequest.CurrentText(new string('x', 60_000)).CharacterCount, "Server B", 50_000), error.Message);
        Assert.Empty(sent.Where(text => text != ValidationText));
        Assert.Equal(copilot && finalCallback ? 1 : 0, sent.Count);
    }

    [Theory]
    [InlineData(50_000, false)]
    [InlineData(80_000, false)]
    [InlineData(50_000, true)]
    [InlineData(80_000, true)]
    public async Task Central_Copilot_transport_uses_the_saved_value_and_standard_input(int limit, bool writing)
    {
        using var folder = new ReviewFolder();
        var sent = new List<string>();
        var (service, _) = TwoCopilotLimits(folder.Path, sent);
        var id = limit == 50_000 ? FirstId : SecondId;
        var text = new string('é', limit - AIRequest.CurrentText("").CharacterCount);
        Task<string> Send(string message) => writing
            ? service.SendWritingAsync(id, message, () => true, TestContext.Current.CancellationToken)
            : service.SendAsync(id, message, TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Send(text + "x"));
        Assert.Contains($"accepts {AIConnectionRules.FormatSendCharacters(limit)}", error.Message);
        Assert.Empty(sent);
        Assert.Equal("Answer", await Send(text));
        Assert.Equal(AIRequest.CurrentText(text).Text, Assert.Single(sent.Where(message => message != ValidationText)));
        Assert.All(sent, message => Assert.InRange(message.Length, 1, limit));
        Assert.Equal(writing ? 2 : 1, sent.Count);
    }

    [Theory]
    [InlineData('x')]
    [InlineData('é')]
    [InlineData('漢')]
    public async Task Two_connections_each_use_their_own_saved_value(char character)
    {
        using var folder = new ReviewFolder();
        var sent = new List<string>();
        var service = TwoLimits(folder.Path, sent: sent);
        var text = new string(character, 60_000);

        Assert.Equal(50_000, service.SendCharactersFor(FirstId));
        Assert.Equal(80_000, service.SendCharactersFor(SecondId));
        Assert.Equal(AIWorkspaceService.TooMuchText(AIRequest.CurrentText(text).CharacterCount, "Server A", 50_000), service.WritingValidationError(FirstId, text));
        Assert.Null(service.WritingValidationError(SecondId, text));
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendWritingAsync(FirstId, text, () => true, CancellationToken.None));
        Assert.Contains("Server A accepts 50,000", blocked.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync(FirstId, text, CancellationToken.None));
        Assert.Empty(sent);

        Assert.Equal("Answer", await service.SendWritingAsync(SecondId, text, () => true, CancellationToken.None));
        Assert.Equal(AIRequest.CurrentText(text).CharacterCount, Assert.Single(sent).Length);
    }

    [Fact]
    public void Copilot_uses_its_saved_value_before_anything_starts()
    {
        using var folder = new ReviewFolder();
        var vault = new CountingVault();
        var id = Guid.NewGuid().ToString("N");
        new IntegrationProfileStore(folder.Path, vault).Save(new IntegrationProfile(id, "copilot", "Copilot", Path.Combine(folder.Path, "copilot.exe"), SendCharacters: 50_000), "");
        var service = new AIWorkspaceService(folder.Path, vault, createCopilotClient: () => throw new InvalidOperationException("Nothing may start."));
        service.Reload();
        Assert.Equal(AIWorkspaceService.TooMuchText(AIRequest.CurrentText(new string('x', 50_001)).CharacterCount, "Copilot", 50_000),
            service.WritingValidationError(id, new string('x', 50_001)));
    }

    [Fact]
    public void No_ai_feature_can_send_around_the_central_limit()
    {
        var src = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "..", "src"));
        var files = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories).Where(path => !Regex.IsMatch(path, @"[\\/](bin|obj)[\\/]")).ToArray();
        Assert.NotEmpty(files);
        var direct = new Regex(@"\b(?:client|_client|copilotClient)\.SendAsync\(|CreateCopilot\(\)\.SendAsync\(", RegexOptions.CultureInvariant);
        var allowed = new[] { "AIWorkspaceService.cs", "AIConnectionClient.cs", "AIConnectionsWindow.cs", "CopilotConnectionWindow.cs" };
        var bypass = files.Where(path => !allowed.Contains(Path.GetFileName(path)) && direct.IsMatch(File.ReadAllText(path))).Select(Path.GetFileName).ToArray();
        Assert.Empty(bypass);
        foreach (var window in new[] { "AIConnectionsWindow.cs", "CopilotConnectionWindow.cs" })
            Assert.Contains("AIWorkspaceService.TooMuchText(", File.ReadAllText(Assert.Single(files, path => Path.GetFileName(path) == window)));
        var oldLimits = new Regex(@"MaximumMessageCharacters|MaximumPromptCharacters|KnowledgeContextLimit|KnowledgeSourceLimit|ContextCharacters|KnowledgeReportBuilder\.MaximumNoteCharacters|MaximumParts", RegexOptions.CultureInvariant);
        Assert.Empty(files.Where(path => oldLimits.IsMatch(File.ReadAllText(path))).Select(Path.GetFileName));
        Assert.Equal(AIConnectionRules.DefaultSendCharacters, KnowledgeLimits.DefaultSendCharacters);
        Assert.Equal(AIConnectionRules.MinimumSendCharacters, KnowledgeLimits.MinimumSendCharacters);
        Assert.Equal(AIConnectionRules.MaximumSendCharacters, KnowledgeLimits.MaximumSendCharacters);
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Help_questions_fit_the_connection_value(bool copilot)
    {
        var registry = CommandRegistry.CreateDefault();
        var message = GuideQuestionPrompt.Build(registry, "How do I export a PDF?", copilot, AIConnectionRules.MinimumSendCharacters);
        Assert.InRange(message.Length, 1, AIConnectionRules.MinimumSendCharacters);
        Assert.EndsWith("How do I export a PDF?", message);
    }

    [Theory]
    [InlineData(413, "{}", true)]
    [InlineData(400, """{"error":{"code":"context_length_exceeded","message":"maximum context length"}}""", true)]
    [InlineData(400, """{"error":{"type":"invalid_request_error","message":"prompt is too long: 300000 tokens"}}""", true)]
    [InlineData(429, """{"error":{"message":"Request too large for model"}}""", true)]
    [InlineData(429, """{"error":{"message":"Slow down"}}""", false)]
    [InlineData(400, """{"error":{"message":"Unknown field"}}""", false)]
    [InlineData(401, """{"error":{"message":"too long ago"}}""", false)]
    public void Size_rejections_are_recognized_and_never_become_a_wait(int status, string body, bool expected)
    {
        var error = AIServiceException.From(status, body, "secret", retryAt: DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(expected, AIWorkspaceService.IsSizeRejection(error));
        if (expected) Assert.Null(error.RetryAt);
    }

    [AvaloniaTheory]
    [InlineData(50_000)]
    [InlineData(80_000)]
    public async Task Explore_reads_and_shares_a_large_note_within_the_connection_value(int limit)
    {
        var sent = new List<string>();
        const string answer = """{"answer":"- We launch on 14 April [1].","sources":[{"id":1,"reason":"Launch"}]}""";
        static HttpResponseMessage LiteralFactSelectors(string text)
        {
            const string marker = "CURRENT TASK AND DATA\n";
            var start = text.LastIndexOf(marker, StringComparison.Ordinal);
            Assert.True(start >= 0);
            using var data = JsonDocument.Parse(text[(start + marker.Length)..]);
            var part = data.RootElement.GetProperty("sourcePart");
            var sourceId = part.GetProperty("sourceId").GetInt32();
            var selected = part.GetProperty("lines").EnumerateArray()
                .Where(line => line.GetProperty("text").GetString()!.Contains("We decided the project launch is on 14 April.", StringComparison.Ordinal))
                .Select(line => new { sourceId, startLine = line.GetProperty("line").GetInt32(), endLine = line.GetProperty("line").GetInt32() }).ToArray();
            return Reply(JsonSerializer.Serialize(new { passages = selected }));
        }
        using var f = new DesktopFixture(directory => TwoLimits(directory,
            text => HasPurpose(text, AIPurpose.ExtractFacts) || HasPurpose(text, AIPurpose.CombineFacts) ? LiteralFactSelectors(text)
                : text.Contains("Answer only from evidence", StringComparison.Ordinal) ? Reply(answer)
                : HasPurpose(text, AIPurpose.Report) ? Reply(ReportJson)
                : HasPurpose(text, AIPurpose.ReportCheck) ? Reply("""{"unsupported":[]}""")
                : HasPurpose(text, AIPurpose.Email) ? Reply("""{"subject":"Launch update","body":"We launch on 14 April."}""")
                : Reply("""{"terms":[],"questions":[]}"""), sent));
        f.Window.AIWorkspace.Select(limit == 50_000 ? FirstId : SecondId);
        var body = string.Concat(Enumerable.Repeat("The project launch plan has one more useful detail here.\n", 2_200));
        var path = await f.WriteAsync("notes/launch-plan.md", "# Launch plan\n\n" + body + "We decided the project launch is on 14 April.");
        Assert.True(await f.Shell.PromoteFolderAsync(Path.GetDirectoryName(path)!));
        f.Shell.Settings.KnowledgeAllowRemote = true;
        f.Window.ShowKnowledgeAssistant();
        var panel = Assert.Single(KnowledgePanels(f.Window));
        Pump(f.Window);
        Find<TextBox>(panel, "Your question").Text = "Summarize the project launch plan";
        Click(Find<Button>(panel, "Find my notes"));
        var send = Find<Button>(panel, "Get my answer");
        var cancel = Find<Button>(panel, "Cancel");
        await Until(() => send.IsEffectivelyVisible && !cancel.IsEnabled);
        Assert.Contains(sent, text => text.Contains("List up to 8 single words", StringComparison.Ordinal));
        Assert.Contains(sent, text => HasPurpose(text, AIPurpose.NoteSelection));
        Assert.All(sent, text => Assert.InRange(text.Length, 1, limit));
        sent.Clear();

        Click(send);
        await Until(() => Find<Button>(panel, "Share this answer").IsEffectivelyVisible && !cancel.IsEnabled);

        Assert.All(sent, text => Assert.True(text.Length <= limit, $"A request of {text.Length} went over {limit}."));
        Assert.True(sent.Count(text => HasPurpose(text, AIPurpose.ExtractFacts)) >= 2);
        Assert.Contains(sent, text => text.Contains("14 April", StringComparison.Ordinal) && HasPurpose(text, AIPurpose.ExtractFacts));
        Assert.Contains("AI read 100% of your 1 ticked note.", Find<TextBlock>(panel, "Explore status").Text);

        var pdf = Path.Combine(f.AppData, "send-limit-report.pdf");
        var preparedCalls = sent.Count;
        var preparedReads = panel.NoteReadCount;
        var preparedRenders = panel.ContentRenderCount;
        panel.PickPdfPath = _ => Task.FromResult<string?>(pdf);
        await PrepareReport(panel, "Save as PDF");
        Find<CheckBox>(panel, "Report reviewed").IsChecked = true;
        Click(Find<Button>(panel, "Finish report"));
        await Until(() => File.Exists(pdf) && !cancel.IsEnabled);
        string? email = null;
        panel.CopyText = text => { email = text; return Task.CompletedTask; };
        Find<RadioButton>(panel, "Copy for email").IsChecked = true;
        Click(Find<Button>(panel, "Share this answer"));
        await Until(() => Find<Button>(panel, "Finish report").IsVisible && !cancel.IsEnabled);
        Find<CheckBox>(panel, "Report reviewed").IsChecked = true;
        Click(Find<Button>(panel, "Finish report"));
        await Until(() => email is not null && !cancel.IsEnabled);
        Assert.Contains(sent, text => HasPurpose(text, AIPurpose.Report));
        Assert.Contains(sent, text => HasPurpose(text, AIPurpose.ReportCheck));
        Assert.DoesNotContain(sent, text => HasPurpose(text, AIPurpose.Email));
        Assert.Equal(preparedCalls, sent.Count);
        Assert.Equal(preparedReads, panel.NoteReadCount);
        Assert.Equal(preparedRenders, panel.ContentRenderCount);
        Assert.All(sent, text => Assert.InRange(text.Length, 1, limit));
    }

    [AvaloniaTheory]
    [InlineData(50_000, "Entire document")]
    [InlineData(80_000, "Entire document")]
    [InlineData(50_000, "Selected text")]
    [InlineData(80_000, "Selected text")]
    [InlineData(50_000, "Instruction only")]
    [InlineData(80_000, "Instruction only")]
    public async Task Every_writing_scope_uses_the_selected_limit_without_splitting(int limit, string scope)
    {
        var sent = new List<string>();
        using var f = new DesktopFixture(directory => TwoLimits(directory, sent: sent));
        f.Window.AIWorkspace.Select(limit == 50_000 ? FirstId : SecondId);
        var text = new string('é', limit - 4_000);
        var note = f.Shell.NewDocument(scope == "Instruction only" ? "Keep the note" : text);
        Pump(f.Window);
        if (scope == "Selected text") f.Window.Editor.Select(0, text.Length);
        f.Window.ShowAIWritingPrompt(preferredScope: scope); Pump(f.Window);
        var panel = Assert.Single(f.Window.OverlayLayer.Children.OfType<AIPromptPanel>());
        Find<TextBox>(panel, "AI writing question").Text = scope == "Instruction only" ? "Write " + text : "Improve";
        Pump(f.Window);
        var before = note.CurrentText;
        var send = Find<Button>(panel, "Generate AI suggestion");
        Assert.True(send.IsEnabled);
        Click(send);
        await WaitForReview(f);
        Assert.Single(sent.Where(message => message != ValidationText));
        Assert.All(sent, message => Assert.InRange(message.Length, 1, limit));
        Assert.Contains(sent, message => message.Contains(text, StringComparison.Ordinal));
        Assert.Equal(before, note.CurrentText);
    }

    [AvaloniaTheory]
    [InlineData(50_000)]
    [InlineData(80_000)]
    public async Task Help_and_Learn_questions_use_the_selected_saved_value_and_warn_before_sending(int limit)
    {
        var sent = new List<string>();
        using var f = new DesktopFixture(directory => TwoLimits(directory, sent: sent));
        var service = f.Window.AIWorkspace;
        using var tutor = new HelpTutorView(service, f.Shell.Registry, () => Task.CompletedTask, null, null, _ => { }, null);
        var selected = service.Connections.Single(connection => connection.SendCharacters == limit);
        tutor.Connections.SelectedItem = selected;
        tutor.Question.Text = "How do I export a PDF? " + new string('é', limit - 4_000);
        Pump(f.Window);
        Assert.True(tutor.SendButton.IsEnabled);
        Click(tutor.SendButton);
        await Until(() => sent.Count > 0 && tutor.SendButton.IsEnabled);
        Assert.All(sent, text => Assert.InRange(text.Length, limit - 4_000, limit));
        var calls = sent.Count;
        tutor.Question.Text = "How do I export a PDF? " + new string('é', limit);
        Pump(f.Window);
        Assert.False(tutor.SendButton.IsEnabled);
        Assert.Contains("too long", ToolTip.GetTip(tutor.SendButton)?.ToString());
        Assert.Equal(calls, sent.Count);
    }
}
