using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Threading;
using Tern.App.Commands;
using Tern.Desktop.ViewModels;

namespace Tern.Desktop.Tests;

public sealed class FolderWorkspaceHierarchyTests
{
    [AvaloniaFact]
    public async Task Dropped_plain_directory_stays_a_folder_without_workspace_metadata()
    {
        using var fixture = new DesktopFixture();
        var note = await fixture.WriteAsync("inside/note.md", "content");
        await fixture.Window.OpenDroppedAsync([Path.GetDirectoryName(note)!]);
        Assert.False(fixture.Shell.HasWorkspace);
        Assert.False(Assert.Single(fixture.Shell.OpenWorkspaces).IsNamedWorkspace);
        Assert.Contains(fixture.Shell.Workspace!.Snapshot.Files, file => Tern.Workspaces.WorkspacePaths.PathsEqual(file.FullPath, note));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(note)!, Tern.Workspaces.WorkspaceDefinition.FileName)));
    }

    [AvaloniaFact]
    public async Task Nested_workspaces_have_one_parent_tree_with_named_HR_child_and_independent_stack()
    {
        using var fixture = new DesktopFixture();
        await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "Company");
        var company = fixture.Shell.Workspace!;
        await fixture.Window.CreateWorkspaceAtAsync(company.Root, "HR");
        var hr = fixture.Shell.Workspace!;
        await company.RescanTreeAsync();
        fixture.Shell.SelectWorkspace(company);
        fixture.Shell.SelectWorkspace(hr);
        await fixture.Window.ShowNavAsync("Files");
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(Assert.Single(fixture.Window.FileTree.Items.OfType<TreeRow>()).Children, row => Tern.Workspaces.WorkspacePaths.PathsEqual(row.FullPath, hr.Root));
        await fixture.Window.CreateWorkspaceEntryAsync(false, "people.md");
        await company.RescanTreeAsync();
        await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "Diagrams");
        await fixture.Window.ShowNavAsync("Files");
        Dispatcher.UIThread.RunJobs();
        var roots = fixture.Window.FileTree.Items.OfType<TreeRow>().ToArray();
        Assert.Equal(2, roots.Length);
        var companyRow = Assert.Single(roots, row => Tern.Workspaces.WorkspacePaths.PathsEqual(row.FullPath, company.Root));
        var hrRow = Assert.Single(companyRow.Children, row => Tern.Workspaces.WorkspacePaths.PathsEqual(row.FullPath, hr.Root));
        Assert.True(hrRow.IsWorkspace);
        Assert.Contains(hrRow.Children, row => row.Name == "people.md");
        Assert.DoesNotContain(roots, row => Tern.Workspaces.WorkspacePaths.PathsEqual(row.FullPath, hr.Root));
        fixture.Window.FileTree.SelectedItem = hrRow;
        Assert.Same(hr, fixture.Shell.Workspace);
        fixture.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var evidence = Path.Combine(Screenshots.Folder, "oct29");
        Directory.CreateDirectory(evidence);
        using var frame = fixture.Window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var stream = File.Create(Path.Combine(evidence, "nested-workspaces.png"));
        frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    [AvaloniaFact]
    public async Task Plain_folder_displays_descendants_and_disables_workspace_search_but_keeps_creation_available()
    {
        using var fixture = new DesktopFixture();
        var note = await fixture.WriteAsync("Company/HR/note.md", "# People");
        Assert.True(await fixture.Shell.OpenFolderAsync(fixture.Docs));
        await fixture.Window.ShowNavAsync("Files");
        Dispatcher.UIThread.RunJobs();
        var rows = fixture.Window.FileTree.Items.OfType<TreeRow>().SelectMany(row => row.Flatten()).ToArray();
        Assert.Contains(rows, row => Tern.Workspaces.WorkspacePaths.PathsEqual(row.FullPath, note));
        Assert.DoesNotContain(rows, row => row.IsWorkspace);
        Assert.False(fixture.Window.CanRunCommand(CommandIds.FindInWorkspace));
        Assert.True(fixture.Window.CanRunCommand(CommandIds.NewFolder));
        Assert.Contains("Folder ·", fixture.Window.WorkspaceTitle.Text, StringComparison.Ordinal);
    }
}
