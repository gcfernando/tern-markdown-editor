using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class PromptSafetyTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clear_recent_confirms_exact_scope_and_preserves_pins_and_files(bool approve)
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("kept.md", "Keep these bytes");
        await f.Shell.OpenFileAsync(path);
        f.Shell.Recent.SetPinned(Tern.Workspaces.RecentKind.File, path, true);
        f.Shell.Recent.AddUrl("https://example.test/unpinned.md");
        var closed = (await f.Shell.OpenFileAsync(await f.WriteAsync("closed.md", "Closed bytes")))!;
        await f.Shell.CloseTabAsync(closed);
        var before = File.ReadAllText(Path.Combine(f.Shell.Paths.StateDirectory, Tern.Workspaces.RecentItemsStore.FileName));
        var pending = f.Window.RunCommandAsync(Tern.App.Commands.CommandIds.ClearRecent);
        var prompt = Assert.Single(f.Window.OwnedWindows.OfType<PromptWindow>());
        try
        {
            var message = Assert.Single(prompt.GetVisualDescendants().OfType<SelectableTextBlock>()).Text!;
            foreach (var scope in new[] { "recent files", "workspaces", "URLs", "Reopen Closed Tab", "pinned" })
                Assert.Contains(scope, message, StringComparison.OrdinalIgnoreCase);
            var clear = prompt.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Clear recent history"));
            Assert.Contains("danger", clear.Classes);
            Assert.False(clear.IsDefault);
            var cancel = prompt.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Cancel"));
            Assert.True(cancel.IsDefault);
            (approve ? clear : cancel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await pending;
            Assert.True(Assert.Single(f.Shell.Recent.Files, entry => entry.Path == path).Pinned);
            if (approve)
            {
                Assert.Single(f.Shell.Recent.Files);
                Assert.Empty(f.Shell.Recent.Urls);
                Assert.Null(await f.Shell.ReopenClosedTabAsync());
                var loaded = new Tern.Workspaces.RecentItemsStore(f.Shell.Paths.StateDirectory);
                Assert.True(Assert.Single(loaded.Files).Pinned);
                Assert.Empty(loaded.Urls);
            }
            else
            {
                Assert.Equal(before, File.ReadAllText(Path.Combine(f.Shell.Paths.StateDirectory, Tern.Workspaces.RecentItemsStore.FileName)));
                Assert.NotNull(await f.Shell.ReopenClosedTabAsync());
            }
            Assert.Equal("Keep these bytes", await File.ReadAllTextAsync(path));
        }
        finally { prompt.Close(); }
    }

    [AvaloniaFact]
    public void Reset_and_shortcut_reset_are_danger_actions_and_never_defaults()
    {
        using var f = new DesktopFixture();
        Window[] windows = [new ResetWindow(f.Shell), new ShortcutsWindow(f.Shell.Registry)];
        foreach (var window in windows)
        {
            try
            {
                window.Show(f.Window);
                window.UpdateLayout();
                var action = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                    b => Equals(b.Content, "Reset selected") || Equals(b.Content, "Reset all"));
                Assert.Contains("danger", action.Classes);
                Assert.DoesNotContain("accent", action.Classes);
                Assert.False(action.IsDefault);
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void AI_connection_deletion_confirms_danger_and_cancel_preserves_metadata_and_credential(bool integration)
    {
        using var f = new DesktopFixture();
        var vault = new TestVault();
        var api = new Tern.Desktop.Services.AIConnectionProfile(Guid.NewGuid().ToString("N"), "Test API", "ollama-chat",
            "http://localhost:11434/api/chat", "test-model", "ollama");
        var tools = new Tern.Desktop.Services.IntegrationProfile(Guid.NewGuid().ToString("N"), "mcp", "Test tools", "https://tools.example/mcp");
        var apiStore = new Tern.Desktop.Services.AIProfileStore(f.Shell.Paths.SettingsDirectory);
        var integrations = new Tern.Desktop.Services.IntegrationProfileStore(f.Shell.Paths.SettingsDirectory, vault);
        if (integration) integrations.Save(tools, "synthetic-token");
        else { apiStore.Save([api]); vault.Save(api.Id, "synthetic-token"); }
        using var window = new AIConnectionsWindow(f.Shell.Paths.SettingsDirectory, vault);
        window.Show(f.Window);
        try
        {
            var saved = window.GetVisualDescendants().OfType<ComboBox>()
                .Single(c => AutomationProperties.GetName(c) == "Saved AI connections");
            saved.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var delete = window.GetVisualDescendants().OfType<Button>()
                .Single(b => AutomationProperties.GetName(b) == "Delete AI connection");
            Assert.Contains("danger", delete.Classes);
            Assert.False(delete.IsDefault);
            delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var prompt = Assert.Single(window.OwnedWindows.OfType<PromptWindow>());
            var confirm = prompt.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Delete connection"));
            Assert.Contains("danger", confirm.Classes);
            Assert.False(confirm.IsDefault);
            var cancel = prompt.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Cancel"));
            Assert.True(cancel.IsDefault);
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("synthetic-token", integration ? integrations.ReadSecret(tools) : vault.Read(api.Id));
            Assert.Single(integration ? integrations.Load().Select(p => p.Id) : apiStore.Load().Select(p => p.Id));
        }
        finally
        {
            foreach (var child in window.OwnedWindows.ToArray()) child.Close();
            window.Close();
        }
    }

    private sealed class TestVault : Tern.Desktop.Services.IAICredentialStore
    {
        private readonly Dictionary<string, string> _values = [];
        public string? Read(string id) => _values.GetValueOrDefault(id);
        public void Save(string id, string value) => _values[id] = value;
        public void Remove(string id) => _values.Remove(id);
    }

    [AvaloniaFact]
    public async Task Explicit_reload_uses_a_danger_action_and_defaults_to_keep_edits()
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("reload.md", "# Saved");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([Tern.Domain.Text.TextEdit.Insert(0, "edit")]);
        var pending = new Tern.Desktop.Services.DesktopDialogs { Owner = f.Window }.ConfirmReloadDiscardingEditsAsync(tab);
        var prompt = Assert.Single(f.Window.OwnedWindows.OfType<PromptWindow>());
        var discard = Assert.Single(prompt.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Discard my edits and reload"));
        Assert.Contains("danger", discard.Classes);
        Assert.False(discard.IsDefault);
        var cancel = Assert.Single(prompt.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Cancel"));
        Assert.True(cancel.IsDefault);
        cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(await pending);
    }

    [AvaloniaTheory]
    [InlineData("Save without copying 2 outside attachments", Tern.App.Documents.OutsideAttachmentChoice.SaveWithoutCopying)]
    [InlineData("Rewrite links to absolute", Tern.App.Documents.OutsideAttachmentChoice.RewriteAbsolute)]
    [InlineData("Cancel", Tern.App.Documents.OutsideAttachmentChoice.Cancel)]
    public async Task Outside_attachment_choices_list_references_and_default_to_cancellation(string label, Tern.App.Documents.OutsideAttachmentChoice expected)
    {
        var owner = new Window { Width = 900, Height = 600 };
        owner.Show();
        try
        {
            var pending = new Tern.Desktop.Services.DesktopDialogs { Owner = owner }
                .AskOutsideAttachmentsAsync(["../images/picture.png", "../files/readme.txt"]);
            var prompt = Assert.Single(owner.OwnedWindows.OfType<PromptWindow>());
            prompt.UpdateLayout();
            var message = Assert.Single(prompt.GetVisualDescendants().OfType<SelectableTextBlock>());
            Assert.Contains("../images/picture.png", message.Text);
            Assert.Contains("../files/readme.txt", message.Text);
            Assert.Contains("may break", message.Text);
            var cancel = Assert.Single(prompt.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Cancel"));
            Assert.True(cancel.IsDefault);
            var choice = Assert.Single(prompt.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, label));
            choice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(expected, await pending);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public void Short_message_uses_content_height_without_unnecessary_scrolling()
    {
        var prompt = new PromptWindow("Confirm", "Changed on disk.", ["Compare", "Cancel"], 1, 1);
        try
        {
            prompt.Show();
            prompt.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var scroller = Assert.Single(prompt.GetVisualDescendants().OfType<ScrollViewer>(), s => s.Content is SelectableTextBlock);
            Assert.True(scroller.Extent.Height <= scroller.Viewport.Height + 1);
            Assert.True(prompt.ClientSize.Height < 300);
        }
        finally { prompt.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("close")]
    [InlineData("escape")]
    [InlineData("owner")]
    public async Task Dismissal_never_selects_a_conflict_action(string dismissal)
    {
        var owner = new Window { Width = 700, Height = 500 };
        owner.Show();
        var pending = PromptWindow.AskAsync(owner, "External changes", "Choose how to handle changed disk content.",
            ["Keep mine", "Use disk", "Merge", "Save copy", "Cancel"], 2, 4);
        var prompt = Assert.Single(owner.OwnedWindows.OfType<PromptWindow>());
        try
        {
            if (dismissal == "escape")
                prompt.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            else if (dismissal == "owner") owner.Close();
            else prompt.Close();
            Assert.Equal(4, await pending.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally { prompt.Close(); owner.Close(); }
    }

    [AvaloniaFact]
    public async Task Explicit_conflict_choice_preserves_its_index()
    {
        var owner = new Window { Width = 700, Height = 500 };
        owner.Show();
        var pending = PromptWindow.AskAsync(owner, "External changes", "Changed on disk.", ["Keep mine", "Use disk", "Cancel"], 2, 2);
        var prompt = Assert.Single(owner.OwnedWindows.OfType<PromptWindow>());
        try
        {
            var button = Assert.Single(prompt.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b) == "Use disk");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, await pending.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally { prompt.Close(); owner.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(360, 300)]
    [InlineData(460, 360)]
    public void Long_conflict_message_keeps_all_actions_inside_a_bounded_dialog(double width, double height)
    {
        var prompt = new PromptWindow("External changes", string.Join(" ", Enumerable.Repeat("Changed document at a long folder path.", 80)),
            ["Keep mine", "Use disk", "Merge", "Save copy", "Cancel"], 2, 4)
        { Width = width, MaxHeight = height };
        try
        {
            prompt.Show();
            prompt.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var buttons = prompt.GetVisualDescendants().OfType<Button>().Where(b => b.Content is "Keep mine" or "Use disk" or "Merge" or "Save copy" or "Cancel").ToArray();
            Assert.Equal(5, buttons.Length);
            foreach (var button in buttons)
            {
                var origin = button.TranslatePoint(default, prompt)!.Value;
                Assert.True(origin.Y >= 0 && origin.Y + button.Bounds.Height <= prompt.ClientSize.Height + 1,
                    $"Action {button.Content} is clipped below the dialog.");
                Assert.True(origin.X >= 0 && origin.X + button.Bounds.Width <= prompt.ClientSize.Width + 1);
            }
            var messageScroller = Assert.Single(prompt.GetVisualDescendants().OfType<ScrollViewer>(), s => s.Content is SelectableTextBlock);
            Assert.True(messageScroller.Extent.Height > messageScroller.Viewport.Height);
        }
        finally { prompt.Close(); }
    }
}
