using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.Domain.Text;

namespace Tern.Desktop.Tests;

public sealed class IssueRegressionTests
{
    [AvaloniaFact]
    public async Task Command_policy_is_visible_and_rechecked_for_empty_new_and_deleted_content()
    {
        using var f = new DesktopFixture();
        Assert.False(f.Window.CanRunCommand(CommandIds.Save));
        Assert.False(f.Window.CanRunCommand(CommandIds.SaveAs));
        await f.Window.RunCommandAsync(CommandIds.ViewReading);
        Assert.Null(f.Shell.ActiveTab);
        var draft = f.Shell.NewDocument();
        Assert.True(f.Window.CanRunCommand(CommandIds.Save));
        draft.ApplyEdits([TextEdit.Insert(0, "draft")]);
        Assert.True(f.Window.CanRunCommand(CommandIds.Save));
        var path = await f.WriteAsync("delete.md", "saved");
        var saved = (await f.Shell.OpenFileAsync(path))!;
        saved.ApplyEdits([TextEdit.Delete(0, saved.CurrentText.Length)]);
        Assert.True(f.Window.CanRunCommand(CommandIds.Save));
        await f.Window.RunCommandAsync(CommandIds.Save);
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(path));
        Assert.False(saved.IsDirty);
        Assert.False(f.Window.CanRunCommand(CommandIds.Save));
    }

    [AvaloniaFact]
    public async Task Rapid_tab_and_view_changes_keep_current_preview_and_clear_history_without_closing_tabs()
    {
        using var f = new DesktopFixture();
        var first = await f.WriteAsync("first.md", "# First\n" + new string('x', 100000));
        var second = await f.WriteAsync("second.md", "# Second\n\nCurrent document");
        await f.Shell.OpenFileAsync(first);
        foreach (var mode in new[] { CommandIds.ViewReading, CommandIds.ViewSplit, CommandIds.ViewSource })
        { await f.Window.RunCommandAsync(mode); await f.Shell.OpenFileAsync(second); }
        await f.Window.RunCommandAsync(CommandIds.ViewSplit);
        await f.Window.RebuildPreviewAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(second, f.Shell.ActiveTab!.FilePath);
        Assert.Equal(f.Shell.ActiveTab.CurrentText.Length, f.Window.PreviewControl.Document!.SourceLength);
        await f.Window.ShowNavAsync("Recent");
        await f.Window.RunCommandAsync(CommandIds.ClearRecent);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(f.Shell.Recent.Files);
        Assert.Equal(2, f.Shell.Tabs.Count);
        Assert.True(File.Exists(first)); Assert.True(File.Exists(second));
    }

    [AvaloniaFact]
    public async Task Capture_current_main_empty_workspace_and_reset_surfaces()
    {
        using var f = new DesktopFixture();
        var output = Path.Combine(Screenshots.Folder, "issues-v1"); Directory.CreateDirectory(output);
        Capture(f.Window, Path.Combine(output, "main-empty.png"));
        var path = await f.WriteAsync("Welcome.md", "---\ntags: [writing, notes]\n---\n# A clearer writing desk\n\nWrite in **Markdown**, read alongside your source.\n\n## Next steps\n\n- Open files without a workspace\n- Search features with F1\n- Save your work\n\n[Read the guide](guide.md)\n");
        await f.Shell.OpenWorkspaceAsync(f.Docs);
        await f.Shell.OpenFileAsync(path);
        await f.Window.RebuildPreviewAsync();
        Capture(f.Window, Path.Combine(output, "main-workspace.png"));
        foreach (var section in f.Window.InspectorContent.Items.OfType<TabItem>())
        {
            f.Window.InspectorContent.SelectedItem = section;
            Capture(f.Window, Path.Combine(output, "inspector-" + section.Header!.ToString()!.ToLowerInvariant() + ".png"));
        }
        f.Shell.Settings.Theme = Tern.App.Settings.ThemeChoice.Midnight; f.Window.ApplyTheme();
        f.Window.InspectorContent.SelectedIndex = 0;
        Capture(f.Window, Path.Combine(output, "main-workspace-midnight.png"));
        var reset = new Tern.Desktop.Views.ResetWindow(f.Shell); reset.Show();
        Capture(reset, Path.Combine(output, "reset.png")); reset.Close();
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        using var stream = File.Create(path); frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
