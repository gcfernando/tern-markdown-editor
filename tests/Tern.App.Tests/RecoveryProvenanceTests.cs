using System.Text.Json;
using System.Text.Json.Nodes;
using Tern.Domain.Documents;
using Tern.Domain.Text;
using Tern.Recovery;

namespace Tern.App.Tests;

public sealed class RecoveryProvenanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_checkpoint_round_trips_external_origin_without_a_source_URL(bool external)
    {
        using var directory = new TempDir();
        var store = new RecoveryStore(directory.File("recovery"));
        var record = new RecoveryRecord(Guid.NewGuid(), null, "import.md", 7, DateTime.UtcNow, null,
            TextFileFormat.NewFileDefault, "Exact text\n", external);
        await store.WriteCheckpointAsync(record);
        var restored = Assert.Single(store.Scan().Entries).Record;
        Assert.Equal(external, restored.IsExternalDraft);
        Assert.Null(restored.OriginalPath);
        Assert.Equal(record.Text, restored.Text);
        Assert.Equal(record.Format, restored.Format);
    }

    [Fact]
    public async Task Legacy_checkpoint_without_origin_field_remains_readable_and_defaults_to_local()
    {
        using var directory = new TempDir();
        var folder = directory.File("recovery");
        var store = new RecoveryStore(folder);
        await store.WriteCheckpointAsync(new RecoveryRecord(Guid.NewGuid(), null, "legacy.md", 1, DateTime.UtcNow, null,
            TextFileFormat.NewFileDefault, "Legacy exact\r\ntext", true));
        var path = Assert.Single(Directory.EnumerateFiles(folder, "*.tern-recovery"));
        var original = await File.ReadAllBytesAsync(path);
        var length = BitConverter.ToInt32(original, 8);
        var header = JsonNode.Parse(original.AsSpan(12, length))!.AsObject();
        Assert.True(header.Remove("IsExternalDraft"));
        var encodedHeader = JsonSerializer.SerializeToUtf8Bytes(header);
        await File.WriteAllBytesAsync(path, [.. original[..8], .. BitConverter.GetBytes(encodedHeader.Length), .. encodedHeader, .. original[(12 + length)..]]);
        var scan = store.Scan();
        Assert.Empty(scan.Corrupt);
        var record = Assert.Single(scan.Entries).Record;
        Assert.False(record.IsExternalDraft);
        Assert.Equal("Legacy exact\r\ntext", record.Text);
    }

    [Fact]
    public async Task Scheduler_captures_external_origin_for_unsaved_drafts_but_not_for_a_saved_local_identity()
    {
        using var directory = new TempDir();
        var store = new RecoveryStore(directory.File("recovery"));
        var session = new DocumentSession(null, "", TextFileFormat.NewFileDefault, null);
        using var scheduler = new RecoveryScheduler(store, TimeSpan.FromHours(1));
        scheduler.Track(session, () => true);
        session.ApplyEdits([TextEdit.Insert(0, "web text\n")]);
        Assert.True(await scheduler.FlushAllAsync());
        Assert.True(Assert.Single(store.Scan().Entries).Record.IsExternalDraft);
        var localPath = directory.File("saved.md");
        session.MarkSaved(session.GetSnapshot(), session.Format, new DiskBaseline(9, DateTime.UtcNow, "SAVED"), localPath);
        session.ApplyEdits([TextEdit.Insert(0, "later ")]);
        Assert.True(await scheduler.FlushAllAsync());
        var latest = Assert.Single(store.Scan().Entries).Record;
        Assert.False(latest.IsExternalDraft);
        Assert.Equal(localPath, latest.OriginalPath);
        Assert.Equal("later web text\n", latest.Text);
    }

    [Fact]
    public async Task Shell_recovery_preserves_external_origin_and_text_then_checkpoints_it_before_retiring_original()
    {
        using var fixture = new ShellFixture();
        var original = new RecoveryRecord(Guid.NewGuid(), null, "web.md", 3, DateTime.UtcNow, null,
            TextFileFormat.NewFileDefault, "Do not normalize on restore\n", true);
        await fixture.Shell.RecoveryStore.WriteCheckpointAsync(original);
        var tab = await fixture.Shell.RestoreRecoveryAsync(original);
        Assert.True(tab.IsExternalDraft);
        Assert.True(tab.IsRecovered);
        Assert.Null(tab.FilePath);
        Assert.Equal(original.Text, tab.CurrentText);
        Assert.True(tab.IsDirty);
        var scan = fixture.Shell.RecoveryStore.Scan();
        Assert.DoesNotContain(scan.Entries, e => e.Record.SessionId == original.SessionId);
        var replacement = Assert.Single(scan.Entries, e => e.Record.SessionId == tab.Session.Id).Record;
        Assert.True(replacement.IsExternalDraft);
        Assert.Null(replacement.OriginalPath);
        Assert.Equal(original.Text, replacement.Text);
    }
}
