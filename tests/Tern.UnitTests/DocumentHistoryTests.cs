using Tern.Recovery;

namespace Tern.UnitTests;

public sealed class DocumentHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tern-history-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Oversized_history_reports_unavailability_and_preserves_existing_versions()
    {
        var path = Path.Combine(_root, "large.md");
        using var store = new DocumentHistoryStore(_root, byteBudget: 1024 * 1024);
        await store.CaptureAsync(path, "previous text");
        var failure = await Assert.ThrowsAsync<IOException>(() => store.CaptureAsync(path, new string('x', 1024 * 1024)));
        Assert.Contains("Version history is unavailable", failure.Message);
        Assert.Contains("Settings", failure.Message);
        Assert.Equal("previous text", Assert.Single(await store.ReadAsync(path)).Text);
    }

    [Fact]
    public async Task Versions_survive_restart_and_duplicate_saves_do_not_add_entries()
    {
        var path = Path.Combine(_root, "notes.md");
        var store = new DocumentHistoryStore(_root);
        await store.CaptureAsync(path, "# Original\r\n🍎\n");
        await store.CaptureAsync(path, "# Changed\r\n🍎\n");
        await store.CaptureAsync(path, "# Changed\r\n🍎\n");
        var versions = await new DocumentHistoryStore(_root).ReadAsync(path);
        Assert.Equal(2, versions.Count);
        Assert.Equal("# Changed\r\n🍎\n", versions[0].Text);
        Assert.Equal("# Original\r\n🍎\n", versions[1].Text);
        Assert.Empty(await store.ReadAsync(Path.Combine(_root, "other.md")));
    }

    [Fact]
    public async Task Retention_keeps_the_thirty_latest_versions()
    {
        var path = Path.Combine(_root, "notes.md");
        var store = new DocumentHistoryStore(_root);
        for (var i = 0; i < 35; i++) await store.CaptureAsync(path, "Version " + i);
        var versions = await store.ReadAsync(path);
        Assert.Equal(30, versions.Count);
        Assert.Equal("Version 34", versions[0].Text);
        Assert.Equal("Version 5", versions[^1].Text);
    }

    [Fact]
    public async Task History_is_hidden_from_a_different_file_recreated_at_the_same_path_and_swept_when_old()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "notes.md");
        await File.WriteAllTextAsync(path, "one");
        var store = new DocumentHistoryStore(_root);
        await store.CaptureAsync(path, "one");
        await File.WriteAllTextAsync(path, "two");
        Assert.Single(await store.ReadAsync(path));

        File.Delete(path);
        await File.WriteAllTextAsync(path, "unrelated");
        File.SetCreationTimeUtc(path, DateTime.UtcNow.AddHours(1));
        Assert.Empty(await store.ReadAsync(path));

        await store.CaptureAsync(path, "unrelated");
        var versions = await store.ReadAsync(path);
        Assert.Equal("unrelated", Assert.Single(versions).Text);

        File.Delete(path);
        Assert.Equal(0, await store.SweepAsync(TimeSpan.FromDays(90)));
        Assert.True(await store.SweepAsync(TimeSpan.FromDays(90), DateTime.UtcNow.AddDays(100)) >= 2);
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Rename_moves_saved_history_and_merges_existing_destination_versions()
    {
        var oldPath = Path.Combine(_root, "old.md");
        var newPath = Path.Combine(_root, "new.md");
        var store = new DocumentHistoryStore(_root);
        await store.CaptureAsync(oldPath, "old history");
        await store.CaptureAsync(newPath, "earlier occupant");
        await store.RenameAsync(oldPath, newPath);
        Assert.Empty(await store.ReadAsync(oldPath));
        var versions = await store.ReadAsync(newPath);
        Assert.Equal(2, versions.Count);
        Assert.Contains(versions, v => v.Text == "old history");
        Assert.Contains(versions, v => v.Text == "earlier occupant");
    }

    [Fact]
    public async Task Tampered_history_is_reported_instead_of_restored()
    {
        // B-02: a damaged entry is skipped and reported (previously ReadAsync threw InvalidDataException for the whole history).
        var path = Path.Combine(_root, "notes.md");
        var store = new DocumentHistoryStore(_root);
        await store.CaptureAsync(path, "original");
        var file = Assert.Single(Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories));
        var json = await File.ReadAllTextAsync(file);
        await File.WriteAllTextAsync(file, json.Replace("original", "tampered", StringComparison.Ordinal));
        Assert.Empty(await store.ReadAsync(path));
        var read = await store.ReadWithDamageAsync(path);
        Assert.Empty(read.Versions);
        Assert.Single(read.Damaged);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Damaged_newest_entry_is_set_aside_and_capture_continues(bool tampered)
    {
        var path = Path.Combine(_root, "notes.md");
        var store = new DocumentHistoryStore(_root);
        await store.CaptureAsync(path, "one");
        await store.CaptureAsync(path, "two");
        var newest = Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).Last();
        var damaged = tampered
            ? (await File.ReadAllTextAsync(newest)).Replace("two", "tampered", StringComparison.Ordinal)
            : string.Empty;
        await File.WriteAllTextAsync(newest, damaged);
        var result = await store.CaptureAsync(path, "three");
        Assert.True(result.Added);
        Assert.Equal(1, result.DamagedSetAside);
        var versions = await store.ReadAsync(path);
        Assert.Equal(["three", "one"], versions.Select(v => v.Text));
        Assert.Single(Directory.GetFiles(_root, "*.corrupt", SearchOption.AllDirectories));
        Assert.Single((await store.ReadWithDamageAsync(path)).Damaged);
    }

    [Fact]
    public async Task Rename_succeeds_when_an_entry_is_damaged_and_labels_round_trip()
    {
        var oldPath = Path.Combine(_root, "old.md");
        var newPath = Path.Combine(_root, "new.md");
        var store = new DocumentHistoryStore(_root);
        await store.CaptureAsync(oldPath, "first", "Original (before first Tern save)");
        await store.CaptureAsync(oldPath, "second");
        var damaged = Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).First();
        await File.WriteAllTextAsync(damaged, "{ not json");
        await store.RenameAsync(oldPath, newPath);
        var read = await store.ReadWithDamageAsync(newPath);
        Assert.Equal(["second"], read.Versions.Select(v => v.Text));
        Assert.Single(read.Damaged);
        Assert.Empty((await store.ReadWithDamageAsync(oldPath)).Versions);
    }

    [Fact]
    public async Task Label_is_stored_and_empty_history_label_applies_only_to_the_first_entry()
    {
        var path = Path.Combine(_root, "notes.md");
        var store = new DocumentHistoryStore(_root);
        var first = await store.CaptureAsync(path, "disk text", "Version before this save", "Original (before first Tern save)");
        Assert.True(first.HistoryWasEmpty);
        await store.CaptureAsync(path, "newer", "Version before this save", "Original (before first Tern save)");
        var versions = await store.ReadAsync(path);
        Assert.Equal("Version before this save", versions[0].Label);
        Assert.Equal("Original (before first Tern save)", versions[1].Label);
    }

    private string FolderOf(string path) =>
        Path.Combine(_root, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()))));

    private void PlantVersion(string path, DateTime savedUtc, string text)
    {
        var folder = FolderOf(path);
        Directory.CreateDirectory(folder);
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        File.WriteAllBytes(Path.Combine(folder, $"{savedUtc.Ticks:D19}-{Guid.NewGuid():N}.json"),
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new DocumentVersion(savedUtc, text, digest)));
    }

    [Fact]
    public async Task Frequent_saving_never_evicts_yesterdays_version()
    {
        var path = Path.Combine(_root, "notes.md");
        PlantVersion(path, DateTime.UtcNow.AddHours(-26), "yesterday");
        var store = new DocumentHistoryStore(_root);
        for (var i = 0; i < 100; i++) await store.CaptureAsync(path, "save " + i);
        var versions = await store.ReadAsync(path);
        Assert.Contains(versions, v => v.Text == "yesterday");
        Assert.Equal(31, versions.Count);
        Assert.Equal("save 99", versions[0].Text);
    }

    [Fact]
    public async Task Older_versions_thin_to_one_per_hour_and_day_inside_the_byte_budget()
    {
        var path = Path.Combine(_root, "notes.md");
        var now = DateTime.UtcNow;
        for (var i = 0; i < 4; i++) PlantVersion(path, now.AddHours(-3).AddMinutes(-i), "hour-3 " + i);
        for (var i = 0; i < 3; i++) PlantVersion(path, now.AddDays(-5).AddMinutes(-i), "day-5 " + i);
        var store = new DocumentHistoryStore(_root);
        await store.CaptureAsync(path, "now");
        var texts = (await store.ReadAsync(path)).Select(v => v.Text).ToList();
        Assert.Equal(["now", "hour-3 0", "day-5 0"], texts);

        var tiny = new DocumentHistoryStore(Path.Combine(_root, "tiny"), byteBudget: 1);
        var big = new string('x', 700_000);
        await tiny.CaptureAsync(path, big + "1");
        await tiny.CaptureAsync(path, big + "2");
        Assert.Single(await tiny.ReadAsync(path));
    }

    [Fact]
    public async Task Recycled_history_expires_after_thirty_days_and_requires_matching_content_to_return()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "notes.md");
        await File.WriteAllTextAsync(path, "kept");
        var store = new DocumentHistoryStore(_root);
        await store.CaptureAsync(path, "kept");
        File.Delete(path);
        Assert.True(await store.TrashAsync(path));
        Assert.Empty(await store.ReadAsync(path));

        await File.WriteAllTextAsync(path, "a different note");
        Assert.Empty(await store.ReadAsync(path));
        Assert.True(store.HasTrashedHistory(path));

        Assert.Equal(0, await store.SweepAsync(TimeSpan.FromDays(90), DateTime.UtcNow.AddDays(10)));
        Assert.True(store.HasTrashedHistory(path));
        Assert.True(await store.SweepAsync(TimeSpan.FromDays(90), DateTime.UtcNow.AddDays(31)) >= 1);
        Assert.False(store.HasTrashedHistory(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
