using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Shell;
using Tern.Desktop.ViewModels;
using Tern.Desktop.Views;
using Tern.Workspaces;

namespace Tern.Desktop.Tests;

public sealed class RecentWorkspaceWorkflowTests
{
    [AvaloniaFact]
    public async Task Recent_workspace_survives_restart_and_click_restores_context_without_duplicates()
    {
        using var fixture = new DesktopFixture();
        Assert.True(await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "Company"));
        var root = fixture.Shell.Workspace!.Root;
        var path = Path.Combine(root, "HR", "People.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "# People\n\nSaved content\n");
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        fixture.Shell.RememberViewState(tab, 5, 0);
        Assert.True(await fixture.Shell.CloseAllWorkspacesAsync());
        using var shell = new ShellViewModel(AppPaths.Under(fixture.AppData), new ScriptedDialogs(), new NullLauncher());
        var window = new MainWindow(shell);
        window.Show();
        try
        {
            await window.ShowNavAsync("Recent"); Pump(window);
            var list = window.FindControl<ListBox>("SideList")!;
            var row = Assert.Single(list.Items.OfType<SideRow>(), r => r.IsWorkspace);
            Assert.Equal("Company", row.Title);
            Assert.Contains(root, row.Detail);
            Assert.False(row.CanRename);
            list.SelectedItem = row;
            await Until(window, () => shell.ActiveTab?.FilePath == path);
            Assert.True(shell.Workspace!.IsNamedWorkspace);
            Assert.Contains(shell.Workspace.Snapshot.Files, file => WorkspacePaths.PathsEqual(file.FullPath, path));
            Assert.Equal(5, shell.GetViewState(path)!.CaretOffset);
            Assert.Equal("Files", window.ActiveNav);
            await window.ShowNavAsync("Recent"); Pump(window);
            list.SelectedItem = Assert.Single(list.Items.OfType<SideRow>(), r => r.IsWorkspace);
            await Until(window, () => window.ActiveNav == "Files");
            Assert.Single(shell.OpenWorkspaces);
            Assert.Single(shell.Tabs);
        }
        finally { window.Close(); Pump(window); }
    }

    [AvaloniaFact]
    public async Task Recent_workspace_menu_removes_only_history_and_never_deletes_or_closes()
    {
        using var fixture = new DesktopFixture();
        Assert.True(await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "Company"));
        var workspace = fixture.Shell.Workspace!;
        await fixture.Window.ShowNavAsync("Recent"); Pump(fixture.Window);
        var list = fixture.Window.FindControl<ListBox>("SideList")!;
        var row = Assert.Single(list.Items.OfType<SideRow>(), r => r.IsWorkspace);
        var container = Assert.IsType<ListBoxItem>(list.ContainerFromItem(row));
        var menuOwner = Assert.Single(container.GetVisualDescendants().OfType<Grid>(), c => c.ContextMenu is not null);
        var menu = menuOwner.ContextMenu!;
        menu.Open(menuOwner); Pump(fixture.Window);
        Assert.Equal(["Open Workspace", "Remove from Recent"], menu.Items.OfType<MenuItem>().Where(i => i.IsVisible).Select(i => i.Header?.ToString() ?? string.Empty).ToArray());
        Assert.Single(menu.Items.OfType<MenuItem>(), i => Equals(i.Header, "Remove from Recent")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close(); Pump(fixture.Window);
        Assert.Empty(fixture.Shell.Recent.Workspaces);
        Assert.Empty(new RecentItemsStore(fixture.Shell.Paths.StateDirectory).Workspaces);
        Assert.Same(workspace, Assert.Single(fixture.Shell.OpenWorkspaces));
        Assert.True(File.Exists(Path.Combine(workspace.Root, WorkspaceDefinition.FileName)));
    }

    [AvaloniaFact]
    public async Task Missing_recent_workspace_remains_visible_and_reports_a_retryable_failure()
    {
        using var fixture = new DesktopFixture();
        var path = Path.Combine(fixture.Docs, "Unavailable");
        fixture.Shell.Recent.AddWorkspace(path);
        await fixture.Window.ShowNavAsync("Recent"); Pump(fixture.Window);
        var list = fixture.Window.FindControl<ListBox>("SideList")!;
        var row = Assert.Single(list.Items.OfType<SideRow>(), r => r.IsWorkspace);
        Assert.True(row.IsMissing);
        list.SelectedItem = row;
        await Until(fixture.Window, () => fixture.Shell.StatusMessage.StartsWith("Could not open workspace", StringComparison.Ordinal));
        Assert.Empty(fixture.Shell.OpenWorkspaces);
        Assert.Single(fixture.Shell.Recent.Workspaces);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Opening_workspace_as_folder_requires_explicit_workspace_choice(bool accept)
    {
        using var fixture = new DesktopFixture();
        Assert.True(await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "Company"));
        var workspace = fixture.Shell.Workspace!;
        var task = fixture.Window.OpenFolderSelectionAsync(workspace.Root);
        await Until(fixture.Window, () => fixture.Window.OwnedWindows.OfType<PromptWindow>().Any());
        Assert.Same(workspace, Assert.Single(fixture.Shell.OpenWorkspaces));
        Assert.True(workspace.IsNamedWorkspace);
        var prompt = Assert.Single(fixture.Window.OwnedWindows.OfType<PromptWindow>());
        var button = Assert.Single(prompt.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, accept ? "Open Workspace" : "Cancel"));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await task;
        Assert.Same(workspace, Assert.Single(fixture.Shell.OpenWorkspaces));
        Assert.True(workspace.IsNamedWorkspace);
        Assert.False(await fixture.Shell.OpenFolderAsync(workspace.Root));
        Assert.True(workspace.IsNamedWorkspace);
    }

    [AvaloniaFact]
    public async Task Workspace_and_folder_sections_use_distinct_icons()
    {
        using var fixture = new DesktopFixture();
        Assert.True(await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "Company"));
        var plain = Path.Combine(fixture.Docs, "Plain"); Directory.CreateDirectory(plain);
        Assert.True(await fixture.Shell.OpenFolderAsync(plain));
        fixture.Window.RefreshLibraryContents(); Pump(fixture.Window);
        var rows = fixture.Window.FileTree.Items.OfType<TreeRow>().ToArray();
        var named = Assert.Single(rows, row => row.IsWorkspace);
        var folder = Assert.Single(rows, row => !row.IsWorkspace);
        Assert.NotEqual(named.IconPath, folder.IconPath);
        Assert.Equal("Workspace", named.SectionIconName);
        Assert.Equal("Folder", folder.SectionIconName);
        await fixture.Window.ShowNavAsync("Recent"); Pump(fixture.Window);
        Assert.Equal(named.IconPath, Assert.Single(fixture.Window.FindControl<ListBox>("SideList")!.Items.OfType<SideRow>(), r => r.IsWorkspace).IconPath);
    }

    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Window window, Func<bool> complete)
    {
        for (var i = 0; i < 200; i++) { Pump(window); if (complete()) return; await Task.Delay(10, TestContext.Current.CancellationToken); }
        Assert.True(complete());
    }
}
