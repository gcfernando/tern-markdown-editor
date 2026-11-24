using System.Text;
using System.Text.Json;
using Tern.App;
using Tern.Desktop.Services;

namespace Tern.Desktop.Tests;

public sealed class CopilotOutputCaptureTests
{
    [Fact]
    public async Task Large_event_noise_and_escaped_multimegabyte_messages_do_not_share_an_output_ceiling()
    {
        var answer = string.Concat(Enumerable.Repeat("Currency € — Unicode 😀 and \"quoted text\".\n", 30_000)) + "FINAL_ANSWER_CANARY";
        var noise = JsonSerializer.Serialize(new { type = "user.message", data = new { content = new string('n', 700_000) } });
        var final = JsonSerializer.Serialize(new { type = "assistant.message", data = new { content = answer } });
        var events = noise + "\n" + JsonSerializer.Serialize(new { type = "assistant.message", data = new { content = "Earlier intermediate message" } })
            + "\n" + final + "\n{\"type\":\"result\",\"exitCode\":0}\n";
        string path;
        await using (var capture = new CopilotOutputCapture())
        {
            path = capture.Path;
            Assert.Throws<IOException>(() => File.ReadAllBytes(path));
            for (var at = 0; at < events.Length; at += 997)
                await capture.AppendAsync(events.AsMemory(at, Math.Min(997, events.Length - at)), TestContext.Current.CancellationToken);
            Assert.True(capture.CompletionObserved);
            var result = await capture.CompleteAsync(TestContext.Current.CancellationToken);
            Assert.True(result.OutputIsReply);
            Assert.Equal(answer, result.Output);
            Assert.DoesNotContain(new string('n', 100), result.Output);
        }
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Encrypted_blocks_roundtrip_original_bytes_across_boundaries_and_are_deleted()
    {
        var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Raw € 😀\r\n", 40_000)));
        string path;
        await using (var spool = new CopilotReplySpool())
        {
            path = spool.FilePath;
            await spool.WriteAsync(bytes, TestContext.Current.CancellationToken);
            await spool.FlushAsync(TestContext.Current.CancellationToken);
            Assert.Throws<IOException>(() => File.ReadAllBytes(path));
            spool.Rewind();
            using var restored = new MemoryStream();
            await spool.CopyToAsync(restored, TestContext.Current.CancellationToken);
            Assert.Equal(bytes, restored.ToArray());
        }
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Credentials_split_across_reads_are_redacted_and_internal_error_events_remain_errors()
    {
        const string secret = "synthetic-credential-key";
        var events = "{\"type\":\"assistant.message\",\"data\":{\"content\":\"" + secret + "\"}}\n"
            + "{\"type\":\"session.error\",\"data\":{\"message\":\"HTTP 413 request too large\"}}\n";
        await using var capture = new CopilotOutputCapture(secret);
        for (var at = 0; at < events.Length; at += 5)
            await capture.AppendAsync(events.AsMemory(at, Math.Min(5, events.Length - at)), TestContext.Current.CancellationToken);
        var result = await capture.CompleteAsync(TestContext.Current.CancellationToken);
        Assert.Equal("[redacted]", result.Output);
        Assert.True(result.EventError);
        Assert.Contains("HTTP 413", result.Error);
        Assert.DoesNotContain(secret, result.Output + result.Error);
    }

    [Fact]
    public async Task Incomplete_event_stream_never_returns_an_earlier_partial_answer()
    {
        await using var capture = new CopilotOutputCapture();
        await capture.AppendAsync("{\"type\":\"assistant.message\",\"data\":{\"content\":\"Partial\"}}\n{\"type\":\"result\"".AsMemory(), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<AIReplyIncompleteException>(() => capture.CompleteAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Malformed_but_complete_event_json_is_not_a_transport_truncation_signal()
    {
        await using var capture = new CopilotOutputCapture();
        await capture.AppendAsync("{\"type\":\"session.started\"}\n{\"type\":\"assistant.message\",\"data\":oops}\n".AsMemory(), TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => capture.CompleteAsync(TestContext.Current.CancellationToken));
        Assert.IsNotType<AIReplyIncompleteException>(error);
    }

    [Fact]
    public async Task Legacy_plain_text_remains_intact_and_cancellation_leaves_no_spool()
    {
        var text = new string('x', 700_000) + " € 😀\r\nFINAL";
        string path;
        await using (var capture = new CopilotOutputCapture())
        {
            path = capture.Path;
            await capture.AppendAsync(text.AsMemory(), TestContext.Current.CancellationToken);
            var result = await capture.CompleteAsync(TestContext.Current.CancellationToken);
            Assert.Equal(text, result.Output);
            Assert.False(result.OutputIsReply);
        }
        Assert.False(File.Exists(path));
        await using (var capture = new CopilotOutputCapture())
        {
            path = capture.Path;
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.AppendAsync("Not saved".AsMemory(), cancel.Token));
        }
        Assert.False(File.Exists(path));
    }
}
