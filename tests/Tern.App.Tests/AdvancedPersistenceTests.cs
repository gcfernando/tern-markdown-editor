using System.Diagnostics;
using System.Text;
using Tern.Domain.Text;
using Tern.Recovery;

namespace Tern.App.Tests;

public sealed class AdvancedPersistenceTests
{
    [Fact]
    public async Task Current_user_secondary_copies_round_trip_without_plaintext_and_report_tampering()
    {
        using var f = new ShellFixture();
        const string text = "Private fixture text 31964083";
        var path = await f.WriteAsync("private.md", text);
        using var history = new DocumentHistoryStore(f.Root.File("protected-history"), protectCopies: true);
        Assert.True((await history.CaptureAsync(path, text)).Added);
        var version = Assert.Single(Directory.GetFiles(f.Root.File("protected-history"), "*.json", SearchOption.AllDirectories));
        Assert.DoesNotContain(text, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(version)));
        Assert.Equal(text, Assert.Single(await history.ReadAsync(path)).Text);
        var recovery = new RecoveryStore(f.Root.File("protected-recovery"), protectCopies: true);
        var record = new RecoveryRecord(Guid.NewGuid(), path, "private.md", 1, DateTime.UtcNow, null,
            LineEndingAnalyzer.Describe(TextEncodingKind.Utf8, false, text), text);
        await recovery.WriteCheckpointAsync(record);
        Assert.Equal(text, Assert.Single(recovery.Scan().Entries).Record.Text);
        var checkpoint = Assert.Single(Directory.GetFiles(recovery.DirectoryPath, "*.tern-recovery"));
        var bytes = await File.ReadAllBytesAsync(checkpoint);
        Assert.DoesNotContain(text, Encoding.UTF8.GetString(bytes));
        bytes[^1] ^= 1;
        await File.WriteAllBytesAsync(checkpoint, bytes);
        var damaged = recovery.Scan();
        Assert.Empty(damaged.Entries);
        Assert.Single(damaged.Corrupt);
    }

    [Fact]
    public async Task Thirty_second_idle_save_writes_the_real_file_and_disabling_it_keeps_edits_off_disk()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("idle.md", "original");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        f.Shell.Settings.AutoSaveEnabled = true;
        f.Shell.Settings.AutoSaveIdleSeconds = 30;
        Assert.True(f.Shell.SaveSettings());
        tab.ApplyEdits([TextEdit.Insert(tab.CurrentText.Length, " first")]);
        await Task.Delay(TimeSpan.FromSeconds(1));
        tab.ApplyEdits([TextEdit.Insert(tab.CurrentText.Length, " last")]);
        var afterLastEdit = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(29));
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        while (tab.IsDirty && afterLastEdit.Elapsed < TimeSpan.FromSeconds(40)) await Task.Delay(50);
        Assert.False(tab.IsDirty);
        Assert.True(afterLastEdit.Elapsed >= TimeSpan.FromSeconds(30));
        Assert.Equal("original first last", await File.ReadAllTextAsync(path));
        Assert.NotEmpty(await f.Shell.HistoryStore.ReadAsync(path));

        f.Shell.Settings.AutoSaveEnabled = false;
        Assert.True(f.Shell.SaveSettings());
        tab.ApplyEdits([TextEdit.Insert(tab.CurrentText.Length, " unsaved")]);
        await Task.Delay(TimeSpan.FromSeconds(31));
        Assert.True(tab.IsDirty);
        Assert.Equal("original first last", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Idle_save_detects_external_conflicts_and_never_overwrites_a_cancelled_conflict()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("conflict.md", "original");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        f.Shell.Settings.AutoSaveEnabled = true;
        f.Shell.Settings.AutoSaveIdleSeconds = 30;
        Assert.True(f.Shell.SaveSettings());
        tab.ApplyEdits([TextEdit.Insert(0, "my ")]);
        await File.WriteAllTextAsync(path, "external change");
        var elapsed = Stopwatch.StartNew();
        while (tab.Conflict is null && elapsed.Elapsed < TimeSpan.FromSeconds(40)) await Task.Delay(50);
        Assert.NotNull(tab.Conflict);
        Assert.True(tab.IsDirty);
        Assert.Equal("my original", tab.CurrentText);
        Assert.Equal("external change", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Dont_save_keeps_a_protected_seven_day_draft_and_reopening_never_overwrites_the_source()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("discard.md", "on disk");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "unsaved ")]);
        f.Dialogs.SaveChoice = Tern.App.Shell.SaveChoice.Discard;
        Assert.True(await f.Shell.CloseTabAsync(tab));
        var entry = Assert.Single(f.Shell.RecentlyDiscarded().Entries);
        Assert.Equal("unsaved on disk", entry.Record.Text);
        var copy = f.Shell.ReopenDiscarded(entry.Record);
        Assert.Null(copy.FilePath);
        Assert.True(copy.IsDirty);
        Assert.Equal(entry.Record.Text, copy.CurrentText);
        Assert.Equal("on disk", await File.ReadAllTextAsync(path));
        var expired = entry.Record with { SessionId = Guid.NewGuid(), SavedAtUtc = DateTime.UtcNow.AddDays(-8), Text = "expired fixture" };
        await f.Shell.DiscardedDrafts.WriteCheckpointAsync(expired);
        Assert.Single(f.Shell.RecentlyDiscarded().Entries);
        Assert.DoesNotContain(f.Shell.DiscardedDrafts.Scan().Entries, item => item.Record.SessionId == expired.SessionId);
    }

    [Fact]
    public async Task Cancelling_a_group_close_creates_no_partial_shelf_and_failed_safety_copy_keeps_every_tab_open()
    {
        using var f = new ShellFixture();
        var first = f.Shell.NewDocument("first unsaved");
        var second = f.Shell.NewDocument("second unsaved");
        f.Dialogs.SaveResponses.Enqueue(Tern.App.Shell.SaveChoice.Discard);
        f.Dialogs.SaveResponses.Enqueue(Tern.App.Shell.SaveChoice.Cancel);
        Assert.False(await f.Shell.CloseDocumentsAsync());
        Assert.Empty(f.Shell.RecentlyDiscarded().Entries);
        Assert.Contains(first, f.Shell.Tabs);
        Assert.Contains(second, f.Shell.Tabs);
        var directory = f.Shell.DiscardedDrafts.DirectoryPath;
        Directory.Delete(directory);
        await File.WriteAllTextAsync(directory, "synthetic storage blocker");
        f.Dialogs.SaveChoice = Tern.App.Shell.SaveChoice.Discard;
        Assert.False(await f.Shell.CloseDocumentsAsync());
        Assert.Contains(first, f.Shell.Tabs);
        Assert.Contains(second, f.Shell.Tabs);
        Assert.Equal("first unsaved", first.CurrentText);
        Assert.Contains("remains open", f.Shell.StatusMessage);
    }

    [Fact]
    public async Task Private_workspace_searches_in_memory_without_history_recovery_or_discarded_copies_and_clear_actions_are_scoped()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("private.md", "private original");
        var outside = f.Root.File("outside.md");
        await File.WriteAllTextAsync(outside, "outside original");
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));
        var tab = (await f.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "saved ")]);
        Assert.True(await f.Shell.SaveTabAsync(tab));
        Assert.NotEmpty(await f.Shell.HistoryStore.ReadAsync(path));
        await f.Shell.HistoryStore.CaptureAsync(outside, "keep outside history");
        Assert.True(await f.Shell.ClearHistoryAsync(tab));
        Assert.Empty(await f.Shell.HistoryStore.ReadAsync(path));
        Assert.NotEmpty(await f.Shell.HistoryStore.ReadAsync(outside));
        await f.Shell.HistoryStore.CaptureAsync(path, "recycled saved version");
        Assert.True(await f.Shell.HistoryStore.TrashAsync(path));
        Assert.True(f.Shell.HistoryStore.HasTrashedHistory(path));
        Assert.True(await f.Shell.ClearHistoryAsync(tab));
        Assert.False(f.Shell.HistoryStore.HasTrashedHistory(path));
        await f.Shell.HistoryStore.CaptureAsync(path, "clear with workspace");
        Assert.True(await f.Shell.ClearHistoryAsync(workspace: f.Shell.Workspace));
        Assert.Empty(await f.Shell.HistoryStore.ReadAsync(path));
        Assert.NotEmpty(await f.Shell.HistoryStore.ReadAsync(outside));
        Assert.True(await f.Shell.SetWorkspacePrivateAsync(f.Shell.Workspace!, true));
        Assert.True(f.Shell.Workspace!.IsPrivate);
        Assert.NotEmpty(f.Shell.Workspace.Search("original").Hits);
        Assert.True(Tern.Workspaces.WorkspaceDefinition.Load(f.Docs)!.Private);
        tab = f.Shell.Tabs.Single(item => item.FilePath == path);
        tab.ApplyEdits([TextEdit.Insert(0, "secret ")]);
        Assert.True(await f.Shell.SaveTabAsync(tab));
        Assert.Empty(await f.Shell.HistoryStore.ReadAsync(path));
        tab.ApplyEdits([TextEdit.Insert(0, "discard private ")]);
        Assert.True(await f.Shell.RetryRecoveryAsync(tab));
        Assert.Empty(f.Shell.RecoveryStore.Scan().Entries);
        var draft = f.Shell.NewDocument("private unnamed draft");
        Assert.True(f.Shell.IsPrivateDocument(draft));
        f.Dialogs.SaveChoice = Tern.App.Shell.SaveChoice.Discard;
        Assert.True(await f.Shell.CloseWorkspaceAsync());
        Assert.True(f.Shell.IsPrivateDocument(draft));
        Assert.True(await f.Shell.RetryRecoveryAsync(draft));
        Assert.True(await f.Shell.CloseTabAsync(draft));
        Assert.Empty(f.Shell.RecoveryStore.Scan().Entries);
        Assert.Empty(f.Shell.RecentlyDiscarded().Entries);
        Assert.Empty(Directory.GetFiles(f.Shell.Paths.IndexDirectory, "*", SearchOption.AllDirectories));
        Assert.NotEmpty(await f.Shell.HistoryStore.ReadAsync(outside));
    }
}
