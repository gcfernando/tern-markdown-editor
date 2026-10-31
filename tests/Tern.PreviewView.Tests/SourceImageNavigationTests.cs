using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Tern.PreviewView.Tests;

public sealed class SourceImageNavigationTests
{
    [AvaloniaTheory]
    [InlineData(800)]
    [InlineData(1200)]
    public async Task Source_navigation_keeps_the_actual_image_visible_after_preceding_images_decode(int width)
    {
        using var bitmap = new WriteableBitmap(new PixelSize(1200, 900), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var encoded = new MemoryStream();
        bitmap.Save(encoded, new PngBitmapEncoderOptions());
        var bytes = encoded.ToArray();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var markdown = string.Join("\n\n", Enumerable.Range(1, 70).Select(i =>
            $"{i}. Item {i}\n\n    A paragraph before the image.\n\n"
            + (i is 1 or 20 or 40 or 48 ? $"    ![Image {i}](https://example.com/{i}.png)\n\n" : "")
            + "    A paragraph after the image."));
        var marker = markdown.IndexOf("![Image 48]", StringComparison.Ordinal);
        var line = 1 + markdown.AsSpan(0, marker).Count('\n');
        var view = new PreviewView
        {
            RemoteImageLoader = async (_, token) =>
            {
                await release.Task.WaitAsync(token);
                return bytes;
            },
            Document = Harness.Build(markdown),
        };
        var window = new Window { Content = view, Width = width, Height = 600 };
        try
        {
            window.Show();
            Harness.Pump(window);
            view.ScrollToSourceLine(line);
            Harness.Pump(window);
            release.SetResult();
            var images = Harness.All<ImageView>(view);
            await Task.WhenAll(images.Select(image => image.LoadingCompletion)).WaitAsync(TimeSpan.FromSeconds(10));
            Harness.Pump(window, 12);
            var target = Assert.Single(Harness.All<ImageView>(view), image => image.Alt == "Image 48");
            Assert.Equal(ImageState.Loaded, target.State);
            var point = target.TranslatePoint(default, view.Scroller)!.Value;
            var rectangle = new Rect(point, target.Bounds.Size);
            Assert.True(rectangle.Intersects(new Rect(view.Scroller.Viewport)),
                $"Target {rectangle} must intersect viewport {view.Scroller.Viewport} after decoding at width {width}.");
            Assert.InRange(point.Y, -1, 100);
        }
        finally
        {
            release.TrySetResult();
            window.Close();
        }
    }
}
