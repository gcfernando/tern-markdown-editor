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
    public async Task Quarantined_damaged_files_are_reported_once_and_valid_checkpoints_stay()
    {
        var store = new RecoveryStore(_dir);
        var id = Guid.NewGuid();
        await store.WriteCheckpointAsync(Record(id, "valid"));
        var bad = Path.Combine(_dir, $"{Guid.NewGuid():N}.00000001.tern-recovery");
        await File.WriteAllTextAsync(bad, "garbage");

        var first = store.Scan();
        Assert.Single(first.Corrupt);
        Assert.Equal(1, store.Quarantine(first.Corrupt.Select(c => c.Path)));

        var second = store.Scan();
        Assert.Empty(second.Corrupt);
        Assert.Equal("valid", Assert.Single(second.Entries).Record.Text);
        Assert.Equal("garbage", await File.ReadAllTextAsync(Path.Combine(store.DamagedDirectory, Path.GetFileName(bad))));
    }

    [Fact]
    public async Task Quarantine_never_overwrites_an_earlier_quarantined_file()
    {
        var store = new RecoveryStore(_dir);
        var name = $"{Guid.NewGuid():N}.00000001.tern-recovery";
        for (var i = 0; i < 2; i++)
        {
            var path = Path.Combine(_dir, name);
            await File.WriteAllTextAsync(path, "garbage" + i);
            Assert.Equal(1, store.Quarantine([path]));
        }
        Assert.Equal(2, Directory.GetFiles(store.DamagedDirectory).Length);
    }

    [Fact]
    public async Task Delete_damaged_removes_only_checkpoint_files_in_the_recovery_folder()
    {
        var store = new RecoveryStore(_dir);
        var id = Guid.NewGuid();
        await store.WriteCheckpointAsync(Record(id, "valid"));
        var bad = Path.Combine(_dir, $"{Guid.NewGuid():N}.00000001.tern-recovery");
        await File.WriteAllTextAsync(bad, "garbage");
        var outside = Path.Combine(Path.GetTempPath(), "tern-outside-" + Guid.NewGuid().ToString("N") + ".tern-recovery");
        var notes = Path.Combine(_dir, "notes.txt");
        await File.WriteAllTextAsync(outside, "x");
        await File.WriteAllTextAsync(notes, "x");
        try
        {
            Assert.Equal(1, store.DeleteDamaged([bad, outside, notes]));
            Assert.False(File.Exists(bad));
            Assert.True(File.Exists(outside));
            Assert.True(File.Exists(notes));
            Assert.Equal("valid", Assert.Single(store.Scan().Entries).Record.Text);
        }
        finally { File.Delete(outside); }
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
    public async Task Retiring_a_missing_directory_or_session_never_throws_and_checkpoints_self_heal()
    {
        var store = new RecoveryStore(Path.Combine(_dir, "gone"));
        Assert.True(store.Retire(Guid.NewGuid()));

        var id = Guid.NewGuid();
        await store.WriteCheckpointAsync(Record(id, "x"));
        Directory.Delete(Path.Combine(_dir, "gone"), true);
        await store.WriteCheckpointAsync(Record(id, "y"));
        Assert.Single(store.Scan().Entries);
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
    public async Task Continuous_half_second_edits_for_thirty_seconds_produce_multiple_checkpoints()
    {
        var store = new RecoveryStore(_dir);
        var session = new Tern.Domain.Documents.DocumentSession("stream.md", "", TextFileFormat.NewFileDefault, null);
        using var scheduler = new RecoveryScheduler(store);
        scheduler.Track(session);
        var checkpoints = 0;
        scheduler.CheckpointWritten += (_, _) => Interlocked.Increment(ref checkpoints);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(30))
        {
            session.ApplyEdits([TextEdit.Insert(session.Buffer.Length, "x")]);
            await Task.Delay(500);
        }
        Assert.True(Volatile.Read(ref checkpoints) >= 2, "Continuous editing must not postpone recovery indefinitely.");
        Assert.True(await scheduler.FlushAllAsync());
        Assert.Equal(session.GetSnapshot().Text, Assert.Single(store.Scan().Entries).Record.Text);
    }

    [Fact]
    public async Task Edits_during_a_slow_write_are_checkpointed_next_without_duplicate_timer_writes()
    {
        var store = new RecoveryStore(_dir);
        var session = new Tern.Domain.Documents.DocumentSession("slow.md", "", TextFileFormat.NewFileDefault, null);
        using var scheduler = new RecoveryScheduler(store, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(80));
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        store.CheckpointPhase = phase =>
        {
            if (phase != "flushed-before-publish" || Interlocked.Increment(ref writes) != 1) return;
            started.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Synthetic write barrier timed out.");
        };
        scheduler.Track(session);
        session.ApplyEdits([TextEdit.Insert(0, "first")]);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 0; i < 10; i++)
            {
                session.ApplyEdits([TextEdit.Insert(session.Buffer.Length, " newer")]);
                await Task.Delay(15);
            }
        }
        finally { release.Set(); }
        await WaitFor(() => store.Scan().Entries.SingleOrDefault()?.Record.Text == session.GetSnapshot().Text);
        await Task.Delay(150);
        Assert.Equal(2, Volatile.Read(ref writes));
    }

    [Theory]
    [InlineData("save")]
    [InlineData("untrack")]
    [InlineData("dispose")]
    public async Task Pending_checkpoint_cannot_revive_after_save_untrack_or_disposal(string action)
    {
        var store = new RecoveryStore(_dir);
        var session = new Tern.Domain.Documents.DocumentSession("retired.md", "", TextFileFormat.NewFileDefault, null);
        using var scheduler = new RecoveryScheduler(store, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.CheckpointPhase = phase =>
        {
            if (phase != "flushed-before-publish") return;
            started.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Synthetic write barrier timed out.");
        };
        scheduler.Track(session);
        session.ApplyEdits([TextEdit.Insert(0, "unsaved")]);
        var flush = Task.Run(scheduler.FlushAllAsync);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (action == "save")
                session.MarkSaved(session.GetSnapshot(), session.Format, new Tern.Domain.Documents.DiskBaseline(1, DateTime.UtcNow, "saved"));
            else if (action == "untrack") scheduler.Untrack(session, retire: true);
            else scheduler.Dispose();
        }
        finally { release.Set(); }
        Assert.True(await flush);
        Assert.Empty(store.Scan().Entries);
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
