using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Automation;
using Avalonia.Media.Imaging;

namespace Tern.PreviewView.Tests;

public sealed class HandbookReadingTests(ITestOutputHelper output)
{
    private static string Handbook => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MarkdownSamples", "Data_Science_Handbook.md"));

    [AvaloniaTheory]
    [InlineData(480)]
    [InlineData(1000)]
    public void Handbook_pipeline_has_a_reachable_horizontal_scrollbar_and_full_label_height(int width)
    {
        var source = Regex.Match(Handbook, @"```mermaid.*?```", RegexOptions.Singleline).Value;
        var (window, view) = Harness.Show(Harness.Build(source + "\n\nAfter diagram."), width, 700);
        try
        {
            var frame = Assert.Single(Harness.All<FitScroller>(view));
            var diagram = Assert.Single(Harness.All<DiagramControl>(view));
            output.WriteLine($"panel={view.Panel.Bounds}, frame={frame.Bounds}, scroll={frame.Scroller.Bounds}, extent={frame.Scroller.Extent}, viewport={frame.Scroller.Viewport}, diagram={diagram.Bounds}, natural={diagram.DiagramWidth}x{diagram.DiagramHeight}");
            Assert.True(frame.Scroller.Bounds.Width <= view.Panel.Bounds.Width + 1);
            Assert.False(frame.Scroller.AllowAutoHide);
            Assert.True(frame.Scroller.Extent.Width > frame.Scroller.Viewport.Width);
            frame.Scroller.Offset = new Vector(double.MaxValue, 0); Harness.Pump(window);
            Assert.True(frame.Scroller.Offset.X > 100);
            Assert.True(frame.Scroller.Viewport.Height >= diagram.Bounds.Height - 1);
            var fit = Assert.Single(Harness.All<Button>(frame), button => AutomationProperties.GetName(button) == "Fit diagram to page width");
            fit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Harness.Pump(window, 8);
            Assert.True(diagram.FitToWidth);
            Assert.True(frame.Scroller.Extent.Width <= frame.Scroller.Viewport.Width + 1);
            fit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Harness.Pump(window, 8);
            Assert.False(diagram.FitToWidth);
            Assert.True(frame.Scroller.Extent.Width > frame.Scroller.Viewport.Width);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Reading_page_buttons_and_top_are_stable_on_the_handbook()
    {
        var (window, view) = Harness.Show(Harness.Build(Handbook), 1100, 700);
        try
        {
            view.ScrollByPage(1); Harness.Pump(window, 8);
            Assert.True(view.Scroller.Offset.Y > 200);
            view.ScrollByPage(-1); Harness.Pump(window, 8);
            Assert.True(view.Scroller.Offset.Y < 100);
            view.ScrollToSourceLine(92); Harness.Pump(window, 8);
            var outputDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/screenshots/reading-mode"));
            Directory.CreateDirectory(outputDirectory);
            using var frame = Harness.Capture(window);
            Assert.NotNull(frame);
            using (var file = File.Create(Path.Combine(outputDirectory, "handbook-pipeline.png"))) frame.Save(file, new PngBitmapEncoderOptions());
            view.ScrollToTop(); Harness.Pump(window, 8);
            Assert.Equal(0, view.Scroller.Offset.Y);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Handbook_thumb_drag_tracks_the_pointer_and_does_not_snap_back()
    {
        var (window, view) = Harness.Show(Harness.Build(Handbook), 900, 650);
        try
        {
            var bar = Harness.All<ScrollBar>(view).Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical
                && b.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault() == view.Scroller);
            var thumb = Assert.Single(Harness.All<Thumb>(bar));
            var point = thumb.TranslatePoint(thumb.Bounds.Center, window)!.Value;
            window.MouseMove(point); window.MouseDown(point, MouseButton.Left);
            var previous = view.Scroller.Offset.Y;
            foreach (var distance in new[] { 80, 160, 240, 320 })
            {
                window.MouseMove(point + new Vector(0, distance)); Harness.Pump(window, 8);
                output.WriteLine($"down {distance}: {previous}->{view.Scroller.Offset.Y}; max={bar.Maximum}");
                Assert.True(view.Scroller.Offset.Y > previous + 20);
                previous = view.Scroller.Offset.Y;
            }
            foreach (var distance in new[] { 240, 160, 80 })
            {
                window.MouseMove(point + new Vector(0, distance)); Harness.Pump(window, 8);
                Assert.True(view.Scroller.Offset.Y < previous - 20);
                previous = view.Scroller.Offset.Y;
            }
            window.MouseUp(point + new Vector(0, 80), MouseButton.Left); Harness.Pump(window, 8);
            var released = view.Scroller.Offset.Y; Harness.Pump(window, 10);
            Assert.InRange(Math.Abs(view.Scroller.Offset.Y - released), 0, 2);
            Assert.True(bar.Bounds.Width >= 24);
            Assert.True(thumb.Bounds.Height >= 56);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Handbook_scroll_positions_keep_visible_code_realized_and_text_unchanged()
    {
        var document = Harness.Build(Handbook);
        var (window, view) = Harness.Show(document, 900, 650);
        try
        {
            foreach (var fraction in new[] { .1, .65, .25, .9, .4, .75, .05 })
            {
                view.Scroller.Offset = new Vector(0, (view.Scroller.Extent.Height - view.Scroller.Viewport.Height) * fraction);
                Harness.Pump(window, 10);
                var top = view.Scroller.Offset.Y - 20;
                var bottom = top + view.Scroller.Viewport.Height;
                var slots = view.Panel.Slots;
                for (var i = 0; i < slots.Count; i++)
                {
                    if (view.Panel.TopOf(i) + slots[i].Height < top || view.Panel.TopOf(i) > bottom) continue;
                    Assert.NotNull(slots[i].Host);
                    Assert.True(slots[i].Host!.IsAttachedToVisualTree());
                }
            }
            Assert.Same(document, view.Document);
        }
        finally { window.Close(); }
    }
}
