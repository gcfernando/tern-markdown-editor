using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Tern.App.Commands;
using Tern.Desktop.Services;
using Tern.Filesystem;

namespace Tern.Desktop.Tests;

public sealed class TabLocationBadgeTests
{
    [AvaloniaFact]
    public async Task Badge_follows_each_active_tab_not_its_filename_or_view_mode()
    {
        using var fixture = new DesktopFixture();
        var path = await fixture.WriteAsync("same.md", "# Local source\n");
        var local = (await fixture.Shell.OpenFileAsync(path))!; Pump(fixture);
        Assert.Equal("Local files", Badge(fixture));
        await fixture.Window.OpenUrlDocumentAsync(new MarkdownUrlDocument(new Uri("https://example.com/same.md"), "same.md", "# Online copy\n"));
        var online = fixture.Shell.ActiveTab!; Pump(fixture);
        Assert.NotSame(local, online); Assert.Equal(local.DisplayName, online.DisplayName);
        Assert.Equal("Online files", Badge(fixture)); Assert.Null(online.FilePath);
        foreach (var mode in new[] { CommandIds.ViewSource, CommandIds.ViewSplit, CommandIds.ViewReading })
        {
            await fixture.Window.RunCommandAsync(mode); Pump(fixture); Assert.Equal("Online files", Badge(fixture));
        }
        fixture.Shell.ActiveTab = local; Pump(fixture); Assert.Equal("Local files", Badge(fixture));
        fixture.Shell.ActiveTab = online; Pump(fixture); Assert.Equal("Online files", Badge(fixture));
        fixture.Shell.NewDocument(); Pump(fixture); Assert.Equal("Local files", Badge(fixture));
        Assert.Equal("# Online copy\n", online.CurrentText); Assert.Equal("# Local source\n", await File.ReadAllTextAsync(path));
    }

    [AvaloniaFact]
    public async Task SaveAs_cancellation_keeps_online_badge_and_success_changes_it_to_local()
    {
        using var fixture = new DesktopFixture();
        await fixture.Window.OpenUrlDocumentAsync(new MarkdownUrlDocument(new Uri("http://localhost:8000/from-server.md"), "from-server.md", "# From server\n"));
        var online = fixture.Shell.ActiveTab!; Pump(fixture); Assert.Equal("Online files", Badge(fixture));
        await fixture.Window.RunCommandAsync(CommandIds.SaveAs); Pump(fixture); // Scripted picker cancels.
        Assert.Null(online.FilePath); Assert.Equal("Online files", Badge(fixture)); Assert.False(online.IsDirty);
        Assert.Equal("http://localhost:8000/from-server.md", online.SourceUrl);
        var path = Path.Combine(fixture.Docs, "saved-copy.md");
        var outcome = await online.SaveAsAsync(path, overwriteExisting: false, cancellationToken: TestContext.Current.CancellationToken);
        Assert.IsType<SaveSucceeded>(outcome); Pump(fixture);
        Assert.Equal("Local files", Badge(fixture)); Assert.Equal(path, online.FilePath);
        Assert.Equal("# From server\n", await File.ReadAllTextAsync(path));
    }

    [AvaloniaFact]
    public async Task Failed_SaveAs_does_not_relabel_or_overwrite_online_copy()
    {
        using var fixture = new DesktopFixture();
        var collision = await fixture.WriteAsync("existing.md", "Keep disk file");
        await fixture.Window.OpenUrlDocumentAsync(new MarkdownUrlDocument(new Uri("https://example.com/copy.md"), "copy.md", "Online edits"));
        var online = fixture.Shell.ActiveTab!;
        var outcome = await online.SaveAsAsync(collision, overwriteExisting: false, cancellationToken: TestContext.Current.CancellationToken);
        Assert.IsNotType<SaveSucceeded>(outcome); Pump(fixture);
        Assert.Null(online.FilePath); Assert.Equal("Online files", Badge(fixture));
        Assert.Equal("Online edits", online.CurrentText); Assert.Equal("Keep disk file", await File.ReadAllTextAsync(collision));
    }

    private static string? Badge(DesktopFixture fixture) => fixture.Window.FindControl<TextBlock>("LocationBadgeText")!.Text;
    private static void Pump(DesktopFixture fixture) { fixture.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); fixture.Window.UpdateLayout(); }
}
