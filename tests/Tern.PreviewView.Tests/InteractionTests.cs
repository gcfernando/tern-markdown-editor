using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Tern.Rendering.Resources;

namespace Tern.PreviewView.Tests;

public class InteractionTests
{
    private static Point LinkCenter(Window window, RichTextBlock block, int index)
    {
        var span = block.Links[index];
        var layout = block.TextLayout;
        var rects = layout.HitTestTextRange(span.Start, span.Length).ToList();
        Assert.NotEmpty(rects);
        var local = rects[0].Center;
        return block.TranslatePoint(local, window) ?? throw new InvalidOperationException("link not in tree");
    }

    private static void Click(Window window, Point point)
    {
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Harness.Pump(window);
    }

    private static string Doc(string dir) => $"""
        # Top

        Go to [local](other.md#part), [missing](gone.md), [web](https://example.com/x), [jump](#target) and [bad](javascript:alert(1)).

        ## Target

        Body text.
        """;

    [AvaloniaFact]
    public void ClickingLinks_RaisesEventsWithTheResolvedDecision()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tern-pv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "other.md"), "# other");
            var options = new Tern.Rendering.Preview.PreviewOptions { DocumentPath = Path.Combine(dir, "main.md"), WorkspaceRoot = dir };
            var (window, view) = Harness.Show(Harness.Build(Doc(dir), options));
            var activated = new List<LinkActivatedEventArgs>();
            var anchors = new List<string>();
            view.LinkActivated += (_, e) => activated.Add(e);
            view.AnchorRequested += (_, e) => anchors.Add(e.Fragment);

            var paragraph = Harness.All<RichTextBlock>(view).First(b => b.Links.Count >= 5);

            Click(window, LinkCenter(window, paragraph, 0));
            var local = Assert.Single(activated);
            var file = Assert.IsType<LocalFile>(local.Resolution);
            Assert.True(file.Exists);
            Assert.Equal("part", file.Fragment);
            Assert.EndsWith("other.md", file.CanonicalPath, StringComparison.OrdinalIgnoreCase);

            Click(window, LinkCenter(window, paragraph, 1));
            Assert.False(Assert.IsType<LocalFile>(activated[1].Resolution).Exists);

            Click(window, LinkCenter(window, paragraph, 2));
            Assert.IsType<ExternalLink>(activated[2].Resolution);
            Assert.Equal("https://example.com/x", activated[2].Destination);

            Click(window, LinkCenter(window, paragraph, 3));
            Assert.Equal(["target"], anchors);
            Assert.Equal(3, activated.Count);

            Click(window, LinkCenter(window, paragraph, 4));
            Assert.IsType<Blocked>(activated[3].Resolution);
            window.Close();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [AvaloniaFact]
    public async Task A_loaded_image_only_link_receives_clicks()
    {
        using var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(300, 50), new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        using var encoded = new MemoryStream();
        bitmap.Save(encoded, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        var view = new PreviewView
        {
            RemoteImageLoader = (_, _) => Task.FromResult<byte[]?>(encoded.ToArray()),
            Document = Harness.Build("[![Download](https://example.com/button.png)](https://example.com/file.zip)"),
        };
        var window = new Window { Width = 700, Height = 400, Content = view };
        window.Show();
        Harness.Pump(window);
        var image = Assert.Single(Harness.All<ImageView>(view));
        await image.LoadingCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Harness.Pump(window);
        Assert.Equal(ImageState.Loaded, image.State);
        var activated = new List<LinkActivatedEventArgs>();
        view.LinkActivated += (_, e) => activated.Add(e);
        var paragraph = Assert.Single(Harness.All<RichTextBlock>(view));
        Click(window, LinkCenter(window, paragraph, 0));
        Assert.Equal("https://example.com/file.zip", Assert.Single(activated).Destination);
        window.Close();
        await view.ImageCleanupCompletion;
    }

    [AvaloniaTheory]
    [InlineData("[Download](https://example.com/file.zip?download=1)", "https://example.com/file.zip?download=1")]
    [InlineData("<https://example.com/page>", "https://example.com/page")]
    [InlineData("https://example.com/page", "https://example.com/page")]
    [InlineData("[Email](mailto:reader@example.com)", "mailto:reader@example.com")]
    [InlineData("[Reference][page]\n\n[page]: https://example.com/page", "https://example.com/page")]
    [InlineData("<a href=\"https://example.com/page\"><strong>HTML link</strong></a>", "https://example.com/page")]
    [InlineData("[**Formatted** link](https://example.com/page)", "https://example.com/page")]
    [InlineData("| Link |\n| --- |\n| [Table link](https://example.com/page) |", "https://example.com/page")]
    [InlineData("> [Quoted link](https://example.com/page)", "https://example.com/page")]
    public void Different_link_forms_activate_their_destination(string markdown, string destination)
    {
        var (window, view) = Harness.Show(Harness.Build(markdown));
        var paragraph = Assert.Single(Harness.All<RichTextBlock>(view), b => b.Links.Count > 0);
        var activated = new List<LinkActivatedEventArgs>();
        view.LinkActivated += (_, e) => activated.Add(e);
        Click(window, LinkCenter(window, paragraph, 0));
        Assert.Equal(destination, Assert.Single(activated).Destination);
        Assert.IsType<ExternalLink>(activated[0].Resolution);
        window.Close();
    }

    [AvaloniaFact]
    public void Repository_readme_download_and_navigation_links_are_clickable()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Tern.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var path = Path.Combine(root.FullName, "README.md");
        var source = File.ReadAllText(path);
        var document = Harness.Build(source, new Tern.Rendering.Preview.PreviewOptions { DocumentPath = path, WorkspaceRoot = root.FullName });
        var (window, view) = Harness.Show(document, 900, 700);
        var activated = new List<LinkActivatedEventArgs>();
        view.LinkActivated += (_, e) => activated.Add(e);
        foreach (var destination in new[]
        {
            "https://sourceforge.net/projects/tern/files/latest/download",
            "https://github.com/gcfernando/tern-markdown-editor/actions/workflows/release.yml",
            "docs/privacy.md", "docs/user-guide.md",
            "https://github.com/gcfernando/tern-markdown-editor/releases",
            "docs/user-guide.md#known-limits",
        })
        {
            var sourceLine = source[..source.IndexOf("](" + destination + ")", StringComparison.Ordinal)].Count(c => c == '\n') + 1;
            view.ScrollToSourceLine(sourceLine);
            Harness.Pump(window);
            var block = Harness.All<RichTextBlock>(view).First(b => b.Links.Any(link => link.Target.Destination == destination));
            var index = block.Links.ToList().FindIndex(link => link.Target.Destination == destination);
            Assert.Equal(index, block.LinkIndexAt(block.TextLayout.HitTestTextRange(block.Links[index].Start, block.Links[index].Length).First().Center));
            Click(window, LinkCenter(window, block, index));
            Assert.True(activated.Count == 1, $"Expected activation for {destination}; received {activated.Count} events. Block bounds: {block.Bounds}; point: {LinkCenter(window, block, index)}");
            Assert.Equal(destination, Assert.Single(activated).Destination);
            if (destination.StartsWith("docs/", StringComparison.Ordinal)) Assert.True(Assert.IsType<LocalFile>(activated[0].Resolution).Exists);
            else Assert.IsType<ExternalLink>(activated[0].Resolution);
            activated.Clear();
        }
        window.Close();
    }

    [AvaloniaFact]
    public void The_entire_visible_link_range_is_clickable_including_the_last_character()
    {
        var (window, view) = Harness.Show(Harness.Build("Before [web](https://example.com/file.zip) after."));
        var paragraph = Assert.Single(Harness.All<RichTextBlock>(view));
        var span = Assert.Single(paragraph.Links);
        var rect = Assert.Single(paragraph.TextLayout.HitTestTextRange(span.Start, span.Length));
        foreach (var fraction in new[] { 0.1, 0.5, 0.9, 0.99 })
        {
            var point = new Point(rect.Left + rect.Width * fraction, rect.Center.Y);
            Assert.Equal(0, paragraph.LinkIndexAt(point));
        }
        window.Close();
    }

    [AvaloniaFact]
    public void BrokenLocalLink_UsesTheBrokenBrush()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tern-pv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "ok.md"), "x");
            var options = new Tern.Rendering.Preview.PreviewOptions { DocumentPath = Path.Combine(dir, "main.md"), WorkspaceRoot = dir };
            var (window, view) = Harness.Show(Harness.Build("[ok](ok.md) and [nope](nope.md)", options));
            var paragraph = Harness.All<RichTextBlock>(view).First(b => b.Links.Count == 2);
            var ok = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(paragraph.Links[0].Runs[0].Foreground).Color;
            var broken = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(paragraph.Links[1].Runs[0].Foreground).Color;
            Assert.Equal(((Avalonia.Media.ISolidColorBrush)PreviewTheme.Resolve(view, PreviewKeys.Link)).Color, ok);
            Assert.Equal(((Avalonia.Media.ISolidColorBrush)PreviewTheme.Resolve(view, PreviewKeys.LinkBroken)).Color, broken);
            Assert.NotEqual(ok, broken);
            window.Close();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [AvaloniaFact]
    public void Links_AreKeyboardReachable()
    {
        var (window, view) = Harness.Show(Harness.Build("[a](https://example.com/a) [b](#x)\n\n# x"));
        var paragraph = Harness.All<RichTextBlock>(view).First(b => b.Links.Count == 2);
        var seen = new List<LinkActivatedEventArgs>();
        view.LinkActivated += (_, e) => seen.Add(e);
        Assert.True(paragraph.Focusable);
        paragraph.Focus(NavigationMethod.Tab);
        Assert.True(paragraph.FocusedLinkIndex >= 0);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Harness.Pump(window);
        Assert.NotEmpty(seen);
        window.Close();
    }

    [AvaloniaFact]
    public void CtrlClickingABlock_RaisesSourceLineActivated()
    {
        var source = "# Title\n\nfirst paragraph\n\nsecond paragraph\n";
        var (window, view) = Harness.Show(Harness.Build(source));
        var lines = new List<int>();
        view.SourceLineActivated += (_, e) => lines.Add(e.Line);
        var second = Harness.All<RichTextBlock>(view).First(b => Harness.PlainText(b).Contains("second", StringComparison.Ordinal));
        var point = second.TranslatePoint(second.TextLayout.HitTestTextRange(0, 3).First().Center, window)!.Value;
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.Control);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.Control);
        Harness.Pump(window);
        Assert.Equal([5], lines);
        window.Close();
    }

    [AvaloniaFact]
    public void DoubleClickingABlock_RaisesSourceLineActivated()
    {
        var source = "# Title\n\nfirst paragraph\n\nsecond paragraph\n";
        var (window, view) = Harness.Show(Harness.Build(source));
        var lines = new List<int>();
        view.SourceLineActivated += (_, e) => lines.Add(e.Line);
        var second = Harness.All<RichTextBlock>(view).First(b => Harness.PlainText(b).Contains("second", StringComparison.Ordinal));
        var point = second.TranslatePoint(second.TextLayout.HitTestTextRange(0, 3).First().Center, window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Harness.Pump(window);
        Assert.Contains(5, lines);
        window.Close();
    }

    [AvaloniaFact]
    public void ScrollToAnchor_BringsTheHeadingToTheTop()
    {
        var text = string.Join("\n\n", Enumerable.Range(0, 120).Select(i => $"Paragraph {i} with some words to fill the line."))
            + "\n\n## Deep Heading\n\nTail\n\n" + string.Join("\n\n", Enumerable.Range(0, 60).Select(i => $"After {i}"));
        var (window, view) = Harness.Show(Harness.Build(text), 900, 500);
        Assert.Equal(0, view.Scroller.Offset.Y, 1);
        Assert.True(view.ScrollToAnchor("deep-heading"));
        Harness.Pump(window);
        Assert.True(view.Scroller.Offset.Y > 500);
        var heading = Harness.All<TextBlock>(view).First(t => (Avalonia.Automation.AutomationProperties.GetName(t) ?? "").Contains("Deep Heading", StringComparison.Ordinal));
        var top = heading.TranslatePoint(default, view.Scroller)!.Value.Y;
        Assert.InRange(top, -5, 60);
        Assert.False(view.ScrollToAnchor("no-such-anchor"));
        window.Close();
    }

    private static string LongDocument(int paragraphs) =>
        string.Join("\n\n", Enumerable.Range(0, paragraphs).Select(i => $"Paragraph number {i} with words to fill it out a bit."));

    [AvaloniaFact]
    public void Raw_html_local_links_use_the_same_decision_as_markdown_links()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tern-pv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "other.md"), "# Other");
            var options = new Tern.Rendering.Preview.PreviewOptions { DocumentPath = Path.Combine(dir, "main.md") };
            var (window, view) = Harness.Show(Harness.Build("<div><a href=\"other.md\">Other</a></div>\n", options));
            var paragraph = Assert.Single(Harness.All<RichTextBlock>(view), block => block.Links.Count == 1);
            var target = Assert.IsType<LocalFile>(paragraph.Links[0].Target.Resolution);
            Assert.True(target.Exists);
            Assert.Equal(Path.Combine(dir, "other.md"), target.CanonicalPath);
            window.Close();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [AvaloniaFact]
    public void ScrollToSourceLine_ThenTopLineMatches_WithoutFeedback()
    {
        var doc = Harness.Build(LongDocument(400));
        var (window, view) = Harness.Show(doc, 900, 500);
        var reported = new List<int>();
        view.TopSourceLineChanged += (_, e) => reported.Add(e.Line);

        view.ScrollToSourceLine(401);
        Harness.Pump(window, 8);
        Assert.InRange(view.TopSourceLine, 395, 407);
        Assert.Empty(reported);

        // A genuine user scroll is reported.
        view.Scroller.Offset = new Vector(0, view.Scroller.Offset.Y + 2000);
        Harness.Pump(window, 8);
        Assert.NotEmpty(reported);
        Assert.True(reported[^1] > 401);
        window.Close();
    }

    [AvaloniaFact]
    public void SuppressNotifications_SilencesTopLineEvents()
    {
        var (window, view) = Harness.Show(Harness.Build(LongDocument(300)), 900, 500);
        var count = 0;
        view.TopSourceLineChanged += (_, _) => count++;
        view.SuppressNotifications = true;
        view.Scroller.Offset = new Vector(0, 3000);
        Harness.Pump(window, 8);
        Assert.Equal(0, count);
        view.SuppressNotifications = false;
        view.Scroller.Offset = new Vector(0, 6000);
        Harness.Pump(window, 8);
        Assert.True(count > 0);
        window.Close();
    }

    [AvaloniaFact]
    public void Editing_KeepsTheScrollPosition()
    {
        var lines = LongDocument(400);
        var (window, view) = Harness.Show(Harness.Build(lines), 900, 500);
        view.ScrollToSourceLine(401);
        Harness.Pump(window, 8);
        var before = view.TopSourceLine;
        view.Document = Harness.Build(lines.Replace("Paragraph number 3 ", "Paragraph number three ", StringComparison.Ordinal));
        Harness.Pump(window, 8);
        Assert.InRange(view.TopSourceLine, before - 3, before + 3);
        window.Close();
    }
}
