using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tern.Desktop.Services;

public sealed class AIServiceException(string message, string details, int statusCode,
    bool modelInvalid = false, bool authenticationInvalid = false, DateTimeOffset? retryAt = null, bool sizeRejected = false, int? replyTokenLimit = null) : InvalidOperationException(message)
{
    public string Details { get; } = details;
    public int StatusCode { get; } = statusCode;
    public bool ModelInvalid { get; } = modelInvalid;
    public bool AuthenticationInvalid { get; } = authenticationInvalid;
    public DateTimeOffset? RetryAt { get; } = retryAt;
    /// <summary>The service refused this request because it holds too much text.</summary>
    public bool SizeRejected { get; } = sizeRejected;
    /// <summary>The largest reply this model allows, when the service refused the requested reply size.</summary>
    public int? ReplyTokenLimit { get; } = replyTokenLimit;
    public const string TooMuchTextMessage = "This AI could not accept that much text at once. Select less, or lower Characters to send at one time for this connection. Your note has not changed.";

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
        var billing = statusCode == 402 || messageText.Contains("insufficient balance", StringComparison.Ordinal)
            || identity.Contains("insufficient_quota", StringComparison.Ordinal) || identity.Contains("credit", StringComparison.Ordinal)
            || identity.Contains("spend_limit", StringComparison.Ordinal) || identity.Contains("usage_limit", StringComparison.Ordinal)
            || identity.Contains("billing", StringComparison.Ordinal) || messageText.Contains("credit balance", StringComparison.Ordinal);
        var replyCap = Regex.Match(providerMessage, @"max_tokens\D{0,8}\d+\s*>\s*(\d+)|max_tokens.{0,60}?(?:less than or equal to|at most|maximum(?: is| of)?)\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        int? replyTokenLimit = statusCode == 400 && replyCap.Success && int.TryParse(replyCap.Groups[1].Success ? replyCap.Groups[1].Value : replyCap.Groups[2].Value, out var cap) ? cap : null;
        var sizeWords = identity + " " + messageText;
        var sizeRejected = replyTokenLimit is null && !invalidAuth && !invalidModel && !billing && (statusCode == 413
            || statusCode is 400 or 422 && Regex.IsMatch(sizeWords, @"context_length_exceeded|maximum context length|string_above_max_length|request_too_large|(?:prompt|input|messages?) (?:is|are) too (?:long|large)|too many tokens|exceeds? the (?:maximum )?context", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200))
            || statusCode == 429 && Regex.IsMatch(sizeWords, @"request[_ ]too[_ ]large", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)));
        if (sizeRejected) retryAt = null;
        string message;
        if (sizeRejected) message = TooMuchTextMessage;
        else if (invalidAuth) message = "The API key was rejected. Replace the key and check its account access.";
        else if (invalidModel) message = "This model is unavailable or not accessible with this connection. Correct the model ID or its permissions before sending again.";
        else if (identity.Contains("spend_limit", StringComparison.Ordinal) || identity.Contains("billing_hard_limit", StringComparison.Ordinal))
            message = "The request failed because the account reached a spending cap. Review the provider's project or organization spending limit; retrying will not restore access.";
        else if (identity.Contains("usage_limit", StringComparison.Ordinal))
            message = "The request failed because the account reached its approved usage limit. Review that limit with the provider before sending again.";
        else if (billing) message = "The request failed because the provider reported exhausted credits or an account quota. Review its billing and credit balance; creating another key will not add credits.";
        else message = statusCode switch
        {
            403 => "Permission denied. Check that this key is allowed to use the selected service, workspace and model.",
            404 => "The request failed because the API resource was not found. Check the full endpoint and selected protocol.",
            429 => "The provider is temporarily rate limiting requests. Wait before trying again; this response does not establish a billing problem.",
            400 or 422 => "The provider rejected the request format or settings. Check the selected protocol, endpoint and model.",
            408 or 504 => "The provider timed out. It may already have received the request; check before sending again.",
            301 or 302 or 307 or 308 =>
                "The request failed because the service redirected it. Redirects are not followed; use its documented full endpoint.",
            _ when statusCode >= 500 => "The request failed because the provider is failing or overloaded. Try again later.",
            _ => $"The request failed with HTTP {statusCode}. Review the sanitized technical details.",
        };
        if (billing) retryAt = null;
        if (retryAt is not null) message += $" Try again after {retryAt.Value.ToLocalTime():HH:mm:ss}.";
        string Safe(string value)
        {
            if (!string.IsNullOrEmpty(secret)) value = value.Replace(secret, "[hidden]", StringComparison.Ordinal);
            value = Regex.Replace(value, @"[^a-zA-Z0-9_.:\-\[\] ]", "");
            return value[..Math.Min(value.Length, 160)];
        }
        var details = $"HTTP {statusCode}\nProvider code: {Safe(code.Length == 0 ? "not supplied" : code)}\nProvider type: {Safe(type.Length == 0 ? "not supplied" : type)}\nRequest ID: {Safe(requestId ?? "not supplied")}";
        return new AIServiceException(message, details, statusCode, invalidModel, invalidAuth, retryAt, sizeRejected, replyTokenLimit);
    }

    private static string String(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
