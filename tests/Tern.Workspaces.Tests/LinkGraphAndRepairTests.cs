using Tern.Filesystem;
using Tern.Workspaces;

namespace Tern.Workspaces.Tests;

public sealed class LinkGraphTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private string P(string relative) => WorkspacePaths.Normalize(_dir.Combine(relative));

    private async Task<LinkGraph> LoadAsync()
    {
        var snapshot = await new WorkspaceScanner().ScanAsync(_dir.Path);
        var graph = new LinkGraph(_dir.Path);
        await graph.LoadAsync(snapshot.Files.Select(f => f.FullPath), new DiskDocumentTextProvider());
        return graph;
    }

    [Fact]
    public async Task Relative_links_resolve_including_parent_folders_spaces_and_fragments()
    {
        _dir.Write("a.md", "[b](sub/b.md) [c](sub/my%20note.md) [up](../x.md) [self](#top)\n\n# Top\n");
        _dir.Write("sub/b.md", "[back](../a.md#top) [bad](../a.md#nope)\n");
        _dir.Write("sub/my note.md", "x\n");
        var graph = await LoadAsync();

        var outgoing = graph.GetOutgoing(P("a.md"));
        var back = graph.GetOutgoing(P("sub/b.md"));

        Assert.Equal(P("sub/b.md"), outgoing[0].TargetPath);
        Assert.Equal(LinkStatus.Ok, outgoing[0].Status);
        Assert.Equal(P("sub/my note.md"), outgoing[1].TargetPath);
        Assert.Equal(LinkStatus.Ok, outgoing[1].Status);
        Assert.Equal(LinkStatus.OutsideWorkspace, outgoing[2].Status);
        Assert.Equal(LinkStatus.Ok, outgoing[3].Status);
        Assert.Equal(LinkStatus.Ok, back[0].Status);
        Assert.Equal(LinkStatus.MissingAnchor, back[1].Status);
        Assert.Equal(P("a.md"), back[1].TargetPath);
    }

    [Fact]
    public async Task External_links_are_not_checked_and_missing_files_are_broken()
    {
        _dir.Write("a.md", "[web](https://example.com) [mail](mailto:x@y.z) [gone](missing.md) ![img](nope.png)\n");
        var graph = await LoadAsync();

        var links = graph.GetOutgoing(P("a.md"));

        Assert.Equal(LinkStatus.External, links[0].Status);
        Assert.Equal(LinkStatus.External, links[1].Status);
        Assert.Equal(LinkStatus.MissingFile, links[2].Status);
        Assert.Equal(LinkStatus.MissingFile, links[3].Status);
        Assert.Equal(2, graph.GetBrokenLinks().Count);
    }

    [Fact]
    public async Task Images_that_exist_resolve()
    {
        _dir.Write("a.md", "![pic](assets/pic.png)\n");
        _dir.Write("assets/pic.png", "not really a png");
        var graph = await LoadAsync();

        Assert.Equal(LinkStatus.Ok, Assert.Single(graph.GetOutgoing(P("a.md"))).Status);
    }

    [Fact]
    public async Task Reference_definitions_are_resolved_like_inline_links()
    {
        _dir.Write("a.md", "See [the b note][b].\n\n[b]: sub/b.md \"Title\"\n[dead]: nothing.md\n");
        _dir.Write("sub/b.md", "x\n");
        var graph = await LoadAsync();

        var broken = Assert.Single(graph.GetBrokenLinks());

        Assert.Equal(P("nothing.md"), broken.TargetPath);
        Assert.Contains(graph.GetBacklinks(P("sub/b.md")), b => b.SourcePath == P("a.md"));
    }

    [Fact]
    public async Task Wiki_links_resolve_by_file_name_title_and_alias_case_insensitively()
    {
        _dir.Write("a.md", "[[Project Plan]] [[project plan|text]] [[Overview]] [[Project Plan#Goals]] [[nothing]]\n");
        _dir.Write("docs/project plan.md", "# Goals\n");
        _dir.Write("docs/x.md", "---\ntitle: Overview\n---\n");
        var graph = await LoadAsync();

        var links = graph.GetOutgoing(P("a.md"));

        Assert.Equal(P("docs/project plan.md"), links[0].TargetPath);
        Assert.Equal(P("docs/project plan.md"), links[1].TargetPath);
        Assert.Equal(P("docs/x.md"), links[2].TargetPath);
        Assert.Equal(LinkStatus.Ok, links[3].Status);
        Assert.Equal(LinkStatus.MissingFile, links[4].Status);
    }

    [Fact]
    public async Task Ambiguous_wiki_links_are_reported_with_every_candidate()
    {
        _dir.Write("a.md", "[[Notes]]\n");
        _dir.Write("one/notes.md", "x\n");
        _dir.Write("two/Notes.md", "x\n");
        var graph = await LoadAsync();

        var link = Assert.Single(graph.GetOutgoing(P("a.md")));

        Assert.Equal(LinkStatus.AmbiguousWikiLink, link.Status);
        Assert.Equal(2, link.Candidates.Count);
        Assert.Single(graph.GetBrokenLinks());
    }

    [Fact]
    public async Task Backlinks_report_source_line_and_context()
    {
        _dir.Write("target.md", "# Target\n");
        _dir.Write("src.md", "intro\n\nSee [the target](target.md) for details.\n\nand [[target]] again\n");
        var graph = await LoadAsync();

        var backlinks = graph.GetBacklinks(P("target.md"));

        Assert.Equal([3, 5], backlinks.Select(b => b.Line).ToArray());
        Assert.All(backlinks, b => Assert.Equal(P("src.md"), b.SourcePath));
        Assert.Equal("See [the target](target.md) for details.", backlinks[0].Context);
    }

    [Fact]
    public async Task Orphans_are_files_nobody_links_to()
    {
        _dir.Write("index.md", "[a](a.md)\n");
        _dir.Write("a.md", "x\n");
        _dir.Write("lonely.md", "[a](a.md)\n");
        var graph = await LoadAsync();

        Assert.Equal([P("index.md"), P("lonely.md")], graph.GetOrphans(allowOutgoing: true).Order().ToArray());
        Assert.Empty(graph.GetOrphans());
    }

    [Fact]
    public async Task Update_overlays_unsaved_text_and_recomputes_dependent_links()
    {
        _dir.Write("a.md", "[b](b.md#intro)\n");
        _dir.Write("b.md", "# Other\n");
        var graph = await LoadAsync();
        Assert.Equal(LinkStatus.MissingAnchor, graph.GetOutgoing(P("a.md"))[0].Status);

        graph.Update(P("b.md"), "# Intro\n");

        Assert.Equal(LinkStatus.Ok, graph.GetOutgoing(P("a.md"))[0].Status);
        Assert.Equal("# Other\n", _dir.Read("b.md"));

        graph.Update(P("a.md"), "[b](b.md#intro) [c](c.md)\n");
        Assert.Equal(2, graph.GetOutgoing(P("a.md")).Count);
        graph.Update(P("c.md"), "new\n");
        Assert.Equal(LinkStatus.Ok, graph.GetOutgoing(P("a.md"))[1].Status);
    }

    [Fact]
    public async Task Remove_turns_incoming_links_into_broken_links_and_drops_backlinks()
    {
        _dir.Write("a.md", "[b](b.md)\n");
        _dir.Write("b.md", "x\n");
        var graph = await LoadAsync();

        File.Delete(_dir.Combine("b.md"));
        graph.Remove(P("b.md"));

        Assert.False(graph.Contains(P("b.md")));
        Assert.Equal(LinkStatus.MissingFile, graph.GetOutgoing(P("a.md"))[0].Status);
        Assert.Empty(graph.GetBacklinks(P("b.md")));
    }

    [Fact]
    public async Task Rename_moves_the_document_and_relinks_without_rereading_the_disk()
    {
        _dir.Write("a.md", "[b](b.md)\n");
        _dir.Write("b.md", "x\n");
        var graph = await LoadAsync();

        File.Move(_dir.Combine("b.md"), _dir.Combine("renamed.md"));
        graph.Rename(P("b.md"), P("renamed.md"));

        Assert.True(graph.Contains(P("renamed.md")));
        Assert.False(graph.Contains(P("b.md")));
        Assert.Equal(LinkStatus.MissingFile, graph.GetOutgoing(P("a.md"))[0].Status);
        Assert.Empty(graph.GetBacklinks(P("b.md")));
    }

    [Fact]
    public async Task Headings_are_exposed_for_quick_open()
    {
        _dir.Write("a.md", "# One\n\n## Two\n");
        var graph = await LoadAsync();

        Assert.Equal(["One", "Two"], graph.GetHeadings().Select(h => h.Heading.Text).ToArray());
    }

    [Fact]
    public async Task Links_inside_code_are_ignored()
    {
        _dir.Write("a.md", "`[x](missing.md)`\n\n```\n[y](missing2.md)\n```\n");
        var graph = await LoadAsync();

        Assert.Empty(graph.GetOutgoing(P("a.md")));
    }

    [Fact]
    public async Task The_graph_works_entirely_from_in_memory_documents()
    {
        var graph = new LinkGraph(_dir.Path, new LinkGraphOptions { PathExists = _ => false });
        graph.Update(P("a.md"), "[b](b.md) [[c]]\n");
        graph.Update(P("b.md"), "# H\n");
        graph.Update(P("c.md"), "x\n");

        Assert.All(graph.GetOutgoing(P("a.md")), l => Assert.Equal(LinkStatus.Ok, l.Status));
        Assert.Equal([P("a.md")], graph.GetOrphans(allowOutgoing: true).ToArray());
        Assert.Empty(graph.GetOrphans());
        await Task.CompletedTask;
    }
}

