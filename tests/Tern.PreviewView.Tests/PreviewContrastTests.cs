using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Tern.Rendering.Preview;

namespace Tern.PreviewView.Tests;

public sealed class PreviewContrastTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Truncation_banner_uses_a_readable_actual_foreground_background_pair(bool paper)
    {
        WithTheme(paper, () =>
        {
            var document = Harness.Build(string.Join("\n\n", Enumerable.Repeat("Paragraph with **words**.", 100)), new PreviewOptions { MaxNodes = 30 });
            Assert.True(document.Truncated);
            var (window, view) = Harness.Show(document);
            try
            {
                Assert.True(view.Banner.IsVisible);
                AssertReadable(view.BannerText.Foreground, view.Banner.Background);
                SetTheme(!paper);
                Harness.Pump(window);
                AssertReadable(view.BannerText.Foreground, view.Banner.Background);
            }
            finally { window.Close(); }
        });
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actual_marked_and_focused_links_keep_contrast_and_restore_their_theme_colours(bool paper)
    {
        WithTheme(paper, () =>
        {
            const string markdown = "<p><a href=\"https://example.com/a\">plain</a> <mark><a href=\"https://example.com/b\">marked</a> <a href=\"missing.md\">broken</a></mark></p>\n\n[other](https://example.com/other)";
            var (window, view) = Harness.Show(Harness.Build(markdown));
            try
            {
                var block = Assert.Single(Harness.All<RichTextBlock>(view), b => b.Links.Count == 3);
                var other = Assert.Single(Harness.All<RichTextBlock>(view), b => b.Links.Count == 1);
                var originalText = block.Inlines!.Text;
                var originalLinks = block.Links.Select(link => (link.Start, link.Length, link.Target.Destination)).ToArray();
                AssertNormalLinks(block, view);

                block.Focus(NavigationMethod.Tab);
                Assert.Equal(0, block.FocusedLinkIndex);
                AssertFocused(block, view);
                window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
                Harness.Pump(window);
                Assert.Equal(1, block.FocusedLinkIndex);
                AssertRestored(block.Links[0], view, PreviewKeys.Link, null);
                AssertFocused(block, view);

                SetTheme(!paper);
                Harness.Pump(window);
                Assert.Equal(1, block.FocusedLinkIndex);
                AssertFocused(block, view);
                AssertRestored(block.Links[0], view, PreviewKeys.Link, null);
                AssertRestored(block.Links[2], view, PreviewKeys.MarkLinkBroken, PreviewKeys.Mark);

                window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
                Harness.Pump(window);
                Assert.Equal(2, block.FocusedLinkIndex);
                AssertRestored(block.Links[1], view, PreviewKeys.MarkLink, PreviewKeys.Mark);
                AssertFocused(block, view);
                other.Focus(NavigationMethod.Tab);
                Harness.Pump(window);
                Assert.Equal(-1, block.FocusedLinkIndex);
                AssertNormalLinks(block, view);
                Assert.Equal(originalText, block.Inlines.Text);
                Assert.Equal(originalLinks, block.Links.Select(link => (link.Start, link.Length, link.Target.Destination)).ToArray());
            }
            finally { window.Close(); }
        });
    }

    private static void AssertNormalLinks(RichTextBlock block, PreviewView view)
    {
        AssertRestored(block.Links[0], view, PreviewKeys.Link, null);
        AssertRestored(block.Links[1], view, PreviewKeys.MarkLink, PreviewKeys.Mark);
        AssertRestored(block.Links[2], view, PreviewKeys.MarkLinkBroken, PreviewKeys.Mark);
        Assert.NotEqual(Colour(block.Links[1].Runs[0].Foreground), Colour(block.Links[2].Runs[0].Foreground));
    }

    private static void AssertRestored(LinkSpan span, PreviewView view, string foreground, string? background)
    {
        foreach (var run in span.Runs)
        {
            Assert.Equal(Colour(PreviewTheme.Resolve(view, foreground)), Colour(run.Foreground));
            if (background is null) Assert.Null(run.Background);
            else Assert.Equal(Colour(PreviewTheme.Resolve(view, background)), Colour(run.Background));
            AssertReadable(run.Foreground, run.Background ?? PreviewTheme.Resolve(view, PreviewKeys.Background));
        }
    }

    private static void AssertFocused(RichTextBlock block, PreviewView view)
    {
        foreach (var run in block.Links[block.FocusedLinkIndex].Runs)
        {
            Assert.Equal(Colour(PreviewTheme.Resolve(view, PreviewKeys.FocusBackground)), Colour(run.Background));
            Assert.Equal(Colour(PreviewTheme.Resolve(view, PreviewKeys.FocusForeground)), Colour(run.Foreground));
            AssertReadable(run.Foreground, run.Background);
            Assert.Contains(TextDecorations.Underline[0], run.TextDecorations!);
        }
    }

    private static Color Colour(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static void AssertReadable(IBrush? foreground, IBrush? background)
    {
        var fg = Colour(foreground);
        var bg = Colour(background);
        Assert.Equal((byte)255, fg.A);
        Assert.Equal((byte)255, bg.A);
        var a = Luminance(fg);
        var b = Luminance(bg);
        var ratio = (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
        Assert.True(ratio >= 4.5, $"Actual text pair {fg}/{bg} has contrast {ratio:F2}:1.");
    }

    private static double Luminance(Color colour)
    {
        static double Linear(byte value) => value / 255.0 <= .04045 ? value / 255.0 / 12.92 : Math.Pow((value / 255.0 + .055) / 1.055, 2.4);
        return .2126 * Linear(colour.R) + .7152 * Linear(colour.G) + .0722 * Linear(colour.B);
    }

    private static void SetTheme(bool paper)
    {
        Application.Current!.Resources.MergedDictionaries.Clear();
        Application.Current.Resources.MergedDictionaries.Add(paper ? PreviewTheme.Paper() : PreviewTheme.Midnight());
    }

    private static void WithTheme(bool paper, Action action)
    {
        var resources = Application.Current!.Resources.MergedDictionaries.ToArray();
        try { SetTheme(paper); action(); }
        finally
        {
            Application.Current.Resources.MergedDictionaries.Clear();
            foreach (var resource in resources) Application.Current.Resources.MergedDictionaries.Add(resource);
        }
    }
}
