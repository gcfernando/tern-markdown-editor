using System.Net;
using System.Net.Http;
using System.Text;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.Services;
using Tern.Desktop.ViewModels;
using Tern.Desktop.Views;
using Tern.App.Shell;

namespace Tern.Desktop.Tests;

public sealed class LibraryFetchRegressionTests
{
    [AvaloniaFact]
    public async Task Failed_recent_url_retains_explicit_recovery_actions_and_retry_opens_without_a_dialog()
    {
        using var f = new DesktopFixture();
        var handler = new PendingHandler();
        f.Window.OnlineLoaderFactory = () => new MarkdownUrlLoader(handler,
            (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
        f.Shell.Recent.AddUrl("http://localhost:8000/retry.md");
        await f.Window.ShowNavAsync("Recent"); Pump(f);
        var list = f.Window.FindControl<ListBox>("SideList")!;
        list.SelectedItem = Assert.Single(list.Items.OfType<SideRow>(), row => row.IsOnline);
        handler.Response.SetResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        for (var attempt = 0; attempt < 100 && f.Shell.Banner is null; attempt++)
        { await Task.Delay(10, TestContext.Current.CancellationToken); Pump(f); }
        Assert.Equal(StatusSeverity.Error, f.Shell.Banner!.Severity);
        Assert.Empty(f.Shell.Tabs);
        var failed = Assert.Single(f.Shell.Activities.Items, item => item.Phase.StartsWith("Online file could not be opened:"));
        Assert.Equal(["Retry", "Open from URL"], failed.Actions.Select(action => action.Label));
        var recovery = new PendingHandler();
        f.Window.OnlineLoaderFactory = () => new MarkdownUrlLoader(recovery,
            (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
        failed.Actions[0].Invoke(); Pump(f);
        Assert.Equal(1, recovery.Calls);
        recovery.Response.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("# Recovered", Encoding.UTF8, "text/markdown") });
        for (var attempt = 0; attempt < 100 && f.Shell.Tabs.Count == 0; attempt++)
        { await Task.Delay(10, TestContext.Current.CancellationToken); Pump(f); }
        Assert.Equal("# Recovered", Assert.Single(f.Shell.Tabs).CurrentText);
        Assert.Null(f.Shell.Banner);
        Assert.Empty(f.Window.OwnedWindows);
        Assert.Contains(f.Shell.Activities.Items, item => item.Id == failed.Id && item.State == ActivityState.Failed);
    }

    [AvaloniaTheory]
    [InlineData("http://192.168.1.1/notes.md")]
    [InlineData("http://server.local/notes.md")]
    public async Task Unsupported_recent_destination_reports_error_without_opening_a_tab_or_dialog(string url)
    {
        using var f = new DesktopFixture();
        f.Shell.Recent.AddUrl(url);
        await f.Window.ShowNavAsync("Recent"); Pump(f);
        var list = f.Window.FindControl<ListBox>("SideList")!;
        list.SelectedItem = Assert.Single(list.Items.OfType<SideRow>(), row => row.IsOnline);
        Pump(f);
        Assert.Empty(f.Shell.Tabs);
        Assert.Contains("private-network", f.Shell.StatusMessage);
        Assert.DoesNotContain(f.Window.OwnedWindows, w => w is OpenUrlWindow);
    }

    [AvaloniaFact]
    public async Task Closed_online_recent_fetches_once_without_a_flashing_dialog()
    {
        using var f = new DesktopFixture();
        var handler = new PendingHandler();
        f.Window.OnlineLoaderFactory = () => new MarkdownUrlLoader(handler,
            (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
        f.Shell.Recent.AddUrl("http://localhost:8000/notes.md");
        await f.Window.ShowNavAsync("Recent"); Pump(f);
        var list = f.Window.FindControl<ListBox>("SideList")!;
        var online = Assert.Single(list.Items.OfType<SideRow>(), row => row.IsOnline);
        list.SelectedItem = online; Pump(f);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(f.Shell.Tabs);
        Assert.DoesNotContain(f.Window.OwnedWindows, w => w is OpenUrlWindow);
        list.SelectedItem = online; Pump(f);
        Assert.Equal(1, handler.Calls);
        handler.Response.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("# From the server\n", Encoding.UTF8, "text/markdown") });
        for (var attempt = 0; attempt < 100 && f.Shell.Tabs.Count == 0; attempt++)
        { await Task.Delay(10, TestContext.Current.CancellationToken); Pump(f); }
        var tab = Assert.Single(f.Shell.Tabs);
        Assert.Equal("# From the server\n", tab.CurrentText);
        Assert.False(tab.IsDirty);
        Assert.DoesNotContain(f.Window.OwnedWindows, w => w is OpenUrlWindow);
        list.SelectedItem = online; Pump(f);
        Assert.Same(tab, f.Shell.ActiveTab);
        Assert.Equal(1, handler.Calls);
    }

    [AvaloniaFact]
    public async Task Repeated_download_clicks_share_one_request_and_window_close_cancels_it()
    {
        using var f = new DesktopFixture();
        var destination = Path.Combine(f.Docs, "downloaded.md");
        f.Dialogs.SavePath = destination;
        var handler = new PendingHandler();
        f.Window.OnlineLoaderFactory = () => new MarkdownUrlLoader(handler,
            (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
        f.Shell.Recent.AddUrl("http://localhost:8000/notes.md");
        await f.Window.ShowNavAsync("Recent"); Pump(f);
        var button = Assert.Single(f.Window.GetVisualDescendants().OfType<Button>(),
            control => control.IsEffectivelyVisible && AutomationProperties.GetName(control) == "Save portable copy");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(f);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(f);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(f.Shell.Tabs);
        f.Window.Close(); Pump(f);
        Assert.False(f.Window.IsVisible);
        Assert.True(handler.RequestToken.IsCancellationRequested);
        handler.Response.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("# Must not be saved\n", Encoding.UTF8, "text/markdown") });
        await Task.Delay(20, TestContext.Current.CancellationToken); Pump(f);
        Assert.False(File.Exists(destination));
        Assert.Empty(f.Shell.Tabs);
        Assert.Empty(f.Window.OwnedWindows);
    }

    private static void Pump(DesktopFixture f)
    { Dispatcher.UIThread.RunJobs(); f.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }

    private sealed class PendingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public CancellationToken RequestToken { get; private set; }
        public TaskCompletionSource<HttpResponseMessage> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; RequestToken = cancellationToken; return Response.Task.WaitAsync(cancellationToken); }
    }
}
