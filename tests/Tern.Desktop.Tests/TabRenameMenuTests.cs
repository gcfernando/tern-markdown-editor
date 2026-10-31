using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.Services;

namespace Tern.Desktop.Tests;

public sealed class TabRenameMenuTests
{
    [AvaloniaFact]
    public async Task Rename_menu_is_available_for_local_drafts_and_disabled_for_online_copies()
    {
        using var fixture = new DesktopFixture();
        var local = fixture.Shell.NewDocument();
        await fixture.Window.OpenUrlDocumentAsync(new MarkdownUrlDocument(new Uri("https://example.com/rename.md"), "rename.md", "# Online"));
        var online = fixture.Shell.ActiveTab!;
        var strip = fixture.Window.FindControl<ListBox>("TabStrip")!;
        foreach (var tab in new[] { local, online })
        {
            strip.SelectedItem = tab;
            Dispatcher.UIThread.RunJobs(); fixture.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var owner = Assert.Single(strip.GetVisualDescendants().OfType<StackPanel>(),
                panel => ReferenceEquals(panel.DataContext, tab) && panel.ContextMenu is not null);
            var menu = owner.ContextMenu!;
            menu.Open(owner); Dispatcher.UIThread.RunJobs();
            var rename = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Tag, "tab.rename"));
            Assert.Equal(ReferenceEquals(tab, local), rename.IsEnabled);
            Assert.Equal(new[] { "Rename", "Close", "Close All", "Close Tabs to the Right", "Close Other Tabs" },
                menu.Items.OfType<MenuItem>().Select(item => item.Header as string));
            Assert.Equal(2, fixture.Shell.Tabs.Count);
            menu.Close();
        }
    }
}
