using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Settings;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class ScrollbarClearanceTests
{
    [AvaloniaTheory]
    [InlineData(ThemeChoice.Paper, 1.0)]
    [InlineData(ThemeChoice.Midnight, 1.0)]
    [InlineData(ThemeChoice.Paper, 2.0)]
    [InlineData(ThemeChoice.Midnight, 2.0)]
    public void Every_settings_category_reserves_clearance_before_visible_scrollbar_tracks(ThemeChoice theme, double scale)
    {
        using var fixture = new DesktopFixture();
        fixture.Shell.Settings.Theme = theme;
        fixture.Shell.Settings.UiScale = scale;
        fixture.Shell.Settings.ReduceMotion = true;
        fixture.Window.ApplySettings(); fixture.Window.ApplyTheme();
        var window = new SettingsWindow(fixture.Shell.Settings, fixture.Shell.Registry) { Width = 1100, Height = 620 };
        window.Show(fixture.Window);
        try
        {
            Pump(window);
            var navigation = window.GetVisualDescendants().OfType<ListBox>().Single(control =>
                AutomationProperties.GetName(control) == "Settings categories");
            var content = window.GetVisualDescendants().OfType<ScrollViewer>().Single(control =>
                AutomationProperties.GetName(control) == "Settings content viewport");
            var observed = 0;
            for (var index = 0; index < navigation.ItemCount; index++)
            {
                navigation.SelectedIndex = index; content.Offset = default; Pump(window);
                observed += ScrollbarClearanceAssertions.AssertTracks(window);
                content.Offset = new Vector(0, content.Extent.Height); Pump(window);
                observed += ScrollbarClearanceAssertions.AssertTracks(window);
            }
            Assert.True(observed > 0, "The category audit must examine real visible scrollbar tracks.");
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            var folder = Path.Combine(Screenshots.Folder, "scrollbar-clearance"); Directory.CreateDirectory(folder);
            using var output = File.Create(Path.Combine(folder, $"settings-{theme}-{scale:0.0}.png"));
            frame.Save(output, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        finally { window.Close(); }
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
    }
}

internal static class ScrollbarClearanceAssertions
{
    // Measure actual layout against the track, not the narrower painted grip.
    // This also covers cards and panels that contain a textbox.
    public static int AssertTracks(Window window)
    {
        var count = 0;
        foreach (var viewer in window.GetVisualDescendants().OfType<ScrollViewer>().Where(control => control.IsEffectivelyVisible))
        {
            var presenter = viewer.GetVisualDescendants().OfType<ScrollContentPresenter>().FirstOrDefault(control =>
                ReferenceEquals(control.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault(), viewer));
            if (presenter is null || presenter.Bounds.Width <= 0 || presenter.Bounds.Height <= 0) continue;
            var origin = presenter.TranslatePoint(default, viewer)!.Value;
            foreach (var bar in viewer.GetVisualDescendants().OfType<ScrollBar>().Where(control =>
                control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0 &&
                ReferenceEquals(control.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault(), viewer)))
            {
                var track = bar.TranslatePoint(default, viewer)!.Value;
                var gap = bar.Orientation == Orientation.Vertical
                    ? track.X - (origin.X + presenter.Bounds.Width)
                    : track.Y - (origin.Y + presenter.Bounds.Height);
                var minimum = viewer.GetVisualAncestors().OfType<TextBox>().Any() ? 3.5 : 11.5;
                Assert.True(gap >= minimum,
                    $"{window.Title}: {AutomationProperties.GetName(viewer)} {bar.Orientation} track touches its content viewport (gap {gap:0.##} DIP).");
                count++;
            }
        }
        return count;
    }
}
