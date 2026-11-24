using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.App.Panels;
using Tern.App.Settings;
using Tern.Desktop.ViewModels;
using Tern.Desktop.Views;
using Tern.Domain.Text;
using Tern.Markdown;
using Tern.Search;
using Tern.Workspaces;

namespace Tern.Desktop.Tests;

public sealed class KnowledgeNavigationWorkflowTests
{
    [AvaloniaFact]
    public async Task Typing_double_brackets_opens_resolving_alias_heading_completion_and_insertion_is_undoable()
    {
        using var f = new DesktopFixture();
        var source = await f.WriteAsync("source.md", "# Source\n\n");
        await f.WriteAsync("target.md", "---\naliases: [Overview]\n---\n# Target\n## Details");
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));
        var tab = (await f.Shell.OpenFileAsync(source))!;
        await Ready(tab);
        var before = tab.CurrentText;
        f.Window.EditorControl.CaretOffset = before.Length;
        f.Window.EditorControl.TextArea.Focus();
        f.Window.EditorControl.TextArea.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "[" });
        f.Window.EditorControl.TextArea.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "[" });
        Pump(f);
        Assert.NotNull(f.Window.Picker);
        var picker = f.Window.Picker!;
        Assert.Contains(picker.Results.Items.OfType<PickerItem>(), item => item.Title.Contains("Overview", StringComparison.Ordinal));
        picker.Input.Text = "#Details";
        Pump(f);
        var selected = Assert.Single(picker.Results.Items.OfType<PickerItem>());
        picker.Results.SelectedItem = selected;
        picker.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Contains("[[target.md#details]]", tab.CurrentText);
        var link = Assert.Single(MarkdownAnalyzer.Analyze(tab.CurrentText, tab.Profile).Links);
        Assert.Equal(LinkStatus.Ok, f.Shell.Workspace!.Graph.Resolve(source, link).Status);
        await f.Window.RunCommandAsync(CommandIds.Undo);
        Assert.Equal(before + "[[", tab.CurrentText);
    }

    [AvaloniaFact]
    public async Task Insert_link_workspace_note_choice_emits_a_resolving_wiki_link()
    {
        using var f = new DesktopFixture();
        var source = await f.WriteAsync("source.md", "Keep ");
        await f.WriteAsync("target.md", "# Target");
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));
        var tab = (await f.Shell.OpenFileAsync(source))!;
        var candidate = KnowledgeNavigation.WikiLinks(f.Shell.Workspace!.Graph, source).Single(candidate => candidate.Label == "Target");
        var dialog = new InsertLinkWindow("", source, [], workspaceNotes: [candidate]);
        dialog.Show(f.Window);
        try
        {
            dialog.GetVisualDescendants().OfType<RadioButton>().Single(button => Equals(button.Content, "Workspace note")).IsChecked = true;
            dialog.WorkspaceInput.SelectedIndex = 0;
            Assert.True(dialog.InsertButton.IsEnabled);
        }
        finally { dialog.Close(); }
        f.Window.LinkInputPicker = _ => Task.FromResult<LinkInput?>(new("Target note", candidate.Destination, true));
        f.Window.EditorControl.CaretOffset = tab.CurrentText.Length;
        await f.Window.RunCommandAsync(CommandIds.InsertLink);
        Assert.Equal("Keep [[target.md|Target note]]", tab.CurrentText);
        Assert.Equal(LinkStatus.Ok, f.Shell.Workspace.Graph.Resolve(source, Assert.Single(MarkdownAnalyzer.Analyze(tab.CurrentText, tab.Profile).Links)).Status);
    }

    [AvaloniaFact]
    public async Task Tags_run_tag_search_and_saved_queries_persist_in_workspace_shortcuts()
    {
        using var f = new DesktopFixture();
        await f.WriteAsync("one.md", "---\ntags: [planning]\n---\n# One\nfirst");
        await f.WriteAsync("two.md", "---\ntags: [planning]\n---\n# Two\nsecond");
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));
        await f.Window.ShowNavAsync("Tags");
        var list = f.Window.FindControl<ListBox>("SideList")!;
        var tag = Assert.Single(list.Items.OfType<SideRow>());
        Assert.Equal("2 document(s)", tag.Detail);
        list.SelectedItem = tag;
        await f.Window.Picker!.QueryWork;
        Assert.Equal("tag:\"planning\"", f.Window.Picker.Input.Text);
        Assert.Equal(2, f.Window.Picker.Results.ItemCount);
        var save = f.Window.Picker.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Save search"));
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Contains("tag:\"planning\"", new SettingsStore(f.Shell.Paths.SettingsDirectory).Load().SavedWorkspaceSearches[f.Docs]);
        Assert.Contains(f.Window.FindControl<StackPanel>("SavedSearchList")!.GetVisualDescendants().OfType<Button>(),
            button => AutomationProperties.GetName(button) == "Run saved search tag:\"planning\"");
    }

    [AvaloniaFact]
    public async Task Task_list_checkboxes_group_filter_and_toggle_a_background_note_with_one_undo()
    {
        using var f = new DesktopFixture();
        var taskPath = await f.WriteAsync("plans\\tasks.md", "---\ntags: [planning]\n---\n# Tasks\n\n- [ ] Review proposal\n");
        var active = await f.Shell.OpenFileAsync(await f.WriteAsync("active.md", "# Active"));
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));
        await f.Window.ShowNavAsync("Tasks");
        var list = f.Window.FindControl<ListBox>("SideList")!;
        Assert.Single(list.Items.OfType<SideRow>(), row => row.IsGroup);
        f.Window.FindControl<TextBox>("TaskTagFilter")!.Text = "missing";
        Pump(f);
        Assert.Empty(list.Items);
        f.Window.FindControl<TextBox>("TaskTagFilter")!.Text = "planning";
        f.Window.FindControl<TextBox>("TaskPathFilter")!.Text = "plans";
        Pump(f);
        var row = Assert.Single(list.Items.OfType<SideRow>(), row => row.IsTask);
        Pump(f);
        var checkbox = Assert.Single(list.GetVisualDescendants().OfType<CheckBox>(), box => ReferenceEquals(box.DataContext, row));
        checkbox.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => f.Shell.Tabs.Any(tab => tab.FilePath == taskPath && tab.CurrentText.Contains("[x]", StringComparison.Ordinal)));
        var taskTab = f.Shell.Tabs.Single(tab => tab.FilePath == taskPath);
        Assert.Same(active, f.Shell.ActiveTab);
        Assert.DoesNotContain("[x]", await File.ReadAllTextAsync(taskPath));
        f.Shell.ActiveTab = taskTab;
        await f.Window.RunCommandAsync(CommandIds.Undo);
        Assert.Contains("[ ] Review proposal", taskTab.CurrentText);
        taskTab.ApplyEdits([new TextEdit(taskTab.CurrentText.IndexOf("Review proposal", StringComparison.Ordinal), "Review proposal".Length, "Changed task")]);
        Assert.False(await f.Window.ToggleWorkspaceTaskAsync(row.Task!));
        Assert.Contains("Changed task", taskTab.CurrentText);
    }

    [AvaloniaFact]
    public async Task Heading_change_offers_reviewed_incoming_anchor_updates_with_undo()
    {
        using var f = new DesktopFixture();
        var target = await f.WriteAsync("target.md", "# Old heading\n");
        var incoming = await f.WriteAsync("incoming.md", "[See](target.md#old-heading)");
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));
        var source = (await f.Shell.OpenFileAsync(incoming))!;
        var targetTab = (await f.Shell.OpenFileAsync(target))!;
        await Ready(targetTab);
        targetTab.ApplyEdits([new TextEdit(2, "Old heading".Length, "New heading")]);
        await Until(() => f.Shell.Banner?.Text.Contains("may need updating", StringComparison.Ordinal) == true);
        var staleOffer = f.Shell.Banner!;
        targetTab.ApplyEdits([new TextEdit(2, "New heading".Length, "Final heading")]);
        await Until(() => f.Shell.Banner is { } banner && !ReferenceEquals(banner, staleOffer) && banner.Text.Contains("may need updating", StringComparison.Ordinal));
        Assert.Single(staleOffer.Actions).Invoke();
        Assert.Contains("#old-heading", source.CurrentText);
        f.Window.RepairReview = plan =>
        {
            Assert.Equal(1, plan.TotalEdits);
            Assert.Contains("#final-heading", Assert.Single(plan.Files).ResultText);
            return Task.FromResult(true);
        };
        var action = Assert.Single(f.Shell.Banner!.Actions);
        action.Invoke();
        await Until(() => source.CurrentText.Contains("#final-heading", StringComparison.Ordinal));
        f.Shell.ActiveTab = source;
        await f.Window.RunCommandAsync(CommandIds.Undo);
        Assert.Contains("#old-heading", source.CurrentText);
        Assert.Equal("[See](target.md#old-heading)", await File.ReadAllTextAsync(incoming));
    }

    [AvaloniaFact]
    public async Task Workspace_replace_reviews_unsaved_and_closed_files_and_keeps_canceled_or_stale_text()
    {
        using var f = new DesktopFixture();
        var openPath = await f.WriteAsync("open.md", "cat disk");
        var closedPath = await f.WriteAsync("closed.md", "cat closed");
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));
        var open = (await f.Shell.OpenFileAsync(openPath))!;
        open.ApplyEdits([new TextEdit(0, open.CurrentText.Length, "cat unsaved")]);
        f.Window.WorkspaceReplaceInput = () => Task.FromResult<FindOptions?>(new("cat", "dog"));
        f.Window.RepairReview = plan => { Assert.Equal(2, plan.TotalEdits); return Task.FromResult(false); };
        await f.Window.RunCommandAsync(CommandIds.ReplaceInWorkspace);
        Assert.Equal("cat unsaved", open.CurrentText);
        Assert.Equal("cat closed", await File.ReadAllTextAsync(closedPath));
        f.Window.RepairReview = _ => Task.FromResult(true);
        await f.Window.RunCommandAsync(CommandIds.ReplaceInWorkspace);
        Assert.Equal("dog unsaved", open.CurrentText);
        Assert.Equal("cat disk", await File.ReadAllTextAsync(openPath));
        Assert.Equal("dog closed", await File.ReadAllTextAsync(closedPath));
        await f.Window.RunCommandAsync(CommandIds.Undo);
        Assert.Equal("cat unsaved", open.CurrentText);
        Assert.Equal((1, 0), await f.Shell.UndoLinkRepairsAsync());
        f.Window.RepairReview = async _ => { await File.WriteAllTextAsync(closedPath, "newer disk"); open.ApplyEdits([new TextEdit(0, open.CurrentText.Length, "newer buffer")]); return true; };
        await f.Window.RunCommandAsync(CommandIds.ReplaceInWorkspace);
        Assert.Equal("newer buffer", open.CurrentText);
        Assert.Equal("newer disk", await File.ReadAllTextAsync(closedPath));
        Assert.Contains("not applied", f.Shell.StatusMessage);
    }

    [AvaloniaFact]
    public async Task Unlinked_alias_mentions_exclude_linked_notes_and_local_graph_shows_one_hop_neighbours()
    {
        using var f = new DesktopFixture();
        var target = await f.WriteAsync("target.md", "---\naliases: [Project overview]\n---\n# Roadmap\n\n[[outgoing.md]]");
        var mention = await f.WriteAsync("mention.md", "Read the Project overview tomorrow.");
        await f.WriteAsync("linked.md", "Project overview: [[target.md]]");
        await f.WriteAsync("outgoing.md", "# Related");
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));
        await f.Shell.OpenFileAsync(target);
        await f.Window.ShowNavAsync("Mentions");
        var list = f.Window.FindControl<ListBox>("SideList")!;
        Assert.Equal(mention, Assert.Single(list.Items.OfType<SideRow>()).Path);
        await f.Window.ShowNavAsync("Graph");
        Assert.Equal(new[] { "linked.md", "outgoing.md" }, list.Items.OfType<SideRow>().Where(row => row.Path is not null).Select(row => Path.GetFileName(row.Path)).Order().ToArray());
    }

    [AvaloniaTheory]
    [InlineData("revision")]
    [InlineData("selection")]
    [InlineData("read-only")]
    public async Task Wiki_completion_refuses_a_stale_or_protected_insertion_target(string change)
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("source.md", "[[");
        await f.WriteAsync("target.md", "# Target");
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));
        var tab = (await f.Shell.OpenFileAsync(path))!;
        f.Window.EditorControl.CaretOffset = 2;
        Assert.True(f.Window.TryShowWikiCompletion());
        var picker = f.Window.Picker!;
        picker.Input.Text = "Target"; Pump(f);
        picker.Results.SelectedIndex = 0;
        if (change == "revision") tab.ApplyEdits([new TextEdit(0, 0, "Newer ")]);
        else if (change == "selection") f.Window.EditorControl.CaretOffset = 0;
        else tab.Session.SetReadOnly(true);
        var expected = tab.CurrentText;
        picker.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal(expected, tab.CurrentText);
        Assert.Contains("changed", f.Shell.StatusMessage);
        Assert.Equal("[[", await File.ReadAllTextAsync(path));
    }

    [AvaloniaFact]
    public async Task Workspace_replacement_preserves_divergent_duplicate_buffers_and_read_only_notes()
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("copies.md", "cat disk");
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));
        var first = (await f.Shell.OpenFileAsync(path))!;
        var second = (await f.Shell.OpenFileAsync(path, duplicateBehavior: Tern.App.Shell.DuplicateOpenBehavior.NewTab))!;
        first.ApplyEdits([new TextEdit(0, first.CurrentText.Length, "cat first")]);
        second.ApplyEdits([new TextEdit(0, second.CurrentText.Length, "cat second")]);
        f.Window.WorkspaceReplaceInput = () => Task.FromResult<FindOptions?>(new("cat", "dog"));
        f.Window.RepairReview = _ => Task.FromResult(true);
        await f.Window.RunCommandAsync(CommandIds.ReplaceInWorkspace);
        Assert.Equal("cat first", first.CurrentText);
        Assert.Equal("cat second", second.CurrentText);
        Assert.Equal("cat disk", await File.ReadAllTextAsync(path));
        second.ApplyEdits([new TextEdit(0, second.CurrentText.Length, first.CurrentText)]);
        second.Session.SetReadOnly(true);
        await f.Window.RunCommandAsync(CommandIds.ReplaceInWorkspace);
        Assert.Equal("cat first", first.CurrentText);
        Assert.Equal("cat first", second.CurrentText);
        Assert.Contains("read-only", f.Shell.StatusMessage);
    }

    private static Task Ready(Tern.App.Documents.DocumentTab tab) => Until(() => tab.Analysis is not null && tab.AnalysisRevision == tab.Session.Revision);
    private static async Task Until(Func<bool> ready)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!ready() && clock.Elapsed < TimeSpan.FromSeconds(15)) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
        Assert.True(ready(), "The expected knowledge workflow did not settle.");
    }
    private static void Pump(DesktopFixture f) { f.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); f.Window.UpdateLayout(); }
}
