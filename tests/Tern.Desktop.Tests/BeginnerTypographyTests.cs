using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Tern.App.Settings;

namespace Tern.Desktop.Tests;

public sealed class BeginnerTypographyTests
{
    [AvaloniaTheory]
    [InlineData(900, 560, 1.0, ThemeChoice.Paper)]
    [InlineData(1440, 900, 1.0, ThemeChoice.Midnight)]
    [InlineData(1440, 900, 1.5, ThemeChoice.Paper)]
    public async Task Interface_type_scales_and_writing_fonts_remain_independent(int width, int height, double scale, ThemeChoice theme)
    {
        using var fixture = new DesktopFixture();
        fixture.Shell.Settings.UiScale = scale;
        fixture.Shell.Settings.Theme = theme;
        fixture.Window.Width = width;
        fixture.Window.Height = height;
        fixture.Window.ApplyTheme();
        fixture.Window.ApplySettings();
        await fixture.Shell.OpenFileAsync(await fixture.WriteAsync("My first note.md", "# My first note\n\nReadable writing with **bold words**.\n\n- [ ] Save this note\n- [x] Open the guide\n"));
        await Task.Delay(400);
        Dispatcher.UIThread.RunJobs();
        fixture.Window.UpdateLayout();
        Assert.Equal(14 * scale, fixture.Window.FontSize);
        var menu = fixture.Window.FindControl<Menu>("ApplicationMenu")!;
        Assert.All(menu.Items.OfType<MenuItem>(), item => Assert.True(item.Bounds.Height >= 24 * scale + 12));
        Assert.Equal(12 * scale, fixture.Window.FindControl<TextBlock>("WorkspaceCount")!.FontSize);
        Assert.Equal(fixture.Shell.Settings.EditorFontSize, fixture.Window.EditorControl.FontSize);
        Assert.Equal(fixture.Shell.Settings.PreviewFontSize, fixture.Window.PreviewControl.FontSize);
        var folder = Path.Combine(Screenshots.Folder, "beginner-typography");
        Directory.CreateDirectory(folder);
        using var frame = fixture.Window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var stream = File.Create(Path.Combine(folder, $"{width}-{theme}-{scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}.png"));
        frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
