using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;

namespace Tern.Desktop.Tests;

public sealed class PreviewLinkTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repository_readme_links_reach_the_browser_or_the_requested_document(bool reading)
    {
        using var f = new DesktopFixture();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Tern.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var readme = Path.Combine(root.FullName, "README.md");
        var source = await File.ReadAllTextAsync(readme);
        f.Dialogs.AllowExternal = true;
        await f.Shell.OpenFileAsync(readme);
        await f.Window.RunCommandAsync(reading ? CommandIds.ViewReading : CommandIds.ViewSplit);
        await f.Window.RebuildPreviewAsync();
        await ClickReadmeLink(f, source, "https://sourceforge.net/projects/tern/files/latest/download", null);
        Assert.Equal("https://sourceforge.net/projects/tern/files/latest/download", Assert.Single(f.Launcher.Opened).AbsoluteUri);
        await ClickReadmeLink(f, source, "https://github.com/gcfernando/tern-markdown-editor/releases", "GitHub Releases");
        Assert.Equal("https://github.com/gcfernando/tern-markdown-editor/releases", f.Launcher.Opened[1].AbsoluteUri);
        Assert.Equal(f.Launcher.Opened, f.Dialogs.ExternalAsked);

        await ClickReadmeLink(f, source, "docs/privacy.md", "network and privacy boundaries");
        await WaitFor(() => f.Shell.ActiveTab?.FilePath == Path.Combine(root.FullName, "docs", "privacy.md"));
        Assert.Equal(Path.Combine(root.FullName, "docs", "privacy.md"), f.Shell.ActiveTab!.FilePath);
        await f.Shell.OpenFileAsync(readme);
        await f.Window.RebuildPreviewAsync();
        await ClickReadmeLink(f, source, "docs/user-guide.md#known-limits", "known limits");
        var target = Path.Combine(root.FullName, "docs", "user-guide.md");
        await WaitFor(() => f.Shell.ActiveTab?.FilePath == target);
        Assert.Equal(target, f.Shell.ActiveTab!.FilePath);
        await WaitFor(() => f.Window.PreviewControl.Document?.FindAnchorSourceLine("known-limits") is not null);
        for (var i = 0; i < 6; i++) { f.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); await Task.Delay(30); }
        await WaitFor(() => f.Window.PreviewControl.TopSourceLine > 100);
        var expectedLine = f.Window.PreviewControl.Document!.FindAnchorSourceLine("known-limits")!.Value;
        Assert.Equal(expectedLine, f.Window.EditorControl.Document.GetLineByOffset(f.Window.EditorControl.CaretOffset).LineNumber);
        f.Window.UpdateLayout();
        var heading = Assert.Single(f.Window.PreviewControl.GetVisualDescendants().OfType<SelectableTextBlock>(), b => PlainText(b.Inlines!) == "Known limits");
        var headingBounds = new Rect(heading.TranslatePoint(default, f.Window.PreviewControl)!.Value, heading.Bounds.Size);
        Assert.True(headingBounds.Intersects(new Rect(f.Window.PreviewControl.Bounds.Size)), $"Heading {headingBounds} must be visible after navigation.");
        Assert.False(f.Shell.ActiveTab.IsDirty);
        Assert.Equal(source, await File.ReadAllTextAsync(readme));
    }

    private static async Task ClickReadmeLink(DesktopFixture f, string source, string destination, string? label)
    {
        var line = source[..source.IndexOf("](" + destination + ")", StringComparison.Ordinal)].Count(c => c == '\n') + 1;
        f.Window.PreviewControl.ScrollToSourceLine(line);
        for (var i = 0; i < 4; i++) { f.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
        var blocks = f.Window.PreviewControl.GetVisualDescendants().OfType<SelectableTextBlock>();
        var block = label is null ? blocks.First() : blocks.First(b => PlainText(b.Inlines!).Contains(label, StringComparison.Ordinal));
        var start = label is null ? 0 : PlainText(block.Inlines!).IndexOf(label, StringComparison.Ordinal);
        var rect = block.TextLayout.HitTestTextRange(start, label?.Length ?? 1).First();
        var point = block.TranslatePoint(rect.Center, f.Window)!.Value;
        f.Window.MouseMove(point);
        f.Window.MouseDown(point, MouseButton.Left);
        f.Window.MouseUp(point, MouseButton.Left);
        await Task.Delay(30);
        Dispatcher.UIThread.RunJobs();
    }

    private static string PlainText(IEnumerable<Inline> inlines) => string.Concat(inlines.Select(inline => inline switch
    {
        Run run => run.Text,
        Span span => PlainText(span.Inlines),
        InlineUIContainer => "\uFFFC",
        _ => "",
    }));

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++) { await Task.Delay(30); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clicking_preview_link_opens_local_document(bool reading)
    {
        using var f = new DesktopFixture();
        var target = await f.WriteAsync("target.md", "# Target\n");
        await f.Shell.OpenFileAsync(await f.WriteAsync("source.md", "Read the [user guide](target.md).\n"));
        await f.Window.RunCommandAsync(reading ? CommandIds.ViewReading : CommandIds.ViewSplit);
        await f.Window.RebuildPreviewAsync();
        f.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var block = Assert.Single(f.Window.PreviewControl.GetVisualDescendants().OfType<SelectableTextBlock>());
        var text = string.Concat(block.Inlines!.OfType<Run>().Select(run => run.Text));
        var rect = block.TextLayout.HitTestTextRange(text.IndexOf("user guide", StringComparison.Ordinal), 10).First();
        var point = block.TranslatePoint(rect.Center, f.Window)!.Value;
        f.Window.MouseMove(point);
        f.Window.MouseDown(point, MouseButton.Left);
        f.Window.MouseUp(point, MouseButton.Left);
        for (var i = 0; i < 10 && f.Shell.ActiveTab?.FilePath != target; i++)
        {
            await Task.Delay(30);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.Equal(target, f.Shell.ActiveTab!.FilePath);
    }
}
