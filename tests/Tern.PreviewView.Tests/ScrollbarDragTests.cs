using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace Tern.PreviewView.Tests;

public sealed class ScrollbarDragTests(ITestOutputHelper output)
{
    [AvaloniaTheory]
    [InlineData(760)]
    public void Held_left_mouse_thumb_drag_moves_down_and_back_up_without_snapback(int width)
    {
        var markdown = string.Join("\n\n", Enumerable.Range(0, 450).Select(i =>
            $"## Section {i}\n\n" + string.Concat(Enumerable.Repeat("Ordinary text with variable wrapping and **emphasis**. ", 2 + i % 12))));
        var (window, view) = Harness.Show(Harness.Build(markdown), width, 600);
        try
        {
            var bar = Harness.All<ScrollBar>(view).Single(b => b.Orientation == Orientation.Vertical
                && b.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault() == view.Scroller);
            var thumb = Harness.All<Thumb>(bar).Single();
            output.WriteLine($"Main vertical scrollbar name={bar.Name}, bounds={bar.Bounds}, min={bar.MinWidth}x{bar.MinHeight}; thumb name={thumb.Name}, bounds={thumb.Bounds}, min={thumb.MinWidth}x{thumb.MinHeight}; extent={view.Scroller.Extent}, viewport={view.Scroller.Viewport}");
            var point = thumb.TranslatePoint(thumb.Bounds.Center, window)!.Value;
            window.MouseMove(point); window.MouseDown(point, MouseButton.Left);
            var previous = view.Scroller.Offset.Y;
            foreach (var distance in new[] { 70, 140, 210, 280 })
            {
                window.MouseMove(point + new Vector(0, distance)); Harness.Pump(window);
                Assert.True(view.Scroller.Offset.Y > previous + 50, $"Drag down stalled: {previous} -> {view.Scroller.Offset.Y}");
                previous = view.Scroller.Offset.Y;
            }
            foreach (var distance in new[] { 210, 140, 70 })
            {
                window.MouseMove(point + new Vector(0, distance)); Harness.Pump(window);
                Assert.True(view.Scroller.Offset.Y < previous - 50, $"Drag up stalled: {previous} -> {view.Scroller.Offset.Y}");
                previous = view.Scroller.Offset.Y;
            }
            window.MouseUp(point + new Vector(0, 70), MouseButton.Left); Harness.Pump(window);
            var released = view.Scroller.Offset.Y; Harness.Pump(window, 8);
            Assert.InRange(Math.Abs(view.Scroller.Offset.Y - released), 0, 2);
            // Extremes must remain reachable despite estimated block heights
            // changing while the pointer is held down.
            point = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            window.MouseMove(point + new Vector(0, 1200)); Harness.Pump(window, 8);
            Assert.InRange(view.Scroller.Extent.Height - view.Scroller.Viewport.Height - view.Scroller.Offset.Y, 0, 2);
            window.MouseMove(point - new Vector(0, 1200)); Harness.Pump(window, 8);
            Assert.InRange(view.Scroller.Offset.Y, 0, 2);
            window.MouseUp(point - new Vector(0, 1200), MouseButton.Left); Harness.Pump(window);
        }
        finally { window.Close(); }
    }
}
