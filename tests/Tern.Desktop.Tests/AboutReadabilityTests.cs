using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class AboutReadabilityTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Expanded_notices_wrap_and_scroll_while_close_remains_visible(bool compact)
    {
        using var fixture = new DesktopFixture();
        fixture.Shell.Settings.UiScale = compact ? 1.5 : 1;
        fixture.Window.ApplySettings();
        var window = new AboutWindow();
        if (compact) { window.Width = window.MinWidth; window.Height = window.MinHeight; }
        window.Show(fixture.Window);
        Pump(window);
        var details = Assert.Single(window.GetVisualDescendants().OfType<Expander>());
        Assert.False(details.IsExpanded);
        var purpose = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Write and read Markdown files", StringComparison.Ordinal) == true);
        Assert.Equal(compact ? 21 : 14, purpose.FontSize);
        var author = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Developed by Gehan Fernando");
        details.IsExpanded = true;
        Pump(window);
        // Default expansion now fits the visible notices when the screen allows.
        // Deliberately constrain the window to exercise the scrolling fallback.
        window.MaxHeight = Math.Min(window.MaxHeight, 440);
        window.Height = Math.Min(window.Height, 440);
        Pump(window);
        var scroll = Assert.Single(window.GetVisualDescendants().OfType<ScrollViewer>(), s => s.Content is StackPanel);
        Assert.True(scroll.Viewport.Width > 200);
        Assert.True(scroll.Viewport.Height > 120);
        var authorPoint = author.TranslatePoint(default, scroll)!.Value;
        Assert.True(authorPoint.Y >= 0 && authorPoint.Y + author.Bounds.Height <= scroll.Viewport.Height);
        var close = Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b) == "Close about");
        var closePoint = close.TranslatePoint(default, window)!.Value;
        Assert.True(close.IsEffectivelyVisible);
        Assert.InRange(closePoint.Y, 0, window.ClientSize.Height - close.Bounds.Height);
        Assert.All(AboutWindow.ThirdParty, component =>
        {
            var label = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == $"{component.Name} — {component.License}");
            Assert.Equal(TextWrapping.Wrap, label.TextWrapping);
            Assert.True(label.Bounds.Width <= scroll.Viewport.Width);
        });
        var folder = Path.Combine(Screenshots.Folder, "beginner-ui");
        Directory.CreateDirectory(folder);
        Capture(window, Path.Combine(folder, compact ? "about-compact-expanded.png" : "about-expanded.png"));
        scroll.Offset = new Vector(0, scroll.Extent.Height);
        Pump(window);
        Assert.True(scroll.Offset.Y > 0);
        var last = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == $"{AboutWindow.ThirdParty[^1].Name} — {AboutWindow.ThirdParty[^1].License}");
        var lastPoint = last.TranslatePoint(default, window)!.Value;
        Assert.True(lastPoint.Y + last.Bounds.Height > 0 && lastPoint.Y < closePoint.Y);
        Capture(window, Path.Combine(folder, compact ? "about-compact-notices-end.png" : "about-notices-end.png"));
        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(window.IsVisible);
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void Capture(Window window, string path)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var stream = File.Create(path);
        frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
