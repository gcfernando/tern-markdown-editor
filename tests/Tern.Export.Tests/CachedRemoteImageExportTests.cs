using System.Text;
using Tern.Rendering.Resources;

namespace Tern.Export.Tests;

public sealed class CachedRemoteImageExportTests
{
    [Fact]
    public void Actual_cached_corpus_images_are_embedded_as_original_bytes_without_remote_requests()
    {
        var manifest = Environment.GetEnvironmentVariable("TERN_PARITY_ASSETS_MANIFEST");
        var images = new List<(string Url, byte[] Bytes)>();
        if (manifest is not null)
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
            foreach (var item in json.RootElement.EnumerateArray())
            {
                Assert.Equal("Downloaded", item.GetProperty("status").GetString());
                images.Add((item.GetProperty("url").GetString()!, File.ReadAllBytes(item.GetProperty("path").GetString()!)));
            }
            Assert.Equal(46, images.Count);
        }
        else images.Add(("https://example.com/badge.svg", File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "python-badge.svg"))));
        foreach (var (url, bytes) in images)
        {
            var result = HtmlExporter.Export("![Original image](" + url + ")", new HtmlExportOptions { RemoteImageBytes = destination => destination == url ? bytes : null });
            Assert.Empty(result.Warnings);
            Assert.Equal(1, result.EmbeddedImageCount);
            Assert.Contains(Convert.ToBase64String(bytes), result.Html, StringComparison.Ordinal);
            Assert.DoesNotContain("src=\"https:", result.Html, StringComparison.Ordinal);
            Assert.DoesNotContain("src=\"http:", result.Html, StringComparison.Ordinal);
        }
    }
    [Fact]
    public void Cached_png_is_embedded_without_a_remote_source_attribute()
    {
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jD1sAAAAASUVORK5CYII=");
        var result = HtmlExporter.Export("![pixel](https://example.com/pixel.png)",
            new HtmlExportOptions { RemoteImageBytes = _ => bytes });
        Assert.Equal(1, result.EmbeddedImageCount);
        Assert.Empty(result.Warnings);
        Assert.Contains("data:image/png;base64," + Convert.ToBase64String(bytes), result.Html);
    }

    [Fact]
    public void Authentic_badge_with_embedded_svg_icon_is_embedded_once_from_host_cache()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "python-badge.svg"));
        var calls = 0;
        var result = HtmlExporter.Export("![Python](https://example.com/badge) ![Again](https://example.com/badge)",
            new HtmlExportOptions { RemoteImageBytes = url => { Assert.Equal("https://example.com/badge", url); calls++; return bytes; } });
        Assert.Equal(1, calls);
        Assert.Equal(1, result.EmbeddedImageCount);
        Assert.Empty(result.Warnings);
        Assert.Contains("data:image/svg+xml;base64," + Convert.ToBase64String(bytes), result.Html);
        Assert.DoesNotContain("src=\"https://", result.Html);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<foreignObject><p>HTML</p></foreignObject>")]
    [InlineData("<rect onclick='alert(1)'/>")]
    [InlineData("<rect style='fill:red'/>")]
    [InlineData("<image href='https://example.com/x.svg'/>")]
    [InlineData("<rect fill='url(https://example.com/x)'/>")]
    [InlineData("<image href='data:image/svg+xml;base64,PHN2Zz48c2NyaXB0Lz48L3N2Zz4='/>")]
    public void Active_or_external_svg_content_is_rejected(string content)
    {
        var bytes = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'>" + content + "</svg>");
        Assert.False(SafeSvgPolicy.IsSafe(bytes));
        var result = HtmlExporter.Export("![bad](https://example.com/bad)", new HtmlExportOptions { RemoteImageBytes = _ => bytes });
        Assert.Equal(0, result.EmbeddedImageCount);
        Assert.Contains("image-placeholder", result.Html);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void Cache_callback_respects_budgets_and_does_not_override_other_block_reasons()
    {
        var calls = 0;
        var result = HtmlExporter.Export("![remote](https://example.com/a) ![script](javascript:alert)",
            new HtmlExportOptions { MaxInlineImageBytes = 2, RemoteImageBytes = _ => { calls++; return new byte[10]; } });
        Assert.Equal(1, calls);
        Assert.Equal(0, result.EmbeddedImageCount);
        Assert.Contains(result.Warnings, warning => warning.Contains("budget", StringComparison.Ordinal));
    }

    [Fact]
    public void Cache_failure_becomes_a_warning_and_placeholder()
    {
        var result = HtmlExporter.Export("![remote](https://example.com/a)",
            new HtmlExportOptions { RemoteImageBytes = _ => throw new IOException("cache unavailable") });
        Assert.Equal(0, result.EmbeddedImageCount);
        Assert.Contains(result.Warnings, warning => warning.Contains("could not be read", StringComparison.Ordinal));
    }
}
