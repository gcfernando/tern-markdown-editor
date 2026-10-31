using System.Text;
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
        Assert.Equal("a\nhard  \nb\n", await File.ReadAllTextAsync(path));
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
        Assert.True(File.Exists(store.FilePath + ".corrupt"));
    }
}
