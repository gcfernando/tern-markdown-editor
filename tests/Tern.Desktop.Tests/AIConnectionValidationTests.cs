using System.Net;
using System.Net.Http;
using System.Text.Json;
using Tern.Desktop.Services;

namespace Tern.Desktop.Tests;

public sealed class AIConnectionValidationTests
{
    [Theory]
    [InlineData("openai", "chat-completions", "https://api.openai.com/v1/chat/completions", "https://api.openai.com/v1/models")]
    [InlineData("anthropic", "anthropic-messages", "https://api.anthropic.com/v1/messages", "https://api.anthropic.com/v1/models")]
    [InlineData("ollama", "ollama-chat", "http://localhost:11434/api/chat", "http://localhost:11434/api/tags")]
    [InlineData("ollama", "chat-completions", "http://localhost:11434/v1/chat/completions", "http://localhost:11434/api/tags")]
    public void Defaults_use_matching_full_endpoints_and_protocols(string service, string protocol, string endpoint, string models)
    {
        Assert.Equal(endpoint, AIServices.DefaultEndpoint(service, protocol));
        var input = new AIConnectionInputs(service, protocol, endpoint, "manual-model", service == "ollama" ? "" : "test-key");
        Assert.Empty(AIConnectionRules.Errors(input));
        Assert.Equal(models, AIConnectionRules.ModelListEndpoint(input));
    }

    [Theory]
    [InlineData("http://remote.example/chat", "chat-completions", false)]
    [InlineData("https://example.test/custom/inference", "chat-completions", true)]
    [InlineData("https://api.openai.com/v1/responses", "chat-completions", false)]
    [InlineData("http://localhost:11434/api/chat", "chat-completions", false)]
    [InlineData("http://localhost:11434/v1/chat/completions", "ollama-chat", false)]
    [InlineData("https://secret@example.test/chat", "chat-completions", false)]
    public void Local_checks_reject_known_mismatches_but_allow_documented_custom_paths(string endpoint, string protocol, bool valid)
    {
        var errors = AIConnectionRules.Errors(new AIConnectionInputs("compatible", protocol, endpoint, "ssss", ""));
        Assert.Equal(valid, !errors.ContainsKey("endpoint"));
    }

    [Fact]
    public async Task Model_discovery_is_an_explicit_GET_and_does_not_verify_generation_or_unlisted_manual_models()
    {
        var calls = 0;
        using var client = new AIConnectionClient(new Handler((request, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.openai.com/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Content);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            return Task.FromResult(Json("{\"data\":[{\"id\":\"listed-model\"}]}"));
        }));
        var result = await client.CheckAsync(new AIConnectionInputs("openai", "chat-completions", AIServices.Get("openai").Endpoint, "ssss", "test-key"), CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.False(result.ModelInvalid);
        Assert.Contains("not verified", result.Model, StringComparison.Ordinal);
        Assert.StartsWith("Not tested", result.Generation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("installed", false)]
    [InlineData("missing", true)]
    public async Task Ollama_checks_actual_installed_models_without_requiring_a_key(string model, bool invalid)
    {
        using var client = new AIConnectionClient(new Handler((request, _) =>
        {
            Assert.Equal("http://localhost:11434/api/tags", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(Json("{\"models\":[{\"name\":\"installed:latest\"}]}"));
        }));
        var result = await client.CheckAsync(new AIConnectionInputs("ollama", "ollama-chat", AIServices.Get("ollama").Endpoint, model, ""), CancellationToken.None);
        Assert.Equal(invalid, result.ModelInvalid);
    }

    [Fact]
    public async Task Custom_endpoints_do_not_guess_discovery_paths_or_send_generation_for_validation()
    {
        var calls = 0;
        using var client = new AIConnectionClient(new Handler((_, _) => { calls++; return Task.FromResult(Json("{}")); }));
        var input = new AIConnectionInputs("compatible", "chat-completions", "https://example.test/team/custom-chat", "manual-model", "");
        var result = await client.CheckAsync(input, CancellationToken.None);
        Assert.Equal(0, calls);
        Assert.Contains("Not tested", result.Connectivity, StringComparison.Ordinal);
        Assert.Null(AIConnectionRules.ModelListEndpoint(input));
        Assert.Equal("https://example.test/documented/model-list", AIConnectionRules.ModelListEndpoint(input with { DiscoveryEndpoint = "https://example.test/documented/model-list" }));
        Assert.Contains("discovery", AIConnectionRules.Errors(input with { DiscoveryEndpoint = "https://another.test/models" }).Keys);
    }

    [Theory]
    [InlineData(401, "invalid_api_key", "API key was rejected", false)]
    [InlineData(403, "permission_denied", "Permission denied", false)]
    [InlineData(404, "model_not_found", "model is unavailable", true)]
    [InlineData(429, "insufficient_quota", "credits or an account quota", false)]
    [InlineData(429, "credit_balance_exhausted", "credits or an account quota", false)]
    [InlineData(429, "project_spend_limit_exceeded", "spending cap", false)]
    [InlineData(429, "organization_usage_limit_exceeded", "approved usage limit", false)]
    [InlineData(429, "rate_limit_exceeded", "rate limiting", false)]
    [InlineData(500, "api_error", "failing or overloaded", false)]
    public async Task Supported_provider_errors_are_distinct_and_sanitized(int status, string code, string expected, bool modelInvalid)
    {
        using var client = new AIConnectionClient(new Handler((_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent(JsonSerializer.Serialize(new { error = new { code, message = "secret-test-key private user message" } })) };
            response.Headers.Add("x-request-id", "request-123");
            return Task.FromResult(response);
        }));
        var error = await Assert.ThrowsAsync<AIServiceException>(() => client.SendAsync(new Uri("https://example.test/chat"), "model", "secret-test-key", "private user message", CancellationToken.None));
        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(modelInvalid, error.ModelInvalid);
        Assert.Contains("request-123", error.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-test-key", error.Message + error.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("private user message", error.Message + error.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retry_after_is_reported_without_automatic_duplicate_requests()
    {
        var calls = 0;
        using var client = new AIConnectionClient(new Handler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{\"error\":{\"code\":\"rate_limit_exceeded\"}}") };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return Task.FromResult(response);
        }));
        var error = await Assert.ThrowsAsync<AIServiceException>(() => client.SendAsync(new Uri("https://example.test/chat"), "model", "", "question", CancellationToken.None));
        Assert.Equal(1, calls);
        Assert.True(error.RetryAt > DateTimeOffset.UtcNow.AddSeconds(20));
    }

