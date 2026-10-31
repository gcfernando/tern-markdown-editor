using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Tern.App.Commands;
using Tern.App.Shell;
using Tern.Desktop.ViewModels;
using Tern.Domain.Text;

namespace Tern.Desktop.Tests;

/// <summary>Independent UI regression for equivalent root spellings and the resulting close ownership.</summary>
public sealed class CanonicalWorkspaceWindowTests
{
    [AvaloniaFact]
    public async Task Trailing_separator_alias_reuses_selector_and_tree_then_prompts_for_its_dirty_document()
    {
        using var fixture = new DesktopFixture();
        var path = await fixture.WriteAsync("canonical.md", "# Canonical\n");
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(fixture.Docs + Path.DirectorySeparatorChar));
        var original = fixture.Shell.Workspace!;
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "unsaved ")]);
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(fixture.Docs));
        await fixture.Window.ShowNavAsync("Files");
        Dispatcher.UIThread.RunJobs();

        var selector = fixture.Window.FindControl<ComboBox>("WorkspaceSelector")!;
        Assert.Same(original, Assert.Single(selector.Items.OfType<WorkspaceSession>()));
        Assert.False(selector.IsVisible); // Visible root sections replace the previous active-root dropdown.
        Assert.Same(original, fixture.Shell.Workspace);
        var row = Assert.Single(fixture.Window.FileTree.Items.OfType<TreeRow>());
        Assert.True(row.IsWorkspace); // Even a single workspace retains its collapse and close controls.
        Assert.Equal(original.Root, row.FullPath);
        Assert.Equal(path, Assert.Single(row.Children).FullPath);
        Assert.Contains("1 workspace(s) open", fixture.Window.WorkspaceCount.Text, StringComparison.Ordinal);

        fixture.Dialogs.SaveChoice = SaveChoice.Cancel;
        await fixture.Window.RunCommandAsync(CommandIds.CloseWorkspace);
        Assert.Equal(1, fixture.Dialogs.SavePrompts);
        Assert.Same(original, Assert.Single(fixture.Shell.OpenWorkspaces));
        Assert.Contains(tab, fixture.Shell.Tabs);
        Assert.Equal("unsaved # Canonical\n", tab.CurrentText);
        fixture.Dialogs.SaveChoice = SaveChoice.Discard;
        await fixture.Window.RunCommandAsync(CommandIds.CloseWorkspace);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(fixture.Shell.OpenWorkspaces);
        Assert.False(fixture.Window.WorkspaceCard.IsVisible);
        Assert.Equal("# Canonical\n", await File.ReadAllTextAsync(path));
    }
}
