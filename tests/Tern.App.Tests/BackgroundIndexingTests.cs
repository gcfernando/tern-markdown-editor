using Tern.App.Shell;

namespace Tern.App.Tests;

public sealed class BackgroundIndexingTests
{
    [Fact]
    public async Task Preparing_a_large_workspace_does_not_post_one_ui_update_per_file()
    {
        using var f = new ShellFixture();
        const int files = 1500;
        await Parallel.ForEachAsync(Enumerable.Range(0, files), CancellationToken.None, async (index, _) =>
            await f.WriteAsync($"note-{index:D4}.md", $"# Note {index}\n[next](note-{(index + 1) % files:D4}.md)\n"));

        var previous = SynchronizationContext.Current;
        var counting = new CountingContext();
        SynchronizationContext.SetSynchronizationContext(counting);
        try
        {
            using var session = await WorkspaceSession.OpenAsync(f.Docs, f.Root.File("index"), treeReady: _ => { });
            Assert.True(session.IsContentReady);
            Assert.Equal(files, session.Graph.DocumentCount);
            Assert.Equal("Search ready", session.ContentStatus);
            Assert.InRange(counting.Posts, 1, 150);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    [Fact]
    public async Task Tree_is_ready_before_indexing_finishes_and_the_workspace_can_close_while_indexing()
    {
        using var f = new ShellFixture();
        await Parallel.ForEachAsync(Enumerable.Range(0, 400), CancellationToken.None, async (index, _) =>
            await f.WriteAsync($"note-{index:D3}.md", $"# Note {index}\nzebra\n"));
        Task<bool>? closing = null;
        var preparingWhenReady = false;

        var opened = await f.Shell.OpenWorkspaceAsync(f.Docs, treeReady: () =>
        {
            var workspace = f.Shell.Workspace!;
            preparingWhenReady = workspace.IsPreparingContent && !workspace.IsContentReady;
            Assert.Equal(400, workspace.Snapshot.Files.Count);
            closing = f.Shell.CloseWorkspaceAsync(workspace);
        });

        Assert.True(preparingWhenReady);
        Assert.False(opened);
        Assert.True(await closing!.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Empty(f.Shell.OpenWorkspaces);
        Assert.False(f.Shell.IsIndexing);
    }

    [Fact]
    public async Task A_second_folder_opens_while_the_first_is_still_indexing_and_both_finish()
    {
        using var f = new ShellFixture();
        await Parallel.ForEachAsync(Enumerable.Range(0, 400), CancellationToken.None, async (index, _) =>
            await f.WriteAsync($"note-{index:D3}.md", $"# Note {index}\nzebra\n"));
        var second = f.Root.File("second");
        Directory.CreateDirectory(second);
        await File.WriteAllTextAsync(Path.Combine(second, "other.md"), "# Other\n");
        Task<bool>? openingSecond = null;

        var first = await f.Shell.OpenWorkspaceAsync(f.Docs, treeReady: () => openingSecond = f.Shell.OpenFolderAsync(second));

        Assert.True(first);
        Assert.True(await openingSecond!.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(2, f.Shell.OpenWorkspaces.Count);
        Assert.False(f.Shell.IsIndexing);
        Assert.NotEmpty(f.Shell.OpenWorkspaces.Single(w => w.IsNamedWorkspace).Search("zebra").Hits);
    }

    [Theory]
    [InlineData("{\"a\":\"one\ntwo\"}", "{\"a\":\"one two\"}")]
    [InlineData("{\"a\":\"one\r\ntwo\"}", "{\"a\":\"one two\"}")]
    [InlineData("{\n\"a\":\"x\\\"y\nz\"\n}", "{\n\"a\":\"x\\\"y z\"\n}")]
    public void Wrapped_AI_replies_become_valid_json_without_touching_structure(string wrapped, string expected) =>
        Assert.Equal(expected, Tern.App.Knowledge.KnowledgeConversation.RepairWrappedStrings(wrapped));

    [Fact]
    public async Task Knowledge_search_skips_license_files_and_duplicates_and_finds_word_forms()
    {
        using var f = new ShellFixture();
        await f.WriteAsync("plan.md", "# Plan\n\nWe decided the project launch is 14 April.");
        await f.WriteAsync("copy.md", "# Plan\n\nWe decided the project launch is 14 April.");
        await f.WriteAsync("licenses/pkg/THIRD-PARTY-NOTICES.md", "# ANGLE\n\nThird party material from the projects listed below and launch rules.");
        await f.WriteAsync("LICENSE.md", "# License\n\nThis project launch decision license text.");
        await f.WriteAsync("other.md", "# Other\n\nThis project has a budget.");
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));

        var result = await new Tern.App.Knowledge.KnowledgeRetrieval().SearchAsync(f.Shell.Workspace!, "What did we decide about the project launch?", new());

        var source = Assert.Single(result.Sources);
        Assert.Contains(source.RelativePath, new[] { "plan.md", "copy.md" });
        Assert.Contains("14 April", source.Excerpt);
        var legal = await new Tern.App.Knowledge.KnowledgeRetrieval().SearchAsync(f.Shell.Workspace!, "What does the project license say?", new());
        Assert.Contains(legal.Sources, item => item.RelativePath == "LICENSE.md");
    }

    private sealed class CountingContext : SynchronizationContext
    {
        private int _posts;
        public int Posts => Volatile.Read(ref _posts);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref _posts);
            base.Post(callback, state);
        }
    }
}
