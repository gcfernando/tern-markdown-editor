using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Tern.Desktop.Services;
using Tern.Desktop.ViewModels;
using Avalonia.VisualTree;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class Oct28PreviewTests
{
    [AvaloniaFact]
    public async Task Capture_url_choices_and_actual_tab_context_for_user_review()
    {
        using var fixture = new DesktopFixture();
        var folder = Path.Combine(Screenshots.Folder, "oct28-preview");
        Directory.CreateDirectory(folder);
        var url = new OpenUrlWindow();
        try
        {
            url.Show(fixture.Window);
            Settle(url);
            Capture(url, Path.Combine(folder, "url-open-or-download.png"));
        }
        finally { url.Close(); }
        var path = await fixture.WriteAsync("local-notes.md", "# Local notes\r\n\r\nA local file for review.\r\n");
        await fixture.Shell.OpenFileAsync(path);
        var tab = fixture.Shell.ActiveTab!;
        await fixture.Window.OpenUrlDocumentAsync(new MarkdownUrlDocument(new Uri("https://example.com/online-guide.md"), "online-guide.md", "# Online guide\n"));
        for (var index = 1; index <= 9; index++)
            fixture.Shell.NewDocument().Session.SetDraftName($"Draft {index:00}");
        fixture.Window.FindControl<ListBox>("TabStrip")!.SelectedItem = tab;
        Settle(fixture.Window);
        await Task.Delay(150, TestContext.Current.CancellationToken); Settle(fixture.Window);
        Capture(fixture.Window, Path.Combine(folder, "tab-navigation.png"));
        var panel = Assert.Single(fixture.Window.FindControl<ListBox>("TabStrip")!.GetVisualDescendants().OfType<StackPanel>(),
            p => ReferenceEquals(p.DataContext, tab) && p.ContextMenu is not null);
        panel.ContextMenu!.Open(panel);
        Settle(fixture.Window);
        await Task.Delay(150, TestContext.Current.CancellationToken); Settle(fixture.Window);
        var popup = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(panel.ContextMenu));
        Capture(popup, Path.Combine(folder, "tab-context-menu.png"));
        panel.ContextMenu.Close();
        var rowPanel = Assert.Single(fixture.Window.FindControl<ListBox>("SideList")!.GetVisualDescendants().OfType<Grid>(),
            grid => grid.DataContext is SideRow row && row.Path == path && grid.ContextMenu is not null);
        rowPanel.ContextMenu!.Open(rowPanel);
        await Task.Delay(150, TestContext.Current.CancellationToken); Settle(fixture.Window);
        Capture(Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(rowPanel.ContextMenu)), Path.Combine(folder, "library-context-menu.png"));
        rowPanel.ContextMenu.Close();
        var add = fixture.Window.FindControl<Button>("WorkspaceAddButton")!;
        var flyout = Assert.IsType<MenuFlyout>(add.Flyout);
        flyout.ShowAt(add);
        await Task.Delay(150, TestContext.Current.CancellationToken); Settle(fixture.Window);
        Capture(Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(flyout.Items.OfType<MenuItem>().First())), Path.Combine(folder, "create-document-menu.png"));
        flyout.Hide();
        var duplicate = new PromptWindow("Already open", "“local-notes.md” is already open. Replace that tab or open another tab?",
            ["Replace existing tab", "Open in new tab", "Cancel"], 2, 2);
        try
        {
            duplicate.Show(fixture.Window);
            await Task.Delay(150, TestContext.Current.CancellationToken); Settle(duplicate);
            Capture(duplicate, Path.Combine(folder, "duplicate-open-choices.png"));
        }
        finally { duplicate.Close(); }
        fixture.Window.FindControl<Button>("StatusLineEnding")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(fixture.Window);
        await Task.Delay(150, TestContext.Current.CancellationToken); Settle(fixture.Window);
        Capture(fixture.Window, Path.Combine(folder, "line-ending-picker.png"));
        fixture.Window.FindControl<Button>("StatusEncoding")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(fixture.Window);
        await Task.Delay(150, TestContext.Current.CancellationToken); Settle(fixture.Window);
        Capture(fixture.Window, Path.Combine(folder, "encoding-picker.png"));
        var rename = new FileNameWindow(false, "Rename local-notes.md", true, "existing.md",
            validateName: _ => "A file named existing.md already exists. Choose a different name.");
        try
        {
            rename.Show(fixture.Window); Settle(rename);
            Assert.Single(rename.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Rename"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Settle(rename);
            await Task.Delay(150, TestContext.Current.CancellationToken); Settle(rename);
            Capture(rename, Path.Combine(folder, "rename-collision.png"));
        }
        finally { rename.Close(); }
    }

    private static void Settle(Window window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }

    private static void Capture(TopLevel topLevel, string path)
    {
        using var bitmap = topLevel.CaptureRenderedFrame();
        Assert.NotNull(bitmap);
        using var stream = File.Create(path);
        bitmap.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
