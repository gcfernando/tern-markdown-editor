using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Tern.App.Commands;
using Tern.App.Shell;

namespace Tern.Desktop.Tests;

public sealed class DisabledCommandFeedbackTests
{
    [AvaloniaTheory]
    [InlineData(CommandIds.Replace, false, "document")]
    [InlineData(CommandIds.ShowHistory, true, "Save")]
    [InlineData(CommandIds.Undo, true, "undo")]
    public async Task Unavailable_commands_explain_why_in_status_and_Activity(string id, bool draft, string reason)
    {
        using var f = new DesktopFixture();
        if (draft) f.Shell.NewDocument("Keep this text");
        f.Shell.SetStatus("Before the shortcut", StatusSeverity.Success);
        await f.Window.RunCommandAsync(id);
        Assert.Contains(reason, f.Shell.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StatusSeverity.Info, f.Shell.StatusSeverity);
        Assert.Contains(f.Shell.Activities.Items, item => item.Phase == f.Shell.StatusMessage && item.Severity == ActivitySeverity.Neutral);
        if (draft) Assert.Equal("Keep this text", f.Shell.ActiveTab!.CurrentText);
    }

    [AvaloniaFact]
    public async Task Workspace_search_shortcut_in_a_browsed_folder_explains_promotion_instead_of_disappearing()
    {
        using var f = new DesktopFixture();
        Assert.True(await f.Shell.OpenFolderAsync(f.Docs));
        f.Window.KeyPress(Key.F, RawInputModifiers.Control | RawInputModifiers.Shift, default, null);
        Assert.Contains("Make this folder a workspace", f.Shell.StatusMessage);
        Assert.Equal(StatusSeverity.Info, f.Shell.StatusSeverity);
        Assert.Contains(f.Shell.Activities.Items, item => item.Phase == f.Shell.StatusMessage);
    }
}
