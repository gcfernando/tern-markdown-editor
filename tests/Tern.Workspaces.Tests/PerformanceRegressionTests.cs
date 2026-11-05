using Tern.Workspaces;

namespace Tern.Workspaces.Tests;

public sealed class PerformanceRegressionTests
{
    [Fact]
    public async Task Shared_snapshot_rechecks_changed_file_stamps_before_reusing_index_entries()
    {
        using var directory = new TempDirectory();
        directory.Write("note.md", "Original");
        var snapshot = await new WorkspaceScanner().ScanAsync(directory.Path);
        using var index = Tern.Search.SearchIndex.Open(directory.Combine("index.db"), directory.Path);
        using var indexer = new Tern.Search.WorkspaceIndexer(directory.Path, index);
        await indexer.IndexSnapshotAsync(snapshot);
        await File.WriteAllTextAsync(directory.Combine("note.md"), "Changed content with another length");
        await indexer.IndexSnapshotAsync(snapshot);
        Assert.Single(indexer.CreateSearchService().Query("Changed").Hits);
        Assert.Empty(indexer.CreateSearchService().Query("Original").Hits);
    }

    [Fact]
    public async Task Disk_provider_rechecks_size_on_open_and_graph_reload_drops_deleted_documents()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("note.md");
        directory.Write("note.md", "# Small");
        var provider = new DiskDocumentTextProvider(1024);
        var graph = new LinkGraph(directory.Path);
        await graph.LoadAsync([path], provider);
        Assert.Equal(1, graph.DocumentCount);
        await File.WriteAllTextAsync(path, new string('x', 2048));
        Assert.Null(await provider.ReadTextAsync(path, CancellationToken.None));
        await graph.LoadAsync([path], provider);
        Assert.Equal(0, graph.DocumentCount);
    }
}
