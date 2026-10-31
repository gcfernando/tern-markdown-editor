using System.Collections.Concurrent;
using System.Text.Json;
using Tern.Workspaces;

namespace Tern.Workspaces.Tests;

public sealed class ChangeCoalescerTests
{
    [Fact]
    public void Create_modify_delete_of_a_scratch_file_vanishes()
    {
        var coalescer = new ChangeCoalescer();

        coalescer.Created("/w/a.md", false);
        coalescer.Changed("/w/a.md", false);
        coalescer.Deleted("/w/a.md");

        Assert.True(coalescer.IsEmpty);
    }

    [Fact]
    public void Repeated_changes_collapse_to_one_entry()
    {
        var coalescer = new ChangeCoalescer();
        for (var i = 0; i < 50; i++)
        {
            coalescer.Changed("/w/a.md", false);
        }

        var change = Assert.Single(coalescer.Drain().Changes);

        Assert.Equal(WorkspaceChangeKind.Changed, change.Kind);
        Assert.True(coalescer.IsEmpty);
    }

    [Fact]
    public void Rename_chains_collapse_and_keep_the_original_path()
    {
        var coalescer = new ChangeCoalescer();

        coalescer.Renamed("/w/a.md", "/w/b.md", false);
        coalescer.Renamed("/w/b.md", "/w/c.md", false);

        var change = Assert.Single(coalescer.Drain().Changes);
        Assert.Equal(new WorkspaceChange(WorkspaceChangeKind.Renamed, "/w/c.md", "/w/a.md"), change);
    }

    [Fact]
    public void Rename_then_delete_reports_the_original_as_deleted()
    {
        var coalescer = new ChangeCoalescer();

        coalescer.Renamed("/w/a.md", "/w/b.md", false);
        coalescer.Deleted("/w/b.md");

        var change = Assert.Single(coalescer.Drain().Changes);
        Assert.Equal(WorkspaceChangeKind.Deleted, change.Kind);
        Assert.Equal("/w/a.md", change.Path);
    }

    [Fact]
    public void Delete_then_create_is_a_modification_as_editors_save_that_way()
    {
        var coalescer = new ChangeCoalescer();

        coalescer.Deleted("/w/a.md");
        coalescer.Created("/w/a.md", false);

        Assert.Equal(WorkspaceChangeKind.Changed, Assert.Single(coalescer.Drain().Changes).Kind);
    }

    [Fact]
    public void Rescan_requests_survive_until_drained()
    {
        var coalescer = new ChangeCoalescer();
        coalescer.RequireRescan();

        Assert.False(coalescer.IsEmpty);
        Assert.True(coalescer.Drain().RescanRequired);
        Assert.False(coalescer.Drain().RescanRequired);
    }
}

