using System.Net;
using System.Net.Http;
using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.Services;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class UrlOpenIntentTests
{
    [AvaloniaTheory]
    [InlineData("Open", false)]
    [InlineData("Download and Open", true)]
    public async Task Explicit_action_returns_validated_text_and_the_requested_intent_without_writing(string action, bool download)
    {
        using var fixture = new DesktopFixture();
        var handler = new Handler();
        using var loader = new MarkdownUrlLoader(handler, (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
        var window = new OpenUrlWindow(loader, "http://localhost:8000/note.md");
        var result = window.ShowDialog<MarkdownUrlDocument?>(fixture.Window);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, handler.Calls);
        var details = Assert.Single(window.GetVisualDescendants().OfType<Expander>(),
            e => AutomationProperties.GetName(e) == "Markdown URL details");
        Assert.False(details.IsExpanded);
        var button = Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, action));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var document = await result.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.NotNull(document);
        Assert.Equal("# Exact\n", document.Text);
        Assert.Equal(download, window.DownloadRequested);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(fixture.Shell.Tabs);
        Assert.Empty(Directory.GetFiles(fixture.Docs));
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("# Exact\n", Encoding.UTF8, "text/markdown") });
        }
    }
}
