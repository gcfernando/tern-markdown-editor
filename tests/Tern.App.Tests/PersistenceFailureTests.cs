using Tern.App.Documents;
using Tern.Domain.Text;
using Tern.Filesystem;
using Tern.Markdown;
using Tern.Recovery;

namespace Tern.App.Tests;

public sealed class PersistenceFailureTests
{
    [Theory]
    [InlineData("temp-written-before-flush")]
    [InlineData("flushed-before-replace")]
    public async Task Failed_backing_save_retains_dirty_text_and_a_recoverable_checkpoint(string phase)
    {
        using var directory = new TempDir();
        var path = directory.File("source.md");
        await File.WriteAllTextAsync(path, "backing source\r\n");
        var files = new DocumentFileService();
        using var tab = await DocumentTab.OpenAsync(path, files, MarkdownProfile.Gfm);
        tab.ApplyEdits([TextEdit.Insert(0, "local edits ")]);
        var revision = tab.Session.Revision;
        var store = new RecoveryStore(directory.File("recovery"));
        using var scheduler = new RecoveryScheduler(store, TimeSpan.FromHours(1));
        scheduler.Track(tab.Session);
        files.SavePhase = reached =>
        {
            if (reached == phase) throw new IOException("Injected disk-full-style write failure.");
        };

        Assert.IsType<SaveFailed>(await tab.SaveAsync());
        Assert.True(tab.IsDirty);
        Assert.Equal(revision, tab.Session.Revision);
        Assert.Equal(0, tab.Session.SavedRevision);
        Assert.NotNull(tab.LastSaveError);
        Assert.Equal("backing source\r\n", await File.ReadAllTextAsync(path));
        Assert.True(await scheduler.FlushAllAsync());
        var checkpoint = Assert.Single(store.Scan().Entries).Record;
        Assert.Equal(tab.CurrentText, checkpoint.Text);
        Assert.Equal(revision, checkpoint.Revision);
    }

    [Fact]
    public async Task Failed_successor_checkpoint_does_not_retire_the_original_recovery()
    {
        using var fixture = new ShellFixture();
        var original = new RecoveryRecord(Guid.NewGuid(), null, "recover me.md", 7, DateTime.UtcNow, null,
            TextFileFormat.NewFileDefault, "only recoverable revision");
        await fixture.Shell.RecoveryStore.WriteCheckpointAsync(original);
        fixture.Shell.RecoveryStore.CheckpointPhase = reached =>
        {
            if (reached == "payload-written-before-flush") throw new IOException("Injected checkpoint persistence failure.");
        };

        var restored = await fixture.Shell.RestoreRecoveryAsync(original);

        Assert.True(restored.IsDirty);
        Assert.True(restored.IsUntitled);
        Assert.Equal(original.Text, restored.CurrentText);
        Assert.NotNull(restored.RecoveryError);
        Assert.Contains(fixture.Shell.RecoveryStore.Scan().Entries, entry => entry.Record.SessionId == original.SessionId && entry.Record.Text == original.Text);
        Assert.Null(restored.CheckpointRevision);
    }
}