public sealed class WorkspaceWatcherTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly ConcurrentQueue<WorkspaceChangeSet> _sets = new();
    private WorkspaceWatcher? _watcher;

    public void Dispose()
    {
        _watcher?.Dispose();
        _dir.Dispose();
    }

    private void Start(TimeSpan? debounce = null)
    {
        _watcher = new WorkspaceWatcher(_dir.Path, debounce: debounce ?? TimeSpan.FromMilliseconds(200));
        _watcher.Changed += (_, set) => _sets.Enqueue(set);
        _watcher.Start();
    }

    private IEnumerable<WorkspaceChange> All => _sets.SelectMany(s => s.Changes);

    private async Task<bool> WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 150; i++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return condition();
    }

    [Fact]
    public async Task A_new_markdown_file_is_reported_as_created()
    {
        Start();

        var path = _dir.Write("new.md", "x");

        Assert.True(await WaitForAsync(() => All.Any(c => c.Kind == WorkspaceChangeKind.Created && WorkspacePaths.PathsEqual(c.Path, path))));
    }

    [Fact]
    public async Task A_burst_of_writes_is_delivered_as_one_batch_with_one_entry_per_file()
    {
        var path = _dir.Write("a.md", "0");
        Start(TimeSpan.FromMilliseconds(400));

        // Keep the writer independent of the test runner's thread pool. Awaiting
        // between writes can stretch the burst beyond the maximum batch latency
        // on a busy runner, where multiple batches are then the correct behavior.
        await Task.Factory.StartNew(() =>
        {
            for (var i = 1; i <= 20; i++) File.WriteAllText(path, new string('x', i));
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        Assert.True(await WaitForAsync(() => All.Any()));
        await Task.Delay(800);
        var forFile = All.Where(c => WorkspacePaths.PathsEqual(c.Path, path)).ToList();
        Assert.Single(forFile);
        Assert.Equal(WorkspaceChangeKind.Changed, forFile[0].Kind);
        Assert.Single(_sets);
    }

    [Fact]
    public async Task Deleting_a_file_is_reported()
    {
        var path = _dir.Write("a.md", "x");
        Start();

        File.Delete(path);

        Assert.True(await WaitForAsync(() => All.Any(c => c.Kind == WorkspaceChangeKind.Deleted && WorkspacePaths.PathsEqual(c.Path, path))));
    }

    [Fact]
    public async Task A_rename_carries_the_old_and_new_path()
    {
        var oldPath = _dir.Write("old.md", "x");
        var newPath = _dir.Combine("new.md");
        Start();

        File.Move(oldPath, newPath);

        Assert.True(await WaitForAsync(() => All.Any(c =>
            c.Kind == WorkspaceChangeKind.Renamed && WorkspacePaths.PathsEqual(c.Path, newPath) && WorkspacePaths.PathsEqual(c.OldPath!, oldPath))));
    }

    [Fact]
    public async Task Folder_renames_are_flagged_as_directories()
    {
        _dir.Write("dir/a.md", "x");
        Start();

        Directory.Move(_dir.Combine("dir"), _dir.Combine("renamed"));

        Assert.True(await WaitForAsync(() => All.Any(c => c.Kind == WorkspaceChangeKind.Renamed && c.IsDirectory)));
    }

    [Fact]
    public async Task Excluded_folders_and_other_file_types_are_ignored()
    {
        Start();

        _dir.Write("node_modules/pkg/readme.md", "x");
        _dir.Write(".git/notes.md", "x");
        _dir.Write("image.png", "x");
        var marker = _dir.Write("marker.md", "x");

        Assert.True(await WaitForAsync(() => All.Any(c => WorkspacePaths.PathsEqual(c.Path, marker))));
        await Task.Delay(500);
        var leaked = All.Where(c => c.Path.Contains("node_modules", StringComparison.Ordinal) || c.Path.Contains(Path.DirectorySeparatorChar + ".git", StringComparison.Ordinal) || c.Path.EndsWith(".png", StringComparison.Ordinal)).ToList();
        Assert.True(leaked.Count == 0, string.Join("; ", leaked.Select(c => $"{c.Kind} {c.Path}")));
    }

    [Fact]
    public async Task Nothing_is_raised_after_stop_or_dispose()
    {
        Start();
        _watcher!.Stop();

        _dir.Write("after-stop.md", "x");
        await Task.Delay(800);

        Assert.Empty(_sets);
    }
}

public sealed class FileOperationsTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly WorkspaceFileOperations _ops;

    public FileOperationsTests()
    {
        _ops = new WorkspaceFileOperations(_dir.Path);
    }

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("con")]
    [InlineData("CON.md")]
    [InlineData("nul.txt")]
    [InlineData("Com1")]
    [InlineData("lpt9.md")]
    [InlineData("a<b.md")]
    [InlineData("a:b.md")]
    [InlineData("a\"b.md")]
    [InlineData("a|b.md")]
    [InlineData("a?b.md")]
    [InlineData("a*b.md")]
    [InlineData("a/b.md")]
    [InlineData("a\\b.md")]
    [InlineData("note.")]
    [InlineData("note ")]
    [InlineData(" note")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("tab\there.md")]
    public void Unsafe_names_are_rejected(string name)
    {
        Assert.False(FileNameValidator.Validate(name).IsValid);
    }

    [Theory]
    [InlineData("note.md")]
    [InlineData("My Notes (2024).md")]
    [InlineData(".hidden.md")]
    [InlineData("日本語.md")]
    [InlineData("console.md")]
    [InlineData("com10.md")]
    public void Ordinary_names_are_accepted(string name)
    {
        Assert.True(FileNameValidator.Validate(name).IsValid);
    }

    [Fact]
    public void Overlong_names_are_rejected_with_a_specific_reason()
    {
        var result = FileNameValidator.Validate(new string('a', 256) + ".md");

        Assert.Equal(NameProblem.TooLong, result.Problem);
        Assert.Equal(NameProblem.ReservedName, FileNameValidator.Validate("PRN").Problem);
        Assert.Equal(NameProblem.TrailingDotOrSpace, FileNameValidator.Validate("a.").Problem);
    }

    [Fact]
    public void Creating_a_file_writes_it_and_refuses_to_overwrite()
    {
        var created = _ops.CreateFile(_dir.Path, "new.md", "# Hello\n");
        var again = _ops.CreateFile(_dir.Path, "new.md", "other");

        Assert.True(created.Succeeded);
        Assert.Equal("# Hello\n", _dir.Read("new.md"));
        Assert.Equal(FileOperationError.AlreadyExists, again.Error);
        Assert.Equal("# Hello\n", _dir.Read("new.md"));
    }

    [Fact]
    public void Auto_rename_picks_a_free_name()
    {
        _dir.Write("note.md", "x");

        var result = _ops.CreateFile(_dir.Path, "note.md", "new", CollisionPolicy.AutoRename);

        Assert.True(result.Succeeded);
        Assert.Equal("note (2).md", Path.GetFileName(result.Path));
        Assert.Equal("x", _dir.Read("note.md"));
    }

    [Fact]
    public void Creating_reports_invalid_names_missing_folders_and_outside_targets()
    {
        using var outside = new TempDirectory();

        Assert.Equal(FileOperationError.InvalidName, _ops.CreateFile(_dir.Path, "CON.md").Error);
        Assert.Equal(FileOperationError.NotFound, _ops.CreateFile(_dir.Combine("missing"), "a.md").Error);
        Assert.Equal(FileOperationError.OutsideWorkspace, _ops.CreateFile(outside.Path, "a.md").Error);
        Assert.False(File.Exists(Path.Combine(outside.Path, "a.md")));
    }

    [Fact]
    public void Creating_folders_validates_and_handles_collisions()
    {
        Assert.True(_ops.CreateFolder(_dir.Path, "docs").Succeeded);
        Assert.Equal(FileOperationError.AlreadyExists, _ops.CreateFolder(_dir.Path, "docs").Error);
        Assert.Equal("docs (2)", Path.GetFileName(_ops.CreateFolder(_dir.Path, "docs", CollisionPolicy.AutoRename).Path));
        Assert.Equal(FileOperationError.InvalidName, _ops.CreateFolder(_dir.Path, "aux").Error);
    }

    [Fact]
    public async Task Rename_validates_the_name_and_the_collision()
    {
        _dir.Write("a.md", "a");
        _dir.Write("b.md", "b");

        Assert.Equal(FileOperationError.InvalidName, (await _ops.RenameAsync(_dir.Combine("a.md"), "bad?.md")).Error);
        Assert.Equal(FileOperationError.AlreadyExists, (await _ops.RenameAsync(_dir.Combine("a.md"), "b.md")).Error);
        Assert.Equal(FileOperationError.NotFound, (await _ops.RenameAsync(_dir.Combine("zzz.md"), "c.md")).Error);
        Assert.Equal("a", _dir.Read("a.md"));
        Assert.Equal("b", _dir.Read("b.md"));

        var ok = await _ops.RenameAsync(_dir.Combine("a.md"), "c.md");
        Assert.True(ok.Succeeded);
        Assert.False(File.Exists(_dir.Combine("a.md")));
        Assert.Equal("a", _dir.Read("c.md"));
    }

    [Fact]
    public async Task Case_only_folder_renames_work_on_case_insensitive_file_systems()
    {
        _dir.Write("docs/a.md", "x");

        var result = await _ops.RenameAsync(_dir.Combine("docs"), "Docs");

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains(Directory.EnumerateDirectories(_dir.Path).Select(Path.GetFileName), n => n == "Docs");
        Assert.Equal("x", _dir.Read("Docs/a.md"));
    }

    [Fact]
    public async Task Moving_handles_collisions_and_workspace_boundaries()
    {
        _dir.Write("a.md", "source");
        _dir.Write("dest/a.md", "existing");
        using var outside = new TempDirectory();

        var refused = await _ops.MoveAsync(_dir.Combine("a.md"), _dir.Combine("dest"));
        var escaped = await _ops.MoveAsync(_dir.Combine("a.md"), outside.Path);
        var renamed = await _ops.MoveAsync(_dir.Combine("a.md"), _dir.Combine("dest"), CollisionPolicy.AutoRename);

        Assert.Equal(FileOperationError.AlreadyExists, refused.Error);
        Assert.Equal(FileOperationError.OutsideWorkspace, escaped.Error);
        Assert.True(renamed.Succeeded);
        Assert.Equal("existing", _dir.Read("dest/a.md"));
        Assert.Equal("source", File.ReadAllText(renamed.Path!));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside.Path));
    }

    [Fact]
    public void Duplicating_a_file_picks_unused_copy_names()
    {
        _dir.Write("note.md", "content");

        var first = _ops.Duplicate(_dir.Combine("note.md"));
        var second = _ops.Duplicate(_dir.Combine("note.md"));

        Assert.Equal("note copy.md", Path.GetFileName(first.Path));
        Assert.Equal("note copy 2.md", Path.GetFileName(second.Path));
        Assert.Equal("content", File.ReadAllText(second.Path!));
    }

    [Fact]
    public void Duplicating_a_folder_copies_everything()
    {
        _dir.Write("proj/a.md", "a");
        _dir.Write("proj/sub/b.md", "b");

        var result = _ops.Duplicate(_dir.Combine("proj"));

        Assert.Equal("proj copy", Path.GetFileName(result.Path));
        Assert.Equal("b", File.ReadAllText(Path.Combine(result.Path!, "sub", "b.md")));
        Assert.Equal("a", _dir.Read("proj/a.md"));
    }
}

