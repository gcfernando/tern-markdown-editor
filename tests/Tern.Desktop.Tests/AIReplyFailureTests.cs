using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Tern.Desktop.Services;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class AIReplyFailureTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";
    private static readonly Uri Endpoint = new("https://example.test/v1/chat/completions");

    private sealed class Installed : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("tern-copilot-reply").FullName;
        public string Path { get; }
        public Installed() { Path = System.IO.Path.Combine(_root, "copilot.exe"); File.WriteAllBytes(Path, [0]); }
        public void Dispose() => Directory.Delete(_root, true);
    }

    private sealed class Reply(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed class Vault : IAICredentialStore
    {
        public string? Read(string id) => "test-key";
        public void Save(string id, string secret) { }
        public void Remove(string id) { }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Task<string> Copilot(CopilotProcessResult result, string path) =>
        new CopilotTextClient((_, _) => Task.FromResult(result)).SendAsync(path, "auto", "test-key", "Reviewed text", TestContext.Current.CancellationToken);

    private static Task<string> Send(string body, string provider = "chat-completions")
    {
        var client = new AIConnectionClient(new Reply(_ => Json(body)));
        return client.SendAsync(Endpoint, "model", "test-key", "Question", AIProviders.Get(provider), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Internal_events_are_never_returned_as_the_answer()
    {
        const string events = "{\"type\":\"session.started\",\"data\":{}}\n{\"type\":\"result\",\"exitCode\":0}\n";
        Assert.Equal("", CopilotTextClient.ExtractReply(events));
        Assert.Equal("{\"answer\":\"Plain\"}", CopilotTextClient.ExtractReply("{\"answer\":\"Plain\"}"));
        Assert.Equal("Plain text reply", CopilotTextClient.ExtractReply("Plain text reply"));
    }

    [Theory]
    [InlineData("HTTP 401 Unauthorized", "sign-in")]
    [InlineData("Error: token has expired", "sign-in")]
    [InlineData("HTTP 429 Too Many Requests", "Wait a minute")]
    [InlineData("rate limit exceeded", "Wait a minute")]
    [InlineData("getaddrinfo ENOTFOUND api.example.test", "internet connection")]
    [InlineData("HTTP 503", "few minutes")]
    [InlineData("402 Payment Required", "credits")]
    public async Task Copilot_failures_say_what_happened_and_what_to_do(string diagnostic, string expected)
    {
        using var installed = new Installed();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Copilot(new(1, "", diagnostic), installed.Path));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostic, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_reported_only_as_an_output_event_is_still_explained()
    {
        using var installed = new Installed();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Copilot(new(0, "{\"type\":\"session.error\",\"data\":{\"message\":\"HTTP 401 Unauthorized\"}}\n"), installed.Path));
        Assert.Contains("sign-in", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Words_in_a_note_are_not_mistaken_for_a_failure_reason()
    {
        using var installed = new Installed();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Copilot(new(3, "{\"type\":\"user.message\",\"data\":{\"content\":\"our token has expired and billing quota\"}}\n"), installed.Path));
        Assert.Contains("code 3", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_copilot_timeout_is_a_plain_failure_but_cancel_stays_a_cancel()
    {
        using var installed = new Installed();
        var slow = new CopilotTextClient(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new CopilotProcessResult(0, ""); })
        { ReplyLimit = TimeSpan.FromMilliseconds(50) };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => slow.SendAsync(installed.Path, "auto", "test-key", "Reviewed text", TestContext.Current.CancellationToken));
        Assert.Contains("too long", error.Message, StringComparison.Ordinal);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancel.CancelAfter(50);
        var waiting = new CopilotTextClient(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new CopilotProcessResult(0, ""); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.SendAsync(installed.Path, "auto", "test-key", "Reviewed text", cancel.Token));
    }

    [Fact]
    public async Task A_complete_large_copilot_reply_preserves_the_final_content()
    {
        using var installed = new Installed();
        var content = new string('x', 600_000) + "FINAL_REPLY_CANARY";
        var reply = await Copilot(new(0, content), installed.Path);
        Assert.Equal(content, reply);
    }

    [Fact]
    public async Task Cancelling_while_the_prompt_is_written_stops_the_process()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var start = new ProcessStartInfo(Path.Combine(system, "ping.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-n", "30", "127.0.0.1" }) start.ArgumentList.Add(argument);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        Process? child = null;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                CopilotTextClient.RunProcessAsync(start, "Reviewed text", cancelled.Token, process => child = Process.GetProcessById(process.Id)));
            Assert.NotNull(child);
            Assert.True(child.WaitForExit(5000), "The process was left running after cancel.");
        }
        finally
        {
            if (child is { HasExited: false }) child.Kill();
            child?.Dispose();
        }
    }

    [Theory]
    [InlineData("<html>Sign in to the proxy</html>")]
    [InlineData("[1,2,3]")]
    public async Task A_reply_that_is_not_a_json_object_gets_a_plain_message(string body)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Send(body));
        Assert.Contains("could not read", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("chat-completions", """{"choices":[{"finish_reason":7,"message":{"content":"Hello"}}]}""", "Hello")]
    [InlineData("chat-completions", """{"choices":[{"message":{"content":null}}]}""", "")]
    [InlineData("ollama-chat", """{"done_reason":1,"message":{"content":"Hello"}}""", "Hello")]
    [InlineData("anthropic-messages", """{"stop_reason":1,"content":[{"type":"text","text":"Hello"}]}""", "Hello")]
    [InlineData("anthropic-messages", """{"stop_reason":"end_turn","content":[]}""", "")]
    public async Task Odd_but_valid_replies_do_not_raise_technical_errors(string provider, string body, string expected) =>
        Assert.Equal(expected, await Send(body, provider));

    [Theory]
    [InlineData("""{"choices":["text"]}""")]
    [InlineData("""{"choices":[{"message":"text"}]}""")]
    public async Task A_wrong_reply_shape_names_the_connection_problem(string body)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Send(body));
        Assert.Contains("does not contain", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(301, "")]
    [InlineData(400, "")]
    [InlineData(401, "")]
    [InlineData(402, "")]
    [InlineData(403, "")]
    [InlineData(404, "")]
    [InlineData(408, "")]
    [InlineData(418, "")]
    [InlineData(500, "")]
    [InlineData(503, "")]
    [InlineData(504, "")]
    [InlineData(429, "project_spend_limit_exceeded")]
    [InlineData(429, "organization_usage_limit_exceeded")]
    [InlineData(429, "insufficient_quota")]
    public void Every_service_failure_shows_the_error_level_on_its_own(int status, string code)
    {
        var body = code.Length == 0 ? "" : "{\"error\":{\"code\":\"" + code + "\"}}";
        Assert.Equal(MessageTone.Error, MessageTones.Classify(AIServiceException.From(status, body, "").Message));
    }

    [Theory]
    [InlineData("chat-completions", """{"choices":[{"finish_reason":"length","message":{"content":"x"}}]}""")]
    [InlineData("chat-completions", """{"unexpected":true}""")]
    [InlineData("ollama-chat", """{"done_reason":"length","message":{"content":"x"}}""")]
    [InlineData("ollama-chat", """{"unexpected":true}""")]
    [InlineData("anthropic-messages", """{"stop_reason":"max_tokens","content":[]}""")]
    [InlineData("anthropic-messages", """{"unexpected":true}""")]
    public async Task A_cut_short_or_unusable_reply_shows_the_error_level(string provider, string body)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Send(body, provider));
        Assert.Equal(MessageTone.Error, MessageTones.Classify(error.Message));
    }

    [Fact]
    public async Task An_unsupported_model_list_shows_the_error_level()
    {
        var client = new AIConnectionClient(new Reply(_ => Json("{\"unexpected\":true}")));
        var input = new AIConnectionInputs("compatible", "chat-completions", Endpoint.ToString(), "model", "test-key", "https://example.test/v1/models", "", null);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CheckAsync(input, TestContext.Current.CancellationToken));
        Assert.Equal(MessageTone.Error, MessageTones.Classify(error.Message));
    }

    [Fact]
    public async Task A_wait_request_during_the_connection_check_is_remembered()
    {
        var folder = Directory.CreateTempSubdirectory("tern-ai-wait").FullName;
        try
        {
            new AIProfileStore(folder).Save([new(Id, "Writing", "chat-completions", Endpoint.ToString(), "model", DiscoveryEndpoint: "https://example.test/v1/models")]);
            var calls = 0;
            var service = new AIWorkspaceService(folder, new Vault(), () => new AIConnectionClient(new Reply(_ =>
            {
                calls++;
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
                return limited;
            })));
            service.Reload();
            await Assert.ThrowsAsync<AIServiceException>(() => service.SendWritingAsync(Id, "Question", () => true, TestContext.Current.CancellationToken));
            Assert.True(service.IsWaitingToRetry(Id));
            var wait = service.WritingValidationError(Id, "Question");
            Assert.StartsWith("Wait until", wait, StringComparison.Ordinal);
            Assert.DoesNotContain("UTC", wait, StringComparison.Ordinal);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendWritingAsync(Id, "Question", () => true, TestContext.Current.CancellationToken));
            Assert.Equal(1, calls);
        }
        finally { Directory.Delete(folder, true); }
    }
}
