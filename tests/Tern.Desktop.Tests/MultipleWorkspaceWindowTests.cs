using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Automation;
using Tern.App.Commands;
using Tern.App.Shell;
using Tern.Desktop.ViewModels;
using Tern.Desktop.Views;
using Tern.Domain.Text;
using Tern.Rendering.Preview;
using Tern.Rendering.Resources;

namespace Tern.Desktop.Tests;

public sealed class MultipleWorkspaceWindowTests
{
    [AvaloniaFact]
    public async Task Attaching_owner_workspace_rebuilds_existing_preview_resource_decisions_without_edit_or_mode_change()
    {
        using var fixture = new DesktopFixture();
        var path = await fixture.WriteAsync("project/sub/document.md", "![Owner asset](../owned.png)\n");
        var project = Path.Combine(fixture.Docs, "project");
        var imagePath = Path.Combine(project, "owned.png");
        await File.WriteAllBytesAsync(imagePath, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a9l8AAAAASUVORK5CYII="));
        var document = (await fixture.Shell.OpenFileAsync(path))!;
        await fixture.Window.RebuildPreviewAsync();
        // Let the original debounce settle before changing ownership, so it cannot mask a missing invalidation.
        await Task.Delay(240);
        Settle(fixture);
        await fixture.Window.RebuildPreviewAsync();
        ImageNode Image() => Assert.Single(Assert.IsType<ParagraphNode>(Assert.Single(fixture.Window.PreviewControl.Document!.Blocks)).Inlines.OfType<ImageNode>());
        Assert.IsType<Blocked>(Image().Resolution);
        var revision = document.Session.Revision;
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(project));
        for (var attempt = 0; attempt < 40 && Image().Resolution is not LocalFile; attempt++)
        { Settle(fixture); await Task.Delay(25); }
        Assert.Equal(imagePath, Assert.IsType<LocalFile>(Image().Resolution).CanonicalPath);
        Assert.Equal(revision, document.Session.Revision);
        Assert.False(document.IsDirty);
        Assert.Equal("![Owner asset](../owned.png)\n", await File.ReadAllTextAsync(path));
    }

