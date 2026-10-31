using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Tern.Desktop.Services;
using Xunit;

namespace Tern.Desktop.Tests;

public class AIConnectionTests
{
    [Theory]
    [InlineData("https://example.com/v1/chat/completions", true)]
    [InlineData("http://localhost:11434/v1/chat/completions", true)]
    [InlineData("http://127.0.0.1:8080/v1/chat/completions", true)]
    [InlineData("http://example.com/v1/chat/completions", false)]
    [InlineData("https://key@example.com/v1/chat/completions", false)]
    [InlineData("https://example.com/?api_key=secret", false)]
    public void Validate_endpoint(string endpoint, bool allowed) => Assert.Equal(allowed, AIConnectionClient.IsAllowedEndpoint(new Uri(endpoint)));

    [Fact]
    public async Task Sends_only_reviewed_text_and_explicit_model_and_key()
    {
        var handler = new Stub(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("local-model", body.RootElement.GetProperty("model").GetString());
            Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
            var messages = body.RootElement.GetProperty("messages");
            Assert.Equal(1, messages.GetArrayLength());
            Assert.Equal("Reviewed Unicode: café 😀", messages[0].GetProperty("content").GetString());
            return Json("{\"choices\":[{\"message\":{\"content\":\"Answer\"}}]}");
        });
        using var client = new AIConnectionClient(handler);
        Assert.Equal("Answer", await client.SendAsync(new Uri("http://localhost/v1/chat/completions"), "local-model", "test-key", "Reviewed Unicode: café 😀", CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_reaches_transport()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new AIConnectionClient(new Stub(async (_, token) => { cancellation.Cancel(); await Task.Delay(Timeout.Infinite, token); return Json("{}"); }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(new Uri("http://localhost/v1/chat/completions"), "m", "", "reviewed", cancellation.Token));
    }

    [Fact]
    public async Task Provider_errors_do_not_echo_remote_body_or_secret()
    {
        using var client = new AIConnectionClient(new Stub((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("sensitive provider diagnostics") })));
        var error = await Assert.ThrowsAsync<AIServiceException>(() => client.SendAsync(new Uri("https://example.com/v1/chat/completions"), "m", "secret-key", "private text", CancellationToken.None));
        Assert.Contains("401", error.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_responses_are_rejected()
    {
        using var client = new AIConnectionClient(new Stub((_, _) => Task.FromResult(Json(new string('x', 1_048_577)))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(new Uri("http://localhost/v1/chat/completions"), "m", "", "reviewed", CancellationToken.None));
    }

    [Fact]
    public async Task Transport_failure_is_actionable_without_exposing_transport_diagnostics()
    {
        using var client = new AIConnectionClient(new Stub((_, _) => throw new HttpRequestException("private diagnostics")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(new Uri("https://example.com/v1/chat/completions"), "m", "", "reviewed", CancellationToken.None));
        Assert.Contains("Connection failed", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private", error.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
