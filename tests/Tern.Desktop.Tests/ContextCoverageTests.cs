using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.ViewModels;
using Tern.Desktop.Services;
using Tern.App.Shell;
using System.Net;
using System.Net.Http;

namespace Tern.Desktop.Tests;

public sealed class ContextCoverageTests
{
    [AvaloniaFact]
    public async Task Closed_online_entry_disables_target_actions_but_close_all_closes_other_tabs_without_fetching()
    {
        using var f = new DesktopFixture();
        await f.Shell.OpenFileAsync(await f.WriteAsync("local-notes.md", "# Local notes\n"));
        f.Shell.NewDocument("Draft changes");
        f.Shell.Recent.AddUrl("http://localhost:8000/online-guide.md");
        var handler = new RecordingHandler();
        f.Window.OnlineLoaderFactory = () => new MarkdownUrlLoader(handler,
            (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
        await f.Window.ShowNavAsync("Recent"); Pump(f);
        var list = f.Window.FindControl<ListBox>("SideList")!;
        var online = Assert.Single(list.Items.OfType<SideRow>(), row => row.IsOnline);
        var container = Assert.IsType<ListBoxItem>(list.ContainerFromItem(online));
        RightClick(f, container, new Point(2, container.Bounds.Height / 2));
        var menu = OpenMenu(f);
        foreach (var tag in new[] { "tab.close", "tab.closeRight", "tab.closeOthers" })
            Assert.False(Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Tag, tag)).IsEnabled);
        var closeAll = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Tag, "tab.closeAll"));
        Assert.True(closeAll.IsEnabled);
        await Task.Delay(100, TestContext.Current.CancellationToken); Pump(f);
        Capture(f.Window, "library-aligned-actions");
        Capture(Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(menu)), "library-closed-entry-context");
        f.Dialogs.SaveChoice = SaveChoice.Discard;
        closeAll.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Pump(f);
        Assert.Empty(f.Shell.Tabs);
        Assert.Equal(0, handler.Calls);
        menu.Close();
    }

    [AvaloniaFact]
    public async Task Library_row_padding_and_empty_space_have_relevant_context_menus_without_opening_files()
    {
        using var f = new DesktopFixture();
        var local = await f.Shell.OpenFileAsync(await f.WriteAsync("local.md", "Local"));
        f.Shell.Recent.AddUrl("http://localhost:8000/online.md");
        var handler = new RecordingHandler();
        f.Window.OnlineLoaderFactory = () => new MarkdownUrlLoader(handler,
            (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
        await f.Window.ShowNavAsync("Recent"); Pump(f);
        var list = f.Window.FindControl<ListBox>("SideList")!;
        var online = Assert.Single(list.Items.OfType<SideRow>(), row => row.IsOnline);
        var container = Assert.IsType<ListBoxItem>(list.ContainerFromItem(online));
        var title = Assert.Single(container.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == online.Title);
        var detail = Assert.Single(container.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == online.Detail);
        var download = Assert.Single(container.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetName(button) == "Download online file");
        var remove = Assert.Single(container.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetName(button) == "Remove file reference");
        foreach (var (control, point) in new (Control, Point)[]
        {
            (container, new Point(2, container.Bounds.Height / 2)),
            (title, new Point(Math.Min(12, title.Bounds.Width / 2), title.Bounds.Height / 2)),
            (detail, new Point(Math.Min(12, detail.Bounds.Width / 2), detail.Bounds.Height / 2)),
            (download, new Point(download.Bounds.Width / 2, download.Bounds.Height / 2)),
            (remove, new Point(remove.Bounds.Width / 2, remove.Bounds.Height / 2))
        })
        {
            RightClick(f, control, point);
            var rowMenu = OpenMenu(f);
            Assert.Contains(rowMenu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Open"));
            Assert.Contains(rowMenu.Items.OfType<MenuItem>(), item => (item.Header as string)?.StartsWith("Download", StringComparison.Ordinal) == true);
            Assert.Same(local, f.Shell.ActiveTab);
            Assert.Single(f.Shell.Tabs);
            Assert.Equal(0, handler.Calls);
            Assert.Contains(online, list.Items.OfType<SideRow>());
            rowMenu.Close(); Pump(f);
        }
        RightClick(f, list, new Point(list.Bounds.Width / 2, list.Bounds.Height - 12));
        var menu = OpenMenu(f);
        Assert.Contains(menu.Items.OfType<MenuItem>(), item => Equals(item.Tag, "file.new"));
        Assert.Contains(menu.Items.OfType<MenuItem>(), item => Equals(item.Tag, "file.openUrl"));
        Assert.Single(f.Shell.Tabs);
        menu.Close();
    }

    [AvaloniaFact]
    public void Tab_padding_and_unused_strip_have_document_context_commands()
    {
        using var f = new DesktopFixture();
        var first = f.Shell.NewDocument();
        f.Shell.NewDocument(); Pump(f);
        var strip = f.Window.FindControl<ListBox>("TabStrip")!;
        var container = Assert.IsType<ListBoxItem>(strip.ContainerFromItem(first));
        RightClick(f, container, new Point(2, container.Bounds.Height / 2));
        var menu = OpenMenu(f);
        Assert.Contains(menu.Items.OfType<MenuItem>(), item => Equals(item.Tag, "tab.close"));
        Assert.Contains(menu.Items.OfType<MenuItem>(), item => Equals(item.Tag, "tab.rename"));
        Assert.Equal(2, f.Shell.Tabs.Count);
        menu.Close(); Pump(f);
        RightClick(f, strip, new Point(strip.Bounds.Width - 12, strip.Bounds.Height / 2));
        menu = OpenMenu(f);
        Assert.Contains(menu.Items.OfType<MenuItem>(), item => Equals(item.Tag, "file.new"));
        Assert.Contains(menu.Items.OfType<MenuItem>(), item => Equals(item.Tag, "tab.closeAll"));
        menu.Close();
    }

    [AvaloniaFact]
    public async Task Online_download_and_offline_rename_share_the_same_action_geometry()
    {
        using var f = new DesktopFixture();
        await f.Shell.OpenFileAsync(await f.WriteAsync("local.md", "Local"));
        f.Shell.Recent.AddUrl("http://localhost:8000/online.md");
        await f.Window.ShowNavAsync("Recent"); Pump(f);
        var buttons = f.Window.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible).ToList();
        var download = Assert.Single(buttons, button => AutomationProperties.GetName(button) == "Download online file");
        var rename = Assert.Single(buttons, button => AutomationProperties.GetName(button) == "Rename local file");
        Assert.Equal(rename.Bounds.Size, download.Bounds.Size);
        Assert.InRange(Math.Abs(rename.TranslatePoint(default, f.Window)!.Value.X - download.TranslatePoint(default, f.Window)!.Value.X), 0, .5);
    }

    [AvaloniaFact]
    public void Tab_navigation_is_hidden_without_overflow_and_visible_when_tabs_need_scrolling()
    {
        using var f = new DesktopFixture();
        f.Window.Width = 1000;
        f.Shell.NewDocument(); Pump(f);
        var left = f.Window.FindControl<Button>("TabsPrevious")!;
        var right = f.Window.FindControl<Button>("TabsNext")!;
        Assert.False(left.IsVisible);
        Assert.False(right.IsVisible);
        Capture(f.Window, "single-tab-no-navigators");
        for (var index = 0; index < 20; index++) f.Shell.NewDocument();
        Pump(f);
        Assert.True(left.IsVisible);
        Assert.True(right.IsVisible);
        Assert.True(left.IsEnabled || right.IsEnabled);
        Capture(f.Window, "overflow-tab-navigators");
    }

    private static void Capture(TopLevel topLevel, string name)
    {
        var folder = Path.Combine(Screenshots.Folder, "oct28-preview");
        Directory.CreateDirectory(folder);
        using var frame = topLevel.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var stream = File.Create(Path.Combine(folder, name + ".png"));
        frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Unexpected request") }); }
    }

    private static ContextMenu OpenMenu(DesktopFixture f) => Assert.Single(f.Window.GetVisualDescendants().OfType<Control>()
        .Select(control => control.ContextMenu).OfType<ContextMenu>().Distinct(), menu => menu.IsOpen);

    private static void RightClick(DesktopFixture f, Control control, Point local)
    {
        var point = control.TranslatePoint(local, f.Window)!.Value;
        f.Window.MouseDown(point, MouseButton.Right);
        f.Window.MouseUp(point, MouseButton.Right);
        Pump(f);
    }

    private static void Pump(DesktopFixture f)
    { Dispatcher.UIThread.RunJobs(); f.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); f.Window.UpdateLayout(); }
}
