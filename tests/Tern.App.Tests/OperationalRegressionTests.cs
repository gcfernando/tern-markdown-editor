using Tern.App.Documents;
using Tern.Domain.Text;
using Tern.Filesystem;
using Tern.Markdown;

namespace Tern.App.Tests;

public sealed class OperationalRegressionTests
{
    public static IEnumerable<object[]> ConflictCases() =>
        from action in new[] { "keep", "disk", "merge", "copy" }
        from dirty in new[] { false, true }
        from deleted in new[] { false, true }
        select new object[] { action, dirty, deleted };

    [Theory]
    [MemberData(nameof(ConflictCases))]
    public async Task Conflict_decisions_preserve_reviewed_text_and_unrelated_disk_bytes(string action, bool dirty, bool deleted)
    {
        using var directory = new TempDir();
        var path = directory.File("note.md");
        const string original = "# Original\n\nFirst paragraph.\n\nLast paragraph.\n";
        const string external = original + "\nExternal paragraph.\n";
        await File.WriteAllTextAsync(path, original);
        using var tab = await DocumentTab.OpenAsync(path, new DocumentFileService(), MarkdownProfile.Gfm);
        if (dirty) tab.ApplyEdits([TextEdit.Insert(0, "Mine.\n\n")]);
        var mine = tab.CurrentText;
        var revision = tab.Session.Revision;
        if (deleted) File.Delete(path); else await File.WriteAllTextAsync(path, external);
        Assert.Equal(DiskCheckResult.Conflict, await tab.CheckDiskAsync(false));
        Assert.NotNull(tab.Conflict);

        if (action == "keep")
        {
            Assert.IsType<SaveSucceeded>(await tab.KeepMineAndOverwriteAsync());
            Assert.Equal(mine, await File.ReadAllTextAsync(path));
            Assert.False(tab.IsDirty);
            Assert.Null(tab.Conflict);
            return;
        }
        if (action == "copy")
        {
            var copy = directory.File("copy.md");
            Assert.IsType<SaveSucceeded>(await tab.SaveCopyAsync(copy, false));
            Assert.Equal(mine, await File.ReadAllTextAsync(copy));
            Assert.Equal(mine, tab.CurrentText);
            Assert.Equal(revision, tab.Session.Revision);
            Assert.Equal(dirty, tab.IsDirty);
            Assert.Equal(path, tab.FilePath);
            Assert.NotNull(tab.Conflict);
        }
        else if (deleted)
        {
            if (action == "disk")
                await Assert.ThrowsAsync<FileNotFoundException>(() => tab.TakeDiskVersionAsync());
            else
                await Assert.ThrowsAsync<FileNotFoundException>(() => tab.TryBuildMergeAsync());
            Assert.Equal(mine, tab.CurrentText);
            Assert.Equal(revision, tab.Session.Revision);
            Assert.Equal(dirty, tab.IsDirty);
            Assert.NotNull(tab.Conflict);
        }
        else if (action == "disk")
        {
            Assert.True(await tab.TakeDiskVersionAsync());
            Assert.Equal(external, tab.CurrentText);
            Assert.False(tab.IsDirty);
            Assert.Null(tab.Conflict);
        }
        else
        {
            var built = await tab.TryBuildMergeAsync();
            Assert.NotNull(built.Merge);
            Assert.True(await tab.ApplyMergedTextAsync(built.Merge.MergedText));
            Assert.Contains("External paragraph.", tab.CurrentText);
            if (dirty) Assert.Contains("Mine.", tab.CurrentText);
            Assert.Equal(external, await File.ReadAllTextAsync(path));
            Assert.True(tab.IsDirty);
            Assert.IsType<SaveSucceeded>(await tab.SaveAsync());
            Assert.Equal(tab.CurrentText, await File.ReadAllTextAsync(path));
            Assert.False(tab.IsDirty);
            return;
        }
        Assert.Equal(!deleted, File.Exists(path));
        if (!deleted) Assert.Equal(external, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Identity_queue_orders_opens_and_disk_checks_after_save_and_recovers_after_a_fault(bool failSave)
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("source.md", "saved");
        var first = await f.WriteAsync("first.md", "first");
        var second = await f.WriteAsync("second.md", "second");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "mine ")]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = new List<string>();
        f.Shell.BeforeSaveWrite = async () =>
        {
            entered.TrySetResult();
            await resume.Task;
            if (failSave) throw new InvalidOperationException("injected save fault");
        };
        f.Shell.FileService.AfterReadForLoadAsync = document =>
        {
            if (document == first || document == second) reads.Add(document);
            return Task.CompletedTask;
        };
        var saving = f.Shell.SaveTabAsync(tab);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var checking = f.Shell.CheckForExternalChangesAsync(tab);
            var openingFirst = f.Shell.OpenFileAsync(first);
            var openingSecond = f.Shell.OpenFileAsync(second);
            Assert.False(checking.IsCompleted);
            Assert.False(openingFirst.IsCompleted);
            Assert.False(openingSecond.IsCompleted);
            resume.TrySetResult();
            if (failSave) await Assert.ThrowsAsync<InvalidOperationException>(() => saving);
            else Assert.True(await saving.WaitAsync(TimeSpan.FromSeconds(5)));
            await checking.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(await openingFirst.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.NotNull(await openingSecond.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal([first, second], reads);
            Assert.False(f.Shell.IsBusy);
            Assert.Null(tab.Conflict);
            Assert.Equal(0, f.Dialogs.ConflictPrompts);
            Assert.Equal(failSave, tab.IsDirty);
            Assert.Equal(failSave ? "saved" : "mine saved", await File.ReadAllTextAsync(path));
        }
        finally { resume.TrySetResult(); }
    }

    [Fact]
    public async Task Saving_after_the_recovery_directory_disappears_keeps_the_file_and_clean_state()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("source.md", "saved");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "mine ")]);
        Directory.CreateDirectory(f.Shell.Paths.RecoveryDirectory);
        Directory.Delete(f.Shell.Paths.RecoveryDirectory, true);
        Assert.True(await f.Shell.SaveTabAsync(tab));
        Assert.Equal("mine saved", await File.ReadAllTextAsync(path));
        Assert.False(tab.IsDirty);
        Assert.Null(tab.LastSaveError);
    }
}
