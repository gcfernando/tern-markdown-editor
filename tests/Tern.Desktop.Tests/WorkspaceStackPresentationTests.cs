using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.ViewModels;
using Tern.Workspaces;
using Tern.App.Commands;

namespace Tern.Desktop.Tests;

public sealed class WorkspaceStackPresentationTests
{
    [AvaloniaFact]
    public async Task Restoring_library_does_not_expand_a_collapsed_workspace_with_a_selected_document()
    {
        using var fixture = new DesktopFixture();
        await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "Company");
        var company = fixture.Shell.Workspace!;
        await fixture.Window.CreateWorkspaceEntryAsync(false, "company.md");
        await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "Diagrams");
        var diagrams = fixture.Shell.Workspace!;
        await fixture.Window.CreateWorkspaceEntryAsync(false, "diagram.md");
        var notes = Path.Combine(fixture.Docs, "Notes");
        Directory.CreateDirectory(notes);
        Assert.True(await fixture.Shell.OpenFolderAsync(notes));
        await fixture.Window.ShowNavAsync("Files");
        Settle(fixture);
        var companyRow = Assert.Single(fixture.Window.FileTree.Items.OfType<TreeRow>(), row => WorkspacePaths.PathsEqual(row.FullPath, company.Root));
        fixture.Window.FileTree.SelectedItem = Assert.Single(companyRow.Children, row => row.Name == "company.md");
        Settle(fixture);
        companyRow = Assert.Single(fixture.Window.FileTree.Items.OfType<TreeRow>(), row => WorkspacePaths.PathsEqual(row.FullPath, company.Root));
        var selectedDocument = Assert.Single(companyRow.Children, row => row.Name == "company.md");
        fixture.Window.FileTree.SelectedItem = selectedDocument;
        fixture.Window.FileTree.Focus();
        Settle(fixture);
        Assert.Same(selectedDocument, fixture.Window.FileTree.SelectedItem);
        companyRow = Assert.Single(fixture.Window.FileTree.Items.OfType<TreeRow>(), row => WorkspacePaths.PathsEqual(row.FullPath, company.Root));
        var collapse = Assert.Single(fixture.Window.FileTree.GetVisualDescendants().OfType<Button>(), button =>
            Equals(button.Content, "Collapse") && ReferenceEquals(button.DataContext, companyRow));
        collapse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(fixture);
        Assert.False(companyRow.IsExpanded);
        await fixture.Window.RunCommandAsync(CommandIds.ToggleSidebar);
        Settle(fixture);
        var restore = fixture.Window.FindControl<Button>("RestoreLibraryButton")!;
        Assert.True(restore.IsEffectivelyVisible);
        restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(fixture);
        var rows = fixture.Window.FileTree.Items.OfType<TreeRow>().ToArray();
        Assert.False(Assert.Single(rows, row => WorkspacePaths.PathsEqual(row.FullPath, company.Root)).IsExpanded);
        Assert.True(Assert.Single(rows, row => WorkspacePaths.PathsEqual(row.FullPath, diagrams.Root)).IsExpanded);
        Assert.True(Assert.Single(rows, row => WorkspacePaths.PathsEqual(row.FullPath, notes)).IsExpanded);
        Assert.True(fixture.Window.WorkspaceAddButton.IsFocused);
    }

    [AvaloniaFact]
    public async Task Browsed_parent_discovers_closed_named_children_without_converting_the_parent()
    {
        using var fixture = new DesktopFixture();
        await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "Company");
        var companyRoot = fixture.Shell.Workspace!.Root;
        await fixture.Window.CreateWorkspaceAtAsync(companyRoot, "HR");
        Assert.True(await fixture.Shell.CloseAllWorkspacesAsync());
        fixture.Shell.ClearRecentHistory();
        Assert.True(await fixture.Shell.OpenFolderAsync(fixture.Docs));
        await fixture.Window.ShowNavAsync("Files");
        Settle(fixture);
        Assert.False(fixture.Shell.HasWorkspace);
        var parent = Assert.Single(fixture.Window.FileTree.Items.OfType<TreeRow>());
        Assert.True(parent.IsSection);
        Assert.False(parent.IsWorkspace);
        var company = Assert.Single(parent.Children, row => row.Name == "Company");
        Assert.True(company.IsWorkspace);
        Assert.True(company.IsSection);
        Assert.False(company.IsSectionOpen);
        Assert.True(Assert.Single(company.Children, row => row.Name == "HR").IsWorkspace);
        var card = Assert.Single(fixture.Window.FileTree.GetVisualDescendants().OfType<Border>(),
            border => border.Name == "WorkspaceSectionCard" && ReferenceEquals(border.DataContext, company));
        Assert.False(Assert.Single(card.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetName(button) == "Close this Library section").IsVisible);
        var open = Assert.Single(card.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetName(button) == "Select this Library section");
        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var attempt = 0; attempt < 100 && !fixture.Shell.HasWorkspace; attempt++)
        { await Task.Delay(10); Settle(fixture); }
        Assert.True(fixture.Shell.HasWorkspace);
        Assert.True(WorkspacePaths.PathsEqual(companyRoot, fixture.Shell.Workspace!.Root));
        Assert.False(File.Exists(Path.Combine(fixture.Docs, WorkspaceDefinition.FileName)));
    }

    [AvaloniaFact]
    public async Task Independent_workspace_cards_show_own_paths_counts_and_expansion_without_active_root_dropdown()
    {
        using var fixture = new DesktopFixture();
        await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "Company");
        var company = fixture.Shell.Workspace!;
        await fixture.Window.CreateWorkspaceEntryAsync(false, "company-note.md");
        await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "Diagrams");
        var diagrams = fixture.Shell.Workspace!;
        await fixture.Window.CreateWorkspaceEntryAsync(false, "flowchart.md");
        await fixture.Window.ShowNavAsync("Files");
        Settle(fixture);

        Assert.False(fixture.Window.WorkspaceCard.IsVisible);
        Assert.False(fixture.Window.FindControl<ComboBox>("WorkspaceSelector")!.IsVisible);
        var cards = fixture.Window.FileTree.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Name == "WorkspaceSectionCard" && border.IsEffectivelyVisible).ToArray();
        Assert.Equal(2, cards.Length);
        foreach (var root in new[] { company, diagrams })
        {
            var card = Assert.Single(cards, border => border.DataContext is TreeRow row && WorkspacePaths.PathsEqual(row.FullPath, root.Root));
            Assert.Equal("Workspace section: " + root.Name, AutomationProperties.GetName(card));
            var texts = card.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToArray();
            Assert.Contains(root.Name, texts);
            Assert.Contains(root.Root, texts);
            Assert.Contains(texts, value => value is not null && value.StartsWith("1 document(s)", StringComparison.Ordinal));
        }
        var companyCard = Assert.Single(cards, border => border.DataContext is TreeRow row && WorkspacePaths.PathsEqual(row.FullPath, company.Root));
        var diagramsCard = Assert.Single(cards, border => border.DataContext is TreeRow row && WorkspacePaths.PathsEqual(row.FullPath, diagrams.Root));
        Assert.True(diagramsCard.TranslatePoint(default, fixture.Window)!.Value.Y > companyCard.TranslatePoint(default, fixture.Window)!.Value.Y + companyCard.Bounds.Height);
        var companyRow = Assert.IsType<TreeRow>(companyCard.DataContext);
        var diagramsRow = Assert.IsType<TreeRow>(diagramsCard.DataContext);
        Assert.True(companyRow.IsExpanded);
        Assert.True(diagramsRow.IsExpanded);
        var collapse = Assert.Single(companyCard.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Collapse"));
        collapse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(fixture);
        Assert.False(companyRow.IsExpanded);
        Assert.True(diagramsRow.IsExpanded);
        Assert.True(companyCard.IsEffectivelyVisible);
        Assert.True(diagramsCard.IsEffectivelyVisible);
        var select = Assert.Single(companyCard.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetName(button) == "Select this Library section");
        select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(fixture);
        Assert.Same(company, fixture.Shell.Workspace);
        var currentRoots = fixture.Window.FileTree.Items.OfType<TreeRow>().ToArray();
        Assert.True(Assert.Single(currentRoots, row => WorkspacePaths.PathsEqual(row.FullPath, company.Root)).IsSelected);
        Assert.False(Assert.Single(currentRoots, row => WorkspacePaths.PathsEqual(row.FullPath, diagrams.Root)).IsSelected);
        var evidence = Path.Combine(Screenshots.Folder, "oct29-followup");
        Directory.CreateDirectory(evidence);
        using var bitmap = fixture.Window.CaptureRenderedFrame();
        Assert.NotNull(bitmap);
        using var stream = File.Create(Path.Combine(evidence, "workspace-stack.png"));
        bitmap.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    [AvaloniaFact]
    public async Task Outside_open_file_remains_visible_when_history_is_cleared_and_workspace_stays_selected()
    {
        using var fixture = new DesktopFixture();
        await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "Company");
        var workspace = fixture.Shell.Workspace!;
        var outside = await fixture.WriteAsync("outside.md", "# Separate file");
        var tab = (await fixture.Shell.OpenFileAsync(outside))!;
        fixture.Shell.ClearRecentHistory();
        fixture.Window.RefreshLibraryContents();
        await fixture.Window.ShowNavAsync("Files");
        Settle(fixture);
        Assert.Same(workspace, fixture.Shell.Workspace);
        var list = fixture.Window.FindControl<ListBox>("SideList")!;
        Assert.True(list.IsEffectivelyVisible);
        Assert.True(fixture.Window.FindControl<TextBlock>("OutsideLibraryHeader")!.IsEffectivelyVisible);
        var row = Assert.Single(list.Items.OfType<SideRow>());
        Assert.True(WorkspacePaths.PathsEqual(outside, row.Path!));
        Assert.Same(tab, row.OpenTab);
        Assert.Equal(Path.GetDirectoryName(outside), row.Detail);
        Assert.Empty(fixture.Shell.Recent.Files);
    }

    [AvaloniaFact]
    public async Task Loose_draft_is_visible_without_any_workspace_and_activates_from_the_outside_list()
    {
        using var fixture = new DesktopFixture();
        var draft = fixture.Shell.NewDocument();
        draft.IsStandaloneDraft = true;
        fixture.Window.RefreshLibraryContents();
        await fixture.Window.ShowNavAsync("Files");
        Settle(fixture);
        Assert.Empty(fixture.Shell.OpenWorkspaces);
        var list = fixture.Window.FindControl<ListBox>("SideList")!;
        Assert.True(list.IsEffectivelyVisible);
        Assert.True(fixture.Window.FindControl<TextBlock>("OutsideLibraryHeader")!.IsEffectivelyVisible);
        Assert.False(fixture.Window.FindControl<TextBlock>("NoWorkspaceText")!.IsVisible);
        var row = Assert.Single(list.Items.OfType<SideRow>());
        Assert.Same(draft, row.OpenTab);
        fixture.Shell.ActiveTab = null;
        list.SelectedItem = row;
        Assert.Same(draft, fixture.Shell.ActiveTab);
    }

    private static void Settle(DesktopFixture fixture)
    {
        Dispatcher.UIThread.RunJobs();
        fixture.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
