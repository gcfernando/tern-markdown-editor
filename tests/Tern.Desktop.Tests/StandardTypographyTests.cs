using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Tern.App.Settings;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class StandardTypographyTests
{
    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void Main_window_and_dialogs_use_the_same_body_size_and_respect_user_scaling(double scale)
    {
        using var fixture = new DesktopFixture();
        Assert.Equal(1, fixture.Shell.Settings.UiScale);
        fixture.Shell.Settings.UiScale = scale;
        fixture.Window.ApplySettings();
        var url = new OpenUrlWindow();
        var about = new AboutWindow();
        try
        {
            url.Show(fixture.Window);
            about.Show(fixture.Window);
            fixture.Window.UpdateLayout();
            Assert.Equal(14 * scale, fixture.Window.FontSize);
            Assert.Equal(fixture.Window.FontSize, url.FontSize);
            var input = Assert.Single(url.GetVisualDescendants().OfType<TextBox>(),
                field => AutomationProperties.GetName(field) == "Markdown URL");
            Assert.Equal(fixture.Window.FontSize, input.FontSize);
            var body = Assert.Single(about.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text?.StartsWith("Write and read Markdown files", StringComparison.Ordinal) == true);
            Assert.Equal(fixture.Window.FontSize, body.FontSize);
            fixture.Window.ShowQuickOpen("");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            fixture.Window.UpdateLayout();
            Assert.Equal(14 * scale, fixture.Window.Picker!.Input.FontSize);
            fixture.Window.HideOverlay();
            Assert.Equal(14, fixture.Window.EditorControl.FontSize);
            Assert.Equal(16, fixture.Window.PreviewControl.FontSize);
        }
        finally { about.Close(); url.Close(); }
    }

    [AvaloniaFact]
    public void Standard_startup_defaults_do_not_overwrite_sizes_the_user_saved()
    {
        using var fixture = new DesktopFixture();
        var store = new SettingsStore(Path.Combine(fixture.AppData, "font-preferences"));
        var defaults = store.Load();
        Assert.Equal(1, defaults.UiScale);
        Assert.Equal(14, defaults.EditorFontSize);
        Assert.Equal(16, defaults.PreviewFontSize);
        defaults.UiScale = 1.5;
        defaults.EditorFontSize = 18;
        defaults.PreviewFontSize = 20;
        store.Save(defaults);
        var reopened = new SettingsStore(Path.Combine(fixture.AppData, "font-preferences")).Load();
        Assert.Equal(1.5, reopened.UiScale);
        Assert.Equal(18, reopened.EditorFontSize);
        Assert.Equal(20, reopened.PreviewFontSize);
    }
}
