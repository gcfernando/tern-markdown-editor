using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;

namespace Tern.Desktop.Tests;

public sealed class ScrollbarInteractionTests
{
    [AvaloniaTheory]
    [InlineData(CommandIds.ViewSource, false)]
    [InlineData(CommandIds.ViewSplit, false)]
    [InlineData(CommandIds.ViewReading, true)]
    [InlineData(CommandIds.ViewSplit, true)]
    public async Task Held_scrollbar_drag_moves_both_directions_with_sync_enabled(string mode, bool preview)
    {
        using var fixture = new DesktopFixture();
        fixture.Shell.Settings.SyncScroll = true;
        var text = string.Join("\n\n", Enumerable.Range(0, 450).Select(i => $"## Section {i}\n\n" + string.Concat(Enumerable.Repeat("Text with variable wrapping and **emphasis**. ", 2 + i % 12))));
        var path = await fixture.WriteAsync("drag-scrollbar.md", text);
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        await fixture.Window.RunCommandAsync(mode);
        await fixture.Window.RebuildPreviewAsync();
        Pump(fixture);
        var scroller = preview ? fixture.Window.PreviewControl.GetVisualDescendants().OfType<ScrollViewer>().First() : null;
        var bar = preview
            ? scroller!.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Vertical && b.GetVisualAncestors().OfType<ScrollViewer>().First() == scroller)
            : fixture.Window.EditorControl.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Vertical);
        var thumb = bar.GetVisualDescendants().OfType<Thumb>().Single();
        Assert.True(bar.Bounds.Width >= 20);
        Assert.True(thumb.Bounds.Height >= 40);
        double Offset() => preview ? scroller!.Offset.Y : fixture.Window.EditorControl.VerticalOffset;
        var point = thumb.TranslatePoint(thumb.Bounds.Center, fixture.Window)!.Value;
        fixture.Window.MouseMove(point); fixture.Window.MouseDown(point, MouseButton.Left);
        var previous = Offset();
        var trace = new List<string>();
        foreach (var distance in new[] { 60, 120, 180, 240 })
        {
            fixture.Window.MouseMove(point + new Vector(0, distance)); Pump(fixture);
            trace.Add($"down {distance}: offset={Offset()} bar={bar.Value}/{bar.Maximum} thumb={thumb.Bounds}");
            Assert.True(Offset() > previous + 20, "Drag down stalled: " + string.Join("; ", trace));
            previous = Offset();
        }
        foreach (var distance in new[] { 180, 120, 60 })
        {
            fixture.Window.MouseMove(point + new Vector(0, distance)); Pump(fixture);
            trace.Add($"up {distance}: offset={Offset()} bar={bar.Value}/{bar.Maximum} thumb={thumb.Bounds}");
            Assert.True(Offset() < previous - 20, "Drag up stalled: " + string.Join("; ", trace));
            previous = Offset();
        }
        fixture.Window.MouseUp(point + new Vector(0, 60), MouseButton.Left); Pump(fixture);
        var released = Offset();
        Pump(fixture);
        Assert.InRange(Math.Abs(Offset() - released), 0, 2);
        Assert.Equal(text, tab.CurrentText); Assert.False(tab.IsDirty);
    }

    private static void Pump(DesktopFixture fixture)
    {
        for (var i = 0; i < 5; i++) { fixture.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    }
}
