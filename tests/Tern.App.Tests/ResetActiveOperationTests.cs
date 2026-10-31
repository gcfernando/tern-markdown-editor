using Tern.App.Settings;
using Tern.App.Shell;

namespace Tern.App.Tests;

public sealed class ResetActiveOperationTests
{
    [Fact]
    public void Status_only_download_blocks_all_selected_reset_categories_until_it_finishes()
    {
        using var fixture = new ShellFixture();
        fixture.Shell.Settings.EditorFontSize = 24;
        fixture.Shell.Recent.AddFile(Path.Combine(fixture.Docs, "recent.md"));
        var cache = Path.Combine(fixture.Shell.Paths.StateDirectory, "remote-cache", new string('a', 64));
        Directory.CreateDirectory(cache);
        var document = Path.Combine(cache, "document.md");
        File.WriteAllText(document, "Cached before the in-flight download completes.");
        var options = new ResetOptions(RecentHistory: true, Preferences: true, RemoteCache: true);
        var operation = fixture.Shell.BeginStatusOperation();
        try
        {
            Assert.False(fixture.Shell.IsBusy);
            Assert.False(fixture.Shell.IsIndexing);
            Assert.True(fixture.Shell.HasActiveStatusOperations);
            Assert.False(fixture.Shell.ResetApplication(options));
            Assert.True(File.Exists(document));
            Assert.Equal(24, fixture.Shell.Settings.EditorFontSize);
            Assert.Single(fixture.Shell.Recent.Files);
            Assert.Contains("downloading", fixture.Shell.StatusMessage, StringComparison.Ordinal);
            Assert.Contains("Nothing was reset", fixture.Shell.StatusMessage, StringComparison.Ordinal);
        }
        finally { operation.Dispose(); }
        Assert.False(fixture.Shell.HasActiveStatusOperations);
        Assert.True(fixture.Shell.ResetApplication(options));
        Assert.False(File.Exists(document));
        Assert.Empty(fixture.Shell.Recent.Files);
        Assert.Equal(new AppSettings().EditorFontSize, fixture.Shell.Settings.EditorFontSize);
    }

    [Fact]
    public void Nested_operations_keep_reset_blocked_until_the_last_scope_is_disposed()
    {
        using var fixture = new ShellFixture();
        using var first = fixture.Shell.BeginStatusOperation();
        var second = fixture.Shell.BeginStatusOperation();
        second.Dispose();
        second.Dispose();
        Assert.True(fixture.Shell.HasActiveStatusOperations);
        Assert.False(fixture.Shell.ResetApplication(new ResetOptions()));
        first.Dispose();
        Assert.False(fixture.Shell.HasActiveStatusOperations);
        Assert.True(fixture.Shell.ResetApplication(new ResetOptions()));
    }
}
