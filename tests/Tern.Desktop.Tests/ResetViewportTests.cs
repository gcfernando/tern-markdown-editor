using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class ResetViewportTests
{
    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Minimum_reset_window_keeps_choices_scrollable_and_actions_reachable(double scale)
    {
        using var fixture = new DesktopFixture();
        fixture.Shell.Settings.UiScale = scale;
        fixture.Window.ApplySettings();
        var window = new ResetWindow(fixture.Shell);
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        try
        {
            window.Show(fixture.Window);
            Settle(window);
            var scroll = Assert.Single(window.GetVisualDescendants().OfType<ScrollViewer>(),
                s => s.Content is StackPanel);
            Assert.True(scroll.Bounds.Height >= 100, $"Choice viewport is only {scroll.Bounds.Height:F1} DIP at {scale:P0}");
            var choices = scroll.GetVisualDescendants().OfType<CheckBox>().ToArray();
            Assert.Equal(5, choices.Length);
            Assert.Contains(choices, choice => AutomationProperties.GetName(choice) == "Cached online documents and assets");
            var first = choices[0].TranslatePoint(default, scroll)!.Value;
            Assert.InRange(first.Y, 0, scroll.Bounds.Height - 1);
            foreach (var choice in choices)
            {
                choice.BringIntoView();
                Settle(window);
                var point = choice.TranslatePoint(default, scroll)!.Value;
                Assert.True(point.Y < scroll.Bounds.Height && point.Y + choice.Bounds.Height > 0);
            }
            foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(b => b is not CheckBox))
            {
                var point = button.TranslatePoint(default, window)!.Value;
                Assert.InRange(point.X, 0, window.Bounds.Width - button.Bounds.Width + 1);
                Assert.InRange(point.Y, 0, window.Bounds.Height - button.Bounds.Height + 1);
            }
            var reset = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                b => AutomationProperties.GetName(b) == "Reset selected application data");
            Assert.True(reset.IsEnabled);
            Assert.True(choices[0].IsChecked);
            Assert.All(choices.Skip(1), choice => Assert.False(choice.IsChecked));
            choices[0].BringIntoView(); Settle(window);
            var folder = Path.Combine(Screenshots.Folder, "reset-viewport");
            Directory.CreateDirectory(folder);
            using var bitmap = window.CaptureRenderedFrame();
            Assert.NotNull(bitmap);
            using var stream = File.Create(Path.Combine(folder, $"reset-minimum-{(int)(scale * 100)}.png"));
            bitmap.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        finally { window.Close(); fixture.Shell.Settings.UiScale = 1; fixture.Window.ApplySettings(); }
    }

    private static void Settle(Window window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
}
