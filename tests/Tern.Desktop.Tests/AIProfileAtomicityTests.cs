using System.ComponentModel;
using Tern.Desktop.Services;

namespace Tern.Desktop.Tests;

/// <summary>Independent fault injection at metadata/vault boundaries. No network or native credential writes.</summary>
public sealed class AIProfileAtomicityTests
{
    [Fact]
    public void Metadata_failure_does_not_replace_the_existing_credential()
    {
        var original = Profile("Original");
        var metadata = new FakeProfiles([original]) { FailOnSave = 1 };
        var vault = new FakeVault(); vault.Secrets[original.Id] = "old-test-key";
        var manager = new AIConnectionManager(metadata, vault);

        Assert.Throws<IOException>(() => manager.Save(original with { Name = "Updated" }, "new-test-key", [original]));

        Assert.Equal(original, Assert.Single(metadata.Profiles));
        Assert.Equal("old-test-key", vault.Read(original.Id));
        Assert.Equal(0, vault.MutationCalls);
    }

    [Fact]
    public void Metadata_failure_during_remove_keeps_the_saved_connection_and_secret()
    {
        var original = Profile("Original");
        var metadata = new FakeProfiles([original]) { FailOnSave = 1 };
        var vault = new FakeVault(); vault.Secrets[original.Id] = "old-test-key";
        var manager = new AIConnectionManager(metadata, vault);

        Assert.Throws<IOException>(() => manager.Remove(original.Id, [original]));

        Assert.Equal(original, Assert.Single(metadata.Profiles));
        Assert.Equal("old-test-key", vault.Read(original.Id));
        Assert.Equal(0, vault.MutationCalls);
    }

    [Fact]
    public void Vault_failure_after_mutating_a_secret_restores_both_previous_values()
    {
        var original = Profile("Original");
        var metadata = new FakeProfiles([original]);
        var vault = new FakeVault { FailAfterMutationOnCall = 1 }; vault.Secrets[original.Id] = "old-test-key";
        var manager = new AIConnectionManager(metadata, vault);

        var error = Assert.Throws<InvalidOperationException>(() => manager.Save(original with { Name = "Updated" }, "new-test-key", [original]));

        Assert.Contains("restored", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, Assert.Single(metadata.Profiles));
        Assert.Equal("old-test-key", vault.Read(original.Id));
        Assert.Equal(2, metadata.SaveCalls);
    }

    [Fact]
    public void Vault_failure_after_deleting_a_secret_restores_the_removed_connection()
    {
        var original = Profile("Original");
        var metadata = new FakeProfiles([original]);
        var vault = new FakeVault { FailAfterMutationOnCall = 1 }; vault.Secrets[original.Id] = "old-test-key";
        var manager = new AIConnectionManager(metadata, vault);

        Assert.Throws<InvalidOperationException>(() => manager.Remove(original.Id, [original]));

        Assert.Equal(original, Assert.Single(metadata.Profiles));
        Assert.Equal("old-test-key", vault.Read(original.Id));
    }

    [Fact]
    public void Failed_creation_removes_the_partially_saved_credential()
    {
        var added = Profile("New");
        var metadata = new FakeProfiles([]);
        var vault = new FakeVault { FailAfterMutationOnCall = 1 };
        var manager = new AIConnectionManager(metadata, vault);

        Assert.Throws<InvalidOperationException>(() => manager.Save(added, "new-test-key", []));

        Assert.Empty(metadata.Profiles);
        Assert.Null(vault.Read(added.Id));
    }

    [Fact]
    public void Failed_compensation_reports_incomplete_restoration_instead_of_success()
    {
        var original = Profile("Original");
        var metadata = new FakeProfiles([original]) { FailOnSave = 2 };
        var vault = new FakeVault { FailAfterMutationOnCall = 1 }; vault.Secrets[original.Id] = "old-test-key";
        var manager = new AIConnectionManager(metadata, vault);

        var error = Assert.Throws<InvalidOperationException>(() => manager.Save(original with { Name = "Updated" }, "new-test-key", [original]));

        Assert.Contains("incomplete", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("old-test-key", vault.Read(original.Id));
        Assert.DoesNotContain("old-test-key", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("new-test-key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_null_profile_entries_are_reported_without_replacing_the_original_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tern-ai-null-profiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var metadata = new AIProfileStore(directory);
            File.WriteAllText(metadata.FilePath, "[null]");
            var error = Assert.Throws<InvalidOperationException>(() => metadata.Load());
            Assert.Contains("invalid", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("[null]", File.ReadAllText(metadata.FilePath));
        }
        finally { File.Delete(Path.Combine(directory, "ai-connections.json")); Directory.Delete(directory); }
    }

    private static AIConnectionProfile Profile(string name) =>
        new(Guid.NewGuid().ToString("N"), name, "chat-completions", "https://service.example/v1/chat/completions", "test-model");

    private sealed class FakeProfiles(IReadOnlyList<AIConnectionProfile> initial) : IAIProfileStore
    {
        public IReadOnlyList<AIConnectionProfile> Profiles { get; private set; } = initial.ToArray();
        public int SaveCalls { get; private set; }
        public int FailOnSave { get; init; }
        public IReadOnlyList<AIConnectionProfile> Load() => Profiles;
        public void Save(IReadOnlyList<AIConnectionProfile> profiles)
        {
            if (++SaveCalls == FailOnSave) throw new IOException("Synthetic metadata persistence failure.");
            Profiles = profiles.ToArray();
        }
    }

    private sealed class FakeVault : IAICredentialStore
    {
        public Dictionary<string, string> Secrets { get; } = new(StringComparer.Ordinal);
        public int MutationCalls { get; private set; }
        public int FailAfterMutationOnCall { get; init; }
        public string? Read(string profileId) => Secrets.GetValueOrDefault(profileId);
        public void Save(string profileId, string secret) { Secrets[profileId] = secret; MaybeFail(); }
        public void Remove(string profileId) { Secrets.Remove(profileId); MaybeFail(); }
        private void MaybeFail()
        {
            if (++MutationCalls == FailAfterMutationOnCall)
                throw new Win32Exception(5, "Synthetic vault failure after mutation.");
        }
    }
}
