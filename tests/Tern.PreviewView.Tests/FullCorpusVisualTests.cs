using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Tern.Rendering.Preview;

namespace Tern.PreviewView.Tests;

public sealed class FullCorpusVisualTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_reference_document_has_untruncated_header_middle_and_end_captures(bool paper)
    {
        var roots = new[] { Environment.GetEnvironmentVariable("TERN_PARITY_CORPUS"), Environment.GetEnvironmentVariable("TERN_PARITY_EXTRA_CORPUS") };
        var files = roots.Where(root => root is not null).SelectMany(root => Directory.GetFiles(root!, "*.md", SearchOption.AllDirectories)).ToArray();
        if (roots.All(root => root is not null)) Assert.Equal(23, files.Length);
        var resources = Application.Current!.Resources.MergedDictionaries.ToArray();
        Application.Current.Resources.MergedDictionaries.Clear();
        Application.Current.Resources.MergedDictionaries.Add(paper ? PreviewTheme.Paper() : PreviewTheme.Midnight());
        try
        {
            foreach (var path in files.Length == 0 ? new string?[] { null } : files)
            {
                var bytes = path is null ? Encoding.UTF8.GetBytes("# Reference\n\nReadable text.\n\n```python\nprint('Original text')\n```\n") : await File.ReadAllBytesAsync(path);
                var document = Harness.Build(Encoding.UTF8.GetString(bytes), new PreviewOptions { DocumentPath = path });
                Assert.False(document.Truncated);
                var (window, view) = Harness.Show(document, 1200, 900);
                try
                {
                    view.FontSize = 16;
                    view.FontFamily = new FontFamily("Segoe UI, Helvetica, Arial, sans-serif");
                    // Compare document styling without the temporary anchor flash.
                    view.ReduceMotion = true;
                    if (Environment.GetEnvironmentVariable("TERN_PARITY_ASSETS") is { } assets)
                        view.RemoteImageLoader = async (url, token) =>
                        {
                            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(new Uri(url).AbsoluteUri))).ToLowerInvariant();
                            return await File.ReadAllBytesAsync(Path.Combine(assets, key + ".img"), token);
                        };
                    var middleHeading = document.Outline.LastOrDefault(heading => heading.Line <= document.LineCount / 2)
                        ?? (document.Outline.Count == 0 ? null : document.Outline[0]);
                    var finalHeading = document.Outline.Count == 0 ? null : document.Outline[^1];
                    var sections = new List<(string Name, int Line, OutlineEntry? Heading)>
                    {
                        ("header", 1, null), ("middle", Math.Max(1, document.LineCount / 2), null), ("end", document.LineCount, null),
                    };
                    if (middleHeading is not null) sections.Add(("matched-middle", middleHeading.Line, middleHeading));
                    if (finalHeading is not null) sections.Add(("matched-end", finalHeading.Line, finalHeading));
                    foreach (var (section, line, heading) in sections)
                    {
                        if (heading is null) view.ScrollToSourceLine(line);
                        else Assert.True(view.ScrollToAnchor(heading.Anchor), $"Missing anchor {heading.Anchor} in {path}");
                        Harness.Pump(window, 8);
                        await Task.WhenAll(Harness.All<ImageView>(view).Select(image => image.LoadingCompletion)).WaitAsync(TimeSpan.FromSeconds(15));
                        Harness.Pump(window, 8);
                        if (Environment.GetEnvironmentVariable("TERN_PARITY_ASSETS") is not null)
                            Assert.All(Harness.All<ImageView>(view), image => Assert.Equal(ImageState.Loaded, image.State));
                        if (heading is not null)
                            Assert.Contains(Harness.All<Avalonia.Controls.SelectableTextBlock>(view), text => Harness.PlainText(text) == heading.Text);
                        if (Environment.GetEnvironmentVariable("TERN_PARITY_FULL_SCREENSHOTS") is { } output)
                        {
                            Directory.CreateDirectory(output);
                            var identity = path is null ? "fixture" : Path.GetRelativePath(Directory.GetCurrentDirectory(), path).Replace(Path.DirectorySeparatorChar, '_').Replace('/', '_');
                            using var frame = Harness.Capture(window);
                            Assert.NotNull(frame);
                            Assert.True(Harness.NonBackgroundPixels(frame, new PixelRect(0, 0, frame.PixelSize.Width, frame.PixelSize.Height)) > 100);
                            using var file = File.Create(Path.Combine(output, identity + "-" + (paper ? "paper" : "midnight") + "-" + section + ".png"));
                            frame.Save(file, new PngBitmapEncoderOptions());
                        }
                    }
                }
                finally { window.Close(); await view.ImageCleanupCompletion.WaitAsync(TimeSpan.FromSeconds(3)); }
                if (path is not null) Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            }
        }
        finally
        {
            Application.Current.Resources.MergedDictionaries.Clear();
            foreach (var resource in resources) Application.Current.Resources.MergedDictionaries.Add(resource);
        }
    }
}
