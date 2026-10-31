using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Tern.Desktop.Services;

namespace Tern.Desktop.Tests;

public sealed class ClaudeConnectionTests
{
    [Fact]
    public async Task Claude_uses_messages_protocol_and_only_reviewed_text()
    {
        using var client = new AIConnectionClient(new Handler(async (request, ct) =>
        {
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("x-api-key")));
            Assert.Equal("2023-06-01", Assert.Single(request.Headers.GetValues("anthropic-version")));
            Assert.Null(request.Headers.Authorization);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(1024, json.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.False(json.RootElement.TryGetProperty("tools", out _));
            var message = Assert.Single(json.RootElement.GetProperty("messages").EnumerateArray());
            Assert.Equal("Reviewed café", message.GetProperty("content").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"content\":[{\"type\":\"text\",\"text\":\"First\"},{\"type\":\"text\",\"text\":\"Second\"}]}", Encoding.UTF8, "application/json") };
        }));
        Assert.Equal("First\nSecond", await client.SendAsync(new Uri("https://api.anthropic.com/v1/messages"), "claude-model", "test-key", "Reviewed café", AIProviders.Get("anthropic-messages"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Claude_rejects_nontext_responses_without_leaking_provider_payload()
    {
        using var response = JsonDocument.Parse("{\"content\":[{\"type\":\"tool_use\",\"input\":{\"secret\":\"private\"}}]}");
        var error = Assert.Throws<InvalidOperationException>(() => AIProviders.Get("anthropic-messages").ReadResponse(response.RootElement));
        Assert.DoesNotContain("private", error.Message, StringComparison.Ordinal);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
