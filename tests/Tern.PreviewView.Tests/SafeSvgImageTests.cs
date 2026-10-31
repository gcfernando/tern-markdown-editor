using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Tern.PreviewView.Tests;

public sealed class SafeSvgImageTests
{
    // Exact fetched Shields payload from the user-requested Python badge, including its embedded SVG logo.
    private static byte[] ActualBadge => Convert.FromBase64String("PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSIyMTMuNSIgaGVpZ2h0PSIyOCIgcm9sZT0iaW1nIiBhcmlhLWxhYmVsPSJQWVRIT046IE9CSkVDVCBNT0RFTCI+PHRpdGxlPlBZVEhPTjogT0JKRUNUIE1PREVMPC90aXRsZT48ZyBzaGFwZS1yZW5kZXJpbmc9ImNyaXNwRWRnZXMiPjxyZWN0IHdpZHRoPSI4OS41IiBoZWlnaHQ9IjI4IiBmaWxsPSIjNTU1Ii8+PHJlY3QgeD0iODkuNSIgd2lkdGg9IjEyNCIgaGVpZ2h0PSIyOCIgZmlsbD0iIzM3NzZhYiIvPjwvZz48ZyBmaWxsPSIjZmZmIiB0ZXh0LWFuY2hvcj0ibWlkZGxlIiBmb250LWZhbWlseT0iVmVyZGFuYSxHZW5ldmEsRGVqYVZ1IFNhbnMsc2Fucy1zZXJpZiIgdGV4dC1yZW5kZXJpbmc9Imdlb21ldHJpY1ByZWNpc2lvbiIgZm9udC1zaXplPSIxMDAiPjxpbWFnZSB4PSI5IiB5PSI3IiB3aWR0aD0iMTQiIGhlaWdodD0iMTQiIGhyZWY9ImRhdGE6aW1hZ2Uvc3ZnK3htbDtiYXNlNjQsUEhOMlp5Qm1hV3hzUFNKM2FHbDBaU0lnY205c1pUMGlhVzFuSWlCMmFXVjNRbTk0UFNJd0lEQWdNalFnTWpRaUlIaHRiRzV6UFNKb2RIUndPaTh2ZDNkM0xuY3pMbTl5Wnk4eU1EQXdMM04yWnlJK1BIUnBkR3hsUGxCNWRHaHZiand2ZEdsMGJHVStQSEJoZEdnZ1pEMGlUVEUwTGpJMUxqRTRiQzQ1TGpJdU56TXVNall1TlRrdU15NDBOUzR6TWk0ek5DNHpOQzR5TlM0ek5DNHhOaTR6TXk0eExqTXVNRFF1TWpZdU1ESXVNaTB1TURFdU1UTldPQzQxYkMwdU1EVXVOak10TGpFekxqVTFMUzR5TVM0ME5pMHVNall1TXpndExqTXVNekV0TGpNekxqSTFMUzR6TlM0eE9TMHVNelV1TVRRdExqTXpMakV0TGpNdU1EY3RMakkyTGpBMExTNHlNUzR3TWtnNExqYzNiQzB1TmprdU1EVXRMalU1TGpFMExTNDFMakl5TFM0ME1TNHlOeTB1TXpNdU16SXRMakkzTGpNMUxTNHlMak0yTFM0eE5TNHpOeTB1TVM0ek5TMHVNRGN1TXpJdExqQTBMakkzTFM0d01pNHlNWFl6TGpBMlNETXVNVGRzTFM0eU1TMHVNRE10TGpJNExTNHdOeTB1TXpJdExqRXlMUzR6TlMwdU1UZ3RMak0yTFM0eU5pMHVNell0TGpNMkxTNHpOUzB1TkRZdExqTXlMUzQxT1MwdU1qZ3RMamN6TFM0eU1TMHVPRGd0TGpFMExURXVNRFV0TGpBMUxURXVNak11TURZdE1TNHlNaTR4TmkweExqQTBMakkwTFM0NE55NHpNaTB1TnpFdU16WXRMalUzTGpRdExqUTBMalF5TFM0ek15NDBNaTB1TWpRdU5DMHVNVFl1TXpZdExqRXVNekl0TGpBMUxqSTBMUzR3TVdndU1UWnNMakEyTGpBeGFEZ3VNVFoyTFM0NE0wZzJMakU0YkMwdU1ERXRNaTQzTlMwdU1ESXRMak0zTGpBMUxTNHpOQzR4TVMwdU16RXVNVGN0TGpJNExqSTFMUzR5Tmk0ek1TMHVNak11TXpndExqSXVORFF0TGpFNExqVXhMUzR4TlM0MU9DMHVNVEl1TmpRdExqRXVOekV0TGpBMkxqYzNMUzR3TkM0NE5DMHVNRElnTVM0eU55NHdOWHB0TFRZdU15QXhMams0YkMwdU1qTXVNek10TGpBNExqUXhMakE0TGpReExqSXpMak0wTGpNekxqSXlMalF4TGpBNUxqUXhMUzR3T1M0ek15MHVNakl1TWpNdExqTTBMakE0TFM0ME1TMHVNRGd0TGpReExTNHlNeTB1TXpNdExqTXpMUzR5TWkwdU5ERXRMakE1TFM0ME1TNHdPWHB0TVRNdU1Ea2dNeTQ1Tld3dU1qZ3VNRFl1TXpJdU1USXVNelV1TVRndU16WXVNamN1TXpZdU16VXVNelV1TkRjdU16SXVOVGt1TWpndU56TXVNakV1T0RndU1UUWdNUzR3TkM0d05TQXhMakl6TFM0d05pQXhMakl6TFM0eE5pQXhMakEwTFM0eU5DNDROaTB1TXpJdU56RXRMak0yTGpVM0xTNDBMalExTFM0ME1pNHpNeTB1TkRJdU1qUXRMalF1TVRZdExqTTJMakE1TFM0ek1pNHdOUzB1TWpRdU1ESXRMakUyTFM0d01XZ3RPQzR5TW5ZdU9ESm9OUzQ0Tkd3dU1ERWdNaTQzTmk0d01pNHpOaTB1TURVdU16UXRMakV4TGpNeExTNHhOeTR5T1MwdU1qVXVNalV0TGpNeExqSTBMUzR6T0M0eUxTNDBOQzR4TnkwdU5URXVNVFV0TGpVNExqRXpMUzQyTkM0d09TMHVOekV1TURjdExqYzNMakEwTFM0NE5DNHdNUzB4TGpJM0xTNHdOQzB4TGpBM0xTNHhOQzB1T1MwdU1pMHVOek10TGpJMUxTNDFPUzB1TXkwdU5EVXRMak16TFM0ek5DMHVNelF0TGpJMUxTNHpOQzB1TVRZdExqTXpMUzR4TFM0ekxTNHdOQzB1TWpVdExqQXlMUzR5TGpBeExTNHhNM1l0TlM0ek5Hd3VNRFV0TGpZMExqRXpMUzQxTkM0eU1TMHVORFl1TWpZdExqTTRMak10TGpNeUxqTXpMUzR5TkM0ek5TMHVNaTR6TlMwdU1UUXVNek10TGpFdU15MHVNRFl1TWpZdExqQTBMakl4TFM0d01pNHhNeTB1TURGb05TNDROR3d1TmprdExqQTFMalU1TFM0eE5DNDFMUzR5TVM0ME1TMHVNamd1TXpNdExqTXlMakkzTFM0ek5TNHlMUzR6Tmk0eE5TMHVNell1TVMwdU16VXVNRGN0TGpNeUxqQTBMUzR5T0M0d01pMHVNakZXTmk0d04yZ3lMakE1YkM0eE5DNHdNWHB0TFRZdU5EY2dNVFF1TWpWc0xTNHlNeTR6TXkwdU1EZ3VOREV1TURndU5ERXVNak11TXpNdU16TXVNak11TkRFdU1EZ3VOREV0TGpBNExqTXpMUzR5TXk0eU15MHVNek11TURndExqUXhMUzR3T0MwdU5ERXRMakl6TFM0ek15MHVNek10TGpJekxTNDBNUzB1TURndExqUXhMakE0ZWlJdlBqd3ZjM1puUGc9PSIvPjx0ZXh0IHRyYW5zZm9ybT0ic2NhbGUoLjEpIiB4PSI1MzIuNSIgeT0iMTc1IiB0ZXh0TGVuZ3RoPSI0ODUiPlBZVEhPTjwvdGV4dD48dGV4dCB0cmFuc2Zvcm09InNjYWxlKC4xKSIgeD0iMTUxNSIgeT0iMTc1IiB0ZXh0TGVuZ3RoPSIxMDAwIiBmb250LXdlaWdodD0iYm9sZCI+T0JKRUNUIE1PREVMPC90ZXh0PjwvZz48L3N2Zz4=");

