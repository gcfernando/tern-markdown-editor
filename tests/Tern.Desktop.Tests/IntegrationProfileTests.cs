using Tern.Desktop.Services;
using Tern.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Avalonia.Threading;

namespace Tern.Desktop.Tests;

public sealed class IntegrationProfileTests
{
    [Fact]
    public void Multiple_integration_profiles_roundtrip_without_secrets_and_delete_removes_token()
    {
        using var folder = new Folder(); var vault = new Vault(); var store = new IntegrationProfileStore(folder.Path, vault);
        var mcp = new IntegrationProfile(Guid.NewGuid().ToString("N"), "mcp", "Tools", "https://tools.example/mcp");
        var copilot = new IntegrationProfile(Guid.NewGuid().ToString("N"), "copilot", "Copilot", System.IO.Path.Combine(folder.Path, "copilot.exe"));
        store.Save(mcp, "mcp-secret"); store.Save(copilot, "github-secret");
        var reopened = new IntegrationProfileStore(folder.Path, vault);
        Assert.Equal(2, reopened.Load().Count); Assert.Equal("github-secret", reopened.ReadSecret(copilot));
        Assert.DoesNotContain("mcp-secret", File.ReadAllText(store.FilePath)); Assert.DoesNotContain("github-secret", File.ReadAllText(store.FilePath));
        reopened.Remove(mcp.Id); Assert.Null(vault.Read(mcp.Id)); Assert.Equal(copilot, Assert.Single(reopened.Load()));
    }

    [Fact]
    public void Failed_vault_update_restores_metadata_and_previous_secret()
    {
        using var folder = new Folder(); var vault = new Vault(); var store = new IntegrationProfileStore(folder.Path, vault);
        var profile = new IntegrationProfile(Guid.NewGuid().ToString("N"), "mcp", "Tools", "https://tools.example/mcp");
        store.Save(profile, "old-secret"); vault.FailNextWrite = true;
        Assert.Throws<InvalidOperationException>(() => store.Save(profile with { Name = "Changed" }, "new-secret"));
        Assert.Equal(profile, Assert.Single(store.Load())); Assert.Equal("old-secret", store.ReadSecret(profile));
    }

    [Theory]
    [InlineData("https://secret@tools.example/mcp")]
    [InlineData("http://remote.example/mcp")]
    [InlineData("https://tools.example/mcp?token=secret")]
    public void Unsafe_destinations_preserve_previous_store(string endpoint)
    {
        using var folder = new Folder(); var store = new IntegrationProfileStore(folder.Path, new Vault());
        var profile = new IntegrationProfile(Guid.NewGuid().ToString("N"), "mcp", "Tools", "https://tools.example/mcp");
        store.Save(profile, "secret"); var before = File.ReadAllText(store.FilePath);
        Assert.Throws<InvalidOperationException>(() => store.Save(profile with { Destination = endpoint }, "secret"));
        Assert.Equal(before, File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void Corrupt_null_metadata_preserves_file_and_reports_expected_error()
    {
        using var folder = new Folder(); var store = new IntegrationProfileStore(folder.Path, new Vault());
        File.WriteAllText(store.FilePath, "[null]");
        Assert.Throws<InvalidOperationException>(() => store.Load());
        Assert.Equal("[null]", File.ReadAllText(store.FilePath));
    }

    [AvaloniaFact]
    public void Saved_integration_picker_restores_secret_and_destination_edit_clears_it()
    {
        using var folder = new Folder(); using var fixture = new DesktopFixture(); var vault = new Vault();
        var store = new IntegrationProfileStore(folder.Path, vault);
        var profile = new IntegrationProfile(Guid.NewGuid().ToString("N"), "mcp", "My tools", "https://tools.example/mcp");
        store.Save(profile, "private-token");
        var destination = new TextBox(); var model = new TextBox { Text = "auto" }; var secret = new TextBox();
        var editor = new IntegrationProfileEditor("mcp", destination, model, secret, new TextBlock(), folder.Path, vault);
        var window = new Window { Content = editor.Content }; window.Show(fixture.Window);
        try
        {
            Dispatcher.UIThread.RunJobs();
            var picker = window.GetVisualDescendants().OfType<ComboBox>().Single(); picker.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(profile.Destination, destination.Text); Assert.Equal("private-token", secret.Text);
            destination.Text = "https://other.example/mcp"; Dispatcher.UIThread.RunJobs();
            Assert.Equal("", secret.Text);
            Assert.Equal("private-token", store.ReadSecret(profile));
        }
        finally { window.Close(); }
    }

    private sealed class Vault : IAICredentialStore
    {
        private readonly Dictionary<string, string> _keys = [];
        public bool FailNextWrite { get; set; }
        public void Save(string id, string key) { if (FailNextWrite) { FailNextWrite = false; throw new InvalidOperationException("test failure"); } _keys[id] = key; }
        public string? Read(string id) => _keys.GetValueOrDefault(id);
        public void Remove(string id) => _keys.Remove(id);
    }
    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tern-integrations-" + Guid.NewGuid().ToString("N"));
        public Folder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
