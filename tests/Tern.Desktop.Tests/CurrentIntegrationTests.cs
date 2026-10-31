using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.ViewModels;

namespace Tern.Desktop.Tests;

public sealed class CurrentIntegrationTests
{
    [AvaloniaFact]
    public async Task Missing_recent_reference_has_distinct_foreground_and_remains_removable()
    {
        using var fixture = new DesktopFixture();
        var path = await fixture.WriteAsync("deleted-reference.md", "# Saved");
        await fixture.Shell.OpenFileAsync(path);
        File.Delete(path);
        await fixture.Window.ShowNavAsync("Recent");
        fixture.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var rows = fixture.Window.FindControl<ListBox>("SideList")!;
        var row = Assert.Single(rows.Items.OfType<SideRow>());
        Assert.True(row.IsMissing);
        var title = Assert.Single(rows.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == row.Title);
        Assert.Contains("missing", title.Classes);
        Assert.True(Application.Current!.TryGetResource("Tern.Danger", Application.Current.ActualThemeVariant, out var danger));
        Assert.Equal(((ISolidColorBrush)danger!).Color, ((ISolidColorBrush)title.Foreground!).Color);
        Assert.Contains("remove reference", row.Detail, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Settings_apply_full_width_body_text_scale_and_sidebar_without_changing_document_fonts()
    {
        using var fixture = new DesktopFixture();
        var settings = fixture.Shell.Settings;
        settings.UiScale = 1.25;
        settings.PreviewFullWidth = false;
        settings.SidebarWidth = 320;
        settings.SidebarVisible = true;
        fixture.Window.ApplySettings();
        fixture.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Assert.False(fixture.Window.PreviewControl.UseFullWidth);
        Assert.Equal(settings.EditorFontSize, fixture.Window.EditorControl.FontSize);
        Assert.Equal(settings.PreviewFontSize, fixture.Window.PreviewControl.FontSize);
        Assert.Equal(320, fixture.Window.FindControl<Grid>("Body")!.ColumnDefinitions[0].Width.Value);
        Assert.Equal(17.5, Application.Current!.Resources["Tern.UiFontSize"]);
        Assert.Equal(15.0, Application.Current.Resources["Tern.SmallFontSize"]);
        settings.PreviewFullWidth = true; settings.SidebarVisible = false;
        fixture.Window.ApplySettings();
        Assert.True(fixture.Window.PreviewControl.UseFullWidth);
        Assert.False(fixture.Window.FindControl<Border>("Sidebar")!.IsVisible);
        Assert.Equal(0, fixture.Window.FindControl<Grid>("Body")!.ColumnDefinitions[0].Width.Value);
    }
}
