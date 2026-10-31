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

public sealed class OpenUrlWindowTests
{
    [AvaloniaFact]
    public async Task Prefilled_history_address_is_manual_unless_explicitly_requested()
    {
        using var fixture = new DesktopFixture(); var handler = new Handler();
        using var loader = new MarkdownUrlLoader(handler, (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
        var window = new OpenUrlWindow(loader, "http://localhost:8000/recent.md");
        window.Show(fixture.Window); Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, handler.Calls);
        Assert.Equal("http://localhost:8000/recent.md", window.GetVisualDescendants().OfType<TextBox>().Single().Text);
        window.Close();

        var automaticHandler = new Handler();
        using var automaticLoader = new MarkdownUrlLoader(automaticHandler, (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
        var automatic = new OpenUrlWindow(automaticLoader, "http://localhost:8000/recent.md", openImmediately: true);
        var result = automatic.ShowDialog<MarkdownUrlDocument?>(fixture.Window);
        var document = await result.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.NotNull(document); Assert.Equal(1, automaticHandler.Calls);
    }

    [AvaloniaFact]
    public void Invalid_address_is_rejected_immediately_and_can_only_be_retried_after_correction()
    {
        using var fixture = new DesktopFixture(); var handler = new Handler();
        using var loader = new MarkdownUrlLoader(handler, (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
        var window = new OpenUrlWindow(loader); window.Show(fixture.Window);
        var status = window.GetVisualDescendants().OfType<TextBlock>().Single(t => AutomationProperties.GetName(t) == "Markdown URL status");
        var originalBrush = status.Foreground;
        var field = window.GetVisualDescendants().OfType<TextBox>().Single();
        var open = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Open"));
        var download = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Download and Open"));
        Assert.False(open.IsEnabled); Assert.False(download.IsEnabled);
        field.Text = "fdsfsfsdf";
        Assert.False(open.IsEnabled); Assert.False(download.IsEnabled);
        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(0, handler.Calls); Assert.NotEqual(originalBrush, status.Foreground);
        Assert.Contains("complete HTTP or HTTPS", status.Text);
        Assert.DoesNotContain("Parameter", status.Text, StringComparison.Ordinal);
        field.Text = "http://localhost:8000/note.md";
        Assert.True(open.IsEnabled); Assert.True(download.IsEnabled);
        Assert.Equal(originalBrush, status.Foreground);
        Assert.Contains("No request has been made", status.Text);
        Assert.Equal(0, handler.Calls);
        field.Text = "ftp://example.com/note.md";
        Assert.False(open.IsEnabled); Assert.Contains("Only HTTP or HTTPS", status.Text);
        field.Text = "https://user:password@example.com/note.md";
        Assert.False(open.IsEnabled); Assert.Contains("Remove the username or password", status.Text);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(404, "Resource not found")]
    [InlineData(401, "Authentication required")]
    [InlineData(403, "Permission denied")]
    [InlineData(500, "500")]
    [InlineData(-1, "could not be reached")]
    [InlineData(-2, "timed out")]
    [InlineData(-3, "web page")]
    public async Task Request_failure_keeps_specific_feedback_and_allows_retry(int scenario, string expected)
    {
        using var fixture = new DesktopFixture();
        using var loader = new MarkdownUrlLoader(new FailureHandler(scenario),
            (_, _) => Task.FromResult(new[] { IPAddress.Loopback }), TimeSpan.FromMilliseconds(100));
        var window = new OpenUrlWindow(loader, "http://localhost:8000/note.md"); window.Show(fixture.Window);
        var open = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Open"));
        var cancel = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Cancel request"));
        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 100 && cancel.IsEnabled; i++)
        { await Task.Delay(10, TestContext.Current.CancellationToken); Dispatcher.UIThread.RunJobs(); }
        var status = window.GetVisualDescendants().OfType<TextBlock>().Single(t => AutomationProperties.GetName(t) == "Markdown URL status");
        Assert.Contains(expected, status.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(open.IsEnabled); Assert.False(cancel.IsEnabled);
        Assert.False(window.GetVisualDescendants().OfType<TextBox>().Single().IsReadOnly);
        Assert.Empty(fixture.Shell.Tabs);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Cancel_request_reports_cancellation_and_restores_valid_input_without_opening_a_document()
    {
        using var fixture = new DesktopFixture();
        using var loader = new MarkdownUrlLoader(new FailureHandler(-2),
            (_, _) => Task.FromResult(new[] { IPAddress.Loopback }), TimeSpan.FromSeconds(10));
        var window = new OpenUrlWindow(loader, "http://localhost:8000/note.md"); window.Show(fixture.Window);
        var open = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Open"));
        var cancel = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Cancel request"));
        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(open.IsEnabled); Assert.True(cancel.IsEnabled);
        cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 100 && cancel.IsEnabled; i++)
        { await Task.Delay(10, TestContext.Current.CancellationToken); Dispatcher.UIThread.RunJobs(); }
        var status = window.GetVisualDescendants().OfType<TextBlock>().Single(t => AutomationProperties.GetName(t) == "Markdown URL status");
        Assert.Contains("Canceled", status.Text); Assert.True(open.IsEnabled); Assert.False(cancel.IsEnabled);
        Assert.Empty(fixture.Shell.Tabs);
        window.Close();
    }

    private sealed class FailureHandler(int scenario) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (scenario == -1) throw new HttpRequestException("test offline");
            if (scenario == -2) await Task.Delay(Timeout.Infinite, token);
            return scenario == -3
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Sign in</html>", Encoding.UTF8, "text/html") }
                : new HttpResponseMessage((HttpStatusCode)scenario) { Content = new StringContent("Error") };
        }
    }

    [AvaloniaFact]
    public async Task Local_server_address_preview_does_not_fetch_until_Open()
    {
        using var fixture = new DesktopFixture(); var handler = new Handler();
        using var loader = new MarkdownUrlLoader(handler, (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
        var window = new OpenUrlWindow(loader); var result = window.ShowDialog<MarkdownUrlDocument?>(fixture.Window);
        var details = Assert.Single(window.GetVisualDescendants().OfType<Expander>());
        details.IsExpanded = true; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        var field = window.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Markdown URL");
        field.Text = "http://localhost:8000/notes.md"; Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, handler.Calls);
        Assert.Contains("localhost:8000/notes.md", window.GetVisualDescendants().OfType<TextBlock>().Single(t => AutomationProperties.GetName(t) == "Markdown fetch address").Text, StringComparison.Ordinal);
        window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Open")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var document = await result.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.NotNull(document); Assert.Equal("localhost", document.SourceUrl.Host); Assert.Equal(1, handler.Calls);
    }

    [AvaloniaFact]
    public async Task Opening_dialog_makes_no_request_and_Open_returns_only_complete_text()
    {
        using var fixture = new DesktopFixture(); var handler = new Handler();
        using var loader = new MarkdownUrlLoader(handler, (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
        var window = new OpenUrlWindow(loader); var result = window.ShowDialog<MarkdownUrlDocument?>(fixture.Window);
        Dispatcher.UIThread.RunJobs(); Assert.Equal(0, handler.Calls);
        var details = Assert.Single(window.GetVisualDescendants().OfType<Expander>());
        details.IsExpanded = true; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        var field = window.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Markdown URL");
        field.Text = "https://github.com/owner/repo/blob/main/notes.md";
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("raw.githubusercontent.com/owner/repo/main/notes.md", window.GetVisualDescendants().OfType<TextBlock>().Single(t => AutomationProperties.GetName(t) == "Markdown fetch address").Text, StringComparison.Ordinal);
        Assert.Equal(0, handler.Calls);
        var open = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Open"));
        Assert.DoesNotContain(open.GetVisualAncestors(), a => a is ScrollViewer);
        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var document = await result.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.NotNull(document); Assert.Equal("# Exact\n", document.Text); Assert.Equal("notes.md", document.SuggestedFileName); Assert.Equal("raw.githubusercontent.com", document.SourceUrl.Host); Assert.Equal(1, handler.Calls);
        Assert.Empty(fixture.Shell.Tabs); Assert.Empty(fixture.Shell.Recent.Files);
    }
    private sealed class Handler : HttpMessageHandler
    { public int Calls { get; private set; } protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("# Exact\n", Encoding.UTF8, "text/markdown") }); } }
}
