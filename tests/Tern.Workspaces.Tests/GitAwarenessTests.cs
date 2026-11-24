using System.Diagnostics;

namespace Tern.Workspaces.Tests;

public sealed class GitAwarenessTests
{
    [Fact]
    public async Task Status_and_committed_comparison_handle_unicode_renames_untracked_and_literal_paths_without_changing_the_index()
    {
        using var root = new TempDirectory();
        using var gitFiles = new GitFixtureFiles(root.Path);
        await Git(root.Path, "init", "--quiet");
        root.Write("nested/note [1].md", "# Original\n");
        root.Write("rename.md", "rename this");
        await Git(root.Path, "add", "--all");
        await Git(root.Path, "-c", "user.name=Tern fixture", "-c", "user.email=fixture@example.invalid",
            "-c", "commit.gpgSign=false", "-c", "core.hooksPath=" + root.Combine("no-hooks"), "commit", "--quiet", "-m", "Synthetic baseline");
        root.Write("nested/note [1].md", "# Changed\n");
        var untracked = root.Write("nested/new \u00e9 note.md", "new");
        await Git(root.Path, "mv", "rename.md", "renamed.md");
        var index = await File.ReadAllBytesAsync(root.Combine(".git/index"));
        var status = await GitAwareness.ReadAsync(root.Combine("nested"));
        Assert.Equal(GitFileState.Modified, status.Files[root.Combine("nested/note [1].md")]);
        Assert.Equal(GitFileState.Untracked, status.Files[untracked]);
        Assert.Equal("# Original\n", await GitAwareness.ReadHeadTextAsync(root.Combine("nested/note [1].md")));
        Assert.Equal("", await GitAwareness.ReadHeadTextAsync(untracked));
        Assert.Equal(index, await File.ReadAllBytesAsync(root.Combine(".git/index")));
        var full = await GitAwareness.ReadAsync(root.Path);
        Assert.Equal(GitFileState.Modified, full.Files[root.Combine("renamed.md")]);
        Assert.Equal("# Changed\n", root.Read("nested/note [1].md"));
    }

    [Fact]
    public async Task Nonrepository_and_unborn_repository_are_explicit_and_never_require_a_commit()
    {
        using var root = new TempDirectory();
        using var gitFiles = new GitFixtureFiles(root.Path);
        Assert.Null((await GitAwareness.ReadAsync(root.Path)).RepositoryRoot);
        var path = root.Write("new.md", "new note");
        await Assert.ThrowsAsync<IOException>(() => GitAwareness.ReadHeadTextAsync(path));
        await Git(root.Path, "init", "--quiet");
        Assert.Equal(GitFileState.Untracked, (await GitAwareness.ReadAsync(root.Path)).Files[path]);
        Assert.Equal("", await GitAwareness.ReadHeadTextAsync(path));
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