    [AvaloniaFact]
    public void Actual_shields_payload_keeps_intrinsic_dimensions_colors_text_and_logo()
    {
        Assert.True(SafeSvgImage.TryCreate(ActualBadge, out var control));
        Assert.NotNull(control);
        Assert.Equal("PYTHON: OBJECT MODEL", AutomationProperties.GetName(control));
        control.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        control.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        control.Measure(Size.Infinity);
        Assert.InRange(control.DesiredSize.Width, 213, 214);
        Assert.Equal(28, control.DesiredSize.Height);
        var window = new Window { Width = 300, Height = 80, Content = new Border { Padding = new Thickness(8), Child = control } };
        window.Show();
        try
        {
            Harness.Pump(window);
            using var frame = Harness.Capture(window);
            Assert.NotNull(frame);
            var origin = control.TranslatePoint(default, window)!.Value;
            Assert.Equal(Color.Parse("#555555"), Pixel(frame, (int)origin.X + 5, (int)origin.Y + 5));
            Assert.Equal(Color.Parse("#3776AB"), Pixel(frame, (int)origin.X + 100, (int)origin.Y + 5));
            Assert.True(WhitePixels(frame, (int)origin.X + 9, (int)origin.Y + 7, 14, 14) > 30, "The actual embedded Python logo must be drawn.");
            Assert.True(WhitePixels(frame, (int)origin.X + 102, (int)origin.Y + 7, 110, 15) > 100, "The actual right-hand text must be drawn.");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("<!DOCTYPE svg [<!ENTITY x SYSTEM 'file:///private.txt'>]><svg width='10' height='10'><text>&x;</text></svg>")]
    [InlineData("<svg width='10' height='10'><script>alert(1)</script></svg>")]
    [InlineData("<svg width='10' height='10' onload='alert(1)'/>")]
    [InlineData("<svg width='10' height='10'><image href='https://example.com/track.svg'/></svg>")]
    [InlineData("<svg width='10' height='10'><image href='file:///private.svg'/></svg>")]
    [InlineData("<svg width='10' height='10'><use href='#shape'/></svg>")]
    [InlineData("<svg width='10' height='10'><foreignObject/></svg>")]
    [InlineData("<svg width='10' height='10'><rect style='fill:red' width='10' height='10'/></svg>")]
    [InlineData("<svg width='10' height='10'><rect fill='url(https://example.com/paint)' width='10' height='10'/></svg>")]
    [InlineData("<svg width='10' height='10'><text font-family='https://example.com/font'>x</text></svg>")]
    [InlineData("<svg width='4097' height='1'/>")]
    [InlineData("<svg width='4096' height='4096'/>")]
    [InlineData("<svg width='10' height='10'><g transform='scale(1000000) scale(1000000)'/></svg>")]
    public void Active_external_or_over_budget_content_is_rejected(string svg)
    {
        Assert.False(SafeSvgImage.TryCreate(Encoding.UTF8.GetBytes(svg), out var control));
        Assert.Null(control);
    }

    [AvaloniaFact]
    public void Nested_images_depth_and_geometry_share_the_same_limits()
    {
        var svg = "<svg width='10' height='10'><path d='M0 0 L1 1'/></svg>";
        for (var depth = 0; depth < 9; depth++)
            svg = "<svg width='10' height='10'><image href='data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg)) + "'/></svg>";
        Assert.False(SafeSvgImage.TryCreate(Encoding.UTF8.GetBytes(svg), out _));
        var deep = "<svg width='10' height='10'>" + string.Concat(Enumerable.Repeat("<g>", 33)) + string.Concat(Enumerable.Repeat("</g>", 33)) + "</svg>";
        Assert.False(SafeSvgImage.TryCreate(Encoding.UTF8.GetBytes(deep), out _));
        var many = "<svg width='10' height='10'>" + string.Concat(Enumerable.Repeat("<rect width='1' height='1'/>", 10_001)) + "</svg>";
        Assert.False(SafeSvgImage.TryCreate(Encoding.UTF8.GetBytes(many), out _));
        var path = "<svg width='10' height='10'><path d='M0 0 " + string.Concat(Enumerable.Repeat("L1 1 ", 10_001)) + "'/></svg>";
        Assert.False(SafeSvgImage.TryCreate(Encoding.UTF8.GetBytes(path), out _));
        Assert.False(SafeSvgImage.TryCreate(new byte[1024 * 1024 + 1], out _));
    }

    [AvaloniaFact]
    public void Local_gradient_clip_and_transform_primitives_are_accepted()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="20">
              <defs>
                <linearGradient id="paint" x2="0" y2="100%"><stop offset="0" stop-color="#bbb" stop-opacity=".1"/><stop offset="1" stop-opacity=".1"/></linearGradient>
                <clipPath id="round"><rect width="100" height="20" rx="3"/></clipPath>
              </defs>
              <g clip-path="url(#round)" transform="translate(1,0) scale(.9)">
                <rect width="100" height="20" fill="#555"/><rect width="100" height="20" fill="url(#paint)"/>
              </g>
            </svg>
            """;
        Assert.True(SafeSvgImage.TryCreate(Encoding.UTF8.GetBytes(svg), out _));
    }

    [AvaloniaFact]
    public void Actual_plain_shields_payload_keeps_gradient_clip_and_blurred_shadow()
    {
        const string svg = """<svg xmlns="http://www.w3.org/2000/svg" width="116" height="20" role="img" aria-label="Format: Markdown"><title>Format: Markdown</title><filter id="blur"><feGaussianBlur stdDeviation="16"/></filter><linearGradient id="s" x2="0" y2="100%"><stop offset="0" stop-color="#bbb" stop-opacity=".1"/><stop offset="1" stop-opacity=".1"/></linearGradient><clipPath id="r"><rect width="116" height="20" rx="3"/></clipPath><g clip-path="url(#r)"><rect width="49" height="20" fill="#555"/><rect x="49" width="67" height="20" fill="#7c3aed"/><rect width="116" height="20" fill="url(#s)"/></g><g fill="#fff" text-anchor="middle" font-family="Verdana,Geneva,DejaVu Sans,sans-serif" text-rendering="geometricPrecision" font-size="110"><g transform="scale(.1)"><g aria-hidden="true" fill="#010101"><text x="255" y="150" fill-opacity=".8" filter="url(#blur)" textLength="390">Format</text><text x="255" y="150" fill-opacity=".3" textLength="390">Format</text></g><text x="255" y="140" textLength="390">Format</text></g><g transform="scale(.1)"><g aria-hidden="true" fill="#010101"><text x="815" y="150" fill-opacity=".8" filter="url(#blur)" textLength="570">Markdown</text><text x="815" y="150" fill-opacity=".3" textLength="570">Markdown</text></g><text x="815" y="140" textLength="570">Markdown</text></g></g></svg>""";
        Assert.True(SafeSvgImage.TryCreate(Encoding.UTF8.GetBytes(svg), out var control));
        Assert.NotNull(control);
        control.Measure(Size.Infinity);
        Assert.Equal(new Size(116, 20), control.DesiredSize);
        Assert.Equal("Format: Markdown", AutomationProperties.GetName(control));
    }
    private static int WhitePixels(WriteableBitmap bitmap, int x, int y, int width, int height)
    {
        var count = 0;
        for (var row = y; row < y + height; row++)
            for (var column = x; column < x + width; column++)
            {
                var pixel = Pixel(bitmap, column, row);
                if (pixel.R > 230 && pixel.G > 230 && pixel.B > 230) count++;
            }
        return count;
    }

    private static unsafe Color Pixel(WriteableBitmap bitmap, int x, int y)
    {
        using var buffer = bitmap.Lock();
        var pixel = (byte*)buffer.Address + y * buffer.RowBytes + x * 4;
        return buffer.Format == Avalonia.Platform.PixelFormat.Rgba8888
            ? Color.FromArgb(pixel[3], pixel[0], pixel[1], pixel[2])
            : Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }
}
