using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.Services;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class UnifiedWorkspaceConnectionTests
{
    [Fact]
    public void All_connection_kinds_reload_and_remember_the_selected_connection()
    {
        using var folder = new Folder(); var vault = new Vault();
        var api = new AIConnectionProfile(Guid.NewGuid().ToString("N"), "API", "ollama-chat", "http://localhost:11434/api/chat", "local-model");
        new AIProfileStore(folder.Path).Save([api]);
        var integrations = new IntegrationProfileStore(folder.Path, vault);
        var copilot = Profile(folder, "copilot"); var mcp = Profile(folder, "mcp");
        integrations.Save(copilot, "copilot-token"); integrations.Save(mcp, "mcp-token");
        var workspace = new AIWorkspaceService(folder.Path, vault); workspace.Reload();
        Assert.Equal(3, workspace.Connections.Count); Assert.Single(workspace.Profiles);
        workspace.Select(copilot.Id);
        var reopened = new AIWorkspaceService(folder.Path, vault); reopened.Reload();
        Assert.Equal(copilot.Id, reopened.SelectedId); Assert.True(reopened.SelectedConnection!.CanWrite);
        reopened.Select(mcp.Id); Assert.False(reopened.SelectedConnection!.CanWrite);
        integrations.Remove(mcp.Id); reopened.Reload();
        Assert.DoesNotContain(reopened.Connections, p => p.Id == mcp.Id);
        Assert.NotNull(reopened.SelectedConnection);
    }

    [Fact]
    public async Task Copilot_writing_probes_then_sends_exact_request_using_saved_credentials()
    {
        using var folder = new Folder(); var vault = new Vault(); var profile = Profile(folder, "copilot");
        File.WriteAllText(profile.Destination, "synthetic"); new IntegrationProfileStore(folder.Path, vault).Save(profile, "private-token");
        var prompts = new List<string>();
        var workspace = new AIWorkspaceService(folder.Path, vault, createCopilotClient: () => new CopilotTextClient((start, _) =>
        {
            Assert.Equal("private-token", start.Environment["COPILOT_GITHUB_TOKEN"]);
            prompts.Add(start.ArgumentList.Single(a => a.StartsWith("--prompt=", StringComparison.Ordinal)));
            return Task.FromResult(new CopilotProcessResult(0, prompts.Count == 1 ? "OK" : "# Updated"));
        }));
        workspace.Reload();
        var request = AIWorkspaceService.WritingMessage("Improve it", "# Original");
        Assert.Null(workspace.WritingValidationError(profile.Id, request));
        Assert.Equal("# Updated", await workspace.SendWritingAsync(profile.Id, request, () => true, TestContext.Current.CancellationToken));
        Assert.Equal(["--prompt=" + AIConnectionClient.ValidationMessage, "--prompt=" + request], prompts);
    }

    [Fact]
    public async Task Copilot_unsupported_mentions_are_rejected_before_any_probe()
    {
        using var folder = new Folder(); var vault = new Vault(); var profile = Profile(folder, "copilot");
        File.WriteAllText(profile.Destination, "synthetic"); new IntegrationProfileStore(folder.Path, vault).Save(profile, "token");
        var calls = 0;
        var workspace = new AIWorkspaceService(folder.Path, vault, createCopilotClient: () => new CopilotTextClient((_, _) =>
        { calls++; return Task.FromResult(new CopilotProcessResult(0, "OK")); }));
        workspace.Reload(); var request = AIWorkspaceService.WritingMessage("Improve", "Email name@example.test");
        Assert.Contains("@mentions", workspace.WritingValidationError(profile.Id, request));
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.SendWritingAsync(profile.Id, request, () => true, TestContext.Current.CancellationToken));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Changed_target_after_Copilot_probe_never_sends_document_and_MCP_never_generates()
    {
        using var folder = new Folder(); var vault = new Vault(); var copilot = Profile(folder, "copilot"); var mcp = Profile(folder, "mcp");
        File.WriteAllText(copilot.Destination, "synthetic"); var store = new IntegrationProfileStore(folder.Path, vault);
        store.Save(copilot, "token"); store.Save(mcp, ""); var targetCurrent = true; var calls = 0;
        var workspace = new AIWorkspaceService(folder.Path, vault, createCopilotClient: () => new CopilotTextClient((_, _) =>
        { calls++; targetCurrent = false; return Task.FromResult(new CopilotProcessResult(0, "OK")); }));
        workspace.Reload();
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.SendWritingAsync(copilot.Id, "document", () => targetCurrent, TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
        Assert.Contains("tool server", workspace.WritingValidationError(mcp.Id, "document"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.SendAsync(mcp.Id, "document", TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }

    [AvaloniaFact]
    public async Task Copilot_updates_the_note_in_one_undoable_edit()
    {
        using var folder = new Folder(); var vault = new Vault(); var profile = Profile(folder, "copilot");
        File.WriteAllText(profile.Destination, "synthetic"); var calls = 0;
        using var f = new DesktopFixture(directory =>
        {
            new IntegrationProfileStore(directory, vault).Save(profile, "token");
            return new AIWorkspaceService(directory, vault, createCopilotClient: () => new CopilotTextClient((_, _) =>
            { calls++; return Task.FromResult(new CopilotProcessResult(0, calls == 1 ? "OK" : "# Better draft")); }));
        });
        var note = f.Shell.NewDocument("# Original"); f.Window.ShowAIWritingPrompt();
        f.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var panel = Assert.Single(f.Window.OverlayLayer.Children.OfType<AIPromptPanel>());
        panel.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "AI writing question").Text = "Improve";
        panel.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Generate and update Markdown")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 150 && f.Window.IsOverlayOpen; i++)
        { await Task.Delay(20, TestContext.Current.CancellationToken); Dispatcher.UIThread.RunJobs(); }
        Assert.False(f.Window.IsOverlayOpen); Assert.Equal("# Better draft", note.CurrentText); Assert.Equal(2, calls);
        await f.Window.RunCommandAsync(Tern.App.Commands.CommandIds.Undo); Assert.Equal("# Original", note.CurrentText);
    }

    [AvaloniaFact]
    public void Saving_modifying_and_removing_integrations_updates_the_main_toolbar_immediately()
    {
        using var f = new DesktopFixture(); var vault = new Vault();
        var store = new IntegrationProfileStore(f.Shell.Paths.SettingsDirectory, vault);
        var profile = new IntegrationProfile(Guid.NewGuid().ToString("N"), "mcp", "Research tools", "https://tools.example/mcp");
        store.Save(profile, ""); Dispatcher.UIThread.RunJobs();
        Assert.True(f.Window.AIBar.IsVisible);
        Assert.Equal(profile.Id, Assert.IsType<WorkspaceConnection>(f.Window.AIConnectionPicker.SelectedItem).Id);
        Assert.Equal("Open MCP tools", f.Window.AIConnectionAction.Content);
        store.Save(profile with { Name = "Renamed tools" }, ""); Dispatcher.UIThread.RunJobs();
        Assert.Equal("Renamed tools", Assert.IsType<WorkspaceConnection>(f.Window.AIConnectionPicker.SelectedItem).Name);
        f.Window.AIConnectionAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        var tools = Assert.Single(f.Window.OwnedWindows.OfType<MCPConnectionsWindow>());
        Assert.Equal(profile.Destination, tools.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "MCP server endpoint").Text);
        tools.Close();
        store.Remove(profile.Id); Dispatcher.UIThread.RunJobs(); Assert.False(f.Window.AIBar.IsVisible);
    }

    private static IntegrationProfile Profile(Folder folder, string kind) => new(Guid.NewGuid().ToString("N"), kind, kind,
        kind == "mcp" ? "https://tools.example/mcp" : System.IO.Path.Combine(folder.Path, "copilot.exe"));
    private sealed class Vault : IAICredentialStore
    {
        private readonly Dictionary<string, string> _values = [];
        public string? Read(string id) => _values.GetValueOrDefault(id);
        public void Save(string id, string secret) => _values[id] = secret;
        public void Remove(string id) => _values.Remove(id);
    }
    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tern-unified-" + Guid.NewGuid().ToString("N"));
        public Folder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
