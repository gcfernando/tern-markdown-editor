using Tern.App.Shell;
using Tern.Domain.Text;
using Tern.Recovery;

namespace Tern.App.Tests;

/// <summary>WP01 (B-01, B-12, B-17, D-01) and WP02 (D-05, B-02, R-03, B-06) behaviour through the real shell.</summary>
public sealed class ErrorBoundaryAndHistoryTests
{
    private static string HistoryRoot(ShellFixture f) => Path.Combine(f.Root.File("appdata"), "state", "history");

    [Theory]
    [InlineData(13)]
    [InlineData(5)]
    public async Task Index_failure_after_save_leaves_file_saved_and_reports_a_warning(int sqliteCode)
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("a.md", "one\n");
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));
        var tab = (await f.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "x")]);
        f.Shell.BeforeWorkspaceSavedNotification = () => throw new Microsoft.Data.Sqlite.SqliteException("injected index failure", sqliteCode);

        Assert.True(await f.Shell.SaveTabAsync(tab));

        Assert.Equal("xone\n", await File.ReadAllTextAsync(path));
        Assert.False(tab.IsDirty);
        Assert.Equal(StatusSeverity.Warning, f.Shell.StatusSeverity);
        Assert.Contains("File saved", f.Shell.StatusMessage);
        Assert.Contains("search index", f.Shell.StatusMessage);
        var activity = Assert.Single(f.Shell.Activities.Items, item => item.Kind == "Saving document");
        Assert.Equal(ActivityState.Completed, activity.State);
        Assert.Equal(ActivitySeverity.Warning, activity.Severity);
        Assert.Equal("Saved with warning", activity.Phase);
        Assert.NotNull(activity.Finished);
    }

    [Fact]
    public async Task Throwing_state_listener_does_not_corrupt_save_bookkeeping()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("a.md", "one\n");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        tab.Session.StateChanged += (_, _) => throw new InvalidOperationException("listener bug");
        tab.ApplyEdits([TextEdit.Insert(0, "x")]);

        Assert.True(await f.Shell.SaveTabAsync(tab));

        Assert.Equal("xone\n", tab.MergeBase);
        Assert.Null(tab.Conflict);
        Assert.False(tab.IsDirty);
        Assert.Equal("xone\n", (await new DocumentHistoryStore(HistoryRoot(f)).ReadAsync(path))[0].Text);
        Assert.Equal(StatusSeverity.Error, f.Shell.Banner?.Severity);
    }

    [Fact]
    public async Task ReportUnexpectedError_shows_banner_with_details_and_copy_without_throwing()
    {
        using var f = new ShellFixture();
        ErrorReportEventArgs? requested = null;
        f.Shell.ErrorReportRequested += (_, e) => requested = e;

        f.Shell.ReportUnexpectedError("Save", new InvalidOperationException("secret detail"));

        Assert.Equal(StatusSeverity.Error, f.Shell.Banner!.Severity);
        Assert.Equal(["Details", "Copy report"], f.Shell.Banner.Actions.Select(a => a.Label));
        Assert.Contains("Your text is safe", f.Shell.Banner.Text);
        f.Shell.Banner.Actions[1].Invoke();
        Assert.True(requested!.Copy);
        Assert.Contains("InvalidOperationException", requested.Report);
    }

    [Fact]
    public async Task Locked_settings_file_warns_and_returns_false_instead_of_throwing()
    {
        using var f = new ShellFixture();
        Assert.True(f.Shell.SaveSettings());
        var settings = Directory.GetFiles(f.Root.File("appdata"), "settings.json", SearchOption.AllDirectories).Single();
        using var hold = new FileStream(settings, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.False(f.Shell.SaveSettings());

        Assert.Equal(StatusSeverity.Warning, f.Shell.StatusSeverity);
        Assert.Contains("Settings could not be saved", f.Shell.StatusMessage);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task First_save_over_an_existing_file_keeps_the_original_in_history()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("a.md", "original\n");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "new ")]);

        Assert.True(await f.Shell.SaveTabAsync(tab));

        var versions = await new DocumentHistoryStore(HistoryRoot(f)).ReadAsync(path);
        Assert.Equal(2, versions.Count);
        Assert.Equal("new original\n", versions[0].Text);
        Assert.Equal("original\n", versions[1].Text);
        Assert.Equal(ShellViewModel.OriginalVersionLabel, versions[1].Label);
    }

    [Fact]
    public async Task Keep_mine_keeps_the_replaced_external_version()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("a.md", "a\nb\nc\n");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "mine\n")]);
        await File.WriteAllTextAsync(path, "external\n");
        f.Dialogs.ConflictChoice = ConflictChoice.KeepMine;

        Assert.True(await f.Shell.SaveTabAsync(tab));

        var texts = (await new DocumentHistoryStore(HistoryRoot(f)).ReadAsync(path)).Select(v => v.Text).ToList();
        Assert.Contains("external\n", texts);
        Assert.Contains("mine\na\nb\nc\n", texts);
    }

    [Fact]
    public async Task History_after_reload_then_save_holds_the_saved_text_not_stale_merge_state()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("a.md", "a\n");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        await File.WriteAllTextAsync(path, "disk v2\n");
        await tab.CheckDiskAsync(autoReloadClean: true);
        tab.ApplyEdits([TextEdit.Insert(0, "edit ")]);

        Assert.True(await f.Shell.SaveTabAsync(tab));

        var versions = await new DocumentHistoryStore(HistoryRoot(f)).ReadAsync(path);
        Assert.Equal("edit disk v2\n", versions[0].Text);
        Assert.Equal("edit disk v2\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Damaged_history_does_not_block_save_and_is_reported()
    {
        using var f = new ShellFixture();
        var path = await f.WriteAsync("a.md", "one\n");
        var store = new DocumentHistoryStore(HistoryRoot(f));
        await store.CaptureAsync(path, "older");
        var entry = Directory.GetFiles(HistoryRoot(f), "*.json", SearchOption.AllDirectories).Single();
        await File.WriteAllTextAsync(entry, string.Empty);
        var tab = (await f.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "x")]);

        Assert.True(await f.Shell.SaveTabAsync(tab));

        Assert.Equal("xone\n", await File.ReadAllTextAsync(path));
        Assert.Contains("damaged history version", f.Shell.StatusMessage);
        Assert.Equal(StatusSeverity.Warning, f.Shell.StatusSeverity);
    }
}
