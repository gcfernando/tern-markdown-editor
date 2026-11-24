using Tern.Filesystem;
using Tern.Search;
using Tern.Workspaces;

namespace Tern.Workspaces.Tests;

public sealed class OnlineOnlyWorkspaceTests
{
    [Fact]
    public async Task Offline_document_keeps_name_and_tree_but_drops_cached_body_until_available_locally()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TempDirectory();
        using var cache = new TempDirectory();
        var path = directory.Write("cloud-journal.md", "# Secret zebra\n[Target](target.md)");
        using var index = SearchIndex.Open(cache.Combine("index.db"), directory.Path);
        using var indexer = new WorkspaceIndexer(directory.Path, index);
        var scanner = new WorkspaceScanner();
        await indexer.IndexSnapshotAsync(await scanner.ScanAsync(directory.Path));
        Assert.Single(indexer.CreateSearchService().Query("zebra").Hits);
        var written = File.GetLastWriteTimeUtc(path);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Offline);
        try
        {
            Assert.True(CloudFiles.IsOnlineOnly(path));
            var snapshot = await new WorkspaceScanner(new WorkspaceScanOptions { MaxFileSizeBytes = 1 }).ScanAsync(directory.Path);
            Assert.True(Assert.Single(snapshot.Files).IsOnlineOnly);
            Assert.True(Assert.Single(snapshot.Tree.Children).IsOnlineOnly);
            Assert.Single(QuickOpen.Match("cloud-journal", snapshot.Files.Select(file => file.RelativePath)));
            Assert.Null(await new DiskDocumentTextProvider().ReadTextAsync(path, CancellationToken.None));
            var graph = new LinkGraph(directory.Path);
            await graph.LoadAsync([path], new DiskDocumentTextProvider());
            Assert.Empty(graph.GetOutgoing(path));
            await indexer.IndexSnapshotAsync(snapshot);
            Assert.Empty(indexer.CreateSearchService().Query("zebra").Hits);
            Assert.Contains("online-only", Assert.Single(indexer.CreateSearchService().Query("journal").Hits).Title);
            Assert.True(CloudFiles.IsOnlineOnly(path));
        }
        finally { File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.Offline); }
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        await indexer.IndexSnapshotAsync(await scanner.ScanAsync(directory.Path));
        Assert.Single(indexer.CreateSearchService().Query("zebra").Hits);
    }
}
