using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Tern.App.Shell;
using Tern.Desktop.ViewModels;
using Tern.Domain.Text;

namespace Tern.Desktop.Tests;

public sealed class DuplicateTaskOverlayTests
{
    [AvaloniaFact]
    public async Task Duplicate_local_tabs_scan_tasks_without_duplicate_keys_and_follow_the_active_buffer()
    {
        using var fixture = new DesktopFixture();
        const string original = "# Original\r\n- [ ] Original task\r\n";
        var path = await fixture.WriteAsync("source.md", original);
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(fixture.Docs));
        var first = (await fixture.Shell.OpenFileAsync(path))!;
        first.ApplyEdits([new TextEdit(0, first.CurrentText.Length, "# First buffer\r\n- [ ] First task\r\n")]);
        var second = (await fixture.Shell.OpenFileAsync(path, duplicateBehavior: DuplicateOpenBehavior.NewTab))!;
        second.ApplyEdits([new TextEdit(0, second.CurrentText.Length, "# Second buffer\r\n- [ ] Second task\r\n")]);

        await fixture.Window.ShowNavAsync("Tasks");
        Pump(fixture);

        Assert.Equal("Second task", Assert.Single(fixture.Window.FindControl<ListBox>("SideList")!.Items.OfType<SideRow>()).Title);
        fixture.Shell.ActiveTab = first;
        Pump(fixture);
        await fixture.Window.ShowNavAsync("Tasks");
        Assert.Equal("First task", Assert.Single(fixture.Window.FindControl<ListBox>("SideList")!.Items.OfType<SideRow>()).Title);
        Assert.Equal("First buffer", Assert.Single(fixture.Shell.Workspace!.Graph.GetHeadings()).Heading.Text);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.True(first.IsDirty);
        Assert.True(second.IsDirty);
        Assert.Equal(2, fixture.Shell.Tabs.Count);
    }

    [AvaloniaFact]
    public async Task Saving_or_closing_one_duplicate_keeps_the_other_unsaved_buffer_in_workspace_graph()
    {
        using var fixture = new DesktopFixture();
        var path = await fixture.WriteAsync("source.md", "# Original\r\n");
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(fixture.Docs));
        var first = (await fixture.Shell.OpenFileAsync(path))!;
        first.ApplyEdits([new TextEdit(0, first.CurrentText.Length, "# First unsaved\r\n")]);
        var second = (await fixture.Shell.OpenFileAsync(path, duplicateBehavior: DuplicateOpenBehavior.NewTab))!;
        second.ApplyEdits([new TextEdit(0, second.CurrentText.Length, "# Second saved\r\n")]);
        fixture.Shell.ActiveTab = first;
        Pump(fixture);

        Assert.True(await fixture.Shell.SaveTabAsync(second));
        Pump(fixture);

        Assert.False(second.IsDirty);
        Assert.True(first.IsDirty);
        Assert.Equal("# Second saved\r\n", await File.ReadAllTextAsync(path));
        Assert.Equal("First unsaved", Assert.Single(fixture.Shell.Workspace!.Graph.GetHeadings()).Heading.Text);
        Assert.True(await fixture.Shell.CloseTabAsync(second));
        Assert.Same(first, Assert.Single(fixture.Shell.Tabs));
        Assert.True(first.IsDirty);
        Assert.Equal("First unsaved", Assert.Single(fixture.Shell.Workspace.Graph.GetHeadings()).Heading.Text);
        Assert.Equal("# First unsaved\r\n", first.CurrentText);
    }

    private static void Pump(DesktopFixture fixture)
    { Dispatcher.UIThread.RunJobs(); fixture.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
}
