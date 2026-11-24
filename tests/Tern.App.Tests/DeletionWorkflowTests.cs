using Tern.App.Shell;
using Tern.Domain.Text;
using Tern.Recovery;
using Tern.Workspaces;

namespace Tern.App.Tests;

/// <summary>Real temporary files with a fake recycle boundary that moves, rather than permanently deletes, bytes.</summary>
public sealed class DeletionWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_delete_confirmation_does_not_block_another_file_open_or_save(bool confirm)
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("confirmed.md", "keep until confirmed");
        await fixture.Shell.OpenFileAsync(path);
        var otherPath = await fixture.WriteAsync("other.md", "other");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Dialogs.DeleteResponse = (_, _) => { entered.TrySetResult(); return answer.Task; };
        var recycle = new FakeRecycle(fixture.Root.File("fake-recycle"));
        var deletion = fixture.Shell.DeleteFileAsync(path, recycle);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(fixture.Shell.IsBusy);
            var waiting = Assert.Single(fixture.Shell.Activities.Items, item => item.Kind == "Recycling Library file");
            Assert.Equal(ActivityState.WaitingForUser, waiting.State);
            Assert.False(waiting.IsIndeterminate);
            Assert.False(waiting.CanCancel);
            var other = (await fixture.Shell.OpenFileAsync(otherPath).WaitAsync(TimeSpan.FromSeconds(1)))!;
            other.ApplyEdits([TextEdit.Insert(0, "saved ")]);
            Assert.True(await fixture.Shell.SaveTabAsync(other).WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal("saved other", await File.ReadAllTextAsync(otherPath));
            Assert.True(File.Exists(path));
            Assert.Equal(0, recycle.Calls);
        }
        finally { answer.TrySetResult(confirm); }
        Assert.Equal(confirm, await deletion);
        Assert.Equal(confirm ? 1 : 0, recycle.Calls);
        Assert.Equal(confirm ? ActivityState.Completed : ActivityState.Canceled,
            Assert.Single(fixture.Shell.Activities.Items, item => item.Kind == "Recycling Library file").State);
        Assert.Equal("saved other", await File.ReadAllTextAsync(otherPath));
    }

    [Fact]
    public async Task Cancel_keeps_file_edits_references_and_history_without_calling_recycle()
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("keep.md", "Disk bytes\r\n");
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "Unsaved ")]);
        await fixture.Shell.HistoryStore.CaptureAsync(path, "Saved history");
        var recycle = new FakeRecycle(fixture.Root.File("fake-recycle"));

        Assert.False(await fixture.Shell.DeleteFileAsync(path, recycle));

        Assert.Equal(1, fixture.Dialogs.DeletePrompts);
        Assert.Equal(1, fixture.Dialogs.LastDeleteUnsavedCount);
        Assert.Equal(0, recycle.Calls);
        Assert.Equal("Disk bytes\r\n", await File.ReadAllTextAsync(path));
        Assert.Same(tab, Assert.Single(fixture.Shell.Tabs));
        Assert.Equal("Unsaved Disk bytes\r\n", tab.CurrentText);
        Assert.True(tab.IsDirty);
        Assert.Single(fixture.Shell.Recent.Files);
        Assert.Single(await fixture.Shell.HistoryStore.ReadAsync(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recycle_failure_keeps_all_application_state_and_original_bytes(bool denied)
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("keep.md", "Disk bytes\r\n");
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        await fixture.Shell.HistoryStore.CaptureAsync(path, "Saved history");
        fixture.Dialogs.DeleteChoice = true;
        var recycle = new FakeRecycle(fixture.Root.File("fake-recycle"))
        { Failure = denied ? new UnauthorizedAccessException("Fake recycle unavailable") : new IOException("Fake recycle unavailable") };

        Assert.False(await fixture.Shell.DeleteFileAsync(path, recycle));

        Assert.Equal(1, recycle.Calls);
        Assert.Same(tab, Assert.Single(fixture.Shell.Tabs));
        Assert.Equal("Disk bytes\r\n", await File.ReadAllTextAsync(path));
        Assert.Single(fixture.Shell.Recent.Files);
        Assert.Single(await fixture.Shell.HistoryStore.ReadAsync(path));
        Assert.Contains("not recycled", fixture.Shell.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StatusSeverity.Error, fixture.Shell.StatusSeverity);
        var activity = Assert.Single(fixture.Shell.Activities.Items, item => item.Kind == "Recycling Library file");
        Assert.Equal(ActivityState.Failed, activity.State);
        Assert.Contains("Fake recycle unavailable", activity.Details);
        Assert.NotNull(activity.Finished);
    }

    [Theory]
    [InlineData("https://example.com/remote.md")]
    [InlineData("folder")]
    [InlineData("missing")]
    [InlineData("")]
    public async Task Online_addresses_and_directories_are_rejected_before_confirmation(string input)
    {
        using var fixture = new ShellFixture();
        var path = input switch { "folder" => fixture.Docs, "missing" => Path.Combine(fixture.Docs, "missing.md"), _ => input };
        var recycle = new FakeRecycle(fixture.Root.File("fake-recycle"));
        fixture.Dialogs.DeleteChoice = true;

        Assert.False(await fixture.Shell.DeleteFileAsync(path, recycle));

        Assert.Equal(0, fixture.Dialogs.DeletePrompts);
        Assert.Equal(0, recycle.Calls);
        Assert.True(Directory.Exists(fixture.Docs));
        Assert.Equal(StatusSeverity.Error, fixture.Shell.StatusSeverity);
        Assert.Equal(ActivityState.Failed, Assert.Single(fixture.Shell.Activities.Items, item => item.Kind == "Recycling Library file").State);
    }

    [Fact]
    public async Task Exclusive_file_access_failure_is_reported_before_confirmation_without_changing_references()
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("locked.md", "Keep bytes");
        var tab = await fixture.Shell.OpenFileAsync(path);
        var recycle = new FakeRecycle(fixture.Root.File("fake-recycle"));
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(await fixture.Shell.DeleteFileAsync(path, recycle));
            Assert.Equal(0, fixture.Dialogs.DeletePrompts);
            Assert.Equal(0, recycle.Calls);
            Assert.Equal(StatusSeverity.Error, fixture.Shell.StatusSeverity);
            Assert.Same(tab, Assert.Single(fixture.Shell.Tabs));
            Assert.Contains(fixture.Shell.Recent.Files, entry => entry.Path == path);
        }
        Assert.Equal("Keep bytes", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Recycle_boundary_error_after_source_is_gone_reports_uncertainty_without_claiming_original_remains()
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("recycled.md", "Recycled bytes");
        var tab = await fixture.Shell.OpenFileAsync(path);
        fixture.Dialogs.DeleteChoice = true;
        var recycle = new FakeRecycle(fixture.Root.File("fake-recycle"))
        { AfterMove = () => throw new IOException("Synthetic shell completion failure") };
        Assert.False(await fixture.Shell.DeleteFileAsync(path, recycle));
        Assert.False(File.Exists(path));
        Assert.Equal("Recycled bytes", await File.ReadAllTextAsync(recycle.MovedPath!));
        Assert.Same(tab, Assert.Single(fixture.Shell.Tabs));
        Assert.Contains(fixture.Shell.Recent.Files, entry => entry.Path == path);
        Assert.Contains("no longer present", fixture.Shell.StatusMessage);
        Assert.Contains("verify the Recycle Bin", fixture.Shell.StatusMessage);
        Assert.DoesNotContain("not recycled", fixture.Shell.StatusMessage);
        Assert.Equal(StatusSeverity.Error, fixture.Shell.StatusSeverity);
    }

    [Fact]
    public async Task Windows_cancellation_is_not_reported_as_failure_or_success()
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("cancel.md", "Keep bytes");
        var tab = await fixture.Shell.OpenFileAsync(path);
        fixture.Dialogs.DeleteChoice = true;
        var recycle = new FakeRecycle(fixture.Root.File("fake-recycle"))
        { Failure = new OperationCanceledException("Synthetic Windows cancellation") };
        Assert.False(await fixture.Shell.DeleteFileAsync(path, recycle));
        Assert.Equal("Keep bytes", await File.ReadAllTextAsync(path));
        Assert.Same(tab, Assert.Single(fixture.Shell.Tabs));
        Assert.Contains(fixture.Shell.Recent.Files, entry => entry.Path == path);
        var activity = Assert.Single(fixture.Shell.Activities.Items, item => item.Kind == "Recycling Library file");
        Assert.Equal(ActivityState.Canceled, activity.State);
        Assert.Equal(StatusSeverity.Warning, fixture.Shell.StatusSeverity);
        Assert.Contains("Windows recycling stopped", fixture.Shell.StatusMessage);
        Assert.DoesNotContain("not recycled", fixture.Shell.StatusMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disk_or_buffer_changes_during_confirmation_abort_without_recycling(bool editBuffer)
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("keep.md", "Original\r\n");
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        var recycle = new FakeRecycle(fixture.Root.File("fake-recycle"));
        fixture.Dialogs.DeleteResponse = async (_, _) =>
        {
            if (editBuffer) tab.ApplyEdits([TextEdit.Insert(0, "New edits ")]);
            else await File.WriteAllTextAsync(path, "New disk version\r\n");
            return true;
        };

        Assert.False(await fixture.Shell.DeleteFileAsync(path, recycle));

        Assert.Equal(0, recycle.Calls);
        Assert.Same(tab, Assert.Single(fixture.Shell.Tabs));
        Assert.Equal(editBuffer ? "New edits Original\r\n" : "Original\r\n", tab.CurrentText);
        Assert.Equal(editBuffer ? "Original\r\n" : "New disk version\r\n", await File.ReadAllTextAsync(path));
        Assert.Equal(StatusSeverity.Error, fixture.Shell.StatusSeverity);
        Assert.Equal(ActivityState.Failed, Assert.Single(fixture.Shell.Activities.Items, item => item.Kind == "Recycling Library file").State);
    }

    [Fact]
    public async Task Successful_recycle_clears_only_the_confirmed_path_tabs_history_recovery_and_session_references()
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("remove.md", "Original bytes\r\n");
        var first = (await fixture.Shell.OpenFileAsync(path))!;
        var second = (await fixture.Shell.OpenFileAsync(path, duplicateBehavior: DuplicateOpenBehavior.NewTab))!;
        first.ApplyEdits([TextEdit.Insert(0, "First edits ")]);
        second.ApplyEdits([TextEdit.Insert(0, "Second edits ")]);
        var otherPath = await fixture.WriteAsync("unrelated.md", "Unrelated bytes\r\n");
        var other = (await fixture.Shell.OpenFileAsync(otherPath))!;
        await fixture.Shell.HistoryStore.CaptureAsync(path, "Deleted path history");
        await fixture.Shell.HistoryStore.CaptureAsync(otherPath, "Retained history");
        await fixture.Shell.RecoveryStore.WriteCheckpointAsync(new RecoveryRecord(first.Session.Id, path, first.DisplayName,
            first.Session.Revision, DateTime.UtcNow, first.Session.Baseline?.Sha256, first.Session.Format, first.CurrentText));
        fixture.Shell.RememberViewState(first, 4, 12);
        Assert.True(fixture.Shell.SaveSession());
        fixture.Dialogs.DeleteChoice = true;
        var recycle = new FakeRecycle(fixture.Root.File("fake-recycle"));

        Assert.True(await fixture.Shell.DeleteFileAsync(path, recycle));

        Assert.Equal(1, recycle.Calls);
        Assert.Equal(2, fixture.Dialogs.LastDeleteUnsavedCount);
        Assert.Equal(0, fixture.Dialogs.SavePrompts);
        Assert.False(File.Exists(path));
        Assert.Equal("Original bytes\r\n", await File.ReadAllTextAsync(recycle.MovedPath!));
        Assert.Same(other, Assert.Single(fixture.Shell.Tabs));
        Assert.Equal("Unrelated bytes\r\n", await File.ReadAllTextAsync(otherPath));
        Assert.DoesNotContain(fixture.Shell.Recent.Files, entry => entry.Path == path);
        Assert.Contains(fixture.Shell.Recent.Files, entry => entry.Path == otherPath);
        Assert.Empty(await fixture.Shell.HistoryStore.ReadAsync(path));
        Assert.Single(await fixture.Shell.HistoryStore.ReadAsync(otherPath));
        Assert.Contains(fixture.Shell.RecoveryStore.Scan().Entries, entry => entry.Record.OriginalPath == path);
        var session = new SessionStore(fixture.Shell.Paths.StateDirectory).Load();
        Assert.DoesNotContain(session.Tabs, entry => entry.Path == path);
        Assert.DoesNotContain(session.FileViews.Keys, key => key == path);
    }

    [Fact]
    public async Task History_of_a_recycled_file_is_kept_and_returns_when_the_file_is_restored_with_the_same_content()
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("restore.md", "Saved text\n");
        await fixture.Shell.OpenFileAsync(path);
        await fixture.Shell.HistoryStore.CaptureAsync(path, "First\n");
        await fixture.Shell.HistoryStore.CaptureAsync(path, "Saved text\n");
        fixture.Dialogs.DeleteChoice = true;
        var recycle = new FakeRecycle(fixture.Root.File("fake-recycle"));

        Assert.True(await fixture.Shell.DeleteFileAsync(path, recycle));
        Assert.Empty(await fixture.Shell.HistoryStore.ReadAsync(path));
        Assert.True(fixture.Shell.HistoryStore.HasTrashedHistory(path));

        File.Move(recycle.MovedPath!, path);
        var versions = await fixture.Shell.HistoryStore.ReadAsync(path);
        Assert.Equal(["Saved text\n", "First\n"], versions.Select(v => v.Text));
        Assert.False(fixture.Shell.HistoryStore.HasTrashedHistory(path));
    }

    [Fact]
    public async Task Unsaved_edits_confirmed_for_discard_stay_in_recovery_after_the_recycle()
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("unsaved.md", "Disk\n");
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "Precious ")]);
        fixture.Dialogs.DeleteChoice = true;

        Assert.True(await fixture.Shell.DeleteFileAsync(path, new FakeRecycle(fixture.Root.File("fake-recycle"))));

        Assert.Contains(fixture.Shell.RecoveryStore.Scan().Entries, entry => entry.Record.Text == "Precious Disk\n");
    }

    [Fact]
    public async Task Recycle_boundary_that_does_not_remove_the_path_cannot_retire_open_edits()
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("keep.md", "Original\r\n");
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        fixture.Dialogs.DeleteChoice = true;
        var recycle = new FakeRecycle(fixture.Root.File("fake-recycle")) { DoNotMove = true };

        Assert.False(await fixture.Shell.DeleteFileAsync(path, recycle));

        Assert.Same(tab, Assert.Single(fixture.Shell.Tabs));
        Assert.True(File.Exists(path));
        Assert.Single(fixture.Shell.Recent.Files);
    }

    [Fact]
    public async Task Newer_edits_during_recycling_keep_their_tab_and_recovery_checkpoint()
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("keep-edits.md", "Original\r\n");
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "Approved old edits ")]);
        await fixture.Shell.RecoveryStore.WriteCheckpointAsync(new RecoveryRecord(tab.Session.Id, path, tab.DisplayName,
            tab.Session.Revision, DateTime.UtcNow, tab.Session.Baseline?.Sha256, tab.Session.Format, tab.CurrentText));
        fixture.Dialogs.DeleteChoice = true;
        var recycle = new FakeRecycle(fixture.Root.File("fake-recycle"))
        { AfterMove = () => { tab.ApplyEdits([TextEdit.Insert(0, "Newer ")]); return Task.CompletedTask; } };

        Assert.True(await fixture.Shell.DeleteFileAsync(path, recycle));

        Assert.False(File.Exists(path));
        Assert.Same(tab, Assert.Single(fixture.Shell.Tabs));
        Assert.True(tab.IsDirty);
        Assert.Equal("Newer Approved old edits Original\r\n", tab.CurrentText);
        Assert.Contains(fixture.Shell.RecoveryStore.Scan().Entries, entry => entry.Record.SessionId == tab.Session.Id);
        Assert.Contains("newer", fixture.Shell.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Disposing_the_shell_while_recycling_does_not_run_cleanup_on_disposed_stores()
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("dispose.md", "Original\r\n");
        await fixture.Shell.OpenFileAsync(path);
        fixture.Dialogs.DeleteChoice = true;
        var recycle = new FakeRecycle(fixture.Root.File("fake-recycle"))
        { AfterMove = () => { fixture.Shell.Dispose(); return Task.CompletedTask; } };

        Assert.True(await fixture.Shell.DeleteFileAsync(path, recycle));

        Assert.False(File.Exists(path));
        Assert.Equal("Original\r\n", await File.ReadAllTextAsync(recycle.MovedPath!));
    }

    [Fact]
    public async Task Missing_recovery_directory_after_recycle_reports_cleanup_warning_without_crashing()
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("warning.md", "Original\r\n");
        await fixture.Shell.OpenFileAsync(path);
        fixture.Dialogs.DeleteChoice = true;
        var recovery = fixture.Shell.Paths.RecoveryDirectory;
        var recycle = new FakeRecycle(fixture.Root.File("fake-recycle"))
        { AfterMove = () => { Directory.Move(recovery, recovery + ".held"); return Task.CompletedTask; } };

        Assert.True(await fixture.Shell.DeleteFileAsync(path, recycle));

        Assert.False(File.Exists(path));
        Assert.Contains("recovery", fixture.Shell.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StatusSeverity.Warning, fixture.Shell.StatusSeverity);
    }

    [Fact]
    public async Task Session_persistence_failure_after_recycle_is_not_overwritten_by_an_all_cleared_message()
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("warning.md", "Original\r\n");
        await fixture.Shell.OpenFileAsync(path);
        Assert.True(fixture.Shell.SaveSession());
        using var heldSession = new FileStream(Path.Combine(fixture.Shell.Paths.StateDirectory, SessionStore.FileName),
            FileMode.Open, FileAccess.Read, FileShare.None);
        fixture.Dialogs.DeleteChoice = true;
        var recycle = new FakeRecycle(fixture.Root.File("fake-recycle"));

        Assert.True(await fixture.Shell.DeleteFileAsync(path, recycle));

        Assert.False(File.Exists(path));
        Assert.Contains("session", fixture.Shell.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StatusSeverity.Warning, fixture.Shell.StatusSeverity);
    }

    [Fact]
    public async Task Recovery_for_an_unconfirmed_live_session_is_not_destroyed_with_the_confirmed_file_tabs()
    {
        using var fixture = new ShellFixture();
        var path = await fixture.WriteAsync("shared.md", "Original\r\n");
        await fixture.Shell.OpenFileAsync(path);
        var foreign = new RecoveryRecord(Guid.NewGuid(), path, "Other live session", 1, DateTime.UtcNow, null,
            TextFileFormat.NewFileDefault, "Unconfirmed work\r\n");
        await fixture.Shell.RecoveryStore.WriteCheckpointAsync(foreign);
        Assert.True(Assert.Single(fixture.Shell.RecoveryStore.Scan().Entries).OwnerIsAlive);
        fixture.Dialogs.DeleteChoice = true;
        var recycle = new FakeRecycle(fixture.Root.File("fake-recycle"));

        Assert.True(await fixture.Shell.DeleteFileAsync(path, recycle));

        Assert.False(File.Exists(path));
        Assert.Contains(fixture.Shell.RecoveryStore.Scan().Entries, entry => entry.Record.SessionId == foreign.SessionId);
    }

    private sealed class FakeRecycle(string directory) : ILocalFileDeletion
    {
        public int Calls { get; private set; }
        public string? MovedPath { get; private set; }
        public Exception? Failure { get; init; }
        public bool DoNotMove { get; init; }
        public Func<Task>? AfterMove { get; init; }

        public async Task MoveToRecycleBinAsync(string path)
        {
            Calls++;
            if (Failure is { } failure) throw failure;
            if (!DoNotMove)
            {
                Directory.CreateDirectory(directory);
                MovedPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".md");
                File.Move(path, MovedPath);
            }
            if (AfterMove is { } after) await after();
        }
    }
}
