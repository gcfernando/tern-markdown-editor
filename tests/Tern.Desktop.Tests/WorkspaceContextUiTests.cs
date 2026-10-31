using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Tern.App.Commands;

namespace Tern.Desktop.Tests;

public sealed class WorkspaceContextUiTests
{
    [AvaloniaFact]
    public async Task Workspace_close_captures_current_editor_position_before_reopening_saved_context()
    {
        using var fixture = new DesktopFixture();
        Assert.True(await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "Company"));
        var root = fixture.Shell.Workspace!.Root;
        var path = Path.Combine(root, "current-position.md");
        var text = "# Company\n\n" + new string('a', 200);
        await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);
        await fixture.Shell.OpenFileAsync(path);
        await fixture.Window.RunCommandAsync(CommandIds.ViewSource);
        fixture.Window.EditorControl.CaretOffset = 87;
        await fixture.Window.RunCommandAsync(CommandIds.CloseWorkspace);
        Assert.Empty(fixture.Shell.Tabs);
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(root));
        Dispatcher.UIThread.RunJobs(); fixture.Window.UpdateLayout();
        Assert.Equal(path, fixture.Shell.ActiveTab!.FilePath);
        Assert.Equal(87, fixture.Window.EditorControl.CaretOffset);
        Assert.False(fixture.Shell.ActiveTab.IsDirty);
        Assert.Equal(text, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }
}
