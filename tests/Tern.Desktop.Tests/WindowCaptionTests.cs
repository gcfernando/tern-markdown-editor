using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;

namespace Tern.Desktop.Tests;

public sealed class WindowCaptionTests
{
    [AvaloniaFact]
    public async Task Reading_scrollbar_stays_visible_has_a_wide_target_and_navigates_the_document()
    {
        using var f = new DesktopFixture();
        var source = string.Join("\n\n", Enumerable.Range(1, 300).Select(i => $"## Heading {i}\n\nReadable paragraph {i}."));
        var path = await f.WriteAsync("scrollbar.md", source);
        var tab = (await f.Shell.OpenFileAsync(path))!;
        await f.Window.RunCommandAsync(CommandIds.ViewReading);
        await f.Window.RebuildPreviewAsync();
        f.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var scroller = f.Window.PreviewControl.GetVisualDescendants().OfType<ScrollViewer>().First();
        var bar = scroller.GetVisualDescendants().OfType<ScrollBar>().First(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
        Assert.False(scroller.AllowAutoHide);
        Assert.True(bar.IsVisible);
        Assert.True(bar.Bounds.Width >= 16);
        Assert.True(bar.Maximum > bar.Minimum);
        bar.Value = bar.Maximum / 2;
        Dispatcher.UIThread.RunJobs();
        f.Window.UpdateLayout();
        Assert.True(scroller.Offset.Y > 0);
        Assert.True(f.Window.PreviewControl.TopSourceLine > 1);
        Assert.Equal(source, tab.CurrentText);
    }

    [AvaloniaTheory]
    [InlineData(24)]
    [InlineData(900)]
    public void Double_click_caption_maximizes_including_empty_caption_space(double x)
    {
        using var f = new DesktopFixture();
        Dispatcher.UIThread.RunJobs();
        var point = new Point(x, 24);
        f.Window.MouseDown(point, MouseButton.Left);
        f.Window.MouseUp(point, MouseButton.Left);
        f.Window.MouseDown(point, MouseButton.Left);
        f.Window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(WindowState.Maximized, f.Window.WindowState);
    }
}
