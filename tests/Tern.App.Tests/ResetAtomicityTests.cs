using Tern.App.Commands;
using Tern.App.Shell;

namespace Tern.App.Tests;

public sealed class ResetAtomicityTests
{
    [Fact]
    public void Failed_preference_reset_restores_live_preferences_and_shortcuts()
    {
        using var fixture = new ShellFixture();
        fixture.Shell.Settings.EditorFontSize = 23;
        Assert.True(fixture.Shell.Registry.SetBinding(CommandIds.Save, "Ctrl+Alt+F12").Succeeded);
        Directory.CreateDirectory(fixture.Shell.Paths.SettingsDirectory);
        var file = Path.Combine(fixture.Shell.Paths.SettingsDirectory, "settings.json");
        Directory.CreateDirectory(file); // Deterministic persistence failure, no permission changes.
        Assert.False(fixture.Shell.ResetApplication(new ResetOptions(RecentHistory: false, Preferences: true)));
        Assert.Equal(23, fixture.Shell.Settings.EditorFontSize);
        Assert.Equal("Ctrl+Alt+F12", fixture.Shell.Registry.GetGesture(CommandIds.Save));
        Assert.Contains("Previous preferences remain active", fixture.Shell.StatusMessage, StringComparison.Ordinal);
    }
}
