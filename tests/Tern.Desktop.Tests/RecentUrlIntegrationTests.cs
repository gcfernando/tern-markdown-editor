using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.Services;
using Tern.Desktop.ViewModels;
using Tern.App.Shell;
using Tern.Workspaces;
using Tern.Domain.Text;

namespace Tern.Desktop.Tests;

public sealed class RecentUrlIntegrationTests
{
    [AvaloniaFact]
    public async Task Imported_URL_is_persisted_as_online_reference_and_removal_keeps_open_draft_and_local_file()
    {
        using var fixture = new DesktopFixture();
        var path = await fixture.WriteAsync("keep.md", "Unchanged local bytes");
        var source = new Uri("http://localhost:8000/online.md");
        await fixture.Window.OpenUrlDocumentAsync(new MarkdownUrlDocument(source, "online.md", "# Online\n"));
        var draft = fixture.Shell.ActiveTab!;
        Assert.False(draft.IsDirty);
        Assert.Equal(source.AbsoluteUri, draft.SourceUrl);
        Assert.Equal(LineEnding.Lf, draft.Session.Format.DominantLineEnding);
        draft.ApplyEdits([TextEdit.Insert(0, "edited ")]);
        var restarted = new RecentItemsStore(AppPaths.Under(fixture.AppData).StateDirectory);
        Assert.Equal(source.AbsoluteUri, Assert.Single(restarted.Urls).Path);
        Assert.Empty(fixture.Shell.Recent.Files);
        await fixture.Window.ShowNavAsync("Files");
        Dispatcher.UIThread.RunJobs();
        Assert.True(fixture.Window.FindControl<ListBox>("SideList")!.IsEffectivelyVisible);
        Assert.False(fixture.Window.FindControl<TextBlock>("NoWorkspaceText")!.IsVisible);
        await fixture.Window.ShowNavAsync("Recent");
        Dispatcher.UIThread.RunJobs();
        var rows = fixture.Window.FindControl<ListBox>("SideList")!.Items.OfType<SideRow>().ToArray();
        var row = Assert.Single(rows, r => r.IsOnline);
        Assert.Equal(source.AbsoluteUri, row.Path); Assert.Equal("online.md", row.Title);
        Assert.Contains("Online", row.Detail, StringComparison.Ordinal); Assert.True(row.CanRemoveReference); Assert.False(row.IsMissing);
        var remove = fixture.Window.FindControl<ListBox>("SideList")!.GetVisualDescendants().OfType<Button>()
            .Single(b => ReferenceEquals(b.DataContext, row) && AutomationProperties.GetName(b) == "Remove file reference");
        remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Empty(fixture.Shell.Recent.Urls); Assert.Empty(new RecentItemsStore(AppPaths.Under(fixture.AppData).StateDirectory).Urls);
        Assert.Same(draft, fixture.Shell.ActiveTab); Assert.Equal("edited # Online\n", draft.CurrentText);
        Assert.Null(draft.FilePath); Assert.True(draft.IsDirty);
        Assert.Equal("Unchanged local bytes", await File.ReadAllTextAsync(path));
    }
}
