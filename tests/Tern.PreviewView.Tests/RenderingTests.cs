using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Tern.Rendering.Preview;
using Tern.Rendering.Resources;

namespace Tern.PreviewView.Tests;

public class RenderingTests
{
    private const string Mermaid = """
        ```mermaid
        flowchart LR
          A[Start] --> B{Choice}
          B -->|yes| C((Done))
          B -.->|no| D[(Store)]
          subgraph S[Group]
            E[Inner] ==> F>Flag]
          end
        ```
        """;

    private const string Sequence = """
        ```mermaid
        sequenceDiagram
          participant A as Alice
          participant B as Bob
          A->>B: Hello
          B-->>A: Hi
          Note over A,B: chatting
        ```
        """;

    [AvaloniaFact]
    public void MathBlock_HasBoundsAndDrawsPixels()
    {
        var (window, view) = Harness.Show(Harness.Build("$$\n\\frac{a+b}{\\sqrt{c}} = \\sum_{i=0}^{n} x_i^2\n$$\n"));
        var math = Assert.Single(Harness.All<MathControl>(view));
        Assert.True(math.Bounds.Width > 40 && math.Bounds.Height > 15);
        Assert.False(math.Result!.IsError);
        var frame = Harness.Capture(window);
        Assert.NotNull(frame);
        Assert.True(Harness.NonBackgroundPixels(frame, Harness.RegionOf(math, window)) > 150);
        window.Close();
    }

