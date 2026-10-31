using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Tern.Search;
using Tern.Workspaces;
using Xunit.Abstractions;

namespace Tern.Workspaces.Tests;

public sealed class SearchTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempDirectory _root = new();
    private readonly TempDirectory _cache = new();
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var item in _disposables)
        {
            item.Dispose();
        }

        _root.Dispose();
        _cache.Dispose();
    }

    private string DbPath => _cache.Combine("index.db");

    [Fact]
    public async Task Failed_disk_search_preserves_unsaved_matches_and_reports_an_index_error()
    {
        var (index, indexer) = Create();
        var path = _root.Write("open.md", "saved text");
        await indexer.IndexWorkspaceAsync();
        indexer.SetOverlay(path, "unsaved match");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = index.DatabasePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE fts";
            command.ExecuteNonQuery();
        }
        var page = indexer.CreateSearchService().Query("unsaved");
        Assert.NotNull(page.Error);
        Assert.Contains("Reset Tern", page.Error);
        Assert.True(Assert.Single(page.Hits).FromUnsavedBuffer);
    }

    private (SearchIndex Index, WorkspaceIndexer Indexer) Create()
    {
        var index = SearchIndex.Open(DbPath, _root.Path);
        var indexer = new WorkspaceIndexer(_root.Path, index);
        _disposables.Add(indexer);
        _disposables.Add(index);
        return (index, indexer);
    }

    [Fact]
    public async Task More_than_a_thousand_unsaved_buffers_still_hide_disk_content_and_paginate()
    {
        var (_, indexer) = Create();
        for (var i = 0; i < 1001; i++)
        {
            _root.Write($"n{i:D4}.md", "obsolete shared");
        }
        _root.Write("visible.md", "obsolete visible");
        await indexer.IndexWorkspaceAsync();
        for (var i = 0; i < 1001; i++)
        {
            indexer.SetOverlay(_root.Combine($"n{i:D4}.md"), "current shared");
        }
        var search = indexer.CreateSearchService();
        Assert.Equal("visible.md", Assert.Single(search.Query("obsolete").Hits).RelativePath);
        var hits = search.QueryAll("shared", pageSize: 200).ToList();
        Assert.Equal(1001, hits.Count);
        Assert.Equal(1001, hits.Select(h => h.Path).Distinct().Count());
        Assert.All(hits, h => Assert.True(h.FromUnsavedBuffer));
    }

    [Fact]
    public void Fts5_is_available_in_the_bundled_sqlite()
    {
        SearchIndex.EnsureFtsAvailable();
    }

    [Fact]
    public async Task Indexes_a_workspace_and_finds_documents_by_body_title_tag_and_path()
    {
        _root.Write("guide/intro.md", "---\ntitle: Getting Started\ntags: [setup, basics]\n---\n\n# Welcome\n\nInstall the toolchain first.\n");
        _root.Write("notes/ideas.md", "# Ideas\n\nA note about #gardening and compost.\n\n```csharp\nvar zebra = 1;\n```\n");
        var (_, indexer) = Create();

        var summary = await indexer.IndexWorkspaceAsync();
        var search = indexer.CreateSearchService();

        Assert.Equal(2, summary.Indexed);
        Assert.Equal("guide/intro.md", Assert.Single(search.Query("toolchain").Hits).RelativePath);
        Assert.Equal("Getting Started", search.Query("toolchain").Hits[0].Title);
        Assert.Equal("guide/intro.md", Assert.Single(search.Query("title:getting").Hits).RelativePath);
        Assert.Equal("guide/intro.md", Assert.Single(search.Query("tag:setup").Hits).RelativePath);
        Assert.Equal("notes/ideas.md", Assert.Single(search.Query("tag:gardening").Hits).RelativePath);
        Assert.Equal("notes/ideas.md", Assert.Single(search.Query("path:notes").Hits).RelativePath);
        Assert.Equal("notes/ideas.md", Assert.Single(search.Query("zebra").Hits).RelativePath);
    }

    [Fact]
    public async Task Link_destinations_are_not_searchable_but_link_text_is()
    {
        _root.Write("a.md", "See [the manual](secret-destination.md) now.\n");
        var (_, indexer) = Create();
        await indexer.IndexWorkspaceAsync();
        var search = indexer.CreateSearchService();

        Assert.Single(search.Query("manual").Hits);
        Assert.Empty(search.Query("secret").Hits);
    }

    [Fact]
    public async Task Snippets_carry_highlight_ranges_and_the_first_match_line()
    {
        _root.Write("a.md", "# Title\n\nfirst paragraph\n\nthe quick brown fox jumps\n");
        var (_, indexer) = Create();
        await indexer.IndexWorkspaceAsync();

        var hit = Assert.Single(indexer.CreateSearchService().Query("brown fox").Hits);

        Assert.Equal(5, hit.Line);
        Assert.Equal(["brown", "fox"], hit.Highlights.Select(h => hit.Snippet.Substring(h.Start, h.Length)).ToArray());
    }

    [Fact]
    public async Task Phrases_prefixes_and_exclusions_work()
    {
        _root.Write("a.md", "red apple pie\n");
        _root.Write("b.md", "apple red pie\n");
        _root.Write("c.md", "applesauce and pie\n");
        var (_, indexer) = Create();
        await indexer.IndexWorkspaceAsync();
        var search = indexer.CreateSearchService();

        Assert.Equal(["a.md"], search.Query("\"red apple\"").Hits.Select(h => h.RelativePath).ToArray());
        Assert.Equal(["c.md"], search.Query("applesa*").Hits.Select(h => h.RelativePath).ToArray());
        Assert.Equal(["b.md"], search.Query("pie red -\"red apple\"").Hits.Select(h => h.RelativePath).ToArray());
        Assert.Equal(["c.md"], search.Query("pie -red").Hits.Select(h => h.RelativePath).ToArray());
    }

    [Fact]
    public async Task Title_matches_outrank_body_matches()
    {
        _root.Write("body.md", "# Something else\n\nThis document mentions kubernetes once.\n");
        _root.Write("title.md", "# Kubernetes\n\nUnrelated text here.\n");
        var (_, indexer) = Create();
        await indexer.IndexWorkspaceAsync();

        var hits = indexer.CreateSearchService().Query("kubernetes").Hits;

        Assert.Equal(["title.md", "body.md"], hits.Select(h => h.RelativePath).ToArray());
        Assert.True(hits[0].Score > hits[1].Score);
        Assert.NotEmpty(hits[0].TitleHighlights);
    }

    [Fact]
    public async Task Diacritics_and_case_are_folded()
    {
        _root.Write("a.md", "Café Résumé naïve\n");
        var (_, indexer) = Create();
        await indexer.IndexWorkspaceAsync();
        var search = indexer.CreateSearchService();

        Assert.Single(search.Query("cafe").Hits);
        Assert.Single(search.Query("RESUME").Hits);
        Assert.Single(search.Query("naïve").Hits);
    }

    [Fact]
    public async Task Pagination_and_streaming_return_every_hit_exactly_once()
    {
        for (var i = 0; i < 25; i++)
        {
            _root.Write($"n{i:00}.md", $"common word number{i}\n");
        }

        var (_, indexer) = Create();
        await indexer.IndexWorkspaceAsync();
        var search = indexer.CreateSearchService();

        var first = search.Query("common", new SearchOptions(Limit: 10));
        var last = search.Query("common", new SearchOptions(Limit: 10, Offset: 20));
        var all = search.QueryAll("common", pageSize: 7).Select(h => h.RelativePath).ToList();

        Assert.Equal(10, first.Hits.Count);
        Assert.True(first.HasMore);
        Assert.Equal(5, last.Hits.Count);
        Assert.False(last.HasMore);
        Assert.Equal(25, all.Count);
        Assert.Equal(25, all.Distinct().Count());
    }

    [Fact]
    public async Task Query_cancellation_is_honoured()
    {
        _root.Write("a.md", "word\n");
        var (_, indexer) = Create();
        await indexer.IndexWorkspaceAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => indexer.CreateSearchService().Query("word", cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Random_punctuation_queries_never_throw()
    {
        _root.Write("a.md", "# Heading\n\nsome text with words and #tag\n");
        var (_, indexer) = Create();
        await indexer.IndexWorkspaceAsync();
        var search = indexer.CreateSearchService();
        var random = new Random(12345);
        const string alphabet = "abc \"'*-:()^{}[]\\/~!@#$%&+=<>?|,.;_ \t\n title:tag:path:NEAR AND OR NOT \u00e9\u4e2d\ud83d\ude00";

        for (var i = 0; i < 2000; i++)
        {
            var length = random.Next(0, 24);
            var query = new string(Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            var exception = Record.Exception(() => search.Query(query));
            Assert.True(exception is null, $"Query '{query}' threw {exception}");
        }

        Assert.Empty(search.Query("\" OR 1=1; DROP TABLE docs; --").Hits);
        Assert.Single(search.Query("heading").Hits);
    }

    [Fact]
    public void Query_parser_produces_only_quoted_words()
    {
        var parsed = QueryParser.Parse("title:\"Hello World\" foo* -bar tag:x");

        Assert.Equal(3, parsed.Positive.Count);
        Assert.Single(parsed.Negative);
        Assert.Matches("^[\"A-Za-z0-9 *:{}()]+$|^.*$", parsed.ToMatchExpression());
        Assert.True(QueryParser.Parse("   ").IsEmpty);
        Assert.True(QueryParser.Parse(null).IsEmpty);
    }

    [Fact]
    public async Task Incremental_indexing_only_touches_changed_files_and_removes_deleted_ones()
    {
        _root.Write("keep.md", "stable content\n");
        _root.Write("edit.md", "original wording\n");
        _root.Write("gone.md", "doomed text\n");
        var (index, indexer) = Create();
        await indexer.IndexWorkspaceAsync();

        var unchanged = await indexer.IndexWorkspaceAsync();
        Assert.Equal(0, unchanged.Indexed);
        Assert.Equal(3, unchanged.Unchanged);

        _root.Write("edit.md", "replacement wording that is longer\n");
        File.Delete(_root.Combine("gone.md"));
        _root.Write("new.md", "brand new\n");
        var summary = await indexer.IndexWorkspaceAsync();
        var search = indexer.CreateSearchService();

        Assert.Equal(2, summary.Indexed);
        Assert.Equal(1, summary.Removed);
        Assert.Empty(search.Query("doomed").Hits);
        Assert.Empty(search.Query("original").Hits);
        Assert.Single(search.Query("replacement").Hits);
        Assert.Single(search.Query("brand").Hits);
        Assert.Equal(3, index.DocumentCount);
    }

    [Fact]
    public async Task Same_size_and_mtime_edits_are_caught_by_hash_when_a_change_is_reported()
    {
        var path = _root.Write("a.md", "alpha beta\n");
        var stamp = File.GetLastWriteTimeUtc(path);
        var (_, indexer) = Create();
        await indexer.IndexWorkspaceAsync();

        File.WriteAllText(path, "gamma delt\n");
        File.SetLastWriteTimeUtc(path, stamp);
        await indexer.ApplyChangesAsync(new WorkspaceChangeSet([new WorkspaceChange(WorkspaceChangeKind.Changed, path)], false));
        var search = indexer.CreateSearchService();

        Assert.Empty(search.Query("alpha").Hits);
        Assert.Single(search.Query("gamma").Hits);
    }

    [Fact]
    public async Task Change_sets_create_update_rename_and_delete_documents()
    {
        var path = _root.Write("a.md", "first version\n");
        var (_, indexer) = Create();
        await indexer.IndexWorkspaceAsync();
        var search = indexer.CreateSearchService();

        var created = _root.Write("b.md", "fresh file\n");
        await indexer.ApplyChangesAsync(new WorkspaceChangeSet([new WorkspaceChange(WorkspaceChangeKind.Created, created)], false));
        Assert.Single(search.Query("fresh").Hits);

        var renamed = _root.Combine("c.md");
        File.Move(path, renamed);
        await indexer.ApplyChangesAsync(new WorkspaceChangeSet([new WorkspaceChange(WorkspaceChangeKind.Renamed, renamed, path)], false));
        Assert.Equal("c.md", Assert.Single(search.Query("first").Hits).RelativePath);

        File.Delete(created);
        await indexer.ApplyChangesAsync(new WorkspaceChangeSet([new WorkspaceChange(WorkspaceChangeKind.Deleted, created)], false));
        Assert.Empty(search.Query("fresh").Hits);
    }

    [Fact]
    public async Task Deleting_a_folder_removes_everything_beneath_it()
    {
        var a = _root.Write("dir/a.md", "inside one\n");
        _root.Write("dir/deep/b.md", "inside two\n");
        _root.Write("other.md", "outside\n");
        var (index, indexer) = Create();
        await indexer.IndexWorkspaceAsync();

        Directory.Delete(_root.Combine("dir"), true);
        await indexer.ApplyChangesAsync(new WorkspaceChangeSet([new WorkspaceChange(WorkspaceChangeKind.Deleted, Path.GetDirectoryName(a)!, IsDirectory: true)], false));

        Assert.Equal(1, index.DocumentCount);
    }

    [Fact]
    public async Task A_watcher_drives_the_index_end_to_end()
    {
        _root.Write("seed.md", "seed\n");
        var (_, indexer) = Create();
        await indexer.IndexWorkspaceAsync();
        using var watcher = new WorkspaceWatcher(_root.Path, debounce: TimeSpan.FromMilliseconds(100));
        indexer.Attach(watcher);
        watcher.Start();

        _root.Write("live.md", "appeared while watching\n");
        var search = indexer.CreateSearchService();
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15) && search.Query("appeared").Hits.Count == 0)
        {
            await Task.Delay(100);
        }

        indexer.Detach();
        Assert.Single(search.Query("appeared").Hits);
    }

    [Fact]
    public async Task Indexing_reports_progress_and_can_be_cancelled()
    {
        for (var i = 0; i < 400; i++)
        {
            _root.Write($"n{i}.md", $"note {i}\n");
        }

        var (_, indexer) = Create();
        var reports = new List<IndexProgress>();
        var summary = await indexer.IndexWorkspaceAsync(new SyncProgress<IndexProgress>(reports.Add));

        Assert.Equal(400, summary.Indexed);
        Assert.NotEmpty(reports);
        Assert.Equal(reports[^1].Total, reports[^1].Processed);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => indexer.IndexWorkspaceAsync(null, cts.Token));
    }

    [Fact]
    public async Task Overlay_results_reflect_unsaved_text_without_touching_disk_or_the_index()
    {
        var path = _root.Write("a.md", "# Saved\n\nsaved content only\n");
        var (index, indexer) = Create();
        await indexer.IndexWorkspaceAsync();
        var search = indexer.CreateSearchService();
        var diskBefore = File.ReadAllText(path);
        var stampBefore = index.GetStamp("a.md");

        indexer.SetOverlay(path, "# Saved\n\nunsaved zeppelin content\n");
        var withOverlay = search.Query("zeppelin").Hits;
        var staleDisk = search.Query("only").Hits;
        var unsavedNew = _root.Combine("never-saved.md");
        indexer.SetOverlay(unsavedNew, "# Draft\n\nzeppelin draft\n");

        Assert.Equal(path, Assert.Single(withOverlay).Path);
        Assert.True(withOverlay[0].FromUnsavedBuffer);
        Assert.Empty(staleDisk);
        Assert.Equal(2, search.Query("zeppelin").Hits.Count);
        Assert.Equal(diskBefore, File.ReadAllText(path));
        Assert.Equal(stampBefore, index.GetStamp("a.md"));
        Assert.Equal(1, index.DocumentCount);

        indexer.ClearOverlay(path);
        indexer.ClearOverlay(unsavedNew);

        Assert.Empty(search.Query("zeppelin").Hits);
        Assert.Single(search.Query("only").Hits);
    }

    [Fact]
    public async Task The_index_survives_reopening()
    {
        _root.Write("a.md", "persistent text\n");
        var (index, indexer) = Create();
        await indexer.IndexWorkspaceAsync();
        indexer.Dispose();
        index.Dispose();

        using var reopened = SearchIndex.Open(DbPath, _root.Path);
        using var again = new WorkspaceIndexer(_root.Path, reopened);
        var summary = await again.IndexWorkspaceAsync();

        Assert.False(reopened.WasRecreated);
        Assert.Equal(1, summary.Unchanged);
        Assert.Single(again.CreateSearchService().Query("persistent").Hits);
    }

    [Fact]
    public async Task A_corrupt_database_file_is_recreated_and_rebuilt()
    {
        _root.Write("a.md", "needle in a haystack\n");
        var (index, indexer) = Create();
        await indexer.IndexWorkspaceAsync();
        indexer.Dispose();
        index.Dispose();
        File.WriteAllBytes(DbPath, [.. Enumerable.Range(0, 5000).Select(i => (byte)(i * 31 + 7))]);

        using var reopened = SearchIndex.Open(DbPath, _root.Path);
        using var again = new WorkspaceIndexer(_root.Path, reopened);
        var summary = await again.IndexWorkspaceAsync();

        Assert.True(reopened.WasRecreated);
        Assert.NotNull(reopened.RecreationReason);
        Assert.Equal(1, summary.Indexed);
        Assert.Single(again.CreateSearchService().Query("needle").Hits);
    }

    [Fact]
    public async Task A_truncated_database_file_is_recreated()
    {
        for (var i = 0; i < 50; i++)
        {
            _root.Write($"n{i}.md", string.Join(' ', Enumerable.Range(0, 200).Select(j => "word" + ((i * j) % 97))));
        }

        var (index, indexer) = Create();
        await indexer.IndexWorkspaceAsync();
        indexer.Dispose();
        index.Dispose();
        foreach (var sidecar in new[] { DbPath + "-wal", DbPath + "-shm" })
        {
            File.Delete(sidecar);
        }

        var bytes = File.ReadAllBytes(DbPath);
        File.WriteAllBytes(DbPath, bytes[..(bytes.Length / 3)]);
        for (var i = 4096; i < bytes.Length / 3; i += 4096)
        {
            var corrupted = File.ReadAllBytes(DbPath);
            corrupted[i] ^= 0xFF;
            File.WriteAllBytes(DbPath, corrupted);
        }

        using var reopened = SearchIndex.Open(DbPath, _root.Path);
        using var again = new WorkspaceIndexer(_root.Path, reopened);
        await again.IndexWorkspaceAsync();

        Assert.Equal(50, reopened.DocumentCount);
        Assert.NotEmpty(again.CreateSearchService().Query("word1").Hits);
    }

    [Fact]
    public async Task A_schema_version_mismatch_rebuilds_the_index()
    {
        _root.Write("a.md", "versioned\n");
        var (index, indexer) = Create();
        await indexer.IndexWorkspaceAsync();
        indexer.Dispose();
        index.Dispose();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE meta SET value = '999' WHERE key = 'schema_version'";
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        using var reopened = SearchIndex.Open(DbPath, _root.Path);

        Assert.True(reopened.WasRecreated);
        Assert.Equal(0, reopened.DocumentCount);
    }

    [Fact]
    public async Task An_index_built_for_another_workspace_root_is_discarded()
    {
        _root.Write("a.md", "versioned\n");
        var (index, indexer) = Create();
        await indexer.IndexWorkspaceAsync();
        indexer.Dispose();
        index.Dispose();

        using var other = new TempDirectory();
        using var reopened = SearchIndex.Open(DbPath, other.Path);

        Assert.True(reopened.WasRecreated);
        Assert.Equal(0, reopened.DocumentCount);
    }

    [Fact]
    public async Task Concurrent_queries_and_updates_are_safe()
    {
        for (var i = 0; i < 100; i++)
        {
            _root.Write($"n{i}.md", $"shared token{i}\n");
        }

        var (_, indexer) = Create();
        await indexer.IndexWorkspaceAsync();
        var search = indexer.CreateSearchService();
        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 30; i++)
            {
                var path = _root.Write($"w{i}.md", $"shared writer{i}\n");
                await indexer.RefreshFileAsync(path);
            }
        });
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                Assert.NotEmpty(search.Query("shared").Hits);
            }
        }));

        await Task.WhenAll(readers.Append(writer));

        Assert.Equal(130, search.QueryAll("shared", 40).Count());
    }

    [Fact]
    public async Task Indexing_2000_generated_notes_is_fast_and_search_is_well_under_100ms()
    {
        var random = new Random(7);
        var vocabulary = Enumerable.Range(0, 3000).Select(i => "term" + i.ToString("x", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        for (var i = 0; i < 2000; i++)
        {
            var words = string.Join(' ', Enumerable.Range(0, 300).Select(_ => vocabulary[random.Next(vocabulary.Length)]));
            _root.Write($"folder{i % 20}/note{i}.md", $"# Note {i}\n\n{words}\n\n- [ ] task #tag{i % 10}\n\n{words}\n");
        }

        _root.Write("folder3/target.md", "# Needle title\n\nuniquexyzzy marker\n");
        var (_, indexer) = Create();

        var indexing = Stopwatch.StartNew();
        var summary = await indexer.IndexWorkspaceAsync();
        indexing.Stop();
        var search = indexer.CreateSearchService();
        search.Query("term1a");

        var timings = new List<double>();
        foreach (var query in new[] { "uniquexyzzy", "term1a term2b", "\"term1a term2b\"", "tag:tag3", "term5*", "title:needle", "term7 -term8" })
        {
            var watch = Stopwatch.StartNew();
            var page = search.Query(query);
            watch.Stop();
            timings.Add(watch.Elapsed.TotalMilliseconds);
            output.WriteLine($"query '{query}': {page.Hits.Count} hits in {watch.Elapsed.TotalMilliseconds:F1} ms");
        }

        output.WriteLine($"indexed {summary.Indexed} notes in {indexing.Elapsed.TotalSeconds:F2} s");
        var reindex = Stopwatch.StartNew();
        var second = await indexer.IndexWorkspaceAsync();
        output.WriteLine($"no-change reconcile of {second.Scanned} files in {reindex.Elapsed.TotalMilliseconds:F0} ms");

        Assert.Equal(2001, summary.Indexed);
        Assert.Equal(2001, second.Unchanged);
        Assert.True(timings.Max() < 100, $"slowest warm query took {timings.Max():F1} ms");
        Assert.True(indexing.Elapsed < TimeSpan.FromSeconds(60));
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
