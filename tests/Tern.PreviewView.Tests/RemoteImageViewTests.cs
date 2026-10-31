using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Tern.Rendering.Resources;

namespace Tern.PreviewView.Tests;

public sealed class RemoteImageViewTests
{
    [AvaloniaFact]
    public async Task Host_image_bytes_are_displayed_and_released_without_modifying_the_destination()
    {
        using var source = new WriteableBitmap(new PixelSize(64, 32), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var encoded = new MemoryStream();
        source.Save(encoded, new PngBitmapEncoderOptions());
        const string destination = "https://example.com/actual-image.png?version=2";
        var calls = 0;
        var image = new ImageView(new Blocked(BlockReason.RemoteResourceBlocked, destination, "Host retrieval required"), "Actual image", null,
            remoteImageLoader: (url, token) =>
            {
                Assert.Equal(destination, url);
                Assert.False(token.IsCancellationRequested);
                calls++;
                return Task.FromResult<byte[]?>(encoded.ToArray());
            });
        var window = new Window { Content = image, Width = 400, Height = 300 };
        try
        {
            window.Show();
            Harness.Pump(window);
            for (var i = 0; i < 200 && image.State == ImageState.Loading; i++) await Task.Delay(10);
            Assert.Equal(ImageState.Loaded, image.State);
            Assert.Equal(1, calls);
            Assert.Equal(new PixelSize(64, 32), Assert.IsType<Bitmap>(Assert.Single(Harness.All<Image>(image)).Source).PixelSize);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
            await image.LoadingCompletion.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.Equal(0, ImageView.LivePixels);
    }

    [AvaloniaFact]
    public async Task Removing_a_remote_image_cancels_its_host_request()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var image = new ImageView(new Blocked(BlockReason.RemoteResourceBlocked, "https://example.com/image.png", "Host retrieval required"), "Image", null,
            remoteImageLoader: async (_, token) =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
                return null;
            });
        var window = new Window { Content = image, Width = 400, Height = 300 };
        try
        {
            window.Show();
            Harness.Pump(window);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            image.Dispose();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await image.LoadingCompletion.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, ImageView.LivePixels);
        }
        finally { window.Close(); }
    }
}
