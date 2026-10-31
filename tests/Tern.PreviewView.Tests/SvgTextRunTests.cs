using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Tern.Rendering.Resources;

namespace Tern.PreviewView.Tests;

public sealed class SvgTextRunTests
{
    [AvaloniaFact]
    public void Styled_subscript_is_smaller_lower_and_advances_the_following_run()
    {
        var bytes = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='240' height='100'><text x='20' y='60' font-family='Arial' font-size='40' fill='#00ff00'>V<tspan baseline-shift='sub' font-size='20' fill='#ff0000'>F</tspan><tspan fill='#0000ff'>V</tspan></text><text x='150' y='60' font-family='Arial' font-size='20' fill='#ff00ff'>F</text></svg>");
        Assert.True(SafeSvgPolicy.IsSafe(bytes));
        Assert.True(SafeSvgImage.TryCreate(bytes, out var control));
        var window = new Window { Width = 240, Height = 100, Background = Brushes.White, Content = control };
        window.Show();
        try
        {
            using var frame = Harness.Capture(window);
            Assert.NotNull(frame);
            var bounds = new (int Left, int Top, int Right, int Bottom)[4];
            for (var i = 0; i < bounds.Length; i++) bounds[i] = (int.MaxValue, int.MaxValue, -1, -1);
            using var buffer = frame.Lock();
            Assert.True(buffer.Format == Avalonia.Platform.PixelFormat.Rgba8888 || buffer.Format == Avalonia.Platform.PixelFormat.Bgra8888, $"Unexpected pixel format: {buffer.Format}");
            var redIndex = buffer.Format == Avalonia.Platform.PixelFormat.Rgba8888 ? 0 : 2;
            var blueIndex = 2 - redIndex;
            unsafe
            {
                for (var y = 0; y < buffer.Size.Height; y++)
                for (var x = 0; x < buffer.Size.Width; x++)
                {
                    var pixel = (byte*)buffer.Address + y * buffer.RowBytes + x * 4;
                    var red = pixel[redIndex];
                    var blue = pixel[blueIndex];
                    var channel = red > 180 && blue > 180 && pixel[1] < 80 ? 3
                        : red > 180 && pixel[1] < 80 && blue < 80 ? 0
                        : pixel[1] > 180 && red < 80 && blue < 80 ? 1
                        : blue > 180 && pixel[1] < 80 && red < 80 ? 2 : -1;
                    if (channel < 0) continue;
                    var old = bounds[channel];
                    bounds[channel] = (Math.Min(old.Left, x), Math.Min(old.Top, y), Math.Max(old.Right, x), Math.Max(old.Bottom, y));
                }
            }
            var diagnostic = $"Format={buffer.Format}; sub={bounds[0]}; first={bounds[1]}; following={bounds[2]}; reference={bounds[3]}";
            Assert.All(bounds, bound => Assert.True(bound.Right >= bound.Left, diagnostic));
            var (sub, first, following) = (bounds[0], bounds[1], bounds[2]);
            Assert.True(sub.Top > first.Top, diagnostic);
            // Compare the same glyph/font to isolate baseline shift from glyph ink metrics.
            var reference = bounds[3];
            Assert.InRange(sub.Top - reference.Top, 3, 5);
            Assert.InRange(sub.Bottom - reference.Bottom, 3, 5);
            Assert.True(sub.Bottom - sub.Top < first.Bottom - first.Top);
            Assert.True(sub.Left > first.Right && following.Left > sub.Right);
            Assert.InRange(Math.Abs(following.Top - first.Top), 0, 1);
            Assert.InRange(Math.Abs(following.Bottom - first.Bottom), 0, 1);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("baseline-shift='999999'")]
    [InlineData("baseline-shift='url(https://example.com)'")]
    [InlineData("x='1 2'")]
    [InlineData("rotate='30'")]
    [InlineData("dx='1 2'")]
    public void Unsupported_or_unbounded_span_positioning_is_rejected(string attributes)
    {
        var bytes = Encoding.UTF8.GetBytes($"<svg xmlns='http://www.w3.org/2000/svg' width='100' height='100'><text x='10' y='30'>V<tspan {attributes}>F</tspan></text></svg>");
        Assert.False(SafeSvgImage.TryCreate(bytes, out _));
    }
}
