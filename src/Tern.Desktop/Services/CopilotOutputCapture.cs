using System.Text;
using System.Text.Json;
using Tern.App;

namespace Tern.Desktop.Services;

/// <summary>Spools a private invocation's stream, then decodes one event at a time. Only its final answer stays in memory.</summary>
internal sealed class CopilotOutputCapture : IAsyncDisposable
{
    private readonly CopilotReplySpool _file;
    private readonly StreamWriter _writer;
    private readonly string? _secret;
    private string _pendingSecret = "";
    private readonly StringBuilder _linePrefix = new();
    private bool _assistantSeen;
    private bool _protocolSeen;
    private bool _completed;
    private long _newlines, _lastLineBytes;
    private bool _previousHighSurrogate;
    private int _depth;
    private bool _insideString, _escaped;
    internal string Path { get; }
    internal bool CompletionObserved { get; private set; }

    internal CopilotOutputCapture(string? secret = null)
    {
        _secret = string.IsNullOrEmpty(secret) ? null : secret;
        _file = new CopilotReplySpool();
        Path = _file.FilePath;
        _writer = new StreamWriter(_file, new UTF8Encoding(false), 4096, leaveOpen: true);
    }

    internal async Task AppendAsync(ReadOnlyMemory<char> characters, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_completed) throw new InvalidOperationException("This reply has already been collected.");
        // Completion detection keeps only a small event prefix, never a growing
        // escaped answer or an accumulated history of event noise.
        foreach (var character in characters.Span)
        {
            if (_insideString)
            {
                if (_escaped) _escaped = false;
                else if (character == '\\') _escaped = true;
                else if (character == '"') _insideString = false;
            }
            else if (character == '"') _insideString = true;
            else if (character is '{' or '[') _depth++;
            else if (character is '}' or ']') _depth--;
            if (character == '\n')
            {
                var type = PrefixType(_linePrefix.ToString());
                if (IsEventType(type)) _protocolSeen = true;
                if (type == "assistant.message") _assistantSeen = true;
                if (type == "result" && _assistantSeen) CompletionObserved = true;
                _linePrefix.Clear();
            }
            else if (_linePrefix.Length < 4096) _linePrefix.Append(character);
        }
        if (_secret is null)
        {
            CountWrittenBytes(characters.Span);
            await _writer.WriteAsync(characters, token).ConfigureAwait(false);
            return;
        }
        // Redact even a credential split across pipe reads before it reaches disk.
        var text = _pendingSecret + characters.ToString();
        var safe = new StringBuilder(text.Length);
        var index = 0;
        while (index <= text.Length - _secret.Length)
        {
            if (text.AsSpan(index).StartsWith(_secret, StringComparison.Ordinal))
            { safe.Append("[redacted]"); index += _secret.Length; }
            else safe.Append(text[index++]);
        }
        _pendingSecret = text[index..];
        var safeText = safe.ToString();
        CountWrittenBytes(safeText);
        await _writer.WriteAsync(safeText.AsMemory(), token).ConfigureAwait(false);
    }

    internal async Task<CopilotProcessResult> CompleteAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_completed) throw new InvalidOperationException("This reply has already been collected.");
        _completed = true;
        CountWrittenBytes(_pendingSecret);
        await _writer.WriteAsync(_pendingSecret.AsMemory(), token).ConfigureAwait(false);
        _pendingSecret = "";
        await _writer.FlushAsync(token).ConfigureAwait(false);
        _file.Rewind();
        string? reply = null;
        var events = false;
        var eventError = false;
        var diagnostics = new StringBuilder();
        try
        {
            // The runtime decodes values from the disk stream incrementally. We do
            // not create a string containing the escaped event line or all events.
            await foreach (var document in JsonSerializer.DeserializeAsyncEnumerable<JsonDocument>(_file,
                topLevelValues: true, options: new JsonSerializerOptions { MaxDepth = 64 }, cancellationToken: token).ConfigureAwait(false))
            {
                using var current = document;
                if (current is null) continue;
                var item = current.RootElement;
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("type", out var typeValue)
                    || typeValue.ValueKind != JsonValueKind.String) continue;
                var type = typeValue.GetString()!;
                if (type == "result" || type.Contains('.', StringComparison.Ordinal)) events = true;
                if (type == "assistant.message" && item.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    reply = content.GetString();
                if (type.Contains("error", StringComparison.OrdinalIgnoreCase))
                { eventError = true; AddDiagnostics(item, diagnostics); }
                if (type == "result" && item.TryGetProperty("exitCode", out var exitCode)
                    && exitCode.ValueKind == JsonValueKind.Number && exitCode.TryGetInt32(out var code) && code != 0)
                { eventError = true; AddDiagnostics(item, diagnostics); }
            }
        }
        catch (JsonException) when (!events && !_protocolSeen && !IsEventType(PrefixType(_linePrefix.ToString())))
        { /* Older CLI versions may return ordinary text instead of events. */ }
        catch (JsonException error) when ((_depth > 0 || _insideString) && error.LineNumber == _newlines
            && error.BytePositionInLine >= _lastLineBytes)
        { throw new AIReplyIncompleteException(); }
        catch (JsonException)
        { throw new InvalidOperationException("Copilot did not finish its reply. Try again; your content has not changed."); }

        if (events) return new(0, reply ?? "", diagnostics.ToString(), OutputIsReply: true, EventError: eventError);
        _file.Rewind();
        using var reader = new StreamReader(_file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        return new(0, await reader.ReadToEndAsync(token).ConfigureAwait(false));
    }

    private static string? PrefixType(string prefix)
    {
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(prefix), isFinalBlock: false, state: default);
            while (reader.Read())
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 && reader.ValueTextEquals("type")
                    && reader.Read() && reader.TokenType == JsonTokenType.String) return reader.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    private static bool IsEventType(string? type) => type is not null && (type == "result" || type.Contains('.', StringComparison.Ordinal));

    private void CountWrittenBytes(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (character == '\n') { _newlines++; _lastLineBytes = 0; _previousHighSurrogate = false; continue; }
            if (char.IsLowSurrogate(character) && _previousHighSurrogate) _lastLineBytes++;
            else _lastLineBytes += character < 0x80 ? 1 : character < 0x800 ? 2 : 3;
            _previousHighSurrogate = char.IsHighSurrogate(character);
        }
    }

    private static void AddDiagnostics(JsonElement value, StringBuilder text)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var message = value.GetString()!;
                text.Append(message.AsSpan(Math.Max(0, message.Length - 65_536))).Append('\n');
                if (text.Length > 65_536) text.Remove(0, text.Length - 65_536);
                break;
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject()) AddDiagnostics(property.Value, text);
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray()) AddDiagnostics(item, text);
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _writer.DisposeAsync().ConfigureAwait(false); }
        finally { await _file.DisposeAsync().ConfigureAwait(false); }
    }
}
