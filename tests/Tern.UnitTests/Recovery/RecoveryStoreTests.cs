using Tern.Domain.Text;
using Tern.Recovery;

namespace Tern.UnitTests.Recovery;

public sealed class RecoveryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tern-rec-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private static RecoveryRecord Record(Guid id, string text, long revision = 1) =>
        new(id, @"C:\docs\a.md", "a.md", revision, DateTime.UtcNow, "abc", TextFileFormat.NewFileDefault with { HasBom = true }, text);

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1048577)]
    public async Task Invalid_header_lengths_are_reported_and_previous_recovery_is_offered(int length)
    {
        var store = new RecoveryStore(_dir);
        var id = Guid.NewGuid();
        await store.WriteCheckpointAsync(Record(id, "safe previous revision"));
        var malformed = Path.Combine(_dir, $"{id:N}.00000002.tern-recovery");
        await File.WriteAllBytesAsync(malformed, [.. "TERNREC1"u8.ToArray(), .. BitConverter.GetBytes(length)]);
        var scan = store.Scan();
        Assert.Equal("safe previous revision", Assert.Single(scan.Entries).Record.Text);
        Assert.Single(scan.Corrupt);
        Assert.True(File.Exists(malformed));
    }

    [Fact]
    public async Task Overlapping_scheduler_flushes_leave_the_newest_revision_recoverable()
    {
        var store = new RecoveryStore(_dir);
        var session = new Tern.Domain.Documents.DocumentSession("a.md", "", TextFileFormat.NewFileDefault, null);
        using var scheduler = new RecoveryScheduler(store, TimeSpan.FromHours(1));
        scheduler.Track(session);
        var written = new List<RecoveryRecord>();
        scheduler.CheckpointWritten += (_, record) => written.Add(record);
        session.ApplyEdits([TextEdit.Insert(0, new string('a', 500000))]);
        var first = scheduler.FlushAllAsync();
        session.ApplyEdits([TextEdit.Insert(0, "newest ")]);
        var second = scheduler.FlushAllAsync();
        Assert.All(await Task.WhenAll(first, second), success => Assert.True(success));
        Assert.Equal(session.GetSnapshot().Text, Assert.Single(store.Scan().Entries).Record.Text);
        Assert.Contains(written, r => r.Revision == session.Revision);
    }

    [Fact]
    public async Task A_checkpoint_round_trips_exactly_including_lone_surrogates()
    {
        var store = new RecoveryStore(_dir);
        var id = Guid.NewGuid();
        var text = "line\r\nwith \U0001F600 and lone \uD800 surrogate\0";

        await store.WriteCheckpointAsync(Record(id, text));
        var scan = store.Scan();

        var entry = Assert.Single(scan.Entries);
        Assert.Equal(text, entry.Record.Text);
        Assert.True(entry.Record.Format.HasBom);
        Assert.Equal(@"C:\docs\a.md", entry.Record.OriginalPath);
        Assert.True(entry.OwnerIsAlive);
        Assert.Empty(scan.Corrupt);
    }

    [Fact]
    public async Task Only_the_two_newest_generations_are_kept()
    {
        var store = new RecoveryStore(_dir);
        var id = Guid.NewGuid();

        for (var i = 1; i <= 5; i++)
        {
            await store.WriteCheckpointAsync(Record(id, $"v{i}", i));
        }

        Assert.Equal(2, Directory.GetFiles(_dir, "*.tern-recovery").Length);
        Assert.Equal("v5", Assert.Single(store.Scan().Entries).Record.Text);
    }

    [Fact]
    public async Task A_corrupt_newest_checkpoint_falls_back_to_the_previous_one()
    {
        var store = new RecoveryStore(_dir);
        var id = Guid.NewGuid();
        await store.WriteCheckpointAsync(Record(id, "good", 1));
        await store.WriteCheckpointAsync(Record(id, "newer", 2));
        var newest = Directory.GetFiles(_dir, "*.tern-recovery").OrderBy(f => f).Last();
        var bytes = await File.ReadAllBytesAsync(newest);
        bytes[^3] ^= 0xFF;
        await File.WriteAllBytesAsync(newest, bytes);

        var scan = store.Scan();

        Assert.Equal("good", Assert.Single(scan.Entries).Record.Text);
        Assert.Single(scan.Corrupt);
    }

    [Fact]
    public async Task A_truncated_checkpoint_is_reported_not_trusted()
    {
        var store = new RecoveryStore(_dir);
        var id = Guid.NewGuid();
        await store.WriteCheckpointAsync(Record(id, "some longer text to truncate"));
        var file = Directory.GetFiles(_dir, "*.tern-recovery").Single();
        var bytes = await File.ReadAllBytesAsync(file);
        await File.WriteAllBytesAsync(file, bytes[..(bytes.Length - 10)]);

        var scan = store.Scan();

        Assert.Empty(scan.Entries);
        Assert.Single(scan.Corrupt);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task An_interrupted_write_leaves_the_previous_checkpoint_readable()
    {
        var store = new RecoveryStore(_dir);
        var id = Guid.NewGuid();
        await store.WriteCheckpointAsync(Record(id, "stable", 1));
        await File.WriteAllBytesAsync(Path.Combine(_dir, $"{id:N}.00000002.tern-recovery.tmp"), [1, 2, 3]);

        Assert.Equal("stable", Assert.Single(store.Scan().Entries).Record.Text);
    }

    [Fact]
    public async Task Retiring_a_session_removes_its_checkpoints()
    {
        var store = new RecoveryStore(_dir);
        var id = Guid.NewGuid();
        await store.WriteCheckpointAsync(Record(id, "x"));

        store.Retire(id);

        Assert.Empty(store.Scan().Entries);
    }

    [Fact]
    public async Task Garbage_files_in_the_directory_are_ignored()
    {
        var store = new RecoveryStore(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "notes.txt"), "hi");
        await File.WriteAllBytesAsync(Path.Combine(_dir, $"{Guid.NewGuid():N}.00000001.tern-recovery"), [0, 1, 2]);

        var scan = store.Scan();

        Assert.Empty(scan.Entries);
        Assert.Single(scan.Corrupt);
    }

    [Fact]
    public async Task The_scheduler_checkpoints_dirty_documents_and_retires_clean_ones()
    {
        var store = new RecoveryStore(_dir);
        var session = new Tern.Domain.Documents.DocumentSession("a.md", "hello", TextFileFormat.NewFileDefault, null);
        using var scheduler = new RecoveryScheduler(store, TimeSpan.FromMilliseconds(30));
        scheduler.Track(session);

        session.ApplyEdits([TextEdit.Insert(5, " world")]);
        await WaitFor(() => store.Scan().Entries.Count == 1);
        Assert.Equal("hello world", store.Scan().Entries[0].Record.Text);

        session.MarkSaved(session.GetSnapshot(), session.Format, new Tern.Domain.Documents.DiskBaseline(1, DateTime.UtcNow, "x"));
        await WaitFor(() => store.Scan().Entries.Count == 0);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.True(condition(), "Condition was not met in time.");
    }
}
