using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Rendering.Preview;

namespace Tern.PreviewView.Tests;

internal static class Harness
{
    public static PreviewDocument Build(string markdown, PreviewOptions? options = null) =>
        PreviewBuilder.Build(markdown, options ?? new PreviewOptions());

    public static (Window Window, PreviewView View) Show(PreviewDocument? document, double width = 900, double height = 700)
    {
        var view = new PreviewView { Document = document };
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        Pump(window);
        return (window, view);
    }

    public static void Pump(Window window, int rounds = 4)
    {
        for (var i = 0; i < rounds; i++)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    public static List<T> All<T>(Visual root) where T : class => root.GetVisualDescendants().OfType<T>().ToList();

    public static string PlainText(TextBlock block) =>
        block.Inlines is { Count: > 0 } inlines
            ? string.Concat(inlines.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text))
            : block.Text ?? string.Empty;

    public static WriteableBitmap? Capture(Window window)
    {
        Pump(window);
        return window.CaptureRenderedFrame();
    }

    /// <summary>Counts pixels that differ from the first pixel inside the region.</summary>
    public static int NonBackgroundPixels(WriteableBitmap bitmap, PixelRect region)
    {
        using var buffer = bitmap.Lock();
        var count = 0;
        unsafe
        {
            var stride = buffer.RowBytes;
            var origin = (byte*)buffer.Address;
            var reference = *(uint*)(origin + (region.Y * stride) + (region.X * 4));
            for (var y = region.Y; y < region.Bottom && y < buffer.Size.Height; y++)
            {
                var row = (uint*)(origin + (y * stride));
                for (var x = region.X; x < region.Right && x < buffer.Size.Width; x++)
                {
                    if (row[x] != reference)
                    {
                        count++;
                    }
                }
            }
        }

        return count;
    }

    public static PixelRect RegionOf(Visual visual, Visual root)
    {
        var origin = visual.TranslatePoint(default, root) ?? default;
        return new PixelRect((int)origin.X, (int)origin.Y, Math.Max(1, (int)visual.Bounds.Width), Math.Max(1, (int)visual.Bounds.Height));
    }
}
