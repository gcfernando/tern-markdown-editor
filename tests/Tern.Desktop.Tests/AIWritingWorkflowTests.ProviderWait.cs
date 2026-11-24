using System.Net;
using System.Net.Http.Headers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Tern.Desktop.Services;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed partial class AIWritingWorkflowTests
{
    private static HttpResponseMessage RateLimited()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
        return response;
    }

    [AvaloniaFact]
    public async Task Provider_wait_is_not_reported_as_a_broken_connection_or_too_much_text()
    {
        using var f = Fixture((_, _) => Task.FromResult(RateLimited()));
        var path = await f.WriteAsync("notes/launch-plan.md", "# Launch plan\n\nWe decided the project launch is on 14 April.");
        Assert.True(await f.Shell.PromoteFolderAsync(Path.GetDirectoryName(path)!));
        f.Shell.Settings.KnowledgeAllowRemote = true;
        f.Window.ShowKnowledgeAssistant();
        var panel = Assert.Single(KnowledgePanels(f.Window));
        Pump(f.Window);
        Find<TextBox>(panel, "Your question").Text = "What did we decide about the project launch?";
        Click(Find<Button>(panel, "Find my notes"));
        var send = Find<Button>(panel, "Get my answer");
        var cancel = Find<Button>(panel, "Cancel");
        await Until(() => send.IsEffectivelyVisible && !cancel.IsEnabled);

        Click(send);
        await Until(() => !cancel.IsEnabled);
        panel.RefreshSettings();

        Assert.True(send.IsEnabled);
        Assert.False(Find<TextBlock>(panel, "Help for choosing notes").IsVisible);
    }

    [Fact]
    public async Task Provider_wait_is_tracked_per_connection_and_keeps_its_own_message()
    {
        using var folder = new ReviewFolder();
        new AIProfileStore(folder.Path).Save([
            new(FirstId, "Writing", "chat-completions", "https://example.test/v1/chat/completions", "model-one", DiscoveryEndpoint: "https://example.test/v1/models"),
            new(SecondId, "Research", "chat-completions", "https://example.test/v1/chat/completions", "model-two", DiscoveryEndpoint: "https://example.test/v1/models"),
        ]);
        var service = new AIWorkspaceService(folder.Path, new CountingVault(),
            () => new AIConnectionClient(new Handler((_, _) => Task.FromResult(RateLimited()))));
        service.Reload();
        Assert.False(service.IsWaitingToRetry(FirstId));

        await Assert.ThrowsAsync<AIServiceException>(() => service.SendAsync(FirstId, "Question", CancellationToken.None));

        Assert.True(service.IsWaitingToRetry(FirstId));
        Assert.False(service.IsWaitingToRetry(SecondId));
        Assert.StartsWith("Wait until", service.WritingValidationError(FirstId, "Question"));
        Assert.Null(service.WritingValidationError(SecondId, "Question"));
    }

    [Fact]
    public void Validating_a_connection_reads_its_credential_once()
    {
        using var folder = new ReviewFolder();
        new AIProfileStore(folder.Path).Save([
            new(FirstId, "Writing", "chat-completions", "https://example.test/v1/chat/completions", "model-one", DiscoveryEndpoint: "https://example.test/v1/models"),
        ]);
        var vault = new CountingVault();
        var service = new AIWorkspaceService(folder.Path, vault);
        service.Reload();
        vault.Reads = 0;

        Assert.Null(service.WritingValidationError(FirstId, "Question"));

        Assert.Equal(1, vault.Reads);
    }

    [Fact]
    public void Preparing_note_text_uses_loaded_connections_and_guards_only_copilot()
    {
        using var folder = new ReviewFolder();
        var vault = new CountingVault();
        var store = new IntegrationProfileStore(folder.Path, vault);
        var copilot = Guid.NewGuid().ToString("N");
        var mcp = Guid.NewGuid().ToString("N");
        store.Save(new IntegrationProfile(copilot, "copilot", "Copilot", Path.Combine(folder.Path, "copilot.exe")), "");
        store.Save(new IntegrationProfile(mcp, "mcp", "Tools", "https://tools.example/mcp"), "");
        new AIProfileStore(folder.Path).Save([
            new(FirstId, "Writing", "chat-completions", "https://example.test/v1/chat/completions", "model-one", DiscoveryEndpoint: "https://example.test/v1/models"),
        ]);
        var service = new AIWorkspaceService(folder.Path, vault);
        service.Reload();
        // Preparing text runs on the UI thread, so it must not need the saved file again.
        File.Delete(store.FilePath);

        Assert.Equal("ask a＠example.test", service.PrepareNoteText(copilot, "ask a@example.test"));
        Assert.Equal("ask a@example.test", service.PrepareNoteText(mcp, "ask a@example.test"));
        Assert.Equal("ask a@example.test", service.PrepareNoteText(FirstId, "ask a@example.test"));
    }

    private sealed class CountingVault : IAICredentialStore
    {
        public int Reads { get; set; }
        public void Save(string profileId, string secret) { }
        public string? Read(string profileId) { Reads++; return null; }
        public void Remove(string profileId) { }
    }

    private sealed class ReviewFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tern-ai-review-" + Guid.NewGuid().ToString("N"));
        public ReviewFolder() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
