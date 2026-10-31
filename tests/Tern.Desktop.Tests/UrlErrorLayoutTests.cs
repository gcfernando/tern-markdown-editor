using System.Net;
using System.Net.Http;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.Services;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class UrlErrorLayoutTests
{
    [AvaloniaFact]
    public async Task Html_rejection_at_minimum_size_and_double_text_keeps_actions_visible_and_error_scrollable()
    {
        using var fixture = new DesktopFixture();
        fixture.Shell.Settings.UiScale = 2;
        fixture.Window.ApplySettings();
        using var loader = new MarkdownUrlLoader(new HtmlHandler(),
            (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
        var window = new OpenUrlWindow(loader, "http://localhost:8000/sign-in.md") { Width = 500, Height = 420 };
        try
        {
            window.Show(fixture.Window);
            Dispatcher.UIThread.RunJobs();
            var open = Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Open"));
            var status = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(),
                t => AutomationProperties.GetName(t) == "Markdown URL status");
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var attempt = 0; attempt < 100 && !open.IsEnabled; attempt++)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
                Dispatcher.UIThread.RunJobs();
            }
            window.UpdateLayout();
            Assert.True(open.IsEnabled);
            Assert.Contains("web page", status.Text, StringComparison.OrdinalIgnoreCase);
            foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(b => Equals(b.Content, "Open") || Equals(b.Content, "Close")))
            {
                var point = button.TranslatePoint(default, window)!.Value;
                Assert.InRange(point.X, 0, window.Bounds.Width - button.Bounds.Width);
                Assert.InRange(point.Y, 0, window.Bounds.Height - button.Bounds.Height);
                Assert.DoesNotContain(button.GetVisualAncestors(), ancestor => ancestor is ScrollViewer);
            }
            var scroll = Assert.Single(status.GetVisualAncestors().OfType<ScrollViewer>());
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var errorPoint = status.TranslatePoint(default, scroll)!.Value;
            Assert.True(scroll.Bounds.Height > 0);
            Assert.True(errorPoint.Y < scroll.Bounds.Height && errorPoint.Y + status.Bounds.Height > 0,
                "The error must automatically appear in the body scroll viewport.");
            var folder = Path.Combine(Screenshots.Folder, "button-alignment");
            Directory.CreateDirectory(folder);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            using var stream = File.Create(Path.Combine(folder, "url-html-error-minimum-200.png"));
            frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        finally
        {
            window.Close();
            fixture.Shell.Settings.UiScale = 1;
            fixture.Window.ApplySettings();
        }
    }

    private sealed class HtmlHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("<!doctype html><html><body>Sign in</body></html>", Encoding.UTF8, "text/html") });
    }
}