public sealed class RecentItemsStoreTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private string FilePath => _dir.Combine("recent.json");

    [Fact]
    public void A_missing_file_starts_empty()
    {
        var store = new RecentItemsStore(_dir.Path);

        Assert.Equal(StoreLoadStatus.Missing, store.LoadStatus);
        Assert.Empty(store.Files);
    }

    [Fact]
    public void Entries_persist_most_recent_first_and_reopening_restores_them()
    {
        var now = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var store = new RecentItemsStore(_dir.Path, clock: () => now = now.AddMinutes(1));

        store.AddFile(_dir.Combine("a.md"));
        store.AddFile(_dir.Combine("b.md"));
        store.AddFile(_dir.Combine("a.md"));
        store.AddWorkspace(_dir.Combine("ws"));
        var reopened = new RecentItemsStore(_dir.Path);

        Assert.Equal(StoreLoadStatus.Loaded, reopened.LoadStatus);
        Assert.Equal(["a.md", "b.md"], reopened.Files.Select(e => Path.GetFileName(e.Path)).ToArray());
        Assert.Single(reopened.Workspaces);
    }

    [Fact]
    public void Pinned_entries_stay_on_top_and_survive_trimming_and_clearing()
    {
        var store = new RecentItemsStore(_dir.Path, maxFiles: 3);
        store.AddFile(_dir.Combine("pinned.md"));
        Assert.True(store.SetPinned(RecentKind.File, _dir.Combine("pinned.md"), true));

        for (var i = 0; i < 10; i++)
        {
            store.AddFile(_dir.Combine($"n{i}.md"));
        }

        // The limit applies to unpinned entries; pinned ones never count against it.
        Assert.Equal(4, store.Files.Count);
        Assert.Equal("pinned.md", Path.GetFileName(store.Files[0].Path));
        Assert.True(store.Files[0].Pinned);
        store.Clear(RecentKind.File);
        Assert.Single(store.Files);
        Assert.False(store.SetPinned(RecentKind.File, _dir.Combine("unknown.md"), true));
    }

    [Fact]
    public void The_list_is_bounded()
    {
        var store = new RecentItemsStore(_dir.Path, maxFiles: 5, maxWorkspaces: 2);

        for (var i = 0; i < 20; i++)
        {
            store.AddFile(_dir.Combine($"{i}.md"));
            store.AddWorkspace(_dir.Combine($"w{i}"));
        }

        Assert.Equal(5, store.Files.Count);
        Assert.Equal(2, store.Workspaces.Count);
        Assert.Equal("19.md", Path.GetFileName(store.Files[0].Path));
    }

    [Fact]
    public void Remove_and_rename_update_the_list()
    {
        var store = new RecentItemsStore(_dir.Path);
        store.AddFile(_dir.Combine("a.md"));
        store.AddFile(_dir.Combine("b.md"));

        store.Rename(RecentKind.File, _dir.Combine("a.md"), _dir.Combine("renamed.md"));
        Assert.True(store.Remove(RecentKind.File, _dir.Combine("b.md")));

        Assert.Equal(["renamed.md"], new RecentItemsStore(_dir.Path).Files.Select(e => Path.GetFileName(e.Path)).ToArray());
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    [InlineData("{\"version\":1,\"files\":\"nope\",\"workspaces\":[1,null,{\"path\":5}]}")]
    public void Corrupt_files_are_tolerated_and_the_store_remains_usable(string content)
    {
        File.WriteAllText(FilePath, content);

        var store = new RecentItemsStore(_dir.Path);
        store.AddFile(_dir.Combine("a.md"));

        Assert.Empty(new RecentItemsStore(_dir.Path).Workspaces);
        Assert.Single(new RecentItemsStore(_dir.Path).Files);
    }

    [Fact]
    public void Unparseable_files_are_kept_aside_instead_of_being_destroyed()
    {
        File.WriteAllText(FilePath, "{ broken");

        var store = new RecentItemsStore(_dir.Path);
        store.AddFile(_dir.Combine("a.md"));

        Assert.Equal(StoreLoadStatus.Corrupt, store.LoadStatus);
        Assert.Contains(Directory.EnumerateFiles(_dir.Path), f => Path.GetFileName(f) != "recent.json" && File.ReadAllText(f) == "{ broken");
    }

    [Fact]
    public void A_newer_version_is_not_overwritten_without_a_backup()
    {
        File.WriteAllText(FilePath, "{\"version\":99,\"files\":[]}");

        var store = new RecentItemsStore(_dir.Path);
        store.AddFile(_dir.Combine("a.md"));

        Assert.Equal(StoreLoadStatus.NewerVersion, store.LoadStatus);
        Assert.Contains(Directory.EnumerateFiles(_dir.Path), f => File.ReadAllText(f).Contains("\"version\":99", StringComparison.Ordinal));
    }

    [Fact]
    public void Saving_is_atomic_and_leaves_no_temporary_files()
    {
        var store = new RecentItemsStore(_dir.Path);

        for (var i = 0; i < 50; i++)
        {
            store.AddFile(_dir.Combine($"{i}.md"));
            using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
        }

        Assert.Equal(["recent.json"], Directory.EnumerateFiles(_dir.Path).Select(f => Path.GetFileName(f)!).ToArray());
        Assert.Null(store.LastSaveError);
    }

    [Fact]
    public void A_failing_disk_does_not_lose_in_memory_state_or_throw()
    {
        var blocked = _dir.Combine("blocked");
        File.WriteAllText(blocked, "a file where the folder should be");
        var store = new RecentItemsStore(Path.Combine(blocked, "inner"));

        store.AddFile(_dir.Combine("a.md"));

        Assert.NotNull(store.LastSaveError);
        Assert.Single(store.Files);
    }
}

