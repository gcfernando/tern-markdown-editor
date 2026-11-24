namespace Tern.App.Tests;

public sealed class SaveFragmentShellTests
{
    [Fact]
    public async Task Opening_a_workspace_lists_a_stale_backup_with_a_review_banner_and_sweeps_stale_temporary_files()
    {
        using var fixture = new ShellFixture();
        await fixture.WriteAsync("x.md", "# Note\n");
        var guid = Guid.NewGuid().ToString("N");
        var backup = await fixture.WriteAsync($".x.md.{guid}.tern-save-backup", "competing text\n");
        var temp = await fixture.WriteAsync($".x.md.{guid}.tern-tmp", "half written");
        File.SetLastWriteTimeUtc(backup, DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(temp, DateTime.UtcNow.AddDays(-3));

        Assert.True(await fixture.Shell.OpenWorkspaceAsync(fixture.Docs));
        await fixture.Shell.SaveFragmentScanTask;

        var fragment = Assert.Single(fixture.Shell.SaveFragments);
        Assert.Equal(backup, fragment.Path);
        Assert.False(File.Exists(temp));
        Assert.Contains(fixture.Shell.Banner!.Actions, a => a.Label == "Review");
        fixture.Shell.ForgetSaveFragment(backup);
        Assert.Empty(fixture.Shell.SaveFragments);
    }
}
