using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.Views;
using Tern.Desktop.Services;

namespace Tern.Desktop.Tests;

public sealed class AILiveBarTests
{
    [AvaloniaFact]
    public void Saving_renaming_editing_and_deleting_connections_updates_the_bar_immediately()
    {
        using var f = new DesktopFixture();
        Assert.False(f.Window.AIBar.IsVisible);
        var store = new AIProfileStore(f.Shell.Paths.SettingsDirectory);
        var manager = new AIConnectionManager(store, new Vault());
        var first = new AIConnectionProfile(Guid.NewGuid().ToString("N"), "Local writing", "ollama-chat", "http://localhost:11434/api/chat", "model-one");
        var current = manager.Save(first, "", []);
        Assert.True(f.Window.AIBar.IsVisible);
        Assert.Equal(first.Id, Assert.IsType<WorkspaceConnection>(f.Window.AIConnectionPicker.SelectedItem).Id);
        var changed = first with { Name = "Renamed writing", Model = "model-two" };
        current = manager.Save(changed, null, current);
        Assert.Equal("Renamed writing", Assert.IsType<WorkspaceConnection>(f.Window.AIConnectionPicker.SelectedItem).Name);
        Assert.Contains("model-two", f.Window.AIConnectionSummary.Text, StringComparison.Ordinal);
        var second = first with { Id = Guid.NewGuid().ToString("N"), Name = "Second connection" };
        current = manager.Save(second, "", current);
        Assert.Equal(2, f.Window.AIConnectionPicker.ItemCount);
        current = manager.Remove(first.Id, current);
        Assert.True(f.Window.AIBar.IsVisible);
        Assert.Equal(second.Id, Assert.IsType<WorkspaceConnection>(f.Window.AIConnectionPicker.SelectedItem).Id);
        manager.Remove(second.Id, current);
        Assert.False(f.Window.AIBar.IsVisible);
        Assert.Equal(0, f.Window.MainToolbarGrid.RowSpacing);
    }

    [AvaloniaFact]
    public async Task External_profile_file_changes_also_show_refresh_and_hide_the_bar()
    {
        using var f = new DesktopFixture();
        var store = new AIProfileStore(f.Shell.Paths.SettingsDirectory);
        var profile = new AIConnectionProfile(Guid.NewGuid().ToString("N"), "External connection", "ollama-chat", "http://localhost:11434/api/chat", "one");
        store.Save([profile]);
        await Until(() => f.Window.AIBar.IsVisible);
        store.Save([profile with { Name = "Updated externally", Model = "two" }]);
        await Until(() => (f.Window.AIConnectionPicker.SelectedItem as WorkspaceConnection)?.Name == "Updated externally");
        Assert.Contains("two", f.Window.AIConnectionSummary.Text, StringComparison.Ordinal);
        File.Delete(store.FilePath);
        await Until(() => !f.Window.AIBar.IsVisible);
    }

    [AvaloniaFact]
    public void Settings_summary_includes_Copilot_and_MCP_connections()
    {
        using var f = new DesktopFixture();
        WorkspaceConnection[] connections = [new("copilot", "My Copilot", "copilot", "auto", "C:/tools/copilot.exe"),
            new("mcp", "My research tools", "mcp", "auto", "https://tools.example/mcp")];
        var settings = new SettingsWindow(f.Shell.Settings, initialCategory: "AI connections", workspaceConnections: () => connections);
        settings.Show(f.Window);
        try
        {
            settings.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var text = settings.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
            Assert.Contains(text, t => t.StartsWith("2 saved connections", StringComparison.Ordinal));
            Assert.Contains("My Copilot", text); Assert.Contains("My research tools", text);
            Assert.Contains(text, t => t.Contains("MCP tools", StringComparison.Ordinal));
        }
        finally { settings.Close(); }
    }

    [Fact]
    public void Blank_editing_credentials_preserve_the_stored_secret_and_duplicate_names_are_rejected()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tern-ai-save-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AIProfileStore(directory); var vault = new Vault(); var manager = new AIConnectionManager(store, vault);
            var profile = new AIConnectionProfile(Guid.NewGuid().ToString("N"), "Writing", "chat-completions", "https://example.test/custom/chat", "m", "compatible", true);
            var current = manager.Save(profile, "test-only-secret", []);
            current = manager.Save(profile with { Name = "Renamed" }, null, current);
            Assert.Equal("test-only-secret", vault.Read(profile.Id));
            Assert.Equal(profile.Id, Assert.Single(store.Load()).Id);
            Assert.True(Assert.Single(store.Load()).UseCustomEndpoint);
            Assert.Throws<ArgumentException>(() => manager.Save(profile with { Id = Guid.NewGuid().ToString("N"), Name = "RENAMED" }, "", current));
            Assert.Single(store.Load());
            Assert.DoesNotContain("test-only-secret", File.ReadAllText(store.FilePath), StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 150 && !condition(); i++) { await Task.Delay(20, TestContext.Current.CancellationToken); Dispatcher.UIThread.RunJobs(); }
        Assert.True(condition());
    }
    private sealed class Vault : IAICredentialStore
    {
        private readonly Dictionary<string, string> _keys = [];
        public string? Read(string profileId) => _keys.GetValueOrDefault(profileId);
        public void Save(string profileId, string secret) => _keys[profileId] = secret;
        public void Remove(string profileId) => _keys.Remove(profileId);
    }
}
