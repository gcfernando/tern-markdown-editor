using Tern.App.Shell;
using Tern.Domain.Text;
using Tern.Workspaces;

namespace Tern.App.Tests;

public sealed class WorkflowPolishTests
{
    [Fact]
    public async Task First_save_keeps_an_unexpected_error_visible_and_records_the_reveal_separately()
    {
        using var f = new ShellFixture();
        var tab = f.Shell.NewDocument("Keep the writing");
        tab.Session.StateChanged += (_, _) => throw new InvalidOperationException("Synthetic state listener failure");
        f.Dialogs.SavePath = Path.Combine(f.Docs, "saved.md");
        Assert.True(await f.Shell.SaveTabAsync(tab));
        Assert.Equal("Keep the writing", await File.ReadAllTextAsync(f.Dialogs.SavePath));
        Assert.False(tab.IsDirty);
        Assert.Equal(StatusSeverity.Error, f.Shell.Banner!.Severity);
        var saved = Assert.Single(f.Shell.Activities.Items, item => item.Phase.StartsWith("Saved to "));
        Assert.Single(saved.Actions).Invoke();
        Assert.Equal([f.Dialogs.SavePath], f.Launcher.Revealed);
    }

    [Fact]
    public async Task First_save_reveal_is_nonmodal_and_retained_in_activity()
    {
        using var f = new ShellFixture();
        var tab = f.Shell.NewDocument("Keep the writing");
        f.Dialogs.SavePath = Path.Combine(f.Docs, "saved.md");
        Assert.True(await f.Shell.SaveTabAsync(tab));
        Assert.Equal("Keep the writing", await File.ReadAllTextAsync(f.Dialogs.SavePath));
        Assert.Equal(StatusSeverity.Success, f.Shell.Banner!.Severity);
        Assert.Contains("Saved to " + f.Docs, f.Shell.Banner.Text);
        var reveal = Assert.Single(f.Shell.Banner.Actions);
        Assert.Equal("Show in folder", reveal.Label);
        reveal.Invoke();
        Assert.Equal([f.Dialogs.SavePath], f.Launcher.Revealed);
        f.Shell.DismissBanner();
        Assert.Contains(f.Shell.Activities.Items, item => item.Actions.Any(action => action.Label == "Show in folder"));
        Assert.Empty(f.Dialogs.Messages);
    }

    [Fact]
    public async Task Import_preserves_original_bytes_and_never_replaces_a_collision()
    {
        using var f = new ShellFixture();
        var destination = Path.Combine(f.Docs, "Workspace");
        Assert.True(await f.Shell.CreateWorkspaceAsync(destination));
        var bytes = new byte[] { 0xFF, 0xFE, 0x23, 0, 0x20, 0, 0x41, 0, 0x0D, 0, 0x0A, 0 };
        var source = Path.Combine(f.Docs, "source.md");
        await File.WriteAllBytesAsync(source, bytes);
        f.Dialogs.OpenPaths = [source, source];
        Assert.Equal(1, await f.Shell.ImportFilesToWorkspaceAsync(f.Shell.Workspace!));
        var copy = Path.Combine(destination, "source.md");
        Assert.Equal(bytes, await File.ReadAllBytesAsync(copy));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
        await File.WriteAllTextAsync(copy, "Existing destination");
        Assert.Equal(0, await f.Shell.ImportFilesToWorkspaceAsync(f.Shell.Workspace!));
        Assert.Equal("Existing destination", await File.ReadAllTextAsync(copy));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
        Assert.Contains(f.Shell.Activities.Items, item => item.State == ActivityState.Failed && item.Details!.Contains("already exists"));
        Assert.Empty(Directory.GetFiles(destination, ".tern-import-*.tmp"));
    }

    [Fact]
    public async Task Restore_reports_changed_files_once_uses_the_disk_baseline_and_ignores_touch_only_changes()
    {
        using var f = new ShellFixture();
        var changed = await f.WriteAsync("changed.md", "# Before");
        var alsoChanged = await f.WriteAsync("also-changed.md", "# Before");
        var touched = await f.WriteAsync("touched.md", "# Unchanged");
        var dirtyPath = await f.WriteAsync("unsaved.md", "# Saved text");
        await f.Shell.OpenFileAsync(changed);
        await f.Shell.OpenFileAsync(alsoChanged);
        await f.Shell.OpenFileAsync(touched);
        var dirty = (await f.Shell.OpenFileAsync(dirtyPath))!;
        dirty.ApplyEdits([TextEdit.Insert(0, "Unsaved, not disk content: ")]);
        Assert.True(f.Shell.SaveSession());
        var snapshot = new SessionStore(f.Shell.Paths.StateDirectory).Load();
        Assert.All(snapshot.Tabs, tab => Assert.Equal(64, tab.DiskSha256!.Length));
        f.Shell.Dispose();
        await File.WriteAllTextAsync(changed, "# After");
        await File.WriteAllTextAsync(alsoChanged, "# Also after");
        File.SetLastWriteTimeUtc(touched, File.GetLastWriteTimeUtc(touched).AddSeconds(10));
        using var restored = new ShellViewModel(f.Shell.Paths, new ScriptedDialogs(), new RecordingLauncher());
        await restored.RestoreSessionAsync();
        var activity = Assert.Single(restored.Activities.Items, item => item.Phase.Contains("changed on disk since"));
        Assert.StartsWith("2 documents", activity.Phase);
        Assert.Contains(changed, activity.Details);
        Assert.Contains(alsoChanged, activity.Details);
        Assert.DoesNotContain(touched, activity.Details);
        Assert.DoesNotContain(dirtyPath, activity.Details);
        Assert.Equal("# After", restored.Tabs.Single(tab => tab.FilePath == changed).CurrentText);
        Assert.Equal("# Saved text", restored.Tabs.Single(tab => tab.FilePath == dirtyPath).CurrentText);
        Assert.All(restored.Tabs, tab => Assert.False(tab.IsDirty));
        Assert.Single(activity.Actions).Invoke();
        Assert.Equal(changed, restored.ActiveTab!.FilePath);
    }
}
