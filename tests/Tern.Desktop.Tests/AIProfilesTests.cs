using System.Text.Json;
using Tern.Desktop.Services;

namespace Tern.Desktop.Tests;

public sealed class AIProfilesTests
{
    [Fact]
    public void Every_adapter_serializes_only_the_explicit_message_and_parses_its_protocol()
    {
        foreach (var provider in AIProviders.All)
        {
            using var request = JsonDocument.Parse(provider.CreateRequest("test-model", "reviewed café 😀"));
            Assert.False(request.RootElement.GetProperty("stream").GetBoolean());
            Assert.Single(request.RootElement.GetProperty("messages").EnumerateArray());
            Assert.Equal("reviewed café 😀", request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
            using var response = JsonDocument.Parse(provider.Id switch
            {
                "chat-completions" => "{\"choices\":[{\"message\":{\"content\":\"response\"}}]}",
                "ollama-chat" => "{\"message\":{\"content\":\"response\"},\"done\":true}",
                "anthropic-messages" => "{\"content\":[{\"type\":\"text\",\"text\":\"response\"}]}",
                _ => throw new InvalidOperationException("Add a protocol-specific response fixture for the new adapter."),
            });
            Assert.Equal("response", provider.ReadResponse(response.RootElement));
        }
    }

    [Fact]
    public void Profiles_round_trip_metadata_and_refuse_embedded_credentials_without_replacing_valid_store()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tern-ai-profile-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AIProfileStore(directory);
            var profile = new AIConnectionProfile(Guid.NewGuid().ToString("N"), "My local model", "ollama-chat", "http://localhost:11434/api/chat", "model");
            store.Save([profile]);
            Assert.Equal(profile, Assert.Single(store.Load()));
            var original = File.ReadAllText(store.FilePath);
            Assert.DoesNotContain("apiKey", original, StringComparison.OrdinalIgnoreCase);
            Assert.Throws<InvalidOperationException>(() => store.Save([profile with { Endpoint = "https://secret@service.example/v1/chat/completions" }]));
            Assert.Equal(original, File.ReadAllText(store.FilePath));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void Windows_credentials_round_trip_without_a_plaintext_file()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new WindowsAICredentialStore();
        var id = Guid.NewGuid().ToString("N");
        try
        {
            Assert.Null(store.Read(id));
            store.Save(id, "artificial-test-key");
            Assert.Equal("artificial-test-key", store.Read(id));
            store.Save(id, "replacement-test-key");
            Assert.Equal("replacement-test-key", store.Read(id));
        }
        finally { store.Remove(id); }
        Assert.Null(store.Read(id));
    }
}
