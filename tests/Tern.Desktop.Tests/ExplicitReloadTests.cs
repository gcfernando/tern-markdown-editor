using Avalonia.Headless.XUnit;
using Tern.App.Commands;
using Tern.Domain.Text;

namespace Tern.Desktop.Tests;

public sealed class ExplicitReloadTests
{
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reload_command_always_confirms_dirty_text_even_when_disk_matches_the_baseline(bool confirm)
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("reload.md", "# Saved text");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(tab.CurrentText.Length, "\nunsaved edit")]);
        var dirty = tab.CurrentText;
        f.Dialogs.ConfirmReload = confirm;
        await f.Window.RunCommandAsync(CommandIds.ReloadFromDisk);
        Assert.Equal(1, f.Dialogs.ReloadPrompts);
        Assert.Equal(confirm ? "# Saved text" : dirty, tab.CurrentText);
        Assert.Equal(!confirm, tab.IsDirty);
        Assert.Equal("# Saved text", await File.ReadAllTextAsync(path));
    }

    [AvaloniaFact]
    public async Task Explicit_reload_of_a_clean_tab_does_not_depend_on_the_auto_reload_preference()
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("reload.md", "# Original");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        f.Shell.Settings.AutoReloadCleanDocuments = false;
        await File.WriteAllTextAsync(path, "# Changed externally");
        await f.Window.RunCommandAsync(CommandIds.ReloadFromDisk);
        Assert.Equal("# Changed externally", tab.CurrentText);
        Assert.False(tab.IsDirty);
        Assert.Equal(0, f.Dialogs.ReloadPrompts);
    }
}
