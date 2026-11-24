using System.Text;
using System.Text.Json.Nodes;
using Tern.App.Documents;
using Tern.App.Settings;
using Tern.Domain.Documents;
using Tern.Domain.Text;
using Tern.Filesystem;
using Tern.Markdown;

namespace Tern.App.Tests;

public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tern-app-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
    }
}

public class DocumentTabTests
{
    private static readonly DocumentFileService Files = new();

    private static Task<DocumentTab> Open(string path) => DocumentTab.OpenAsync(path, Files, MarkdownProfile.Extended);

    [Fact]
    public async Task Open_edit_save_round_trips_bytes_and_clears_dirty()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("# Hi\r\nline\r\n"));
        using var tab = await Open(path);

        Assert.Equal("CRLF", tab.LineEndingText);
        tab.ApplyEdits([TextEdit.Insert(0, "x")]);
        Assert.True(tab.IsDirty);

        var result = await tab.SaveAsync();
        Assert.IsType<SaveSucceeded>(result);
        Assert.False(tab.IsDirty);
        Assert.Equal("x# Hi\r\nline\r\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Save_reports_conflict_when_disk_changed_and_keep_mine_overwrites()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        await File.WriteAllTextAsync(path, "one\n");
        using var tab = await Open(path);
        tab.ApplyEdits([TextEdit.Insert(0, "mine ")]);
        await File.WriteAllTextAsync(path, "other\n");

        var first = await tab.SaveAsync();
        Assert.IsType<SaveConflict>(first);
        Assert.Equal(ConflictKind.ModifiedOnDisk, tab.Conflict?.Kind);
        Assert.Equal("other\n", await File.ReadAllTextAsync(path));

        var forced = await tab.KeepMineAndOverwriteAsync();
        Assert.IsType<SaveSucceeded>(forced);
        Assert.Null(tab.Conflict);
        Assert.Equal("mine one\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task CheckDisk_auto_reloads_clean_document()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        await File.WriteAllTextAsync(path, "one\n");
        using var tab = await Open(path);
        await File.WriteAllTextAsync(path, "two\n");

        Assert.Equal(DiskCheckResult.ReloadedSilently, await tab.CheckDiskAsync(true));
        Assert.Equal("two\n", tab.CurrentText);
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public async Task CheckDisk_flags_conflict_for_dirty_document_and_keeps_buffer()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        await File.WriteAllTextAsync(path, "one\n");
        using var tab = await Open(path);
        tab.ApplyEdits([TextEdit.Insert(0, "mine ")]);
        await File.WriteAllTextAsync(path, "two\n");

        Assert.Equal(DiskCheckResult.Conflict, await tab.CheckDiskAsync(true));
        Assert.Equal("mine one\n", tab.CurrentText);
        Assert.Equal(ConflictKind.ModifiedOnDisk, tab.Conflict?.Kind);
    }

    [Fact]
    public async Task CheckDisk_treats_identical_rewrite_as_unchanged_content()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        await File.WriteAllTextAsync(path, "same\n");
        using var tab = await Open(path);
        tab.ApplyEdits([TextEdit.Insert(0, "same\n")]);
        await File.WriteAllTextAsync(path, "same\nsame\n");

        var result = await tab.CheckDiskAsync(true);
        Assert.Equal(DiskCheckResult.BaselineRefreshed, result);
        Assert.Null(tab.Conflict);
    }

    [Fact]
    public async Task Deleted_file_is_reported_and_save_recreates_it_after_acceptance()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        await File.WriteAllTextAsync(path, "one\n");
        using var tab = await Open(path);
        File.Delete(path);

        Assert.Equal(DiskCheckResult.Conflict, await tab.CheckDiskAsync(true));
        Assert.Equal(ConflictKind.DeletedOnDisk, tab.Conflict?.Kind);

        tab.AcceptMissingFile();
        Assert.IsType<SaveSucceeded>(await tab.SaveAsync());
        Assert.Equal("one\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Take_disk_version_replaces_buffer_and_is_undoable_state_clean()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        await File.WriteAllTextAsync(path, "one\n");
        using var tab = await Open(path);
        tab.ApplyEdits([TextEdit.Insert(0, "mine ")]);
        await File.WriteAllTextAsync(path, "disk\n");
        await tab.CheckDiskAsync(true);

        await tab.TakeDiskVersionAsync();
        Assert.Equal("disk\n", tab.CurrentText);
        Assert.False(tab.IsDirty);
        Assert.Null(tab.Conflict);
    }

    [Fact]
    public async Task Merge_combines_non_overlapping_edits_and_then_saves()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        await File.WriteAllTextAsync(path, "a\nb\nc\n");
        using var tab = await Open(path);
        tab.ApplyEdits([TextEdit.Insert(0, "A-mine\n")]);
        await File.WriteAllTextAsync(path, "a\nb\nc\nd-disk\n");

        var merge = await tab.BuildMergeAsync();
        Assert.False(merge.HasConflicts);
        await tab.ApplyMergedTextAsync(merge.MergedText);
        Assert.IsType<SaveSucceeded>(await tab.SaveAsync());
        Assert.Equal("A-mine\na\nb\nc\nd-disk\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Save_as_untitled_creates_file_and_refuses_overwrite_without_consent()
    {
        using var dir = new TempDir();
        using var tab = DocumentTab.CreateUntitled(Files, MarkdownProfile.Gfm, "hello");
        Assert.True(tab.IsUntitled);
        var path = dir.File("new.md");
        Assert.IsType<SaveSucceeded>(await tab.SaveAsAsync(path, false));
        Assert.False(tab.IsUntitled);
        Assert.Equal(path, tab.FilePath);

        using var other = DocumentTab.CreateUntitled(Files, MarkdownProfile.Gfm, "other");
        var refused = await other.SaveAsAsync(path, false);
        Assert.IsNotType<SaveSucceeded>(refused);
        Assert.Equal("hello", await File.ReadAllTextAsync(path));
        Assert.IsType<SaveSucceeded>(await other.SaveAsAsync(path, true));
    }

    [Fact]
    public async Task Save_copy_leaves_document_state_untouched()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        await File.WriteAllTextAsync(path, "one\n");
        using var tab = await Open(path);
        tab.ApplyEdits([TextEdit.Insert(0, "x")]);
        Assert.IsType<SaveSucceeded>(await tab.SaveCopyAsync(dir.File("copy.md"), false));
        Assert.True(tab.IsDirty);
        Assert.Equal(path, tab.FilePath);
        Assert.Equal("one\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Line_ending_conversion_rewrites_text_and_marks_dirty()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        await File.WriteAllTextAsync(path, "a\nb\n");
        using var tab = await Open(path);
        tab.ConvertLineEndings(LineEnding.CrLf);
        Assert.Equal("a\r\nb\r\n", tab.CurrentText);
        Assert.True(tab.IsDirty);
        Assert.Equal("CRLF", tab.LineEndingText);
        Assert.IsType<SaveSucceeded>(await tab.SaveAsync());
        Assert.Equal("a\r\nb\r\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Encoding_conversion_refuses_unrepresentable_text_and_writes_requested_bom()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        await File.WriteAllTextAsync(path, "caf\u00e9 \u4e2d\n");
        using var tab = await Open(path);
        Assert.False(tab.TryConvertEncoding(TextEncodingKind.Latin1, false));

        Assert.True(tab.TryConvertEncoding(TextEncodingKind.Utf8, true));
        Assert.True(tab.IsDirty);
        Assert.IsType<SaveSucceeded>(await tab.SaveAsync());
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
    }

    [Fact]
    public async Task Invalid_utf8_opens_read_only_and_refuses_save()
    {
        using var dir = new TempDir();
        var path = dir.File("bad.md");
        byte[] original = [0x61, 0xFF, 0xFE, 0x62, 0x80];
        await File.WriteAllBytesAsync(path, original);
        using var tab = await Open(path);
        Assert.True(tab.IsReadOnly);
        Assert.NotNull(tab.ReadOnlyReason);
        Assert.IsType<SaveFailed>(await tab.SaveAsync());
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Save_options_trim_whitespace_and_add_final_newline_but_keep_hard_breaks()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        await File.WriteAllTextAsync(path, "a   \nhard  \nb\t");
        using var tab = await Open(path);
        var result = await tab.SaveAsync(new TabSaveOptions(EnsureFinalNewline: true, TrimTrailingWhitespace: true));
        Assert.IsType<SaveSucceeded>(result);
        // "a" is followed by text, so its three spaces are a valid hard break and are reduced to two.
        Assert.Equal("a  \nhard  \nb\n", await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("one   \ntwo", "one  \ntwo\n")]
    [InlineData("one     \ntwo", "one  \ntwo\n")]
    [InlineData("one   \r\ntwo", "one  \r\ntwo\r\n")]
    [InlineData("one\t   \ntwo", "one  \ntwo\n")]
    [InlineData("one   \n\ntwo", "one\n\ntwo\n")]
    [InlineData("one   \n   \ntwo", "one\n\ntwo\n")]
    [InlineData("one \t\ntwo", "one\ntwo\n")]
    [InlineData("one   ", "one\n")]
    public async Task Trim_on_save_keeps_hard_breaks_of_three_or_more_spaces_only_before_text(string source, string expected)
    {
        using var dir = new TempDir();
        var path = dir.File("breaks.md");
        await File.WriteAllTextAsync(path, source);
        using var tab = await Open(path);
        Assert.IsType<SaveSucceeded>(await tab.SaveAsync(new TabSaveOptions(EnsureFinalNewline: true, TrimTrailingWhitespace: true)));
        Assert.Equal(expected, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task Save_trims_current_snapshot_but_preserves_code_and_front_matter(string newline)
    {
        using var dir = new TempDir();
        var path = dir.File("protected.md");
        var source = "---\nyaml: value   \n---\n\nprose   \n\n```text\nfenced   \n```\n\n    indented   \n\nlast\t";
        await File.WriteAllTextAsync(path, source.Replace("\n", newline));
        using var tab = await Open(path);
        tab.ApplyEdits([TextEdit.Insert(tab.CurrentText.Length, "\n\n~~~\nnew code   \n~~~".Replace("\n", newline))]);
        var before = tab.CurrentText;
        var expected = before.Replace("prose   ", "prose").Replace("last\t", "last") + newline;

        Assert.IsType<SaveSucceeded>(await tab.SaveAsync(new(EnsureFinalNewline: true, TrimTrailingWhitespace: true)));
        Assert.Equal(expected, await File.ReadAllTextAsync(path));
        Assert.Equal(expected, tab.CurrentText);
        Assert.False(tab.IsDirty);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("conflict")]
    [InlineData("cancel")]
    public async Task Unsuccessful_save_does_not_apply_whitespace_options_or_change_revision(string outcome)
    {
        using var dir = new TempDir();
        var path = dir.File("protected.md");
        await File.WriteAllTextAsync(path, "original   ");
        var files = new DocumentFileService();
        using var tab = await DocumentTab.OpenAsync(path, files, MarkdownProfile.Extended);
        tab.ApplyEdits([TextEdit.Insert(0, "edit ")]);
        var before = tab.CurrentText;
        var revision = tab.Session.Revision;
        var options = new TabSaveOptions(EnsureFinalNewline: true, TrimTrailingWhitespace: true);
        using var cancellation = new CancellationTokenSource();

        if (outcome == "conflict")
        {
            await File.WriteAllTextAsync(path, "external");
            Assert.IsType<SaveConflict>(await tab.SaveAsync(options));
        }
        else if (outcome == "cancel")
        {
            files.SavePhase = _ => cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tab.SaveAsync(options, cancellation.Token));
        }
        else
            Assert.IsType<SaveFailed>(await tab.SaveAsAsync(dir.Path, false, options));

        Assert.Equal(before, tab.CurrentText);
        Assert.Equal(revision, tab.Session.Revision);
        Assert.True(tab.IsDirty);
        Assert.Equal(outcome == "conflict" ? "external" : "original   ", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tern-tmp"));
    }

    [Fact]
    public async Task Bomless_utf16_is_read_only_until_explicitly_reopened_without_writing_original()
    {
        using var dir = new TempDir();
        var path = dir.File("utf16.md");
        var bytes = Encoding.Unicode.GetBytes("# Hello\r\nWorld");
        await File.WriteAllBytesAsync(path, bytes);
        using var tab = await Open(path);

        Assert.True(tab.IsReadOnly);
        Assert.Contains("Reopen with encoding", tab.ReadOnlyReason);
        Assert.Contains("UTF-16LE", tab.ReadOnlyReason);
        Assert.IsType<SaveFailed>(await tab.SaveAsync());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.True(await tab.ReopenWithEncodingAsync(TextEncodingKind.Utf16LittleEndian));
        Assert.False(tab.IsReadOnly);
        Assert.Equal("# Hello\r\nWorld", tab.CurrentText);
        Assert.False(tab.IsDirty);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Analysis_is_published_for_current_text()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        await File.WriteAllTextAsync(path, "# One\n\n## Two\n");
        using var tab = await Open(path);
        for (var i = 0; i < 100 && tab.Analysis is null; i++)
        {
            await Task.Delay(20);
        }

        Assert.NotNull(tab.Analysis);
        Assert.Equal(2, tab.Analysis!.Headings.Count);
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void Missing_file_gives_defaults()
    {
        using var dir = new TempDir();
        var s = new SettingsStore(dir.Path).Load();
        Assert.Equal(ThemeChoice.System, s.Theme);
        Assert.Equal(14, s.EditorFontSize);
    }

    [Fact]
    public void Round_trips_and_clamps_extremes()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.Path);
        store.Save(new AppSettings { Theme = ThemeChoice.Paper, EditorFontSize = 500, TabSize = 0, KeyBindings = { ["x"] = "Ctrl+K" } });
        var s = store.Load();
        Assert.Equal(ThemeChoice.Paper, s.Theme);
        Assert.Equal(48, s.EditorFontSize);
        Assert.Equal(1, s.TabSize);
        Assert.Equal("Ctrl+K", s.KeyBindings["x"]);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults_and_is_preserved()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.Path);
        File.WriteAllText(store.FilePath, "{ not json");
        var s = store.Load();
        Assert.Equal(ThemeChoice.System, s.Theme);
        Assert.NotNull(store.LastLoadProblem);
        Assert.Equal("{ not json", File.ReadAllText(Assert.Single(Directory.GetFiles(dir.Path, "settings.json.*.corrupt"))));
    }

    [Theory]
    [InlineData("Theme")]
    [InlineData("DefaultProfile")]
    [InlineData("DefaultViewMode")]
    [InlineData("RemoteImages")]
    public void Unknown_enum_defaults_only_that_property_and_preserves_shortcuts(string property)
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.Path);
        var json = JsonNode.Parse("""
            {"Theme":"Paper","DefaultProfile":"CommonMark","DefaultViewMode":"Reading",
             "RemoteImages":"Never","EditorFontSize":19,"KeyBindings":{"file.save":"Ctrl+Alt+S"}}
            """)!.AsObject();
        json[property] = "UnknownFutureValue";
        File.WriteAllText(store.FilePath, json.ToJsonString());

        var settings = store.Load();
        Assert.Equal(property == "Theme" ? ThemeChoice.System : ThemeChoice.Paper, settings.Theme);
        Assert.Equal(property == "DefaultProfile" ? MarkdownProfile.Extended : MarkdownProfile.CommonMark, settings.DefaultProfile);
        Assert.Equal(property == "DefaultViewMode" ? ViewMode.Split : ViewMode.Reading, settings.DefaultViewMode);
        Assert.Equal(property == "RemoteImages" ? RemoteImagePolicy.Ask : RemoteImagePolicy.Never, settings.RemoteImages);
        Assert.Equal(19, settings.EditorFontSize);
        Assert.Equal("Ctrl+Alt+S", settings.KeyBindings["file.save"]);
        Assert.Contains(property, store.LastLoadProblem);
        Assert.True(File.Exists(store.FilePath));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.corrupt"));
    }

    [Fact]
    public async Task Startup_retries_a_two_second_lock_without_quarantining_settings()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.Path);
        store.Save(new AppSettings { Theme = ThemeChoice.Paper, EditorFontSize = 21 });
        var original = File.ReadAllBytes(store.FilePath);
        using var locked = new FileStream(store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(2)); locked.Dispose(); });
        var settings = await Task.Run(store.Load);
        await release;
        Assert.Equal(ThemeChoice.Paper, settings.Theme);
        Assert.Equal(21, settings.EditorFontSize);
        Assert.Null(store.LastLoadProblem);
        Assert.Equal(original, File.ReadAllBytes(store.FilePath));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.corrupt"));
    }

    [Fact]
    public void Persistent_read_failure_blocks_default_settings_from_overwriting_the_unread_primary()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.Path);
        store.Save(new AppSettings { Theme = ThemeChoice.Paper, EditorFontSize = 21 });
        var original = File.ReadAllBytes(store.FilePath);
        AppSettings settings;
        using (var locked = new FileStream(store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            settings = store.Load();
        Assert.NotNull(store.LastLoadProblem);
        Assert.Throws<IOException>(() => store.Save(settings));
        Assert.Equal(original, File.ReadAllBytes(store.FilePath));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.corrupt"));
        Assert.Equal(ThemeChoice.Paper, store.Load().Theme);
        store.Save(settings);
    }

    [Fact]
    public void Backup_recovers_zero_byte_primary_and_successive_corruptions_keep_distinct_bytes()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.Path);
        store.Save(new AppSettings { Theme = ThemeChoice.Paper, EditorFontSize = 20, KeyBindings = { ["file.save"] = "Ctrl+Alt+S" } });
        var backup = File.ReadAllBytes(store.FilePath + ".bak");
        foreach (var corrupt in new[] { "", "{ second corruption" })
        {
            File.WriteAllText(store.FilePath, corrupt);
            var restored = store.Load();
            Assert.Equal(ThemeChoice.Paper, restored.Theme);
            Assert.Equal(20, restored.EditorFontSize);
            Assert.Equal("Ctrl+Alt+S", restored.KeyBindings["file.save"]);
            Assert.Contains("Settings were restored from backup", store.LastLoadProblem);
            Assert.Equal(backup, File.ReadAllBytes(store.FilePath + ".bak"));
            Assert.Equal(backup, File.ReadAllBytes(store.FilePath));
        }
        var preserved = Directory.GetFiles(dir.Path, "settings.json.*.corrupt");
        Assert.Equal(2, preserved.Length);
        Assert.Contains(preserved, path => File.ReadAllText(path) == "");
        Assert.Contains(preserved, path => File.ReadAllText(path) == "{ second corruption");
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }

    [Fact]
    public void Version_one_migrates_nullable_collections_and_preserves_preferences()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.Path);
        File.WriteAllText(store.FilePath, """{"Version":1,"Theme":"Paper","EditorFontSize":17,"KeyBindings":null,"WorkspaceExclusions":null}""");
        var settings = store.Load();
        Assert.Equal(2, settings.Version);
        Assert.Empty(settings.KeyBindings);
        Assert.Empty(settings.WorkspaceExclusions);
        Assert.Equal(ThemeChoice.Paper, settings.Theme);
        Assert.Equal(17, settings.EditorFontSize);
        store.Save(settings);
        Assert.Equal(2, store.Load().Version);
    }

    [Fact]
    public void Shell_shows_one_warning_for_recovery_and_does_not_repeat_it_after_dismissal()
    {
        using var dir = new TempDir();
        var paths = Tern.App.Shell.AppPaths.Under(dir.Path);
        var store = new SettingsStore(paths.SettingsDirectory);
        store.Save(new AppSettings { Theme = ThemeChoice.Paper });
        File.WriteAllBytes(store.FilePath, []);
        using var shell = new Tern.App.Shell.ShellViewModel(paths, new ScriptedDialogs(), new RecordingLauncher());
        Assert.Equal(ThemeChoice.Paper, shell.Settings.Theme);
        Assert.Equal(Tern.App.Shell.StatusSeverity.Warning, shell.Banner?.Severity);
        Assert.Contains("Settings were restored from backup", shell.Banner?.Text);
        Assert.Single(shell.Activities.Items.Where(item => item.Phase.Contains("Settings were restored from backup")));
        shell.DismissBanner();
        Assert.True(shell.SaveSettings());
        Assert.Null(shell.Banner);
        store.Load();
        Assert.Null(store.LastLoadProblem);
    }
}
