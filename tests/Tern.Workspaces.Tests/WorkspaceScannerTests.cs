using System.Diagnostics;
using Tern.Workspaces;
using Xunit.Abstractions;

namespace Tern.Workspaces.Tests;

public sealed class WorkspaceScannerTests(ITestOutputHelper output)
{
    [Fact]
    public async Task File_links_outside_the_workspace_are_excluded_but_internal_links_are_allowed()
    {
        using var dir = new TempDirectory();
        using var outside = new TempDirectory();
        dir.Write("real.md", "inside");
        outside.Write("secret.md", "outside confidential");
        try
        {
            File.CreateSymbolicLink(dir.Combine("outside.md"), outside.Combine("secret.md"));
            File.CreateSymbolicLink(dir.Combine("inside.md"), dir.Combine("real.md"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new Xunit.Sdk.XunitException($"UNMET acceptance: file-link confinement was not exercised because the environment refused file symlink creation. {ex.GetType().Name}: {ex.Message}");
        }
        var snapshot = await new WorkspaceScanner().ScanAsync(dir.Path);
        Assert.DoesNotContain(snapshot.Files, f => f.RelativePath == "outside.md");
        Assert.Contains(snapshot.Files, f => f.RelativePath == "inside.md");
        Assert.Contains(snapshot.Warnings, w => w.Path == dir.Combine("outside.md") && w.Kind == ScanWarningKind.SkippedLink);
        var graph = new LinkGraph(dir.Path);
        await graph.LoadAsync([dir.Combine("outside.md")], new DiskDocumentTextProvider());
        Assert.Equal(0, graph.DocumentCount);
    }

    [Fact]
    public async Task A_directory_link_escape_is_rejected_at_index_read_time()
    {
        using var dir = new TempDirectory();
        using var outside = new TempDirectory();
        outside.Write("secret.md", "confidentialtoken");
        if (!TryCreateDirectoryLink(dir.Combine("escape"), outside.Path, output)) return;
        using var cache = new TempDirectory();
        using var index = Tern.Search.SearchIndex.Open(cache.Combine("search.db"), dir.Path);
        using var indexer = new Tern.Search.WorkspaceIndexer(dir.Path, index);
        await indexer.RefreshFileAsync(dir.Combine("escape/secret.md"));
        Assert.Equal(0, index.DocumentCount);
        Assert.Empty(indexer.CreateSearchService().Query("confidentialtoken").Hits);
        Assert.False(WorkspacePaths.IsPhysicallyContained(dir.Path, dir.Combine("escape/secret.md")));
    }
    private static async Task<IReadOnlyList<string>> ScanRelative(string root, WorkspaceScanOptions? options = null)
    {
        var snapshot = await new WorkspaceScanner(options).ScanAsync(root);
        return [.. snapshot.Files.Select(f => f.RelativePath).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task Finds_markdown_files_recursively_with_relative_paths_and_metadata()
    {
        using var dir = new TempDirectory();
        dir.Write("a.md", "# A");
        dir.Write("notes/b.markdown", "bb");
        dir.Write("notes/deep/c.MDOWN", "ccc");
        dir.Write("notes/d.mkd", "d");
        dir.Write("readme.txt", "not markdown");
        dir.Write("image.png", "x");

        var snapshot = await new WorkspaceScanner().ScanAsync(dir.Path);

        Assert.Equal(["a.md", "notes/b.markdown", "notes/d.mkd", "notes/deep/c.MDOWN"], snapshot.Files.Select(f => f.RelativePath).Order(StringComparer.Ordinal));
        var b = snapshot.Files.Single(f => f.RelativePath == "notes/b.markdown");
        Assert.Equal(2, b.Length);
        Assert.Equal(File.GetLastWriteTimeUtc(dir.Combine("notes/b.markdown")), b.LastWriteUtc);
        Assert.Equal(dir.Combine("notes/b.markdown"), b.FullPath);
    }

    [Fact]
    public async Task Text_files_are_included_only_when_requested()
    {
        using var dir = new TempDirectory();
        dir.Write("a.md", "x");
        dir.Write("b.txt", "y");

        Assert.Equal(["a.md"], await ScanRelative(dir.Path));
        Assert.Equal(["a.md", "b.txt"], await ScanRelative(dir.Path, new WorkspaceScanOptions { IncludeTextFiles = true }));
    }

    [Fact]
    public async Task Default_exclusions_skip_tooling_folders_at_any_depth()
    {
        using var dir = new TempDirectory();
        dir.Write("keep.md", "x");
        dir.Write(".git/HEAD.md", "x");
        dir.Write("node_modules/pkg/readme.md", "x");
        dir.Write(".obsidian/workspace.md", "x");
        dir.Write("src/bin/out.md", "x");
        dir.Write("src/obj/out.md", "x");
        dir.Write("src/docs/keep2.md", "x");

        Assert.Equal(["keep.md", "src/docs/keep2.md"], await ScanRelative(dir.Path));
    }

    [Fact]
    public async Task Hidden_directories_are_included_unless_excluded()
    {
        using var dir = new TempDirectory();
        dir.Write(".secret/a.md", "x");
        dir.Write("b.md", "x");

        Assert.Equal([".secret/a.md", "b.md"], await ScanRelative(dir.Path));
        Assert.Equal(["b.md"], await ScanRelative(dir.Path, new WorkspaceScanOptions { ExcludeHiddenDirectories = true }));
    }

    [Fact]
    public async Task User_patterns_exclude_files_and_folders()
    {
        using var dir = new TempDirectory();
        dir.Write("a.md", "x");
        dir.Write("drafts/b.md", "x");
        dir.Write("notes/c.draft.md", "x");
        dir.Write("notes/d.md", "x");

        var options = new WorkspaceScanOptions { ExcludePatterns = ["drafts/", "*.draft.md"] };

        Assert.Equal(["a.md", "notes/d.md"], await ScanRelative(dir.Path, options));
    }

    [Fact]
    public async Task Ternignore_supports_comments_wildcards_directories_negation_and_anchoring()
    {
        using var dir = new TempDirectory();
        dir.Write(".ternignore", "# comment\n\n*.tmp.md\narchive/\n/only-root.md\ndocs/**/internal/\nsecret?.md\n!keep.tmp.md\n");
        dir.Write("a.md", "x");
        dir.Write("one.tmp.md", "x");
        dir.Write("keep.tmp.md", "x");
        dir.Write("archive/old.md", "x");
        dir.Write("sub/archive/old.md", "x");
        dir.Write("only-root.md", "x");
        dir.Write("sub/only-root.md", "x");
        dir.Write("docs/internal/a.md", "x");
        dir.Write("docs/x/y/internal/b.md", "x");
        dir.Write("docs/public/c.md", "x");
        dir.Write("secret1.md", "x");
        dir.Write("secret12.md", "x");

        var found = await ScanRelative(dir.Path);

        Assert.Equal(["a.md", "docs/public/c.md", "keep.tmp.md", "secret12.md", "sub/only-root.md"], found);
    }

    [Fact]
    public async Task Ternignore_trailing_slash_does_not_hide_a_file_with_that_name()
    {
        using var dir = new TempDirectory();
        dir.Write(".ternignore", "build/\n");
        dir.Write("build", "a file named build, not a folder");
        dir.Write("notes/build.md", "x");
        dir.Write("build.md", "x");

        Assert.Equal(["build.md", "notes/build.md"], await ScanRelative(dir.Path, new WorkspaceScanOptions { IncludeTextFiles = true }));
    }

    [Fact]
    public async Task Ternignore_can_be_disabled()
    {
        using var dir = new TempDirectory();
        dir.Write(".ternignore", "a.md\n");
        dir.Write("a.md", "x");

        Assert.Empty(await ScanRelative(dir.Path));
        Assert.Equal(["a.md"], await ScanRelative(dir.Path, new WorkspaceScanOptions { UseIgnoreFile = false }));
    }

    [Fact]
    public async Task Max_depth_limits_descent()
    {
        using var dir = new TempDirectory();
        dir.Write("a.md", "x");
        dir.Write("l1/b.md", "x");
        dir.Write("l1/l2/c.md", "x");

        Assert.Equal(["a.md"], await ScanRelative(dir.Path, new WorkspaceScanOptions { MaxDepth = 0 }));
        Assert.Equal(["a.md", "l1/b.md"], await ScanRelative(dir.Path, new WorkspaceScanOptions { MaxDepth = 1 }));
        Assert.Equal(3, (await ScanRelative(dir.Path)).Count);
    }

    [Fact]
    public async Task Files_over_the_size_cap_are_skipped_with_a_warning()
    {
        using var dir = new TempDirectory();
        dir.Write("small.md", "x");
        dir.Write("big.md", new string('x', 5000));

        var snapshot = await new WorkspaceScanner(new WorkspaceScanOptions { MaxFileSizeBytes = 1000 }).ScanAsync(dir.Path);

        Assert.Equal(["small.md"], snapshot.Files.Select(f => f.RelativePath));
        var warning = Assert.Single(snapshot.Warnings);
        Assert.Equal(ScanWarningKind.FileTooLarge, warning.Kind);
        Assert.EndsWith("big.md", warning.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Tree_lists_folders_first_in_natural_order_and_counts_files()
    {
        using var dir = new TempDirectory();
        dir.Write("note10.md", "x");
        dir.Write("note2.md", "x");
        dir.Write("zeta/inner.md", "x");
        dir.Write("alpha/one.md", "x");
        dir.Write("alpha/two.md", "x");
        Directory.CreateDirectory(dir.Combine("empty"));

        var snapshot = await new WorkspaceScanner().ScanAsync(dir.Path);
        var names = snapshot.Tree.Children.Select(c => c.Name).ToList();

        Assert.Equal(["alpha", "zeta", "note2.md", "note10.md"], names);
        Assert.Equal(5, snapshot.Tree.FileCount);
        Assert.Equal(2, snapshot.Tree.Children.Single(c => c.Name == "alpha").FileCount);
        Assert.DoesNotContain("empty", names);
    }

    [Fact]
    public async Task Empty_folders_can_be_kept_for_the_explorer()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.Combine("empty"));
        dir.Write("a.md", "x");

        var snapshot = await new WorkspaceScanner(new WorkspaceScanOptions { PruneEmptyDirectories = false }).ScanAsync(dir.Path);

        Assert.Contains(snapshot.Tree.Children, c => c.Name == "empty" && c.IsDirectory);
    }

    [Fact]
    public async Task Extensions_are_matched_case_insensitively()
    {
        using var dir = new TempDirectory();
        dir.Write("UPPER.MD", "x");

        Assert.Equal(["UPPER.MD"], await ScanRelative(dir.Path));
    }

    [Fact]
    public async Task Scan_can_be_cancelled()
    {
        using var dir = new TempDirectory();
        for (var i = 0; i < 50; i++)
        {
            dir.Write($"d{i}/f.md", "x");
        }

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WorkspaceScanner().ScanAsync(dir.Path, null, cts.Token));
    }

    [Fact]
    public async Task Streaming_enumeration_yields_every_file()
    {
        using var dir = new TempDirectory();
        for (var i = 0; i < 30; i++)
        {
            dir.Write($"d{i % 3}/f{i}.md", "x");
        }

        var seen = new List<string>();
        await foreach (var file in new WorkspaceScanner().EnumerateFilesAsync(dir.Path))
        {
            seen.Add(file.RelativePath);
        }

        Assert.Equal(30, seen.Distinct().Count());
    }

    [Fact]
    public async Task Missing_root_is_reported_clearly()
    {
        using var dir = new TempDirectory();

        await Assert.ThrowsAnyAsync<IOException>(() => new WorkspaceScanner().ScanAsync(dir.Combine("nope")));
    }

    [Fact]
    public async Task Paths_longer_than_260_characters_are_scanned()
    {
        using var dir = new TempDirectory();
        var deep = string.Join('/', Enumerable.Repeat(new string('d', 40), 7));
        WriteLongFixture(dir, deep + "/long.md");

        var found = await ScanRelative(dir.Path);

        Assert.Single(found);
        Assert.EndsWith("/long.md", found[0], StringComparison.Ordinal);
        Assert.True(dir.Combine(found[0]).Length > 260);
    }

    private static void WriteLongFixture(TempDirectory dir, string relative)
    {
        try
        {
            dir.Write(relative, "x");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new Xunit.Sdk.XunitException($"UNMET acceptance: the environment could not create the long-path fixture required for traversal acceptance. {ex.GetType().Name}: {ex.Message}");
        }
    }

    [Fact]
    public async Task Directory_links_are_not_followed_by_default_and_loops_terminate()
    {
        using var dir = new TempDirectory();
        dir.Write("a.md", "x");
        dir.Write("sub/b.md", "x");
        if (!TryCreateDirectoryLink(dir.Combine("sub/loop"), dir.Path, output))
        {
            return;
        }

        var found = await ScanRelative(dir.Path);

        Assert.Equal(["a.md", "sub/b.md"], found);
    }

    [Fact]
    public async Task Following_links_never_enters_cycles_or_leaves_the_root()
    {
        using var dir = new TempDirectory();
        using var outside = new TempDirectory();
        outside.Write("secret.md", "outside the workspace");
        dir.Write("a.md", "x");
        dir.Write("sub/b.md", "x");
        var madeLoop = TryCreateDirectoryLink(dir.Combine("sub/loop"), dir.Path, output);
        var madeOut = TryCreateDirectoryLink(dir.Combine("escape"), outside.Path, output);
        if (!madeLoop || !madeOut)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var found = await ScanRelative(dir.Path, new WorkspaceScanOptions { FollowDirectoryLinks = true });
        stopwatch.Stop();

        Assert.Equal(["a.md", "sub/b.md"], found);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_link_to_another_folder_inside_the_root_is_followed_only_when_enabled()
    {
        using var dir = new TempDirectory();
        dir.Write("real/note.md", "x");
        Directory.CreateDirectory(dir.Combine("other"));
        if (!TryCreateDirectoryLink(dir.Combine("other/alias"), dir.Combine("real"), output))
        {
            return;
        }

        var withoutFollow = await ScanRelative(dir.Path);
        var follow = await new WorkspaceScanner(new WorkspaceScanOptions { FollowDirectoryLinks = true }).ScanAsync(dir.Path);

        Assert.Equal(["real/note.md"], withoutFollow);
        Assert.Contains(follow.Files, f => f.RelativePath == "real/note.md");
        Assert.True(follow.Files.Count <= 2);
    }

    [Fact]
    public async Task Case_insensitive_comparison_applies_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDirectory();
        dir.Write(".ternignore", "NOTES/\n");
        dir.Write("notes/a.md", "x");
        dir.Write("b.md", "x");

        Assert.Equal(["b.md"], await ScanRelative(dir.Path));
    }

    // Tries a symlink, then a junction. Windows acceptance fails explicitly if neither can be exercised.
    internal static bool TryCreateDirectoryLink(string link, string target, ITestOutputHelper output)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (OperatingSystem.IsWindows())
            {
                var info = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var process = Process.Start(info)!;
                process.WaitForExit();
                if (process.ExitCode == 0 && Directory.Exists(link))
                {
                    return true;
                }
            }

            if (OperatingSystem.IsWindows())
            {
                throw new Xunit.Sdk.XunitException($"UNMET acceptance: the environment refused both symlink and junction creation, so directory-link safety was not exercised. {ex.GetType().Name}: {ex.Message}");
            }
            output.WriteLine("Not applicable to this Windows release: directory links could not be created on this platform. " + ex.Message);
            return false;
        }
    }
}
