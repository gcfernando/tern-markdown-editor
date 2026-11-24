using Tern.Filesystem;
using Tern.Search;
using Tern.Workspaces;

namespace Tern.Workspaces.Tests;

public sealed class LargeDocumentWorkspaceTests
{
    [Fact]
    public async Task Twenty_MiB_document_is_visible_in_tree_and_Quick_Open_and_readable_for_link_planning()
    {
        using var directory = new TempDirectory();
        var text = "# Large synthetic note\n[Target](target.md)\n" + new string(' ', 20 * 1024 * 1024);
        directory.Write("large-journal.md", text);
        var snapshot = await new WorkspaceScanner().ScanAsync(directory.Path, cancellationToken: CancellationToken.None);
        var file = Assert.Single(snapshot.Files);
        Assert.Equal("large-journal.md", file.RelativePath);
        Assert.Equal("large-journal.md", Assert.Single(snapshot.Tree.Children).Name);
        Assert.DoesNotContain(snapshot.Warnings, warning => warning.Kind == ScanWarningKind.FileTooLarge);
        Assert.Equal(file.RelativePath, Assert.Single(QuickOpen.Match("large-journal", snapshot.Files.Select(item => item.RelativePath))).Item);
        Assert.Equal(text, await new DiskDocumentTextProvider().ReadTextAsync(file.FullPath, CancellationToken.None));
        var graph = new LinkGraph(directory.Path);
        await graph.LoadAsync([file.FullPath], new DiskDocumentTextProvider());
        Assert.Equal(1, graph.DocumentCount);
        Assert.Single(graph.GetOutgoing(file.FullPath));
        using var cache = new TempDirectory();
        using var index = SearchIndex.Open(cache.Combine("large-index.db"), directory.Path);
        using var indexer = new WorkspaceIndexer(directory.Path, index);
        var indexed = await indexer.IndexSnapshotAsync(snapshot);
        Assert.Equal(1, indexed.Indexed);
        Assert.Equal(0, indexed.Failed);
        Assert.Equal("large-journal.md", Assert.Single(indexer.CreateSearchService().Query("synthetic").Hits).RelativePath);
    }

    [Fact]
    public async Task Default_scan_and_text_provider_reject_documents_beyond_the_editor_limit()
    {
        using var directory = new TempDirectory();
        var file = directory.Combine("oversized.md");
        using (var stream = File.Create(file)) stream.SetLength(DocumentFileService.MaximumEagerLoadBytes + 1L);
        Assert.Equal(DocumentFileService.MaximumEagerLoadBytes, new WorkspaceScanOptions().MaxFileSizeBytes);
        var snapshot = await new WorkspaceScanner().ScanAsync(directory.Path, cancellationToken: CancellationToken.None);
        Assert.Empty(snapshot.Files);
        Assert.Contains(snapshot.Warnings, warning => warning.Kind == ScanWarningKind.FileTooLarge);
        Assert.Null(await new DiskDocumentTextProvider().ReadTextAsync(file, CancellationToken.None));
    }

    [Fact]
    public async Task Explicit_smaller_scan_and_reader_limits_are_preserved()
    {
        using var directory = new TempDirectory();
        directory.Write("bounded.md", new string('x', 1025));
        var snapshot = await new WorkspaceScanner(new WorkspaceScanOptions { MaxFileSizeBytes = 1024 })
            .ScanAsync(directory.Path, cancellationToken: CancellationToken.None);
        Assert.Empty(snapshot.Files);
        Assert.Null(await new DiskDocumentTextProvider(1024).ReadTextAsync(directory.Combine("bounded.md"), CancellationToken.None));
    }
}
