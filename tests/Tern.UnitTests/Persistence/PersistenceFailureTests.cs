using Tern.Domain.Text;
using Tern.Filesystem;
using Tern.Recovery;
using Tern.UnitTests.Filesystem;

namespace Tern.UnitTests.Persistence;

public sealed class PersistenceFailureTests
{
    [Theory]
    [InlineData("temp-written-before-flush", false)]
    [InlineData("temp-written-before-flush", true)]
    [InlineData("flushed-before-replace", false)]
    [InlineData("flushed-before-replace", true)]
    [InlineData("replaced-before-verify", false)]
    [InlineData("replaced-before-verify", true)]
    public async Task Save_reports_injected_io_or_permission_failure_and_preserves_both_versions(string phase, bool permissionDenied)
    {
        using var directory = new TempDirectory();
        var path = directory.File("source.md");
        await File.WriteAllTextAsync(path, "original disk\r\n");
        var files = new DocumentFileService();
        var loaded = await files.LoadAsync(path);
        files.SavePhase = reached =>
        {
            if (reached == phase) ThrowWriteFailure(permissionDenied);
        };

        var result = Assert.IsType<SaveFailed>(await files.SaveAsync(path, "unsaved revision\n", loaded.Format, loaded.Baseline));
        if (permissionDenied) Assert.IsType<UnauthorizedAccessException>(result.Exception);
        else Assert.IsType<IOException>(result.Exception);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tern-tmp"));
        var backups = Directory.GetFiles(directory.Path, "*.tern-save-backup");
        if (phase == "replaced-before-verify")
        {
            var backup = Assert.Single(backups);
            Assert.Equal("original disk\r\n", await File.ReadAllTextAsync(backup));
            Assert.Equal("unsaved revision\n", await File.ReadAllTextAsync(path));
            Assert.Contains(backup, result.Reason, StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(backups);
            Assert.Equal("original disk\r\n", await File.ReadAllTextAsync(path));
        }
    }

    [Theory]
    [InlineData("payload-written-before-flush", false)]
    [InlineData("payload-written-before-flush", true)]
    [InlineData("flushed-before-publish", false)]
    [InlineData("flushed-before-publish", true)]
    [InlineData("published-before-prune", false)]
    [InlineData("published-before-prune", true)]
    public async Task Checkpoint_failure_keeps_the_previous_generation_and_never_trusts_a_partial_file(string phase, bool permissionDenied)
    {
        using var directory = new TempDirectory();
        var store = new RecoveryStore(directory.Path);
        var id = Guid.NewGuid();
        await store.WriteCheckpointAsync(Checkpoint(id, "previous checkpoint", 1));
        var previous = Assert.Single(Directory.GetFiles(directory.Path, "*.tern-recovery"));
        store.CheckpointPhase = reached =>
        {
            if (reached == phase) ThrowWriteFailure(permissionDenied);
        };

        var failure = await Record.ExceptionAsync(() => store.WriteCheckpointAsync(Checkpoint(id, "new checkpoint", 2)));
        if (permissionDenied) Assert.IsType<UnauthorizedAccessException>(failure);
        else Assert.IsType<IOException>(failure);
        Assert.True(File.Exists(previous));
        var scan = store.Scan();
        Assert.Empty(scan.Corrupt);
        Assert.Equal(phase == "published-before-prune" ? "new checkpoint" : "previous checkpoint", Assert.Single(scan.Entries).Record.Text);
    }

    internal static RecoveryRecord Checkpoint(Guid id, string text, long revision) =>
        new(id, null, "crash-fixture.md", revision, DateTime.UtcNow, null, TextFileFormat.NewFileDefault, text);

    private static void ThrowWriteFailure(bool permissionDenied)
    {
        if (permissionDenied) throw new UnauthorizedAccessException("Injected permission denial at the persistence boundary.");
        throw new IOException("Injected I/O failure at the persistence boundary (for example disk full).", unchecked((int)0x80070070));
    }
}