    [AvaloniaFact]
    public void MathBlock_ErrorShowsSourceAndMessage()
    {
        var (window, view) = Harness.Show(Harness.Build("$$\n\\frac{1\n$$\n"));
        var texts = Harness.All<TextBlock>(view).Select(Harness.PlainText).ToList();
        Assert.Contains(texts, t => t.Contains("\\frac{1", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.StartsWith("Math error", StringComparison.Ordinal));
        window.Close();
    }

    [AvaloniaFact]
    public void InlineMath_IsReadableUnicodeInTheTextFlow()
    {
        var (window, view) = Harness.Show(Harness.Build("Energy $E = mc^2$ here.\n"));
        var paragraph = Harness.All<RichTextBlock>(view).First();
        Assert.Contains("mc", Harness.PlainText(paragraph), StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void Flowchart_DrawsNodesAndPixels()
    {
        var (window, view) = Harness.Show(Harness.Build(Mermaid));
        var diagram = Assert.Single(Harness.All<DiagramControl>(view));
        Assert.True(diagram.ElementCount >= 6);
        Assert.True(diagram.Bounds.Width > 100 && diagram.Bounds.Height > 50);
        var frame = Harness.Capture(window);
        Assert.True(Harness.NonBackgroundPixels(frame!, Harness.RegionOf(diagram, window)) > 500);
        window.Close();
    }

    [AvaloniaFact]
    public void SequenceDiagram_DrawsPixels()
    {
        var (window, view) = Harness.Show(Harness.Build(Sequence));
        var diagram = Assert.Single(Harness.All<DiagramControl>(view));
        Assert.Equal(2, diagram.ElementCount);
        var frame = Harness.Capture(window);
        Assert.True(Harness.NonBackgroundPixels(frame!, Harness.RegionOf(diagram, window)) > 500);
        window.Close();
    }

    [AvaloniaFact]
    public void WideDiagram_ScalesDownToTheColumn()
    {
        var chain = string.Join("\n", Enumerable.Range(0, 14).Select(i => $"  N{i}[Node number {i}] --> N{i + 1}[Node number {i + 1}]"));
        var (window, view) = Harness.Show(Harness.Build($"```mermaid\nflowchart LR\n{chain}\n```\n"), 700, 600);
        var diagram = Assert.Single(Harness.All<DiagramControl>(view));
        Assert.True(diagram.DiagramWidth > 1000);
        Assert.True(Harness.All<FitScroller>(view).Count > 0);
        window.Close();
    }

    [AvaloniaFact]
    public void UnsupportedDiagrams_ShowSourceWithNotice()
    {
        var (window, view) = Harness.Show(Harness.Build("```mermaid\njourney\n  title Pet care\n  section Morning\n    Feed dogs: 5: Owner\n```\n\n```plantuml\n@startuml\nA -> B\n@enduml\n```\n\n```mermaid\nflowchart LR\n  A --> \n```\n"));
        Assert.Empty(Harness.All<DiagramControl>(view));
        var codes = Harness.All<CodeBlockView>(view);
        Assert.Equal(3, codes.Count);
        var texts = Harness.All<TextBlock>(view).Select(Harness.PlainText).ToList();
        Assert.Single(texts, t => t.Contains("not supported offline", StringComparison.Ordinal) && t.Contains("journey", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("could not be drawn", StringComparison.Ordinal));
        Assert.Contains(codes, c => c.Code.Contains("Feed dogs: 5: Owner", StringComparison.Ordinal));
        Assert.Contains(codes, c => c.Code.Contains("@startuml", StringComparison.Ordinal));
        window.Close();
    }

    [AvaloniaFact]
    public void CodeBlock_DrawsPixels()
    {
        var (window, view) = Harness.Show(Harness.Build("```csharp\nvar total = items.Sum(i => i.Price);\nConsole.WriteLine(total);\n```\n"));
        var code = Assert.Single(Harness.All<CodeBlockView>(view));
        var frame = Harness.Capture(window);
        Assert.True(Harness.NonBackgroundPixels(frame!, Harness.RegionOf(code, window)) > 300);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Images_RenderLocalRasterAndNeverExternal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tern-pv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Window? window = null;
        try
        {
            using (var bitmap = new WriteableBitmap(new PixelSize(64, 32), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul))
            {
                using (var buffer = bitmap.Lock())
                {
                    unsafe
                    {
                        new Span<uint>((void*)buffer.Address, 64 * 32).Fill(0xFF2DD4BF);
                    }
                }

                bitmap.Save(Path.Combine(dir, "pic.png"), new PngBitmapEncoderOptions());
            }

            File.WriteAllText(Path.Combine(dir, "vector.svg"), "<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>");
            var options = new PreviewOptions { DocumentPath = Path.Combine(dir, "main.md"), WorkspaceRoot = dir };
            var shown = Harness.Show(Harness.Build("![A picture](pic.png)\n\n![Vector](vector.svg)\n\n![Gone](missing.png)\n\n![Remote](https://example.com/x.png)\n\n<div><img src=\"pic.png\" alt=\"HTML picture\"></div>\n", options));
            window = shown.Window;
            var view = shown.View;
            var images = Harness.All<ImageView>(view);
            Assert.Equal(5, images.Count);
            for (var i = 0; i < 200 && images.Any(image => image.State == ImageState.Loading); i++)
            {
                await Task.Delay(10);
            }

            Harness.Pump(window);
            Assert.Equal(ImageState.Loaded, images[0].State);
            Assert.Equal(ImageState.Unavailable, images[1].State);
            Assert.Equal(ImageState.Unavailable, images[2].State);
            Assert.Equal(ImageState.Unavailable, images[3].State);
            Assert.Equal(ImageState.Loaded, images[4].State);
            var texts = Harness.All<TextBlock>(images[1]).Select(t => t.Text).ToList();
            Assert.Contains(texts, t => (t ?? "").Contains("image unavailable", StringComparison.Ordinal));
            Assert.Contains(Harness.All<TextBlock>(images[2]).Select(t => t.Text), t => (t ?? "").Contains("image unavailable", StringComparison.Ordinal));
            Assert.True(Harness.All<Image>(images[0]).Count == 1);
            Assert.True(ImageView.LivePixels > 0);
            window.Close();
            window = null;
            Dispatcher.UIThread.RunJobs();
            await view.ImageCleanupCompletion.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, ImageView.LivePixels);
        }
        finally
        {
            window?.Close();
            Directory.Delete(dir, true);
        }
    }

    [AvaloniaFact]
    public async Task Tall_images_are_downsampled_and_reload_after_reattachment()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tern-pv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Window? window = null;
        try
        {
            var path = Path.Combine(dir, "tall.png");
            using (var bitmap = new WriteableBitmap(new PixelSize(64, 4096), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul))
            {
                bitmap.Save(path, new PngBitmapEncoderOptions());
            }

            var options = new PreviewOptions { DocumentPath = Path.Combine(dir, "main.md") };
            var shown = Harness.Show(Harness.Build("![Tall](tall.png)", options));
            window = shown.Window;
            var view = shown.View;
            var image = Assert.Single(Harness.All<ImageView>(view));
            await WaitForImageAsync(image);
            var decoded = Assert.IsType<Bitmap>(Assert.Single(Harness.All<Image>(image)).Source);
            Assert.InRange(decoded.PixelSize.Height, 1, 2048);
            Assert.InRange(decoded.PixelSize.Width, 1, 64);
            view.FontSize += 1;
            Harness.Pump(window);
            var rebuiltImage = Assert.Single(Harness.All<ImageView>(view));
            await WaitForImageAsync(rebuiltImage);
            var rebuiltBitmap = Assert.IsType<Bitmap>(Assert.Single(Harness.All<Image>(rebuiltImage)).Source);
            Assert.NotSame(decoded, rebuiltBitmap);
            await view.ImageCleanupCompletion.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal((long)rebuiltBitmap.PixelSize.Width * rebuiltBitmap.PixelSize.Height, ImageView.LivePixels);
            decoded = rebuiltBitmap;
            window.Content = null;
            Dispatcher.UIThread.RunJobs();
            await view.ImageCleanupCompletion.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, ImageView.LivePixels);
            window.Content = view;
            Harness.Pump(window);
            // Inherited preview font properties can legitimately rebuild its block controls
            // when the whole preview is reattached. Verify the currently realised image.
            var reattachedImage = Assert.Single(Harness.All<ImageView>(view));
            await WaitForImageAsync(reattachedImage);
            var reloaded = Assert.IsType<Bitmap>(Assert.Single(Harness.All<Image>(reattachedImage)).Source);
            Assert.NotSame(decoded, reloaded);
            Assert.InRange(reloaded.PixelSize.Height, 1, 2048);
            window.Close();
            window = null;
            Dispatcher.UIThread.RunJobs();
            await view.ImageCleanupCompletion.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, ImageView.LivePixels);
        }
        finally
        {
            window?.Close();
            Directory.Delete(dir, true);
        }
    }

