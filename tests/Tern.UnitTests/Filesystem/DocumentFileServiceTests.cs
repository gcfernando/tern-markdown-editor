using System.Text;
using Tern.Domain.Documents;
using Tern.Domain.Text;
using Tern.Filesystem;

namespace Tern.UnitTests.Filesystem;

public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tern-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                System.IO.File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
    }
}

public class DocumentFileServiceTests
{
    private readonly DocumentFileService _files = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_temporary_replacement_lock_retries_without_losing_a_competing_edit(bool competingEdit)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDirectory();
        var path = dir.File("sharing.md");
        await File.WriteAllTextAsync(path, "base");
        var loaded = await _files.LoadAsync(path);
        Task release = Task.CompletedTask;
        _files.BeforeReplace = target =>
        {
            var reader = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            release = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(80);
                    if (competingEdit) await File.WriteAllTextAsync(target, "competing edit");
                }
                finally { reader.Dispose(); }
            });
        };
        var outcome = await _files.SaveAsync(path, "mine", loaded.Format, loaded.Baseline);
        await release;
        if (competingEdit)
        {
            // A failed native swap may already have created a backup. In that
            // case saving must stop conservatively rather than retry the swap.
            Assert.True(outcome is SaveConflict or SaveFailed, outcome.ToString());
            var preserved = Directory.GetFiles(dir.Path).Where(file => File.ReadAllText(file) == "competing edit").ToArray();
            Assert.NotEmpty(preserved);
            if (await File.ReadAllTextAsync(path) != "competing edit")
            {
                var failure = Assert.IsType<SaveFailed>(outcome);
                Assert.Contains(preserved, file => failure.Reason.Contains(file, StringComparison.Ordinal));
            }
        }
        else
        {
            Assert.IsType<SaveSucceeded>(outcome);
            Assert.Equal("mine", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.GetFiles(dir.Path, "*.tern-save-backup"));
        }
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tern-tmp"));
    }

    [Fact]
    public async Task A_competing_write_in_the_last_replacement_window_is_preserved_and_reported()
    {
        using var dir = new TempDirectory();
        var path = dir.File("race.md");
        await File.WriteAllTextAsync(path, "base");
        var loaded = await _files.LoadAsync(path);
        _files.BeforeReplace = target => File.WriteAllText(target, "competing edit");
        var outcome = Assert.IsType<SaveFailed>(await _files.SaveAsync(path, "mine", loaded.Format, loaded.Baseline));
        var backup = Assert.Single(Directory.GetFiles(dir.Path, "*.tern-save-backup"));
        Assert.Equal("competing edit", await File.ReadAllTextAsync(backup));
        Assert.Contains(backup, outcome.Reason, StringComparison.Ordinal);
        Assert.Equal("mine", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task A_retained_writable_handle_cannot_lose_its_named_displaced_file()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows sharing is the guarantee exercised here.
        using var dir = new TempDirectory();
        var path = dir.File("retained.md");
        await File.WriteAllTextAsync(path, "base");
        var loaded = await _files.LoadAsync(path);
        SaveFailed outcome;
        string backup;
        using (var retainedWriter = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            outcome = Assert.IsType<SaveFailed>(await _files.SaveAsync(path, "mine", loaded.Format, loaded.Baseline));
            backup = Assert.Single(Directory.GetFiles(dir.Path, "*.tern-save-backup"));
            retainedWriter.Position = 0;
            retainedWriter.SetLength(0);
            retainedWriter.Write("late retained writer"u8);
            retainedWriter.Flush(flushToDisk: true);
        }
        Assert.Equal("late retained writer", await File.ReadAllTextAsync(backup));
        Assert.Contains(backup, outcome.Reason, StringComparison.Ordinal);
        Assert.Equal("mine", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task A_no_op_save_performs_no_replacement_and_preserves_write_time()
    {
        using var dir = new TempDirectory();
        var path = dir.File("same.md");
        await File.WriteAllTextAsync(path, "same");
        File.SetLastWriteTimeUtc(path, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var loaded = await _files.LoadAsync(path);
        _files.BeforeReplace = _ => throw new InvalidOperationException("No-op save must not replace.");
        Assert.IsType<SaveSucceeded>(await _files.SaveAsync(path, loaded.Text, loaded.Format, loaded.Baseline));
        Assert.Equal(loaded.Baseline.LastWriteUtc, File.GetLastWriteTimeUtc(path));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tern-save-backup"));
    }

    [Fact]
    public async Task A_successful_replacement_removes_its_verified_backup()
    {
        using var dir = new TempDirectory();
        var path = dir.File("normal.md");
        await File.WriteAllTextAsync(path, "base");
        var loaded = await _files.LoadAsync(path);
        Assert.IsType<SaveSucceeded>(await _files.SaveAsync(path, "mine", loaded.Format, loaded.Baseline));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tern-save-backup"));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tern-tmp"));
    }

    [Fact]
    public async Task Cancellation_after_replacement_keeps_the_displaced_file_backup()
    {
        using var dir = new TempDirectory();
        var path = dir.File("cancel.md");
        await File.WriteAllTextAsync(path, "base");
        var loaded = await _files.LoadAsync(path);
        using var cancellation = new CancellationTokenSource();
        _files.BeforeReplace = _ => cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _files.SaveAsync(path, "mine", loaded.Format, loaded.Baseline, cancellationToken: cancellation.Token));
        var backup = Assert.Single(Directory.GetFiles(dir.Path, "*.tern-save-backup"));
        Assert.Equal("base", await File.ReadAllTextAsync(backup));
    }

    [Theory]
    [InlineData("# Hi\r\n\r\ntext\r\n")]
    [InlineData("# Hi\n\ntext")]
    [InlineData("a\r\nb\nc\rd\n")]
    [InlineData("")]
    [InlineData("caf\u00e9 \U0001F600\n")]
    public async Task Opening_and_saving_without_changes_leaves_bytes_identical(string content)
    {
        using var dir = new TempDirectory();
        var path = dir.File("doc.md");
        var original = Encoding.UTF8.GetBytes(content);
        await File.WriteAllBytesAsync(path, original);

        var loaded = await _files.LoadAsync(path);
        var outcome = await _files.SaveAsync(path, loaded.Text, loaded.Format, loaded.Baseline);

        Assert.IsType<SaveSucceeded>(outcome);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Bom_and_utf16_survive_a_save()
    {
        using var dir = new TempDirectory();
        var path = dir.File("doc.md");
        byte[] original = [.. new byte[] { 0xFF, 0xFE }, .. Encoding.Unicode.GetBytes("hi\r\nthere")];
        await File.WriteAllBytesAsync(path, original);

        var loaded = await _files.LoadAsync(path);
        await _files.SaveAsync(path, loaded.Text, loaded.Format, loaded.Baseline);

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task A_targeted_edit_changes_only_that_text()
    {
        using var dir = new TempDirectory();
        var path = dir.File("doc.md");
        await File.WriteAllBytesAsync(path, "* one\r\n* two\r\n\r\n[ref]: http://x\r\n"u8.ToArray());

        var loaded = await _files.LoadAsync(path);
        var edited = loaded.Text.Replace("two", "TWO", StringComparison.Ordinal);
        await _files.SaveAsync(path, edited, loaded.Format, loaded.Baseline);

        Assert.Equal("* one\r\n* TWO\r\n\r\n[ref]: http://x\r\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Saving_after_an_external_edit_is_refused_and_the_disk_is_untouched()
    {
        using var dir = new TempDirectory();
        var path = dir.File("doc.md");
        await File.WriteAllTextAsync(path, "original");
        var loaded = await _files.LoadAsync(path);

        await File.WriteAllTextAsync(path, "changed elsewhere");
        var outcome = await _files.SaveAsync(path, "my edit", loaded.Format, loaded.Baseline);

        var conflict = Assert.IsType<SaveConflict>(outcome);
        Assert.Equal(ConflictKind.ModifiedOnDisk, conflict.Kind);
        Assert.Equal("changed elsewhere", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Touching_the_file_without_changing_content_is_not_a_conflict()
    {
        using var dir = new TempDirectory();
        var path = dir.File("doc.md");
        await File.WriteAllTextAsync(path, "same");
        var loaded = await _files.LoadAsync(path);

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(1));
        var outcome = await _files.SaveAsync(path, "new", loaded.Format, loaded.Baseline);

        Assert.IsType<SaveSucceeded>(outcome);
    }

    [Fact]
    public async Task Saving_a_file_deleted_on_disk_reports_a_conflict()
    {
        using var dir = new TempDirectory();
        var path = dir.File("doc.md");
        await File.WriteAllTextAsync(path, "x");
        var loaded = await _files.LoadAsync(path);
        File.Delete(path);

        var outcome = await _files.SaveAsync(path, "x2", loaded.Format, loaded.Baseline);

        Assert.Equal(ConflictKind.DeletedOnDisk, Assert.IsType<SaveConflict>(outcome).Kind);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Save_as_over_an_existing_file_requires_explicit_overwrite()
    {
        using var dir = new TempDirectory();
        var path = dir.File("other.md");
        await File.WriteAllTextAsync(path, "precious");

        var refused = await _files.SaveAsync(path, "new", TextFileFormat.NewFileDefault, null);
        Assert.Equal(ConflictKind.TargetExists, Assert.IsType<SaveConflict>(refused).Kind);
        Assert.Equal("precious", await File.ReadAllTextAsync(path));

        var allowed = await _files.SaveAsync(path, "new", TextFileFormat.NewFileDefault, null, overwriteExisting: true);
        Assert.IsType<SaveSucceeded>(allowed);
        Assert.Equal("new", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task A_new_file_is_created_with_the_requested_format()
    {
        using var dir = new TempDirectory();
        var path = dir.File("sub", "new.md");
        var format = TextFileFormat.NewFileDefault with { HasBom = true };

        var outcome = await _files.SaveAsync(path, "hello", format, null);

        Assert.IsType<SaveSucceeded>(outcome);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o' }, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task A_read_only_file_fails_cleanly_and_keeps_its_content()
    {
        using var dir = new TempDirectory();
        var path = dir.File("ro.md");
        await File.WriteAllTextAsync(path, "keep me");
        var loaded = await _files.LoadAsync(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        var outcome = await _files.SaveAsync(path, "overwrite", loaded.Format, loaded.Baseline);

        Assert.IsType<SaveFailed>(outcome);
        Assert.Equal("keep me", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(dir.Path, "*.tern-tmp"));
    }

    [Fact]
    public async Task A_locked_file_fails_cleanly_and_leaves_no_temp_files()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDirectory();
        var path = dir.File("locked.md");
        await File.WriteAllTextAsync(path, "keep me");
        var loaded = await _files.LoadAsync(path);

        await using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var outcome = await _files.SaveAsync(path, "overwrite", loaded.Format, loaded.Baseline);
            Assert.IsType<SaveFailed>(outcome);
        }

        Assert.Equal("keep me", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(dir.Path, "*.tern-tmp"));
    }

    [Fact]
    public async Task Text_that_does_not_fit_the_encoding_is_rejected_without_writing()
    {
        using var dir = new TempDirectory();
        var path = dir.File("latin.md");
        await File.WriteAllBytesAsync(path, [(byte)'a']);
        var loaded = await _files.LoadAsync(path, TextEncodingKind.Latin1);

        var outcome = await _files.SaveAsync(path, "emoji \U0001F600", loaded.Format, loaded.Baseline);

        Assert.IsType<SaveFailed>(outcome);
        Assert.Equal(new byte[] { (byte)'a' }, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Undecodable_files_open_read_only()
    {
        using var dir = new TempDirectory();
        var path = dir.File("bad.md");
        await File.WriteAllBytesAsync(path, [(byte)'a', 0xFF, 0xFE, 0xFD]);

        var loaded = await _files.LoadAsync(path);

        Assert.True(loaded.ShouldOpenReadOnly);
    }

    [Fact]
    public async Task Saving_through_a_symlink_updates_the_target_and_keeps_the_link()
    {
        using var dir = new TempDirectory();
        var target = dir.File("target.md");
        var link = dir.File("link.md");
        await File.WriteAllTextAsync(target, "before");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new Xunit.Sdk.XunitException($"UNMET acceptance: the environment could not create the file symlink required to verify safe saving. {ex.GetType().Name}: {ex.Message}");
        }

        var loaded = await _files.LoadAsync(link);
        var outcome = await _files.SaveAsync(link, "after", loaded.Format, loaded.Baseline);

        Assert.IsType<SaveSucceeded>(outcome);
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal("after", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task Session_stays_dirty_when_edited_after_the_saved_snapshot()
    {
        using var dir = new TempDirectory();
        var path = dir.File("doc.md");
        await File.WriteAllTextAsync(path, "one");
        var loaded = await _files.LoadAsync(path);
        var session = new DocumentSession(path, loaded.Text, loaded.Format, loaded.Baseline);

        session.ApplyEdits([TextEdit.Insert(3, " two")]);
        var snapshot = session.GetSnapshot();
        var outcome = Assert.IsType<SaveSucceeded>(await _files.SaveAsync(path, snapshot.Text, session.Format, session.Baseline));
        session.ApplyEdits([TextEdit.Insert(7, " three")]);
        session.MarkSaved(snapshot, session.Format, outcome.Baseline);

        Assert.True(session.IsDirty);
        Assert.Equal("one two", await File.ReadAllTextAsync(path));
    }
}

internal static class TempDirectoryExtensions
{
    public static string File(this TempDirectory dir, string folder, string name)
    {
        var folderPath = Path.Combine(dir.Path, folder);
        Directory.CreateDirectory(folderPath);
        return Path.Combine(folderPath, name);
    }
}
