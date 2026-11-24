using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Tern.Desktop.Services;

namespace Tern.Desktop.Tests;

/// <summary>Provider behavior that decides whether a large request was really read.</summary>
public sealed class AIProviderLimitTests
{
    private sealed class Stub(Func<string, int, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            return reply(body, Bodies.Count);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData(100, 4096)]
    [InlineData(256_000, 262_144)]
    public void Ollama_requests_a_context_large_enough_for_the_text(int characters, int expected)
    {
        using var json = JsonDocument.Parse(AIProviders.Get("ollama-chat").CreateRequest("model", new string('x', characters)));
        Assert.Equal(expected, json.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
    }

    [Theory]
    [InlineData("""{"message":{"content":"Done"},"prompt_eval_count":4096}""", true)]
    [InlineData("""{"message":{"content":"Done"},"prompt_eval_count":60000}""", false)]
    [InlineData("""{"message":{"content":"Done"},"prompt_eval_count":3}""", false)] // A cached prompt reports only its new tokens.
    [InlineData("""{"message":{"content":"Done"}}""", false)]
    public async Task A_local_model_that_cut_the_prompt_is_a_size_rejection(string reply, bool cut)
    {
        using var client = new AIConnectionClient(new Stub((_, _) => Json(HttpStatusCode.OK, reply)));
        var send = client.SendAsync(new Uri("http://localhost:11434/api/chat"), "model", "", new string('x', 200_000), AIProviders.Get("ollama-chat"), CancellationToken.None);
        if (cut) Assert.True(AIWorkspaceService.IsSizeRejection(await Assert.ThrowsAsync<AIServiceException>(() => send)));
        else Assert.Equal("Done", await send);
    }

    [Fact]
    public async Task An_older_Claude_model_is_retried_with_its_own_reply_cap_and_not_treated_as_too_much_text()
    {
        var model = "older-model-" + Guid.NewGuid().ToString("N");
        var stub = new Stub((body, _) => body.Contains("\"max_tokens\":16000", StringComparison.Ordinal)
            ? Json(HttpStatusCode.BadRequest, """{"type":"error","error":{"type":"invalid_request_error","message":"max_tokens: 16000 > 4096, which is the maximum allowed number of output tokens"}}""")
            : Json(HttpStatusCode.OK, """{"content":[{"type":"text","text":"Answer"}]}"""));
        using var client = new AIConnectionClient(stub);
        var provider = AIProviders.Get("anthropic-messages");
        var endpoint = new Uri("https://api.anthropic.test/v1/messages");

        Assert.Equal("Answer", await client.SendAsync(endpoint, model, "key", "Question", provider, CancellationToken.None));
        Assert.Equal(2, stub.Bodies.Count);
        Assert.Contains("\"max_tokens\":4096", stub.Bodies[1]);
        Assert.Equal("Answer", await client.SendAsync(endpoint, model, "key", "Question", provider, CancellationToken.None));
        Assert.Equal(3, stub.Bodies.Count); // The cap is remembered, so later requests do not fail first.
    }

    [Theory]
    [InlineData(400, """{"error":{"code":"context_length_exceeded","message":"This model's maximum context length is 8192 tokens"}}""", true)]
    [InlineData(400, """{"error":{"message":"This model's maximum context length is 65536 tokens"}}""", true)]
    [InlineData(400, """{"type":"error","error":{"type":"invalid_request_error","message":"prompt is too long: 210000 tokens > 200000 maximum"}}""", true)]
    [InlineData(413, """{"error":{"type":"request_too_large","message":"Request exceeds the maximum size"}}""", true)]
    [InlineData(429, """{"error":{"code":"rate_limit_exceeded","message":"Request too large for gpt on tokens per min"}}""", true)]
    [InlineData(429, """{"error":{"code":"rate_limit_exceeded","message":"Rate limit reached"}}""", false)]
    [InlineData(400, """{"error":{"message":"max_tokens: 16000 > 4096"}}""", false)]
    [InlineData(400, """{"error":{"message":"The model name is too long"}}""", false)]
    [InlineData(404, """{"error":{"code":"model_not_found","message":"too large"}}""", false)]
    [InlineData(402, """{"error":{"message":"Insufficient balance"}}""", false)]
    public void Only_real_size_errors_make_explore_use_smaller_parts(int status, string body, bool expected) =>
        Assert.Equal(expected, AIWorkspaceService.IsSizeRejection(AIServiceException.From(status, body, "secret")));

    [Theory]
    [InlineData("HTTP 413 Payload Too Large", true)]
    [InlineData("status code: 413", true)]
    [InlineData("prompt is too long", true)]
    [InlineData("at run (index.js:413:12)", false)]
    public void Copilot_size_errors_ignore_stack_line_numbers(string diagnostics, bool expected) =>
        Assert.Equal(expected, CopilotTextClient.IsTooMuchText(diagnostics));

    [Fact]
    public void Accented_text_is_not_inflated_by_the_write_request()
    {
        var text = string.Concat(Enumerable.Repeat("é", 50_000));
        Assert.InRange(AIWorkspaceService.WritingMessage("Improve", text).Length, 50_000, 51_000);
    }

    [Fact]
    public void Size_messages_use_the_right_levels()
    {
        Assert.Equal(Tern.Desktop.Views.MessageTone.Error, Tern.Desktop.Views.MessageTones.Classify(AIServiceException.TooMuchTextMessage));
        Assert.Equal(Tern.Desktop.Views.MessageTone.Warning, Tern.Desktop.Views.MessageTones.Classify(AIWorkspaceService.TooMuchText(120_000, "Server A", 100_000)));
    }

    [Fact]
    public async Task A_long_prompt_cannot_deadlock_when_the_cli_writes_output_first()
    {
        var shell = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        Assert.True(File.Exists(shell));
        // Writes more than a pipe buffer before reading any input, then reads it all.
        var script = Path.Combine(Path.GetTempPath(), "tern-pipe-" + Guid.NewGuid().ToString("N") + ".cmd");
        await File.WriteAllTextAsync(script, "@echo off\r\nfor /L %%i in (1,1,2500) do echo early output line that fills the pipe buffer quickly\r\nmore > nul\r\necho finished\r\n");
        try
        {
            var start = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c"); start.ArgumentList.Add(script);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result = await CopilotTextClient.RunProcessAsync(start, string.Concat(Enumerable.Repeat("A line of note text.\n", 12_000)), deadline.Token);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("finished", result.Output);
        }
        finally { File.Delete(script); }
    }
}