    [AvaloniaFact]
    public async Task Image_bitmap_survives_transient_reparent_and_releases_after_durable_detach()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tern-pv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Window? window = null;
        try
        {
            var path = Path.Combine(dir, "picture.png");
            using (var source = new WriteableBitmap(new PixelSize(64, 32), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul))
            {
                source.Save(path, new PngBitmapEncoderOptions());
            }

            var image = new ImageView(new LocalFile(path, null, false, true), "Picture", null);
            var first = new Border { Child = image };
            var second = new Border();
            window = new Window { Width = 400, Height = 300, Content = new StackPanel { Children = { first, second } } };
            window.Show();
            Harness.Pump(window);
            await WaitForImageAsync(image);
            var decoded = Assert.IsType<Bitmap>(Assert.Single(Harness.All<Image>(image)).Source);

            first.Child = null;
            second.Child = image;
            Harness.Pump(window);
            Assert.Equal(ImageState.Loaded, image.State);
            Assert.Same(decoded, Assert.Single(Harness.All<Image>(image)).Source);

            second.Child = null;
            Dispatcher.UIThread.RunJobs();
            await image.LoadingCompletion.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, ImageView.LivePixels);
            first.Child = image;
            Harness.Pump(window);
            await WaitForImageAsync(image);
            Assert.NotSame(decoded, Assert.Single(Harness.All<Image>(image)).Source);
            window.Close();
            window = null;
            Dispatcher.UIThread.RunJobs();
            await image.LoadingCompletion.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, ImageView.LivePixels);
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
            Directory.Delete(dir, true);
        }
    }

    private static async Task WaitForImageAsync(ImageView image)
    {
        for (var i = 0; i < 200 && image.State == ImageState.Loading; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(ImageState.Loaded, image.State);
    }

    [AvaloniaFact]
    public async Task Canceled_inflight_decode_releases_its_reservation_when_loading_completes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tern-pv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Window? window = null;
        ImageView? image = null;
        try
        {
            var path = Path.Combine(dir, "picture.png");
            using (var source = new WriteableBitmap(new PixelSize(64, 32), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul))
            {
                source.Save(path, new PngBitmapEncoderOptions());
            }

            image = new ImageView(new LocalFile(path, null, false, true), "Picture", null, beforeDecode: () =>
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("The decoder lifecycle checkpoint was not released.");
            });
            window = new Window { Width = 400, Height = 300, Content = image };
            window.Show();
            Harness.Pump(window);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, ImageView.ActiveDecodes);
            Assert.Equal(64 * 32, ImageView.LivePixels);

            image.Dispose();
            Assert.False(image.LoadingCompletion.IsCompleted);
            Assert.Equal(64 * 32, ImageView.LivePixels);
            release.Set();
            await image.LoadingCompletion.WaitAsync(TimeSpan.FromSeconds(2));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, ImageView.ActiveDecodes);
            Assert.Equal(0, ImageView.LivePixels);
            Assert.Empty(Harness.All<Image>(image));
        }
        finally
        {
            release.Set();
            window?.Close();
            Dispatcher.UIThread.RunJobs();
            if (image is not null) await image.LoadingCompletion.WaitAsync(TimeSpan.FromSeconds(2));
            Directory.Delete(dir, true);
        }
    }

    [AvaloniaFact]
    public async Task Replacing_documents_and_pruning_image_slots_release_their_pixels()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tern-pv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Window? window = null;
        try
        {
            var path = Path.Combine(dir, "picture.png");
            using (var source = new WriteableBitmap(new PixelSize(64, 32), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul))
            {
                source.Save(path, new PngBitmapEncoderOptions());
            }

            var options = new PreviewOptions { DocumentPath = Path.Combine(dir, "main.md") };
            var shown = Harness.Show(Harness.Build("![Picture](picture.png)", options), 900, 500);
            window = shown.Window;
            var view = shown.View;
            for (var revision = 0; revision < 4; revision++)
            {
                var currentImage = Assert.Single(Harness.All<ImageView>(view));
                await WaitForImageAsync(currentImage);
                Assert.Equal(64 * 32, ImageView.LivePixels);
                view.Document = Harness.Build("# No image " + revision, options);
                Harness.Pump(window);
                Dispatcher.UIThread.RunJobs();
                await currentImage.LoadingCompletion.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(0, ImageView.LivePixels);
                view.Document = Harness.Build("![Picture](picture.png)", options);
                Harness.Pump(window);
            }

            var longSource = "![Picture](picture.png)\n\n" + string.Join("\n\n", Enumerable.Range(0, 400).Select(index => $"Paragraph {index} with enough words to occupy the reading column."));
            view.Document = Harness.Build(longSource, options);
            Harness.Pump(window);
            var imageToPrune = Assert.Single(Harness.All<ImageView>(view));
            await WaitForImageAsync(imageToPrune);
            view.ScrollToSourceLine(601);
            Harness.Pump(window, 8);
            Dispatcher.UIThread.RunJobs();
            await imageToPrune.LoadingCompletion.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Empty(Harness.All<ImageView>(view));
            Assert.Equal(0, ImageView.LivePixels);
            window.Close();
            window = null;
            Dispatcher.UIThread.RunJobs();
            await view.ImageCleanupCompletion.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, ImageView.LivePixels);
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
            Directory.Delete(dir, true);
        }
    }

    [AvaloniaFact]
    public void RichDocument_IsSavedAsPngForBothThemes()
    {
        var markdown = """
            # Tern preview

            A paragraph with **bold**, *italic*, ~~strike~~, `inline code`, a [link](https://example.com) and math $a^2+b^2=c^2$.

            ## Lists

            - First
            - Second
              - Nested
            - [x] Done
            - [ ] Pending

            > A quotation with a bar on the left.

            > [!NOTE]
            > Useful information.

            > [!WARNING]
            > Be careful here.

            ```csharp
            public int Add(int a, int b) => a + b;
            ```

            | Name | Value |
            |------|-------|
            | Alpha | 1 |
            | Beta | 2 |

            $$
            x = \frac{-b \pm \sqrt{b^2-4ac}}{2a}
            $$

            ```mermaid
            flowchart LR
              A[Write] --> B{Preview?}
              B -->|yes| C[Read]
              B -->|no| A
            ```
            """;
        var document = Harness.Build(markdown);
        var temp = Path.GetTempPath();
        foreach (var (name, theme) in new[] { ("midnight", PreviewTheme.Midnight()), ("paper", PreviewTheme.Paper()) })
        {
            Application.Current!.Resources.MergedDictionaries.Clear();
            Application.Current.Resources.MergedDictionaries.Add(theme);
            var (window, _) = Harness.Show(document, 900, 1300);
            var frame = Harness.Capture(window);
            Assert.NotNull(frame);
            var path = Path.Combine(temp, $"tern-preview-{name}.png");
            frame.Save(path, new PngBitmapEncoderOptions());
            Assert.True(new FileInfo(path).Length > 5000);
            window.Close();
        }

        Application.Current!.Resources.MergedDictionaries.Clear();
        Application.Current.Resources.MergedDictionaries.Add(PreviewTheme.Midnight());
    }
}
