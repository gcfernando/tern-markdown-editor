using System.Diagnostics;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.Desktop.ViewModels;
using Tern.Desktop.Views;
using Tern.Domain.Text;

namespace Tern.Desktop.Tests;

public sealed class GitWorkflowTests
{
    [AvaloniaFact]
    public async Task Actual_library_markers_and_compare_command_show_committed_disk_and_unsaved_differences_without_writing_git()
    {
        using var f = new DesktopFixture();
        using var gitFiles = new GitFixtureFiles(f.Docs);
        await Git(f.Docs, "init", "--quiet");
        var path = await f.WriteAsync("note.md", "# Committed\n");
        await Git(f.Docs, "add", "note.md");
        await Git(f.Docs, "-c", "user.name=Tern fixture", "-c", "user.email=fixture@example.invalid",
            "-c", "commit.gpgSign=false", "-c", "core.hooksPath=" + Path.Combine(f.Docs, "no-hooks"), "commit", "--quiet", "-m", "Synthetic baseline");
        var indexPath = Path.Combine(f.Docs, ".git", "index");
        var index = await File.ReadAllBytesAsync(indexPath);
        await File.WriteAllTextAsync(path, "# Changed on disk\n");
        var untracked = await f.WriteAsync("new.md", "untracked");
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));
        var tree = f.Window.FindControl<TreeView>("FileTree")!;
        IEnumerable<TreeRow> Rows() => tree.ItemsSource!.Cast<TreeRow>().SelectMany(root => root.Flatten());
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (!Rows().Any(row => row.FullPath == path && row.HasGitChange) && DateTime.UtcNow < deadline)
        { await Task.Delay(25); Dispatcher.UIThread.RunJobs(); f.Window.UpdateLayout(); }
        Assert.Equal("Changed since last commit", Rows().Single(row => row.FullPath == path).GitState);
        Assert.Equal("Untracked in Git", Rows().Single(row => row.FullPath == untracked).GitState);
        Assert.Contains(tree.GetVisualDescendants().OfType<Control>(), control => AutomationProperties.GetName(control) == "Changed since last commit" && control.IsVisible);
        var tab = (await f.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(tab.CurrentText.Length, "unsaved addition")]);
        var command = f.Window.RunCommandAsync(CommandIds.GitDiff);
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (!f.Window.OwnedWindows.OfType<GitDiffWindow>().Any() && DateTime.UtcNow < deadline)
        { await Task.Delay(25); Dispatcher.UIThread.RunJobs(); }
        var dialog = Assert.Single(f.Window.OwnedWindows.OfType<GitDiffWindow>());
        try
        {
            dialog.UpdateLayout();
            var diff = Assert.Single(dialog.GetVisualDescendants().OfType<DiffView>());
            Assert.Equal("# Committed\n", diff.LeftSource);
            Assert.Equal("# Changed on disk\nunsaved addition", diff.RightSource);
            Assert.True(diff.CanStep);
        }
        finally { dialog.Close(); await command; }
        Assert.Equal("# Changed on disk\n", await File.ReadAllTextAsync(path));
        Assert.Equal(index, await File.ReadAllBytesAsync(indexPath));
        Assert.True(tab.IsDirty);
    }

    private static async Task Git(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-C"); start.ArgumentList.Add(root);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token));
        Assert.True(process.ExitCode == 0, await error);
    }

    private sealed class GitFixtureFiles(string root) : IDisposable
    {
        public void Dispose()
        {
            var git = Path.Combine(root, ".git");
            if (!Directory.Exists(git)) return;
            foreach (var file in Directory.EnumerateFiles(git, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        }
    }
}