    [Fact]
    public async Task Timeout_is_distinct_from_user_cancellation()
    {
        using var client = new AIConnectionClient(new Handler((_, _) => throw new TaskCanceledException()));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(new Uri("https://example.test/chat"), "m", "", "question", CancellationToken.None));
        Assert.Contains("timed out", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_model_list_route_is_not_reported_as_a_confirmed_invalid_model()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.NotFound);
        var error = AIConnectionClient.ServiceError(response, "{\"error\":{\"message\":\"The models endpoint was not found\"}}", "");
        Assert.False(error.ModelInvalid);
        Assert.Contains("resource was not found", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anthropic_uses_its_schema_supported_version_and_optional_workspace_header()
    {
        using var client = new AIConnectionClient(new Handler(async (request, token) =>
        {
            Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("x-api-key")));
            Assert.Equal("2023-06-01", Assert.Single(request.Headers.GetValues("anthropic-version")));
            Assert.Equal("workspace-test", Assert.Single(request.Headers.GetValues("anthropic-workspace-id")));
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal(" exact question \n", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
            return Json("{\"content\":[{\"type\":\"text\",\"text\":\"Answer\"}]}");
        }));
        Assert.Equal("Answer", await client.SendAsync(new Uri(AIServices.Get("anthropic").Endpoint), "model", "test-key", " exact question \n",
            AIProviders.Get("anthropic-messages"), CancellationToken.None, "workspace-test"));
    }

    [Theory]
    [InlineData("openai", "chat-completions", "{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}")]
    [InlineData("anthropic", "anthropic-messages", "{\"content\":[{\"type\":\"text\",\"text\":\"OK\"}]}")]
    [InlineData("ollama", "ollama-chat", "{\"message\":{\"content\":\"OK\"}}")]
    public async Task Live_validation_posts_only_fixed_test_to_configured_model(string service, string protocol, string response)
    {
        var calls = 0;
        using var client = new AIConnectionClient(new Handler(async (request, token) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(AIServices.Get(service).Endpoint, request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("chosen-model", body.RootElement.GetProperty("model").GetString());
            Assert.Equal(AIConnectionClient.ValidationMessage, body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
            return Json(response);
        }));
        var check = await client.ValidateAsync(new AIConnectionInputs(service, protocol, AIServices.Get(service).Endpoint, "chosen-model", service == "ollama" ? "" : "test-key"), CancellationToken.None);
        Assert.Equal(1, calls); Assert.StartsWith("Passed", check.Generation); Assert.Empty(check.Models);
    }

    [Theory]
    [InlineData(401, "invalid_api_key")]
    [InlineData(404, "model_not_found")]
    [InlineData(429, "insufficient_quota")]
    public async Task Live_validation_surfaces_actual_provider_errors(int status, string code)
    {
        using var client = new AIConnectionClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent(JsonSerializer.Serialize(new { error = new { code, message = "test-key provider failure" } })) })));
        var error = await Assert.ThrowsAsync<AIServiceException>(() => client.ValidateAsync(new AIConnectionInputs("openai", "chat-completions", AIServices.Get("openai").Endpoint, "model", "test-key"), CancellationToken.None));
        Assert.DoesNotContain("test-key", error.Message);
    }

    [Fact]
    public async Task Live_validation_rejects_empty_or_wrong_protocol_response()
    {
        using var client = new AIConnectionClient(new Handler((_, _) => Task.FromResult(Json("{\"message\":{\"content\":\"OK\"}}"))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ValidateAsync(new AIConnectionInputs("openai", "chat-completions", AIServices.Get("openai").Endpoint, "model", "test-key"), CancellationToken.None));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
}
