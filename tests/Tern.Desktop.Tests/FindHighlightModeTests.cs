using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Tern.App.Commands;
using Tern.App.Settings;
using Tern.Desktop.Themes;

namespace Tern.Desktop.Tests;

public sealed class FindHighlightModeTests
{
    [AvaloniaTheory]
    [InlineData(ThemeChoice.Paper)]
    [InlineData(ThemeChoice.Midnight)]
    public async Task Find_paints_the_selected_word_in_all_modes_and_clears_on_close(ThemeChoice theme)
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("find.md", "# Search example\n\nA hot word and another **hot** word.\n");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        ThemeManager.Apply(Application.Current!, theme);
        f.Window.PreviewControl.ReduceMotion = true;
        f.Window.ShowFindBar(false);
        await f.Window.SearchAsync("hot");
        foreach (var mode in new[] { CommandIds.ViewSource, CommandIds.ViewSplit, CommandIds.ViewReading })
        {
            await f.Window.RunCommandAsync(mode);
            await f.Window.RebuildPreviewAsync();
            Pump(f);
            using var frame = f.Window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.Equal("hot", f.Window.EditorControl.SelectedText);
            if (f.Window.PreviewHost.IsVisible)
                Assert.True(HighlightOutlinePixels(frame, f.Window.PreviewControl, f.Window) > 5);
            frame.Save(Path.Combine(Screenshots.Folder, $"find-highlight-{theme}-{mode.Replace('.', '-')}.png"));
        }
        f.Window.FindNext(true);
        Pump(f);
        Assert.Equal("2 of 2", f.Window.FindStatusText);
        using (var next = f.Window.CaptureRenderedFrame())
        {
            Assert.NotNull(next);
            Assert.True(HighlightOutlinePixels(next, f.Window.PreviewControl, f.Window) > 5);
        }
        f.Window.FindBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Pump(f);
        Assert.False(f.Window.FindBar.IsVisible);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
        using var closed = f.Window.CaptureRenderedFrame();
        Assert.NotNull(closed);
        closed.Save(Path.Combine(Screenshots.Folder, $"find-highlight-{theme}-closed.png"));
        Assert.Equal(0, HighlightOutlinePixels(closed, f.Window.PreviewControl, f.Window));
        Assert.False(tab.IsDirty);
    }

    private static int HighlightOutlinePixels(WriteableBitmap bitmap, Control control, Window window)
    {
        Assert.True(control.TryFindResource(ThemeManager.FindMatchOutline, control.ActualThemeVariant, out var value));
        var color = Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
        var origin = control.TranslatePoint(default, window)!.Value;
        using var buffer = bitmap.Lock();
        var row = new byte[buffer.RowBytes];
        var count = 0;
        for (var y = Math.Max(0, (int)origin.Y); y < Math.Min(buffer.Size.Height, origin.Y + control.Bounds.Height); y++)
        {
            Marshal.Copy(buffer.Address + y * buffer.RowBytes, row, 0, row.Length);
            for (var x = Math.Max(0, (int)origin.X); x < Math.Min(buffer.Size.Width, origin.X + control.Bounds.Width); x++)
                if (Math.Max(row[x * 4], row[x * 4 + 2]) == Math.Max(color.R, color.B)
                    && Math.Min(row[x * 4], row[x * 4 + 2]) == Math.Min(color.R, color.B)
                    && row[x * 4 + 1] == color.G) count++;
        }
        return count;
    }

    private static void Pump(DesktopFixture f)
    { for (var i = 0; i < 6; i++) { f.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); } }
}
