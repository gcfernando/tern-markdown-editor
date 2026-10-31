using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace Tern.Desktop.Tests;

public sealed class StatusBarLayoutTests
{
    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void New_document_label_and_status_fields_are_centered_without_vertical_clipping(double scale)
    {
        using var fixture = new DesktopFixture();
        fixture.Shell.Settings.UiScale = scale;
        fixture.Window.ApplySettings();
        fixture.Shell.NewDocument();
        Dispatcher.UIThread.RunJobs();
        fixture.Window.UpdateLayout();
        var bar = fixture.Window.FindControl<Border>("StatusBar")!;
        var label = fixture.Window.FindControl<TextBlock>("StatusPath")!;
        Assert.Equal("New document (not saved)", label.Text);
        var barPoint = bar.TranslatePoint(default, fixture.Window)!.Value;
        foreach (var name in new[] { "StatusPath", "StatusPosition", "StatusWords", "StatusEncoding", "StatusLineEnding", "StatusProfile", "StatusSaved" })
        {
            var control = fixture.Window.FindControl<Control>(name)!;
            var point = control.TranslatePoint(default, fixture.Window)!.Value;
            Assert.True(control.Bounds.Height > 0);
            Assert.True(point.Y >= barPoint.Y + 1, name + " touches the top border");
            Assert.True(point.Y + control.Bounds.Height <= barPoint.Y + bar.Bounds.Height - 1, name + " is clipped at the bottom");
            Assert.InRange(Math.Abs(point.Y + control.Bounds.Height / 2 - (barPoint.Y + bar.Bounds.Height / 2)), 0, 1);
        }
        var folder = Path.Combine(Screenshots.Folder, "status-bar");
        Directory.CreateDirectory(folder);
        using var frame = fixture.Window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var stream = File.Create(Path.Combine(folder, $"new-document-{scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}.png"));
        frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
