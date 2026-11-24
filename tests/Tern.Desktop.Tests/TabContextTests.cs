using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Shell;

namespace Tern.Desktop.Tests;

public sealed class TabContextTests
{
    [AvaloniaFact]
    public async Task Saved_tab_context_copies_and_reveals_its_path_and_reopens_a_closed_file()
    {
        using var fixture = new DesktopFixture();
        var path = await fixture.WriteAsync("target.md", "# Target");
        var target = await fixture.Shell.OpenFileAsync(path);
        fixture.Shell.NewDocument("Other unsaved work");
        Settle(fixture);
        var owner = Assert.Single(fixture.Window.TabStrip.GetVisualDescendants().OfType<StackPanel>(),
            panel => ReferenceEquals(panel.DataContext, target) && panel.ContextMenu is not null);
        var menu = owner.ContextMenu!;
        menu.Open(owner); Settle(fixture);
        MenuItem Action(string tag) => Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Tag, tag));
        Assert.EndsWith("…", Action("tab.rename").Header!.ToString());
        Action("tab.copyPath").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(path, await fixture.Window.Clipboard!.TryGetTextAsync());
        Action("tab.reveal").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal([path], fixture.Launcher.Revealed);
        Action("tab.close").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Settle(fixture);
        Assert.DoesNotContain(target, fixture.Shell.Tabs);
        Assert.Equal("Other unsaved work", fixture.Shell.ActiveTab!.CurrentText);
        Action("tab.reopen").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        for (var attempt = 0; attempt < 100 && fixture.Shell.ActiveTab?.FilePath != path; attempt++)
        { await Task.Delay(10, TestContext.Current.CancellationToken); Settle(fixture); }
        Assert.Equal(path, fixture.Shell.ActiveTab!.FilePath);
        Assert.Equal("# Target", fixture.Shell.ActiveTab.CurrentText);
        Assert.Contains(fixture.Shell.Tabs, tab => tab.CurrentText == "Other unsaved work" && tab.IsDirty);
        menu.Close();
    }

    [AvaloniaTheory]
    [InlineData("tab.close", 2)]
    [InlineData("tab.closeAll", 0)]
    [InlineData("tab.closeRight", 2)]
    [InlineData("tab.closeOthers", 1)]
    public void Tab_context_actions_use_the_clicked_tab_and_preserve_cancel(string tag, int expected)
    {
        using var fixture = new DesktopFixture();
        fixture.Shell.NewDocument("first");
        var target = fixture.Shell.NewDocument("target");
        fixture.Shell.NewDocument("last");
        Settle(fixture);
        var strip = fixture.Window.FindControl<ListBox>("TabStrip")!;
        var owner = Assert.Single(strip.GetVisualDescendants().OfType<StackPanel>(),
            p => ReferenceEquals(p.DataContext, target) && p.ContextMenu is not null);
        var menu = owner.ContextMenu!;
        menu.Open(owner);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["tab.rename", "tab.copyPath", "tab.reveal", "tab.reopen", "tab.close", "tab.closeAll", "tab.closeRight", "tab.closeOthers"],
            menu.Items.OfType<MenuItem>().Select(item => item.Tag));
        Assert.IsType<Separator>(menu.Items[4]);
        var action = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Tag, tag));
        action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Settle(fixture);
        Assert.Equal(3, fixture.Shell.Tabs.Count);
        Assert.Contains(target, fixture.Shell.Tabs);
        fixture.Dialogs.SaveChoice = SaveChoice.Discard;
        action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Settle(fixture);
        Assert.Equal(expected, fixture.Shell.Tabs.Count);
        menu.Close();
    }

    [AvaloniaFact]
    public void Arrow_navigation_scrolls_overflow_and_selected_tab_is_brought_back_into_view()
    {
        using var fixture = new DesktopFixture();
        fixture.Window.Width = 1000;
        var first = fixture.Shell.NewDocument();
        for (var index = 0; index < 15; index++) fixture.Shell.NewDocument();
        Settle(fixture);
        var strip = fixture.Window.FindControl<ListBox>("TabStrip")!;
        var scroll = Assert.Single(strip.GetVisualDescendants().OfType<ScrollViewer>());
        var left = fixture.Window.FindControl<Button>("TabsPrevious")!;
        var right = fixture.Window.FindControl<Button>("TabsNext")!;
        Assert.True(left.IsEnabled);
        var previous = scroll.Offset.X;
        left.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(fixture);
        Assert.True(scroll.Offset.X < previous);
        strip.SelectedItem = first;
        Settle(fixture);
        Assert.InRange(scroll.Offset.X, 0, .5);
        Assert.False(left.IsEnabled);
        Assert.True(right.IsEnabled);
        var container = strip.ContainerFromItem(first)!;
        var selectedPoint = container.TranslatePoint(default, scroll)!.Value;
        Assert.True(selectedPoint.X >= 0 && selectedPoint.X + container.Bounds.Width <= scroll.Bounds.Width);
        right.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(fixture);
        Assert.True(scroll.Offset.X > 0);
        var folder = Path.Combine(Screenshots.Folder, "tabs"); Directory.CreateDirectory(folder);
        using var bitmap = fixture.Window.CaptureRenderedFrame(); Assert.NotNull(bitmap);
        using var stream = File.Create(Path.Combine(folder, "tab-arrow-overflow.png"));
        bitmap.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static void Settle(DesktopFixture fixture)
    { Dispatcher.UIThread.RunJobs(); fixture.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
}