    [AvaloniaFact]
    public async Task Create_and_switch_workspaces_displays_separate_tree_roots_and_preserves_documents()
    {
        using var fixture = new DesktopFixture();
        Assert.True(await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "First"));
        var first = fixture.Shell.Workspace!;
        await fixture.Window.CreateWorkspaceEntryAsync(false, "first-note");
        var firstTab = fixture.Shell.ActiveTab!;
        firstTab.ApplyEdits([TextEdit.Insert(0, "unsaved first")]);
        Assert.True(await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "Second"));
        var second = fixture.Shell.Workspace!;
        await fixture.Window.ShowNavAsync("Files");
        Settle(fixture);

        var roots = fixture.Window.FileTree.Items.OfType<TreeRow>().ToArray();
        Assert.Equal(2, roots.Length);
        Assert.All(roots, row => Assert.True(row.IsWorkspace));
        Assert.Contains(roots, row => row.FullPath == first.Root);
        Assert.Contains(roots, row => row.FullPath == second.Root);
        var selector = fixture.Window.FindControl<ComboBox>("WorkspaceSelector")!;
        Assert.False(selector.IsVisible);
        Assert.False(fixture.Window.WorkspaceCard.IsVisible);
        var firstSectionSelect = Assert.Single(fixture.Window.FileTree.GetVisualDescendants().OfType<Button>(),
            button => AutomationProperties.GetName(button) == "Select this Library section"
                && button.DataContext is TreeRow row && row.FullPath == first.Root);
        firstSectionSelect.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle(fixture);
        Assert.Same(first, fixture.Shell.Workspace);
        Assert.Same(firstTab, fixture.Shell.ActiveTab);
        Assert.True(firstTab.IsDirty);
        Assert.Equal("unsaved first", firstTab.CurrentText);
        Assert.Equal(first.Root, fixture.Window.FindControl<TextBlock>("WorkspaceLocation")!.Text);
        Assert.False(await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "First"));
        Assert.Equal(2, fixture.Shell.OpenWorkspaces.Count);
    }

    [AvaloniaFact]
    public async Task Creating_file_in_selected_tree_root_uses_that_root_even_when_another_workspace_is_active()
    {
        using var fixture = new DesktopFixture();
        Assert.True(await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "First"));
        var first = fixture.Shell.Workspace!;
        Assert.True(await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "Second"));
        var second = fixture.Shell.Workspace!;
        fixture.Shell.SelectWorkspace(first);
        Settle(fixture);
        fixture.Window.FileTree.SelectedItem = Assert.Single(fixture.Window.FileTree.Items.OfType<TreeRow>(), row => row.FullPath == second.Root);
        await fixture.Window.CreateWorkspaceEntryAsync(false, "belongs-to-second");
        Settle(fixture);
        Assert.True(File.Exists(Path.Combine(second.Root, "belongs-to-second.md")));
        Assert.False(File.Exists(Path.Combine(first.Root, "belongs-to-second.md")));
        Assert.Same(second, fixture.Shell.Workspace);
        Assert.Equal(Path.Combine(second.Root, "belongs-to-second.md"), fixture.Shell.ActiveTab!.FilePath);
    }

    [AvaloniaFact]
    public async Task Close_all_workspace_command_cancels_safely_then_keeps_standalone_and_unnamed_documents()
    {
        using var fixture = new DesktopFixture();
        Assert.True(await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "First"));
        await fixture.Window.CreateWorkspaceEntryAsync(false, "workspace-note");
        var workspaceTab = fixture.Shell.ActiveTab!;
        workspaceTab.ApplyEdits([TextEdit.Insert(0, "workspace draft")]);
        var workspacePath = workspaceTab.FilePath!;
        Assert.True(await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "Second"));
        var standalonePath = await fixture.WriteAsync("standalone.md", "independent");
        var standalone = (await fixture.Shell.OpenFileAsync(standalonePath))!;
        var unnamed = fixture.Shell.NewDocument("keep unnamed");
        await fixture.Window.RunCommandAsync(CommandIds.CloseAllWorkspaces);
        Assert.Equal(2, fixture.Shell.OpenWorkspaces.Count);
        Assert.Contains(workspaceTab, fixture.Shell.Tabs);
        fixture.Dialogs.SaveChoice = SaveChoice.Discard;
        await fixture.Window.RunCommandAsync(CommandIds.CloseAllWorkspaces);
        Settle(fixture);
        Assert.Empty(fixture.Shell.OpenWorkspaces);
        Assert.Contains(standalone, fixture.Shell.Tabs);
        Assert.Contains(unnamed, fixture.Shell.Tabs);
        Assert.Equal(2, fixture.Shell.Tabs.Count);
        Assert.False(fixture.Window.WorkspaceCard.IsVisible);
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(workspacePath));
        Assert.Equal("independent", await File.ReadAllTextAsync(standalonePath));
        Assert.Equal("keep unnamed", unnamed.CurrentText);
    }

    [AvaloniaFact]
    public async Task Link_repair_uses_document_owner_and_refuses_an_unrelated_workspace_target()
    {
        using var fixture = new DesktopFixture();
        Assert.True(await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "First"));
        var first = fixture.Shell.Workspace!;
        var source = Path.Combine(first.Root, "source.md");
        var target = Path.Combine(first.Root, "correct.md");
        await File.WriteAllTextAsync(source, "[Target](missing.md)\n");
        await File.WriteAllTextAsync(target, "# Target\n");
        var document = (await fixture.Shell.OpenFileAsync(source))!;
        Assert.True(await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "Second"));
        var unrelated = Path.Combine(fixture.Shell.Workspace!.Root, "other.md");
        await File.WriteAllTextAsync(unrelated, "# Other\n");
        var row = new LinkRowView("Target", "missing.md", 1, true, "!");
        await fixture.Window.RepairLinkAsync(document, row, unrelated);
        Assert.Equal("[Target](missing.md)\n", document.CurrentText);
        await fixture.Window.RepairLinkAsync(document, row, target);
        Assert.Equal("[Target](correct.md)\n", document.CurrentText);
        Assert.True(document.IsDirty);
        Assert.Equal("[Target](missing.md)\n", await File.ReadAllTextAsync(source));
    }

    [AvaloniaFact]
    public async Task Preview_and_export_keep_document_owner_resources_when_another_workspace_is_selected()
    {
        using var fixture = new DesktopFixture();
        Assert.True(await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "First"));
        var first = fixture.Shell.Workspace!;
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a9l8AAAAASUVORK5CYII=");
        var imagePath = Path.Combine(first.Root, "owned.png");
        await File.WriteAllBytesAsync(imagePath, png);
        var documentPath = Path.Combine(first.Root, "document.md");
        await File.WriteAllTextAsync(documentPath, "![Owned image](owned.png)\n");
        var document = (await fixture.Shell.OpenFileAsync(documentPath))!;
        Assert.True(await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "Second"));
        var second = fixture.Shell.Workspace!;
        Assert.Same(document, fixture.Shell.ActiveTab);
        Assert.Same(second, fixture.Shell.Workspace);
        await fixture.Window.RebuildPreviewAsync();
        Settle(fixture);
        var paragraph = Assert.IsType<ParagraphNode>(Assert.Single(fixture.Window.PreviewControl.Document!.Blocks));
        var image = Assert.Single(paragraph.Inlines.OfType<ImageNode>());
        Assert.Equal(imagePath, Assert.IsType<LocalFile>(image.Resolution).CanonicalPath);

        var exportCommand = fixture.Window.RunCommandAsync(CommandIds.ExportHtml);
        await Task.Yield();
        Settle(fixture);
        var dialog = Assert.Single(fixture.Window.OwnedWindows.OfType<ExportWindow>());
        try
        {
            var output = Path.Combine(fixture.AppData, "owned-export.html");
            Assert.True(await dialog.ExportAsync(output));
            Assert.Contains(Convert.ToBase64String(png), await File.ReadAllTextAsync(output), StringComparison.Ordinal);
        }
        finally { dialog.Close(); await exportCommand; }
        Assert.False(document.IsDirty);
        Assert.Equal("![Owned image](owned.png)\n", document.CurrentText);
    }

    private static void Settle(DesktopFixture fixture)
    {
        fixture.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        fixture.Window.UpdateLayout();
    }
}