public sealed class LinkRepairTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly DocumentFileService _files = new();

    public void Dispose() => _dir.Dispose();

    private string P(string relative) => WorkspacePaths.Normalize(_dir.Combine(relative));

    private async Task<(LinkGraph Graph, LinkRepairPlanner Planner, WorkspaceFileOperations Ops)> SetupAsync()
    {
        var snapshot = await new WorkspaceScanner().ScanAsync(_dir.Path);
        var provider = new DiskDocumentTextProvider();
        var graph = new LinkGraph(_dir.Path);
        await graph.LoadAsync(snapshot.Files.Select(f => f.FullPath), provider);
        var planner = new LinkRepairPlanner(graph, provider);
        return (graph, planner, new WorkspaceFileOperations(_dir.Path, planner));
    }

    [Fact]
    public async Task Renaming_a_file_rewrites_links_preserving_titles_anchors_and_syntax()
    {
        _dir.Write("a.md", "See [old](old.md#sec \"A title\") and ![i](old.md) and <old.md> end.\n\n[ref]: old.md#x\n");
        _dir.Write("old.md", "# Sec\n\n## X\n");
        var (_, planner, _) = await SetupAsync();
        var before = _dir.Read("a.md");

        var plan = await planner.PlanRenameAsync(P("old.md"), P("new name.md"));

        var repair = Assert.Single(plan.Files, f => f.Path == P("a.md"));
        Assert.True(plan.Files.Single(f => f.Path == P("new name.md")).WasMoved);
        Assert.Equal(before, _dir.Read("a.md"));
        Assert.Contains("[old](new%20name.md#sec \"A title\")", repair.ResultText);
        Assert.Contains("[ref]: new%20name.md#x", repair.ResultText);
        Assert.Contains("See [old]", repair.ResultText);
        Assert.NotEmpty(repair.Previews);
        Assert.Contains(repair.Previews, p => p.Before.Contains("old.md#sec", StringComparison.Ordinal) && p.After.Contains("new%20name.md#sec", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Applying_a_rename_plan_writes_files_through_safe_save()
    {
        _dir.Write("a.md", "[x](old.md)\n");
        _dir.Write("sub/b.md", "[x](../old.md#h)\n");
        _dir.Write("old.md", "# H\n");
        var (_, planner, ops) = await SetupAsync();

        var moved = await ops.RenameAsync(P("old.md"), "renamed.md");
        var result = await moved.Plan!.ApplyAsync(_files);

        Assert.True(moved.Succeeded);
        Assert.True(result.Succeeded);
        Assert.Equal(2, result.AppliedCount);
        Assert.Equal("[x](renamed.md)\n", _dir.Read("a.md"));
        Assert.Equal("[x](../renamed.md#h)\n", _dir.Read("sub/b.md"));
        Assert.True(File.Exists(_dir.Combine("renamed.md")));
        Assert.Empty(Directory.EnumerateFiles(_dir.Path, "*.tern-tmp", SearchOption.AllDirectories));
        Assert.NotNull(planner);
    }

    [Fact]
    public async Task Moving_a_folder_rewrites_inbound_links_and_the_moved_files_own_relative_links()
    {
        _dir.Write("index.md", "[p](proj/plan.md) [n](proj/notes/n.md)\n");
        _dir.Write("proj/plan.md", "[n](notes/n.md) [home](../index.md) [self](#top)\n\n# Top\n");
        _dir.Write("proj/notes/n.md", "[plan](../plan.md) [index](../../index.md)\n");
        _dir.Write("shared.md", "[plan](proj/plan.md)\n");
        Directory.CreateDirectory(_dir.Combine("archive"));
        var (_, _, ops) = await SetupAsync();

        var moved = await ops.MoveAsync(P("proj"), P("archive"));
        var result = await moved.Plan!.ApplyAsync(_files);

        Assert.True(moved.Succeeded, moved.Message);
        Assert.True(result.Succeeded);
        Assert.Equal("[p](archive/proj/plan.md) [n](archive/proj/notes/n.md)\n", _dir.Read("index.md"));
        Assert.Equal("[n](notes/n.md) [home](../../index.md) [self](#top)\n\n# Top\n", _dir.Read("archive/proj/plan.md"));
        Assert.Equal("[plan](../plan.md) [index](../../../index.md)\n", _dir.Read("archive/proj/notes/n.md"));
        Assert.Equal("[plan](archive/proj/plan.md)\n", _dir.Read("shared.md"));
    }

    [Fact]
    public async Task Links_between_files_that_move_together_stay_untouched()
    {
        _dir.Write("proj/a.md", "[b](b.md)\n");
        _dir.Write("proj/b.md", "[a](a.md)\n");
        _dir.Write("dest/keep.md", "x\n");
        var (_, planner, _) = await SetupAsync();

        var plan = await planner.PlanRenameAsync(P("proj"), P("dest/proj"));

        Assert.Equal(0, plan.TotalEdits);
    }

    [Fact]
    public async Task Wiki_links_follow_a_rename_only_when_the_name_changes()
    {
        _dir.Write("a.md", "[[old]] and [[old|label]] and [[old#Sec]] and [[other]]\n");
        _dir.Write("old.md", "# Sec\n");
        _dir.Write("other.md", "x\n");
        _dir.Write("folder/readme.md", "x\n");
        var (_, planner, _) = await SetupAsync();

        var renamed = await planner.PlanRenameAsync(P("old.md"), P("fresh.md"));
        var movedOnly = await planner.PlanRenameAsync(P("old.md"), P("folder/old.md"));

        Assert.Equal("[[fresh]] and [[fresh|label]] and [[fresh#Sec]] and [[other]]\n", Assert.Single(renamed.Files, f => f.Path == P("a.md")).ResultText);
        Assert.DoesNotContain(movedOnly.Files, f => f.Path == P("a.md") && f.Edits.Count > 0);
    }

    [Fact]
    public async Task Crlf_documents_keep_their_line_endings()
    {
        _dir.Write("a.md", "line one\r\n[x](old.md)\r\nline three\r\n");
        _dir.Write("old.md", "x\r\n");
        var (_, _, ops) = await SetupAsync();

        var moved = await ops.RenameAsync(P("old.md"), "new.md");
        await moved.Plan!.ApplyAsync(_files);

        Assert.Equal("line one\r\n[x](new.md)\r\nline three\r\n", File.ReadAllText(_dir.Combine("a.md")));
    }

    [Fact]
    public async Task Case_only_renames_are_planned_and_applied()
    {
        _dir.Write("a.md", "[x](note.md)\n");
        _dir.Write("note.md", "x\n");
        var (_, _, ops) = await SetupAsync();

        var moved = await ops.RenameAsync(P("note.md"), "Note.md");
        var result = await moved.Plan!.ApplyAsync(_files);

        Assert.True(moved.Succeeded, moved.Message);
        Assert.True(result.Succeeded);
        Assert.Equal("[x](Note.md)\n", _dir.Read("a.md"));
        Assert.Contains(Directory.EnumerateFiles(_dir.Path).Select(Path.GetFileName), n => n == "Note.md");
    }

    [Fact]
    public async Task A_file_changed_on_disk_after_planning_is_left_untouched_and_the_rest_still_applies()
    {
        _dir.Write("a.md", "[x](old.md)\n");
        _dir.Write("b.md", "[x](old.md)\n");
        _dir.Write("old.md", "x\n");
        var (_, planner, _) = await SetupAsync();
        var plan = await planner.PlanRenameAsync(P("old.md"), P("new.md"));
        const string edited = "[x](old.md)\n\nthe user kept typing in another program\n";
        File.WriteAllText(_dir.Combine("a.md"), edited);

        var result = await plan.ApplyAsync(_files);

        Assert.False(result.Succeeded);
        Assert.Equal(RepairStatus.Conflict, result.Files.Single(f => f.Repair.Path == P("a.md")).Status);
        Assert.Equal(RepairStatus.Applied, result.Files.Single(f => f.Repair.Path == P("b.md")).Status);
        Assert.Equal(edited, File.ReadAllText(_dir.Combine("a.md")));
        Assert.Equal("[x](new.md)\n", _dir.Read("b.md"));
        Assert.Empty(Directory.EnumerateFiles(_dir.Path, "*.tern-tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_deleted_file_is_reported_as_a_failure_not_recreated()
    {
        _dir.Write("a.md", "[x](old.md)\n");
        _dir.Write("old.md", "x\n");
        var (_, planner, _) = await SetupAsync();
        var plan = await planner.PlanRenameAsync(P("old.md"), P("new.md"));
        File.Delete(_dir.Combine("a.md"));

        var result = await plan.ApplyAsync(_files);

        Assert.NotEqual(RepairStatus.Applied, result.Files.Single(f => f.Repair.Path == P("a.md")).Status);
        Assert.False(File.Exists(_dir.Combine("a.md")));
    }

    [Fact]
    public async Task The_apply_filter_lets_the_app_skip_documents_with_unsaved_changes()
    {
        _dir.Write("a.md", "[x](old.md)\n");
        _dir.Write("b.md", "[x](old.md)\n");
        _dir.Write("old.md", "x\n");
        var (_, planner, _) = await SetupAsync();
        var plan = await planner.PlanRenameAsync(P("old.md"), P("new.md"));

        var result = await plan.ApplyAsync(_files, repair => repair.Path != P("a.md"));

        Assert.Equal("[x](old.md)\n", _dir.Read("a.md"));
        Assert.Equal("[x](new.md)\n", _dir.Read("b.md"));
        Assert.Equal(1, result.AppliedCount);
    }

    [Fact]
    public async Task Planning_uses_overlaid_buffers_when_the_provider_has_them()
    {
        _dir.Write("a.md", "nothing here\n");
        _dir.Write("old.md", "x\n");
        var snapshot = await new WorkspaceScanner().ScanAsync(_dir.Path);
        var provider = new OverlayTextProvider(new DiskDocumentTextProvider());
        var graph = new LinkGraph(_dir.Path);
        await graph.LoadAsync(snapshot.Files.Select(f => f.FullPath), provider);
        provider.Set(P("a.md"), "now [x](old.md)\n");
        graph.Update(P("a.md"), "now [x](old.md)\n");

        var plan = await new LinkRepairPlanner(graph, provider).PlanRenameAsync(P("old.md"), P("new.md"));

        Assert.Equal("now [x](new.md)\n", Assert.Single(plan.Files, f => f.Path == P("a.md")).ResultText);
        Assert.Equal("nothing here\n", _dir.Read("a.md"));
    }

    [Fact]
    public async Task Heading_renames_repair_fragment_links_in_other_documents_and_the_same_one()
    {
        const string oldText = "# Title\n\n## Old Heading\n\nsee [here](#old-heading)\n";
        const string newText = "# Title\n\n## Brand New\n\nsee [here](#old-heading)\n";
        _dir.Write("doc.md", oldText);
        _dir.Write("other.md", "[a](doc.md#old-heading) [b](doc.md#title) [[doc#Old Heading]]\n");
        var (_, planner, _) = await SetupAsync();

        var plan = await planner.PlanHeadingRenameAsync(P("doc.md"), oldText, newText);

        var other = Assert.Single(plan.Files, f => f.Path == P("other.md"));
        Assert.Equal("[a](doc.md#brand-new) [b](doc.md#title) [[doc#Brand New]]\n", other.ResultText);
        Assert.Equal("# Title\n\n## Old Heading\n\nsee [here](#old-heading)\n", _dir.Read("doc.md"));
        Assert.Single(plan.Files, f => f.Path == P("doc.md"));
    }

    [Fact]
    public async Task Moving_into_itself_is_rejected_and_nothing_moves()
    {
        _dir.Write("proj/a.md", "x\n");
        var (_, _, ops) = await SetupAsync();

        var result = await ops.MoveAsync(P("proj"), P("proj"));
        Directory.CreateDirectory(_dir.Combine("proj/inner"));
        var nested = await ops.MoveAsync(P("proj"), P("proj/inner"));

        Assert.False(nested.Succeeded);
        Assert.Equal(FileOperationError.InvalidTarget, nested.Error);
        Assert.True(File.Exists(_dir.Combine("proj/a.md")));
        Assert.NotNull(result);
    }
}
