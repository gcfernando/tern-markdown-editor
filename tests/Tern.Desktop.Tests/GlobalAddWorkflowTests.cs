using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.App.Shell;
using Tern.Desktop.ViewModels;
using Tern.Desktop.Views;
using Tern.Workspaces;

namespace Tern.Desktop.Tests;

/// <summary>Exercises the actual Library +Add flyout, modal decisions, filesystem and Library activation together.</summary>
public sealed class GlobalAddWorkflowTests
{
    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Outside_draft_is_visible_and_clickable_with_zero_one_or_multiple_workspaces(int workspaceCount)
    {
        using var fixture = new DesktopFixture();
        await OpenRootsAsync(fixture, workspaceCount);
        var selected = fixture.Shell.Workspace;
        ClickAdd(fixture, CommandIds.NewFile);
        if (workspaceCount > 0) await ChooseAsync(fixture, "Outside all workspaces");
        await UntilAsync(fixture, () => fixture.Shell.Tabs.Count == 1 && !fixture.Window.OwnedWindows.Any());
        var draft = Assert.Single(fixture.Shell.Tabs);
        Assert.True(draft.IsStandaloneDraft);
        Assert.Null(draft.FilePath);
        Assert.Same(selected, fixture.Shell.Workspace);
        Assert.Equal("Files", fixture.Window.ActiveNav); Pump(fixture);
        var (list, row) = FindDraftRow(fixture, draft);
        Assert.True(list.IsEffectivelyVisible);
        Assert.NotNull(list.ContainerFromItem(row));
        var another = fixture.Shell.NewDocument("Another open document");
        Assert.Same(another, fixture.Shell.ActiveTab);
        Pump(fixture);
        (list, row) = FindDraftRow(fixture, draft);
        list.SelectedItem = row; Pump(fixture);
        Assert.Same(draft, fixture.Shell.ActiveTab);
        Assert.Empty(fixture.Window.OwnedWindows);
        Assert.Equal(workspaceCount, fixture.Shell.OpenWorkspaces.Count);
    }

    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Selected_destination_is_captured_once_and_file_creation_does_not_ask_where_again(int workspaceCount)
    {
        using var fixture = new DesktopFixture();
        var roots = await OpenRootsAsync(fixture, workspaceCount);
        var intended = roots[0];
        fixture.Shell.SelectWorkspace(intended); Pump(fixture);
        ClickAdd(fixture, CommandIds.NewWorkspaceFile);
        var decision = await OneDialogAsync(fixture);
        ClickAdd(fixture, CommandIds.NewWorkspaceFile);
        Assert.Same(decision, Assert.Single(fixture.Window.OwnedWindows));
        Assert.NotNull(FindButton(decision, "Use selected location"));
        Assert.DoesNotContain(decision.GetVisualDescendants().OfType<TextBox>(), t => AutomationProperties.GetName(t) == "Parent folder");
        // The displayed choice must retain its target even if background shell selection changes.
        if (roots.Length > 1) fixture.Shell.SelectWorkspace(roots[1]);
        ClickButton(decision, "Use selected location");
        var create = await CreateDialogAsync(fixture);
        var parent = Input(create, "Parent folder");
        Assert.Equal(intended.Root, parent.Text);
        Assert.True(parent.IsReadOnly);
        Input(create, "File name").Text = "chosen-target.md"; Pump(fixture);
        Assert.True(FindButton(create, "Create").IsEnabled);
        ClickButton(create, "Create");
        var expected = Path.Combine(intended.Root, "chosen-target.md");
        await UntilAsync(fixture, () => fixture.Shell.ActiveTab?.FilePath is { } path && WorkspacePaths.PathsEqual(path, expected));
        Assert.True(File.Exists(expected));
        foreach (var root in roots.Where(root => !ReferenceEquals(root, intended))) Assert.False(File.Exists(Path.Combine(root.Root, "chosen-target.md")));
        Assert.Empty(fixture.Window.OwnedWindows);
        Assert.Equal(workspaceCount, fixture.Shell.OpenWorkspaces.Count);
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Outside_folder_uses_one_parent_and_name_form_then_remains_a_separate_plain_folder_section(int workspaceCount)
    {
        using var fixture = new DesktopFixture();
        var roots = await OpenRootsAsync(fixture, workspaceCount);
        var parent = Path.Combine(fixture.Docs, "outside-parent"); Directory.CreateDirectory(parent);
        ClickAdd(fixture, CommandIds.NewFolder);
        if (workspaceCount > 0) await ChooseAsync(fixture, "Outside all workspaces");
        var create = await CreateDialogAsync(fixture);
        Assert.False(Input(create, "Parent folder").IsReadOnly);
        Assert.DoesNotContain(roots, root => Input(create, "Parent folder").Text is { } initial
            && (WorkspacePaths.PathsEqual(root.Root, initial) || root.IsInside(initial)));
        Input(create, "Parent folder").Text = parent;
        Input(create, "Folder name").Text = "External"; Pump(fixture);
        Assert.True(FindButton(create, "Create").IsEnabled);
        ClickButton(create, "Create");
        var expected = Path.Combine(parent, "External");
        await UntilAsync(fixture, () => fixture.Shell.OpenWorkspaces.Any(root => WorkspacePaths.PathsEqual(root.Root, expected)));
        Assert.True(Directory.Exists(expected));
        Assert.False(File.Exists(Path.Combine(expected, WorkspaceDefinition.FileName)));
        Assert.False(Assert.Single(fixture.Shell.OpenWorkspaces, root => WorkspacePaths.PathsEqual(root.Root, expected)).IsNamedWorkspace);
        Assert.Equal(workspaceCount, fixture.Shell.OpenWorkspaces.Count(root => root.IsNamedWorkspace));
        Assert.All(roots, root => Assert.False(root.IsInside(expected)));
        Assert.Equal("Files", fixture.Window.ActiveNav); Pump(fixture);
        var displayedRoot = Assert.Single(VisibleTreeRoots(fixture), row => WorkspacePaths.PathsEqual(row.FullPath, expected));
        Assert.True(displayedRoot.IsDirectory); Assert.False(displayedRoot.IsWorkspace);
        var card = Assert.Single(fixture.Window.GetVisualDescendants().OfType<Border>(), border => border.Name == "WorkspaceSectionCard"
            && border.DataContext is TreeRow row && WorkspacePaths.PathsEqual(row.FullPath, expected));
        Assert.True(card.IsEffectivelyVisible);
        Assert.Equal("Folder section: External", AutomationProperties.GetName(card));
        Assert.Empty(fixture.Window.OwnedWindows);
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Cancel_at_destination_or_combined_form_keeps_files_tabs_and_workspace_state(int workspaceCount)
    {
        using var fixture = new DesktopFixture();
        await OpenRootsAsync(fixture, workspaceCount);
        var selected = fixture.Shell.Workspace;
        var entries = Directory.EnumerateFileSystemEntries(fixture.Docs, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        if (workspaceCount > 0)
        {
            ClickAdd(fixture, CommandIds.NewFolder);
            await ChooseAsync(fixture, "Cancel destination choice");
            await UntilAsync(fixture, () => !fixture.Window.OwnedWindows.Any());
            Assert.Empty(fixture.Shell.Tabs);
        }
        ClickAdd(fixture, CommandIds.NewFolder);
        if (workspaceCount > 0) await ChooseAsync(fixture, "Outside all workspaces");
        var create = await CreateDialogAsync(fixture);
        Input(create, "Folder name").Text = "Must not exist";
        ClickButton(create, "Cancel");
        await UntilAsync(fixture, () => !fixture.Window.OwnedWindows.Any());
        Assert.Equal(entries, Directory.EnumerateFileSystemEntries(fixture.Docs, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray());
        Assert.Empty(fixture.Shell.Tabs);
        Assert.Same(selected, fixture.Shell.Workspace);
        Assert.Equal(workspaceCount, fixture.Shell.OpenWorkspaces.Count);
    }

    [AvaloniaTheory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(2, true)]
    public async Task Outside_physical_file_or_workspace_uses_the_exact_parent_and_name_once(int workspaceCount, bool workspace)
    {
        using var fixture = new DesktopFixture();
        var roots = await OpenRootsAsync(fixture, workspaceCount);
        var parent = Path.Combine(fixture.Docs, "explicit-outside-parent"); Directory.CreateDirectory(parent);
        ClickAdd(fixture, workspace ? CommandIds.CreateWorkspace : CommandIds.NewWorkspaceFile);
        if (workspaceCount > 0) await ChooseAsync(fixture, "Outside all workspaces");
        var create = await CreateDialogAsync(fixture);
        Input(create, "Parent folder").Text = parent;
        Input(create, workspace ? "Workspace name" : "File name").Text = workspace ? "Independent" : "standalone-note";
        Pump(fixture);
        Assert.True(FindButton(create, "Create").IsEnabled);
        ClickButton(create, "Create");
        var expected = Path.Combine(parent, workspace ? "Independent" : "standalone-note.md");
        await UntilAsync(fixture, () => workspace
            ? fixture.Shell.OpenWorkspaces.Any(root => WorkspacePaths.PathsEqual(root.Root, expected))
            : fixture.Shell.ActiveTab?.FilePath is { } path && WorkspacePaths.PathsEqual(path, expected));
        Assert.Empty(fixture.Window.OwnedWindows);
        Assert.All(roots, root => Assert.False(root.IsInside(expected)));
        if (workspace)
        {
            Assert.True(File.Exists(Path.Combine(expected, WorkspaceDefinition.FileName)));
            Assert.True(Assert.Single(fixture.Shell.OpenWorkspaces, root => WorkspacePaths.PathsEqual(root.Root, expected)).IsNamedWorkspace);
            Assert.Equal(workspaceCount + 1, fixture.Shell.OpenWorkspaces.Count);
        }
        else
        {
            Assert.True(File.Exists(expected));
            Assert.Null(fixture.Shell.WorkspaceForPath(expected));
            Assert.Equal(workspaceCount, fixture.Shell.OpenWorkspaces.Count);
            Assert.Contains(fixture.Window.GetVisualDescendants().OfType<ListBox>().Where(list => list.IsEffectivelyVisible)
                .SelectMany(list => list.Items.OfType<SideRow>()), row => row.Path is { } path && WorkspacePaths.PathsEqual(path, expected));
        }
    }

    [AvaloniaFact]
    public async Task Outside_creation_rejects_an_inside_workspace_parent_in_the_same_form_without_a_second_prompt()
    {
        using var fixture = new DesktopFixture();
        var roots = await OpenRootsAsync(fixture, 2);
        ClickAdd(fixture, CommandIds.NewWorkspaceFile);
        await ChooseAsync(fixture, "Outside all workspaces");
        var create = await CreateDialogAsync(fixture);
        Input(create, "Parent folder").Text = roots[0].Root;
        Input(create, "File name").Text = "not-outside.md"; Pump(fixture);
        Assert.False(FindButton(create, "Create").IsEnabled);
        Assert.Single(fixture.Window.OwnedWindows);
        Assert.False(File.Exists(Path.Combine(roots[0].Root, "not-outside.md")));
        ClickButton(create, "Cancel");
        await UntilAsync(fixture, () => !fixture.Window.OwnedWindows.Any());
        Assert.Empty(fixture.Shell.Tabs);
    }

    private static async Task<WorkspaceSession[]> OpenRootsAsync(DesktopFixture fixture, int count)
    {
        var roots = new List<WorkspaceSession>();
        for (var i = 0; i < count; i++)
        {
            Assert.True(await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "Workspace " + (i + 1)));
            roots.Add(fixture.Shell.Workspace!);
        }
        await fixture.Window.ShowNavAsync("Files"); Pump(fixture);
        return roots.ToArray();
    }

    private static void ClickAdd(DesktopFixture fixture, string id)
    {
        var button = fixture.Window.FindControl<Button>("WorkspaceAddButton")!;
        Assert.True(button.IsEffectivelyVisible);
        var flyout = Assert.IsType<MenuFlyout>(button.Flyout);
        var command = Assert.Single(flyout.Items.OfType<MenuItem>(), item => Equals(item.Tag, id));
        Assert.True(command.IsEnabled, "The global +Add action must be available before a root is open.");
        command.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump(fixture);
    }

    private static async Task ChooseAsync(DesktopFixture fixture, string name)
    { var dialog = await OneDialogAsync(fixture); Assert.NotNull(FindButton(dialog, name)); ClickButton(dialog, name); Pump(fixture); }

    private static async Task<Window> CreateDialogAsync(DesktopFixture fixture)
    {
        await UntilAsync(fixture, () => fixture.Window.OwnedWindows.Any(w => w.GetVisualDescendants().OfType<TextBox>().Any(t => AutomationProperties.GetName(t) == "Parent folder")));
        return Assert.Single(fixture.Window.OwnedWindows);
    }

    private static async Task<Window> OneDialogAsync(DesktopFixture fixture)
    { await UntilAsync(fixture, () => fixture.Window.OwnedWindows.Any()); return Assert.Single(fixture.Window.OwnedWindows); }

    private static TextBox Input(Window dialog, string name) => Assert.Single(dialog.GetVisualDescendants().OfType<TextBox>(), box => AutomationProperties.GetName(box) == name);
    private static Button FindButton(Window dialog, string name) => Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetName(button) == name || Equals(button.Content, name));
    private static void ClickButton(Window dialog, string name) => FindButton(dialog, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static (ListBox List, SideRow Row) FindDraftRow(DesktopFixture fixture, Tern.App.Documents.DocumentTab draft)
    {
        var found = fixture.Window.GetVisualDescendants().OfType<ListBox>().Where(list => list.IsEffectivelyVisible)
            .SelectMany(list => list.Items.OfType<SideRow>().Where(row => ReferenceEquals(row.OpenTab, draft)).Select(row => (List: list, Row: row))).ToArray();
        return Assert.Single(found);
    }

    private static TreeRow[] VisibleTreeRoots(DesktopFixture fixture) => fixture.Window.GetVisualDescendants().OfType<TreeView>()
        .Where(tree => tree.IsEffectivelyVisible).SelectMany(tree => tree.Items.OfType<TreeRow>()).ToArray();

    private static async Task UntilAsync(DesktopFixture fixture, Func<bool> completed)
    {
        for (var attempt = 0; attempt < 160; attempt++)
        { Pump(fixture); if (completed()) return; await Task.Delay(10, TestContext.Current.CancellationToken); }
        Assert.True(completed(), "The action did not complete. Any unexpected second modal remains visible for this assertion.");
    }

    private static void Pump(DesktopFixture fixture)
    { Dispatcher.UIThread.RunJobs(); fixture.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
}