public sealed class SessionStoreTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void A_missing_file_gives_an_empty_session()
    {
        var store = new SessionStore(_dir.Path);

        Assert.Same(SessionSnapshot.Empty, store.Load());
        Assert.Equal(StoreLoadStatus.Missing, store.LastLoadStatus);
    }

    [Fact]
    public void A_session_round_trips()
    {
        var snapshot = new SessionSnapshot(
            _dir.Combine("ws"),
            [new TabState(_dir.Combine("a.md"), 12, 34.5), new TabState(_dir.Combine("b.md"), 0, 0)],
            _dir.Combine("b.md"),
            new WindowLayoutHint(10, 20, 1200, 800, true, 260, false),
            new Dictionary<string, FileViewState> { [_dir.Combine("c.md")] = new FileViewState(5, 6.5) });
        var store = new SessionStore(_dir.Path);

        Assert.True(store.Save(snapshot));
        var loaded = new SessionStore(_dir.Path).Load();

        Assert.Equal(snapshot.LastWorkspace, loaded.LastWorkspace);
        Assert.Equal(snapshot.Tabs, loaded.Tabs);
        Assert.Equal(snapshot.ActiveTab, loaded.ActiveTab);
        Assert.Equal(snapshot.Window, loaded.Window);
        Assert.Equal(new FileViewState(5, 6.5), loaded.FileViews[_dir.Combine("c.md")]);
        Assert.Contains("\"version\"", File.ReadAllText(_dir.Combine("session.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void Hand_edited_nonsense_is_sanitised()
    {
        File.WriteAllText(_dir.Combine("session.json"), """
            {"version":1,"activeTab":"/not/open.md","tabs":[{"path":"/a.md","caret":-5,"scroll":-1},{"path":""},{"path":"/a.md"},5,{"caret":3}],
             "window":{"width":-100,"height":"tall","maximized":"yes"},"fileViews":[{"path":"/x.md","caret":"abc"}]}
            """);

        var loaded = new SessionStore(_dir.Path).Load();

        Assert.Equal(2, loaded.Tabs.Count);
        Assert.All(loaded.Tabs, tab => Assert.Equal(new TabState("/a.md", 0, 0), tab));
        Assert.Null(loaded.ActiveTab);
        Assert.Null(loaded.Window?.Width);
        Assert.Single(loaded.FileViews);
    }

    [Fact]
    public void Tab_count_is_bounded()
    {
        var tabs = Enumerable.Range(0, SessionStore.MaxTabs + 50).Select(i => new TabState(_dir.Combine($"{i}.md"), 0, 0)).ToList();
        var store = new SessionStore(_dir.Path);
        store.Save(new SessionSnapshot(null, tabs, null, null, new Dictionary<string, FileViewState>()));

        Assert.Equal(SessionStore.MaxTabs, store.Load().Tabs.Count);
    }

    [Theory]
    [InlineData("{ nope")]
    [InlineData("")]
    [InlineData("[]")]
    public void Corrupt_files_give_an_empty_session_without_throwing(string content)
    {
        File.WriteAllText(_dir.Combine("session.json"), content);

        var store = new SessionStore(_dir.Path);

        Assert.Same(SessionSnapshot.Empty, store.Load());
        Assert.True(store.Save(SessionSnapshot.Empty with { LastWorkspace = "/w" }));
        Assert.Equal("/w", store.Load().LastWorkspace);
    }

    [Fact]
    public void A_newer_version_is_preserved_when_the_app_saves_over_it()
    {
        File.WriteAllText(_dir.Combine("session.json"), "{\"version\":42,\"tabs\":[]}");
        var store = new SessionStore(_dir.Path);

        store.Load();
        store.Save(SessionSnapshot.Empty);

        Assert.Equal(StoreLoadStatus.NewerVersion, store.LastLoadStatus);
        Assert.Contains(Directory.EnumerateFiles(_dir.Path), f => File.ReadAllText(f).Contains("\"version\":42", StringComparison.Ordinal));
    }

    [Fact]
    public void Concurrent_saves_never_leave_a_torn_file()
    {
        var store = new SessionStore(_dir.Path);
        var tabs = Enumerable.Range(0, 100).Select(i => new TabState(_dir.Combine($"{i}.md"), i, i)).ToList();
        var snapshot = new SessionSnapshot(null, tabs, null, null, new Dictionary<string, FileViewState>());

        Parallel.For(0, 40, _ => store.Save(snapshot));

        Assert.Equal(100, new SessionStore(_dir.Path).Load().Tabs.Count);
        Assert.Equal(["session.json"], Directory.EnumerateFiles(_dir.Path).Select(f => Path.GetFileName(f)!).ToArray());
    }

    [Fact]
    public void Failed_saves_are_reported_not_thrown()
    {
        var blocked = _dir.Combine("blocked");
        File.WriteAllText(blocked, "x");
        var store = new SessionStore(Path.Combine(blocked, "inner"));

        Assert.False(store.Save(SessionSnapshot.Empty));
        Assert.NotNull(store.LastSaveError);
    }
}
