using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tern.Desktop.Services;

public sealed class AIServiceException(string message, string details, int statusCode,
    bool modelInvalid = false, bool authenticationInvalid = false, DateTimeOffset? retryAt = null) : InvalidOperationException(message)
{
    public string Details { get; } = details;
    public int StatusCode { get; } = statusCode;
    public bool ModelInvalid { get; } = modelInvalid;
    public bool AuthenticationInvalid { get; } = authenticationInvalid;
    public DateTimeOffset? RetryAt { get; } = retryAt;

    public static AIServiceException From(int statusCode, string body, string secret, string? requestId = null, DateTimeOffset? retryAt = null)
    {
        string code = "", type = "", providerMessage = "";
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) providerMessage = error.GetString() ?? "";
                else if (error.ValueKind == JsonValueKind.Object)
                {
                    code = String(error, "code"); type = String(error, "type"); providerMessage = String(error, "message");
                }
            }
        }
        catch (JsonException) { /* HTML and non-JSON errors still have an actionable HTTP category. */ }
        var identity = (code + " " + type).ToLowerInvariant();
        var messageText = providerMessage.ToLowerInvariant();
        var invalidModel = identity.Contains("model_not_found", StringComparison.Ordinal) || identity.Contains("invalid_model", StringComparison.Ordinal)
            || (statusCode == 404 && (messageText.StartsWith("model:", StringComparison.Ordinal)
                || System.Text.RegularExpressions.Regex.IsMatch(messageText, "model ['\"][^'\"]+['\"] (?:was )?not found")));
        var invalidAuth = statusCode == 401 || identity.Contains("invalid_api_key", StringComparison.Ordinal)
            || identity.Contains("authentication_error", StringComparison.Ordinal);
        var billing = identity.Contains("insufficient_quota", StringComparison.Ordinal) || identity.Contains("credit", StringComparison.Ordinal)
            || identity.Contains("spend_limit", StringComparison.Ordinal) || identity.Contains("usage_limit", StringComparison.Ordinal)
            || identity.Contains("billing", StringComparison.Ordinal) || messageText.Contains("credit balance", StringComparison.Ordinal);
        string message;
        if (invalidAuth) message = "The API key was rejected. Replace the key and check its account access.";
        else if (invalidModel) message = "This model is unavailable or not accessible with this connection. Correct the model ID or its permissions before sending again.";
        else if (identity.Contains("spend_limit", StringComparison.Ordinal) || identity.Contains("billing_hard_limit", StringComparison.Ordinal))
            message = "The account reached a spending cap. Review the provider's project or organization spending limit; retrying will not restore access.";
        else if (identity.Contains("usage_limit", StringComparison.Ordinal))
            message = "The account reached its approved usage limit. Review that limit with the provider before sending again.";
        else if (billing) message = "The provider reported exhausted credits or an account quota. Review its billing and credit balance; creating another key will not add credits.";
        else message = statusCode switch
        {
            403 => "Permission denied. Check that this key is allowed to use the selected service, workspace and model.",
            404 => "The API resource was not found. Check the full endpoint and selected protocol.",
            429 => "The provider is temporarily rate limiting requests. Wait before trying again; this response does not establish a billing problem.",
            400 or 422 => "The provider rejected the request format or settings. Check the selected protocol, endpoint and model.",
            408 or 504 => "The provider timed out. It may already have received the request; check before sending again.",
            301 or 302 or 307 or 308 =>
                "The service redirected this request. Redirects are not followed; use its documented full endpoint.",
            _ when statusCode >= 500 => "The provider is currently failing or overloaded. Try again later.",
            _ => $"The provider returned HTTP {statusCode}. Review the sanitized technical details.",
        };
        if (billing) retryAt = null;
        if (retryAt is not null) message += $" Retry after {retryAt.Value:HH:mm:ss 'UTC'}.";
        string Safe(string value)
        {
            if (!string.IsNullOrEmpty(secret)) value = value.Replace(secret, "[hidden]", StringComparison.Ordinal);
            value = Regex.Replace(value, @"[^a-zA-Z0-9_.:\-\[\] ]", "");
            return value[..Math.Min(value.Length, 160)];
        }
        var details = $"HTTP {statusCode}\nProvider code: {Safe(code.Length == 0 ? "not supplied" : code)}\nProvider type: {Safe(type.Length == 0 ? "not supplied" : type)}\nRequest ID: {Safe(requestId ?? "not supplied")}";
        return new AIServiceException(message, details, statusCode, invalidModel, invalidAuth, retryAt);
    }

    private static string String(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
