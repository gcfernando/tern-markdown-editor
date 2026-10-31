using System.Text.Json;

namespace Tern.Workspaces.Tests;

public sealed class RecentUrlStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tern-recent-url-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void URLs_persist_canonical_authority_but_keep_path_and_query_case_distinct()
    {
        var now = DateTime.UtcNow;
        var store = new RecentItemsStore(_directory, clock: () => now = now.AddSeconds(1));
        store.AddUrl("HTTPS://EXAMPLE.COM:443/Guide.md?part=A#intro");
        store.AddUrl("https://example.com/Guide.md?part=A#other");
        Assert.Single(store.Urls);
        store.AddUrl("https://example.com/guide.md?part=A");
        store.AddUrl("https://example.com/Guide.md?part=a");
        var loaded = new RecentItemsStore(_directory);
        Assert.Equal(3, loaded.Urls.Count); Assert.Empty(loaded.Files); Assert.Empty(loaded.Workspaces);
        Assert.Contains(loaded.Urls, e => e.Path == "https://example.com/Guide.md?part=A");
        Assert.All(loaded.Urls, e => Assert.DoesNotContain('#', e.Path));
    }

    [Fact]
    public void URL_limits_pins_remove_clear_and_notifications_do_not_change_file_history()
    {
        var now = DateTime.UtcNow; var store = new RecentItemsStore(_directory, clock: () => now = now.AddSeconds(1), maxUrls: 2); var changes = 0;
        store.Changed += (_, _) => changes++;
        var file = Path.Combine(_directory, "local.md"); store.AddFile(file);
        store.AddUrl("http://localhost:8000/pinned.md"); Assert.True(store.SetPinned(RecentKind.Url, "http://localhost:8000/pinned.md", true));
        foreach (var number in Enumerable.Range(0, 5)) store.AddUrl($"https://example.com/{number}.md");
        Assert.Equal(3, store.Urls.Count); Assert.True(store.Urls[0].Pinned); Assert.True(changes >= 8);
        Assert.True(store.Remove(RecentKind.Url, "https://example.com/4.md"));
        store.Clear(RecentKind.Url); Assert.Single(store.Urls); Assert.Single(store.Files);
        store.ClearAll(); Assert.Empty(store.Urls); Assert.Empty(new RecentItemsStore(_directory).Urls); Assert.Empty(store.Files);
    }

    [Theory]
    [InlineData("file:///C:/notes.md")]
    [InlineData("https://user:password@example.com/a.md")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a URL")]
    public void Invalid_or_credential_URLs_are_not_stored(string url)
    {
        var store = new RecentItemsStore(_directory);
        Assert.Throws<ArgumentException>(() => store.AddUrl(url)); Assert.Empty(store.Urls);
    }

    [Fact]
    public void Legacy_v1_without_URLs_loads_and_new_v1_skips_invalid_URL_rows()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, RecentItemsStore.FileName), "{\"version\":1,\"files\":[],\"workspaces\":[]}");
        var legacy = new RecentItemsStore(_directory); Assert.Empty(legacy.Urls);
        legacy.AddUrl("http://[::1]:8000/notes.md");
        using (var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(_directory, RecentItemsStore.FileName))))
        { Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32()); Assert.Single(json.RootElement.GetProperty("urls").EnumerateArray()); }
        File.WriteAllText(Path.Combine(_directory, RecentItemsStore.FileName), "{\"version\":1,\"files\":[],\"workspaces\":[],\"urls\":[{\"path\":\"file:///C:/bad.md\"},{\"path\":\"https://u:p@example.com/bad.md\"},{\"path\":\"http://localhost:8000/good.md\"}]}");
        Assert.Equal("http://localhost:8000/good.md", Assert.Single(new RecentItemsStore(_directory).Urls).Path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
