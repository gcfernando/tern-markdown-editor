using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Tern.Desktop.Services;
using Tern.Domain.Text;
using Tern.Workspaces;

namespace Tern.Desktop.Tests;

public sealed class ForwardedArgumentsTests
{
    [AvaloniaFact]
    public async Task Bulk_session_restore_runs_queued_input_before_finishing_and_keeps_the_active_note_edit()
    {
        using var f = new DesktopFixture();
        var paths = new List<string>();
        for (var index = 0; index < 64; index++)
            paths.Add(await f.WriteAsync($"session-batch-{index:D2}.md", $"# Session {index}"));
        Assert.True(new SessionStore(f.Shell.Paths.StateDirectory).Save(new SessionSnapshot(null,
            paths.Select(path => new TabState(path, 0, 0)).ToArray(), paths[^1], null,
            new Dictionary<string, FileViewState>())
        { ActiveTabIndex = 63 }));
        f.Shell.UiDispatcher = new AvaloniaUiDispatcher();
        var queued = false;
        var typed = false;
        f.Shell.Tabs.CollectionChanged += (_, _) =>
        {
            if (queued || f.Shell.Tabs.Count != 2) return;
            queued = true;
            Dispatcher.UIThread.Post(() =>
            {
                Assert.InRange(f.Shell.Tabs.Count, 2, 9);
                f.Window.UpdateLayout();
                f.Window.Editor.CaretOffset = f.Window.Editor.Document.TextLength;
                f.Window.Editor.TextArea.Focus();
                f.Window.Editor.TextArea.RaiseEvent(new TextInputEventArgs
                { RoutedEvent = InputElement.TextInputEvent, Text = " typed during restore" });
                typed = true;
            }, DispatcherPriority.Input);
        };
        await Dispatcher.UIThread.InvokeAsync(() => f.Shell.RestoreSessionAsync());
        Assert.True(typed);
        Assert.Equal(paths, f.Shell.Tabs.Select(tab => tab.FilePath));
        Assert.Equal(paths[^1], f.Shell.ActiveTab!.FilePath);
        Assert.Equal("# Session 63 typed during restore", f.Shell.ActiveTab.CurrentText);
        Assert.True(f.Shell.ActiveTab.IsDirty);
        Assert.Equal("# Session 63", await File.ReadAllTextAsync(paths[^1]));
    }

    [AvaloniaFact]
    public async Task Bulk_arguments_keep_the_displayed_document_stable_for_real_input_until_the_last_file_is_ready()
    {
        using var f = new DesktopFixture();
        var live = f.Shell.NewDocument("# Existing unsaved document");
        var paths = new List<string>();
        for (var index = 0; index < 64; index++)
            paths.Add(await f.WriteAsync($"bulk-{index:D2}.md", $"# Requested {index}"));
        var selections = new List<string?>();
        f.Shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(f.Shell.ActiveTab)) selections.Add(f.Shell.ActiveTab?.FilePath);
        };
        var typed = false;
        f.Shell.Tabs.CollectionChanged += (_, _) =>
        {
            if (typed || f.Shell.Tabs.Count != 9) return;
            typed = true;
            Dispatcher.UIThread.RunJobs();
            f.Window.UpdateLayout();
            f.Window.Editor.CaretOffset = f.Window.Editor.Document.TextLength;
            f.Window.Editor.TextArea.Focus();
            f.Window.KeyTextInput(" typed during bulk open");
        };
        await Startup.OpenArgumentsAsync(f.Window, f.Shell, paths);
        Assert.True(typed);
        Assert.Equal([paths[0], paths[^1]], selections);
        Assert.Equal(paths[^1], f.Shell.ActiveTab!.FilePath);
        var first = Assert.Single(f.Shell.Tabs, tab => tab.FilePath == paths[0]);
        Assert.Equal("# Requested 0 typed during bulk open", first.CurrentText);
        Assert.True(first.IsDirty);
        Assert.Equal("# Requested 0", await File.ReadAllTextAsync(paths[0]));
        Assert.Equal("# Existing unsaved document", live.CurrentText);
        Assert.Contains(live, f.Shell.Tabs);
        Assert.Equal(65, f.Shell.Tabs.Count);
    }

    [AvaloniaFact]
    public async Task Forwarded_arguments_open_new_tabs_without_restoring_old_tabs_or_losing_live_edits()
    {
        using var f = new DesktopFixture();
        var previous = await f.WriteAsync("previous-session.md", "# Previous");
        var live = (await f.Shell.OpenFileAsync(await f.WriteAsync("live.md", "# Live")))!;
        live.ApplyEdits([TextEdit.Insert(live.CurrentText.Length, "\nunsaved live edit")]);
        var saved = new SessionStore(f.Shell.Paths.StateDirectory);
        Assert.True(saved.Save(new SessionSnapshot(null, [new(previous, 0, 0)], previous, null,
            new Dictionary<string, FileViewState>())));
        var forwarded = await f.WriteAsync("forwarded file.md", "# Forwarded");
        await Startup.OpenArgumentsAsync(f.Window, f.Shell, [forwarded]);
        Assert.Equal(2, f.Shell.Tabs.Count);
        Assert.DoesNotContain(f.Shell.Tabs, tab => tab.FilePath == previous);
        Assert.Equal(forwarded, f.Shell.ActiveTab!.FilePath);
        Assert.True(live.IsDirty);
        Assert.Contains("unsaved live edit", live.CurrentText);
    }
}
