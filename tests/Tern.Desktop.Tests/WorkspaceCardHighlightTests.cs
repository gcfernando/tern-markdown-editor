using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.ViewModels;
using Tern.Workspaces;

namespace Tern.Desktop.Tests;

public sealed class WorkspaceCardHighlightTests
{
    [AvaloniaFact]
    public async Task Nested_workspace_open_loads_all_root_branches_and_reveals_the_selected_path()
    {
        using var fixture = new DesktopFixture();
        var root = Path.Combine(fixture.Docs, "Root");
        var nested = Path.Combine(root, "Folder1", "Workspace2");
        Directory.CreateDirectory(nested);
        WorkspaceDefinition.Create(root, "Root");
        WorkspaceDefinition.Create(nested, "Workspace2");
        await File.WriteAllTextAsync(Path.Combine(root, "Sibling.md"), "# Sibling");
        await File.WriteAllTextAsync(Path.Combine(nested, "Deep.md"), "# Deep");
        fixture.Shell.Recent.AddWorkspace(nested);
        await fixture.Window.ShowNavAsync("Recent");
        var list = fixture.Window.FindControl<ListBox>("SideList")!;
        list.SelectedItem = Assert.Single(list.Items.OfType<SideRow>(), row => row.IsWorkspace);
        for (var i = 0; i < 200 && fixture.Window.ActiveNav != "Files"; i++)
        { Dispatcher.UIThread.RunJobs(); await Task.Delay(10, TestContext.Current.CancellationToken); }
        Assert.Equal(root, Assert.Single(fixture.Shell.OpenWorkspaces).Root);
        var outer = Assert.Single(fixture.Window.FileTree.Items.OfType<TreeRow>());
        Assert.Equal(root, outer.FullPath);
        Assert.Contains(outer.Children, row => row.Name == "Sibling.md");
        var folder = Assert.Single(outer.Children, row => row.Name == "Folder1");
        var child = Assert.Single(folder.Children);
        Assert.Equal(nested, child.FullPath);
        Assert.True(outer.IsExpanded); Assert.True(folder.IsExpanded); Assert.True(child.IsExpanded);
        Assert.Contains(child.Children, row => row.Name == "Deep.md");
        folder.IsExpanded = false;
        fixture.Window.RefreshLibraryContents();
        Assert.False(Assert.Single(Assert.Single(fixture.Window.FileTree.Items.OfType<TreeRow>()).Children, row => row.Name == "Folder1").IsExpanded);
    }

    [AvaloniaFact]
    public async Task Selected_and_hovered_workspace_keeps_only_its_rounded_card_highlight()
    {
        using var fixture = new DesktopFixture();
        Assert.True(await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "Company"));
        await fixture.Window.ShowNavAsync("Files");
        Dispatcher.UIThread.RunJobs(); fixture.Window.UpdateLayout();
        var row = Assert.Single(fixture.Window.FileTree.Items.OfType<TreeRow>());
        fixture.Window.FileTree.SelectedItem = row;
        Dispatcher.UIThread.RunJobs(); fixture.Window.UpdateLayout();
        var card = Assert.Single(fixture.Window.FileTree.GetVisualDescendants().OfType<Border>(), border => border.Name == "WorkspaceSectionCard" && border.IsEffectivelyVisible);
        var item = card.FindAncestorOfType<TreeViewItem>()!;
        var point = card.TranslatePoint(new Point(20, 20), fixture.Window)!.Value;
        fixture.Window.MouseMove(point);
        Dispatcher.UIThread.RunJobs(); fixture.Window.UpdateLayout();
        Assert.True(item.IsSelected);
        Assert.True(item.IsPointerOver);
        var layout = Assert.Single(item.GetVisualDescendants().OfType<Border>(), border => border.Name == "PART_LayoutRoot");
        Assert.Equal(0, Assert.IsAssignableFrom<ISolidColorBrush>(layout.Background).Color.A);
        Assert.True(card.CornerRadius.TopLeft > 0);
        Assert.True(row.IsSelected);
    }
}
