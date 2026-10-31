using Tern.Recovery;

namespace Tern.UnitTests;

public sealed class DocumentHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tern-history-tests", Guid.NewGuid().ToString("N"));

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
        var path = Path.Combine(_root, "notes.md");
        var store = new DocumentHistoryStore(_root);
        await store.CaptureAsync(path, "original");
        var file = Assert.Single(Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories));
        var json = await File.ReadAllTextAsync(file);
        await File.WriteAllTextAsync(file, json.Replace("original", "tampered", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
