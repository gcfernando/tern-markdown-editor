using System.Security.Cryptography;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Tern.Rendering.Preview;

namespace Tern.PreviewView.Tests;

public sealed class RepositoryToolsParityTests
{
    [AvaloniaTheory]
    [InlineData(800, false)]
    [InlineData(1200, false)]
    [InlineData(800, true)]
    [InlineData(1200, true)]
    public async Task Tools_repository_headers_tables_details_and_real_screenshots_remain_readable(int width, bool paper)
    {
        var resources = Application.Current!.Resources.MergedDictionaries.ToArray();
        Application.Current.Resources.MergedDictionaries.Clear();
        Application.Current.Resources.MergedDictionaries.Add(paper ? PreviewTheme.Paper() : PreviewTheme.Midnight());
        try
        {
            foreach (var name in new[] { "README.md", "Files/ai_coding_plugins_beginners_guide.md", "Files/iptv_world_explorer_2026.md" })
            {
                var root = Environment.GetEnvironmentVariable("TERN_PARITY_EXTRA_CORPUS");
                var path = root is null ? null : Path.Combine(root, name);
                var source = path is null ? "<a id=\"top\"></a>\n<h1 align=\"center\">Developer tools</h1>\n\n| Tool | Purpose |\n|---|---|\n| Editor | Markdown |\n\n<details>\n<summary>Example</summary>\n\nUseful details.\n\n</details>" : await File.ReadAllTextAsync(path);
                var before = path is null ? null : SHA256.HashData(await File.ReadAllBytesAsync(path));
                var document = Harness.Build(source, new PreviewOptions { DocumentPath = path, WorkspaceRoot = root });
                Assert.False(document.Truncated);
                var (window, view) = Harness.Show(document, width, 1000);
                try
                {
                    view.FontSize = 16;
                    view.FontFamily = new FontFamily("Segoe UI, Helvetica, Arial, sans-serif");
                    if (Environment.GetEnvironmentVariable("TERN_PARITY_ASSETS") is { } assets)
                        view.RemoteImageLoader = async (url, token) =>
                        {
                            var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(new Uri(url).AbsoluteUri))).ToLowerInvariant();
                            var file = Path.Combine(assets, key + ".img");
                            return File.Exists(file) ? await File.ReadAllBytesAsync(file, token) : null;
                        };
                    Harness.Pump(window, 8);
                    await ImagesReady(view);
                    Save(window, name, width, paper, "header");
                    if (path is not null && name.EndsWith("iptv_world_explorer_2026.md", StringComparison.Ordinal))
                    {
                        var marker = source.IndexOf("![VLC Open Media", StringComparison.Ordinal);
                        Assert.True(marker >= 0);
                        view.ScrollToSourceLine(1 + source.AsSpan(0, marker).Count('\n'));
                        Harness.Pump(window, 8);
                        await ImagesReady(view);
                        var actualImage = Assert.Single(Harness.All<ImageView>(view), image => image.Alt.StartsWith("VLC Open Media", StringComparison.Ordinal) && image.State == ImageState.Loaded);
                        var position = actualImage.TranslatePoint(default, view);
                        Assert.NotNull(position);
                        Assert.InRange(position.Value.Y, -1, 100);
                        Assert.True(position.Value.Y + actualImage.Bounds.Height > 0);
                        Save(window, name, width, paper, "actual-screenshots");
                    }
                }
                finally { window.Close(); await view.ImageCleanupCompletion.WaitAsync(TimeSpan.FromSeconds(3)); }
                if (path is not null) Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(path)));
            }
        }
        finally
        {
            Application.Current.Resources.MergedDictionaries.Clear();
            foreach (var dictionary in resources) Application.Current.Resources.MergedDictionaries.Add(dictionary);
        }
    }

    private static async Task ImagesReady(PreviewView view)
    {
        for (var i = 0; i < 300 && Harness.All<ImageView>(view).Any(image => image.State == ImageState.Loading); i++) await Task.Delay(10);
        Assert.DoesNotContain(Harness.All<ImageView>(view), image => image.State == ImageState.Loading);
        if (Environment.GetEnvironmentVariable("TERN_PARITY_ASSETS") is not null)
            Assert.All(Harness.All<ImageView>(view), image => Assert.Equal(ImageState.Loaded, image.State));
    }

    private static void Save(Avalonia.Controls.Window window, string name, int width, bool paper, string section)
    {
        var output = Environment.GetEnvironmentVariable("TERN_PARITY_SCREENSHOTS");
        if (output is null) return;
        Directory.CreateDirectory(output);
        using var frame = Harness.Capture(window);
        Assert.NotNull(frame);
        using var stream = File.Create(Path.Combine(output, "tools-" + Path.GetFileNameWithoutExtension(name) + "-" + (paper ? "paper" : "midnight") + "-" + width + "-" + section + ".png"));
        frame.Save(stream, new PngBitmapEncoderOptions());
    }
}
