using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.ViewModels;
using Tern.Desktop.Views;
using Tern.Workspaces;

namespace Tern.Desktop.Tests;

public sealed class WorkspaceContextMenuTests
{
    [AvaloniaTheory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    public async Task Right_click_child_uses_its_visible_menu_without_opening_or_retargeting(bool directory, bool open, bool padding)
    {
        using var fixture = new DesktopFixture();
        var root = Path.Combine(fixture.Docs, "Company");
        var folder = Path.Combine(root, "Department");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(root, "Review.md");
        await File.WriteAllTextAsync(file, "# Review");
        WorkspaceDefinition.Create(root, "Company");
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(root));
        var company = fixture.Shell.Workspace!;
        fixture.Window.RefreshLibraryContents();
        if (open) await fixture.Shell.OpenFileAsync(file);
        await fixture.Window.ShowNavAsync("Files");
        Pump(fixture);
        Assert.Single(fixture.Window.FileTree.Items.OfType<TreeRow>()).IsExpanded = true;
        Pump(fixture);
        var target = directory ? folder : file;
        var container = Assert.Single(fixture.Window.FileTree.GetVisualDescendants().OfType<TreeViewItem>(),
            item => item.DataContext is TreeRow row && WorkspacePaths.PathsEqual(row.FullPath, target));
        var grid = Assert.Single(container.GetVisualDescendants().OfType<Grid>(), control =>
            control.ContextMenu is not null && control.IsEffectivelyVisible
            && control.FindAncestorOfType<TreeViewItem>() == container);
        var tab = fixture.Shell.ActiveTab;
        var tabs = fixture.Shell.Tabs.Count;
        var point = (padding ? (Control)container : grid).TranslatePoint(new Point(padding ? 2 : 35, 15), fixture.Window)!.Value;
        fixture.Window.MouseDown(point, MouseButton.Right);
        fixture.Window.MouseUp(point, MouseButton.Right);
        Pump(fixture);
        Assert.True(grid.ContextMenu!.IsOpen);
        var labels = grid.ContextMenu.Items.OfType<MenuItem>().Where(item => item.IsVisible).Select(item => item.Header?.ToString()).ToArray();
        Assert.Equal(directory
            ? ["Create File…", "Create Folder…", "Create Workspace…", "Close Folder Documents", "Collapse Folder"]
            : open
                ? ["Open", "Rename…", "Delete…", "Close all", "Close tabs to the right", "Close other tabs", "Close Document"]
                : ["Open", "Rename…", "Delete…", "Close all", "Close tabs to the right", "Close other tabs"], labels);
        Assert.Same(tab, fixture.Shell.ActiveTab);
        Assert.Equal(tabs, fixture.Shell.Tabs.Count);
        Assert.Same(company, fixture.Shell.Workspace);
        grid.ContextMenu.Close();
    }

    [AvaloniaTheory]
    [InlineData("file", "File name", "targeted.md")]
    [InlineData("workspace", "Folder name", "HR")]
    public async Task Right_click_workspace_name_creates_in_that_card_without_another_destination_question(string action, string fieldName, string itemName)
    {
        using var fixture = new DesktopFixture();
        Assert.True(await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "Company"));
        var company = fixture.Shell.Workspace!;
        Assert.True(await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "Diagrams"));
        var diagrams = fixture.Shell.Workspace!;
        await fixture.Window.ShowNavAsync("Files");
        Pump(fixture);
        var card = Assert.Single(fixture.Window.FileTree.GetVisualDescendants().OfType<Border>(), border => border.Name == "WorkspaceSectionCard"
            && border.DataContext is TreeRow row && WorkspacePaths.PathsEqual(row.FullPath, company.Root));
        var title = Assert.Single(card.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Company");
        var point = title.TranslatePoint(new Point(Math.Max(1, title.Bounds.Width / 2), Math.Max(1, title.Bounds.Height / 2)), fixture.Window)!.Value;
        fixture.Window.MouseDown(point, MouseButton.Right);
        fixture.Window.MouseUp(point, MouseButton.Right);
        Pump(fixture);
        var menu = Assert.IsType<ContextMenu>(card.ContextMenu);
        Assert.True(menu.IsOpen);
        Assert.Contains(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Create File…"));
        Assert.Contains(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Create Workspace…"));
        Assert.Same(diagrams, fixture.Shell.Workspace); // Merely asking for a context menu must not retarget the application.
        var command = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Tag, action));
        command.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        await UntilAsync(fixture, () => fixture.Window.OwnedWindows.Any());
        var dialog = Assert.IsType<FileNameWindow>(Assert.Single(fixture.Window.OwnedWindows));
        Assert.DoesNotContain(fixture.Window.OwnedWindows, window => window is AddDestinationWindow);
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == company.Root
            || text.Text is { } detail && detail.EndsWith(company.Root, StringComparison.Ordinal));
        var input = Assert.Single(dialog.GetVisualDescendants().OfType<TextBox>(), text => AutomationProperties.GetName(text) == fieldName);
        input.Text = itemName;
        var create = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Create"));
        create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var target = Path.Combine(company.Root, itemName);
        await UntilAsync(fixture, () => action == "file" ? File.Exists(target) : fixture.Shell.OpenWorkspaces.Any(workspace => WorkspacePaths.PathsEqual(workspace.Root, target)));
        Assert.False(File.Exists(Path.Combine(diagrams.Root, itemName)));
        Assert.False(Directory.Exists(Path.Combine(diagrams.Root, itemName)));
        if (action == "workspace") Assert.NotNull(WorkspaceDefinition.Load(target));
        Assert.Empty(fixture.Window.OwnedWindows);
    }

    private static async Task UntilAsync(DesktopFixture fixture, Func<bool> completed)
    {
        for (var attempt = 0; attempt < 160; attempt++)
        { Pump(fixture); if (completed()) return; await Task.Delay(10, TestContext.Current.CancellationToken); }
        Assert.True(completed(), "The contextual creation did not finish.");
    }

    private static void Pump(DesktopFixture fixture)
    { Dispatcher.UIThread.RunJobs(); fixture.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
}
