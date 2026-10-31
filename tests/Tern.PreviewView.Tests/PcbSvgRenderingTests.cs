using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using System.Text;
using Tern.Rendering.Preview;
using Tern.Rendering.Resources;

namespace Tern.PreviewView.Tests;

public sealed class PcbSvgRenderingTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_pcb_schematic_and_board_render_as_images_in_both_themes(bool paper)
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MarkdownSamples", "pcb_circuit_diagram.md"));
        var document = Harness.Build(source);
        var schematics = document.Blocks.OfType<ParagraphNode>().Where(p => p.Inlines.OfType<ImageNode>().Any()).ToArray();
        Assert.Equal(2, schematics.Length);
        var resources = Application.Current!.Resources.MergedDictionaries.ToArray();
        Application.Current.Resources.MergedDictionaries.Clear();
        Application.Current.Resources.MergedDictionaries.Add(paper ? PreviewTheme.Paper() : PreviewTheme.Midnight());
        try
        {
            foreach (var schematic in schematics)
            {
                var data = Assert.IsType<InlineImage>(Assert.Single(schematic.Inlines.OfType<ImageNode>()).Resolution).Data;
                var onlyImage = Harness.Build(Encoding.UTF8.GetString(data));
                var (window, view) = Harness.Show(onlyImage, 900, 600);
                try
                {
                    var image = Assert.Single(Harness.All<ImageView>(view));
                    for (var i = 0; i < 200 && image.State == ImageState.Loading; i++) await Task.Delay(10);
                    Harness.Pump(window);
                    Assert.Equal(ImageState.Loaded, image.State);
                    Assert.DoesNotContain(Harness.All<TextBlock>(image), text => (text.Text ?? "").Contains("unavailable", StringComparison.Ordinal));
                    using var frame = Harness.Capture(window);
                    Assert.NotNull(frame);
                    Assert.True(Harness.NonBackgroundPixels(frame, Harness.RegionOf(image, window)) > 5000);
                    if (Environment.GetEnvironmentVariable("TERN_PCB_SCREENSHOTS") is { } output)
                    {
                        Directory.CreateDirectory(output);
                        var name = image.Alt.StartsWith("5 V", StringComparison.Ordinal) ? "schematic" : "board";
                        using var file = File.Create(Path.Combine(output, $"pcb-{name}-{(paper ? "paper" : "midnight")}.png"));
                        frame.Save(file, new PngBitmapEncoderOptions());
                    }
                }
                finally { window.Close(); await view.ImageCleanupCompletion.WaitAsync(TimeSpan.FromSeconds(3)); }
            }
        }
        finally
        {
            Application.Current.Resources.MergedDictionaries.Clear();
            foreach (var resource in resources) Application.Current.Resources.MergedDictionaries.Add(resource);
        }
    }

    [AvaloniaFact]
    public async Task Both_user_supplied_local_circuit_svg_assets_render_without_a_network_loader()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MarkdownSamples");
        var source = "![LED circuit](assets/led-series.svg)\n\n![Voltage divider](assets/voltage-divider.svg)";
        var (window, view) = Harness.Show(Harness.Build(source, new PreviewOptions { DocumentPath = Path.Combine(root, "circuits.md"), WorkspaceRoot = root }), 900, 1000);
        try
        {
            var images = Harness.All<ImageView>(view);
            Assert.Equal(2, images.Count);
            for (var i = 0; i < 200 && images.Any(image => image.State == ImageState.Loading); i++) await Task.Delay(10);
            Harness.Pump(window);
            Assert.All(images, image => Assert.Equal(ImageState.Loaded, image.State));
            using var frame = Harness.Capture(window);
            Assert.NotNull(frame);
            Assert.All(images, image => Assert.True(Harness.NonBackgroundPixels(frame, Harness.RegionOf(image, window)) > 500));
        }
        finally { window.Close(); await view.ImageCleanupCompletion.WaitAsync(TimeSpan.FromSeconds(3)); }
    }
}
