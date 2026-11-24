using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.App.Settings;
using Tern.Desktop.Services;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class GuiPreferencesAndTutorTests
{
    private static void Click(Window window, string label) => window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, label)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public async Task Cancel_discards_mutable_pending_preferences_and_keeps_the_live_registry()
    {
        using var f = new DesktopFixture();
        var live = f.Shell.Settings; live.WorkspaceExclusions.Add("keep/");
        var originalGesture = f.Shell.Registry.GetGesture(CommandIds.Save);
        var dialog = new SettingsWindow(live, f.Shell.Registry); dialog.Show(f.Window); dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        dialog.WorkingSettings.WorkspaceExclusions.Add("pending/");
        dialog.WorkingSettings.KeyBindings[CommandIds.Save] = "Ctrl+Alt+F8";
        dialog.WorkingSettings.EditorFontSize = 24;
        Click(dialog, "Cancel");
        var prompt = Assert.Single(dialog.OwnedWindows.OfType<PromptWindow>());
        Click(prompt, "Discard changes");
        await Task.Delay(20); Dispatcher.UIThread.RunJobs();
        Assert.False(dialog.IsVisible);
        Assert.Equal(new[] { "keep/" }, live.WorkspaceExclusions);
        Assert.Equal(14, live.EditorFontSize);
        Assert.Equal(originalGesture, f.Shell.Registry.GetGesture(CommandIds.Save));
    }

    [AvaloniaFact]
    public async Task Failed_apply_keeps_pending_values_for_retry_and_cancel_keeps_the_last_successful_apply()
    {
        using var f = new DesktopFixture(); var fail = true;
        var dialog = new SettingsWindow(f.Shell.Settings, f.Shell.Registry, commit: () => { if (fail) throw new IOException("Synthetic settings store failure"); });
        dialog.Show(f.Window); dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        dialog.WorkingSettings.EditorFontSize = 23;
        Click(dialog, "Apply");
        Assert.Equal(14, f.Shell.Settings.EditorFontSize); Assert.Equal(23, dialog.WorkingSettings.EditorFontSize);
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("could not be saved", StringComparison.Ordinal) == true);
        fail = false; Click(dialog, "Apply"); Assert.Equal(23, f.Shell.Settings.EditorFontSize);
        dialog.WorkingSettings.EditorFontSize = 31; Click(dialog, "Cancel"); Assert.Equal(23, f.Shell.Settings.EditorFontSize);
        var prompt = Assert.Single(dialog.OwnedWindows.OfType<PromptWindow>());
        Click(prompt, "Discard changes");
        await Task.Delay(20); Dispatcher.UIThread.RunJobs();
        Assert.False(dialog.IsVisible);
        Assert.Equal(23, f.Shell.Settings.EditorFontSize);
    }

    [AvaloniaFact]
    public void Help_uses_the_global_persistent_choice_and_deleted_profiles_never_substitute_another_destination()
    {
        using var f = new DesktopFixture();
        var directory = f.Shell.Paths.SettingsDirectory; var store = new AIProfileStore(directory);
        AIConnectionProfile first = new("11111111111111111111111111111111", "Writing", "ollama-chat", "http://localhost:11434/api/chat", "model-one");
        AIConnectionProfile second = new("22222222222222222222222222222222", "Help", "ollama-chat", "http://localhost:11434/api/chat", "model-two");
        store.Save([first, second]);
        var workspace = new AIWorkspaceService(directory, new EmptyVault(), () => throw new InvalidOperationException("Opening Help must not create a provider request"));
        workspace.Reload(); workspace.Select(first.Id); workspace.SelectHelp(second.Id);
        var reopened = new AIWorkspaceService(directory, new EmptyVault()); reopened.Reload();
        Assert.Equal(second.Id, reopened.SelectedId); Assert.Equal(second.Id, reopened.HelpSelectedId);
        Assert.Equal(second.Id, workspace.SelectedId);
        using var tutor = new HelpTutorView(workspace, f.Shell.Registry, () => Task.CompletedTask, null, null, _ => { }, null);
        var window = new Window { Content = tutor }; window.Show(f.Window); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        tutor.Question.Text = "Keep my typed question";
        store.Save([first]); workspace.Reload(); Dispatcher.UIThread.RunJobs();
        Assert.Null(tutor.Connections.SelectedItem); Assert.Equal("Keep my typed question", tutor.Question.Text);
        Assert.Null(workspace.SelectedId); Assert.True(tutor.SendButton.IsEnabled); // Installed guides can answer offline.
        store.Save([second]); workspace.Reload(); workspace.Reload();
        Assert.Null(workspace.SelectedConnection); Assert.Null(workspace.HelpSelectedConnection);
        window.Close();
    }

    private sealed class EmptyVault : IAICredentialStore
    {
        public string? Read(string profileId) => null;
        public void Save(string profileId, string secret) { }
        public void Remove(string profileId) { }
    }
}
