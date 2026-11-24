using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Shell;
using Tern.Desktop.Services;
using Tern.Desktop.ViewModels;
using Tern.Workspaces;

namespace Tern.Desktop.Tests;

public sealed class LibrarySelectionOrderTests
{
    [AvaloniaTheory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Existing_file_or_tab_activation_keeps_library_order_and_containers(bool selectRow, bool reducedMotion)
    {
        var reader = MotionPolicy.SystemAnimationsReader;
        MotionPolicy.SystemAnimationsReader = () => true;
        try
        {
            using var f = new DesktopFixture();
            f.Shell.Settings.ReduceMotion = reducedMotion;
            f.Window.ApplySettings();
            Assert.Equal(reducedMotion, MotionPolicy.IsReduced);
            Assert.Equal(reducedMotion, MotionPolicy.Duration(MotionPurpose.Tab) == TimeSpan.Zero);
            await f.Window.ShowNavAsync("Recent");
            var readme = await f.WriteAsync("README.md", "# Readme\n");
            var notices = await f.WriteAsync("THIRD_PARTY_NOTICES.md", "# Notices\n");
            var readmeTab = (await f.Shell.OpenFileAsync(readme))!;
            await f.Shell.OpenFileAsync(notices);
            await f.Window.ShowNavAsync("Files");
            Pump(f);
            var list = f.Window.SideList;
            var rows = list.Items.OfType<SideRow>().ToArray();
            var source = list.ItemsSource;
            var containers = list.GetVisualDescendants().OfType<ListBoxItem>().ToArray();
            Assert.Equal(new[] { notices, readme }, rows.Select(row => row.Path));
            Assert.Equal(2, containers.Length);

            if (selectRow) list.SelectedItem = rows[1];
            else f.Shell.SelectTab(readmeTab, TabActivationReason.User);
            await Until(f, () => ReferenceEquals(readmeTab, f.Shell.ActiveTab));
            Pump(f);

            Assert.Equal(reducedMotion, MotionPolicy.IsReduced);
            Assert.Equal(new[] { notices, readme }, list.Items.OfType<SideRow>().Select(row => row.Path));
            Assert.Same(source, list.ItemsSource);
            Assert.All(rows, row => Assert.Same(row, Assert.Single(list.Items.OfType<SideRow>(), current => current.Path == row.Path)));
            Assert.All(containers, container => Assert.Contains(list.GetVisualDescendants(), current => ReferenceEquals(container, current)));
            Assert.Equal("# Readme\n", readmeTab.CurrentText);
            if (selectRow)
            {
                Assert.Equal(readme, f.Shell.Recent.Files[0].Path);
                await f.Window.ShowNavAsync("Recent");
                Pump(f);
                Assert.Equal(readme, list.Items.OfType<SideRow>().First().Path);
                await f.Window.ShowNavAsync("Files");
                Pump(f);
                Assert.Equal(new[] { notices, readme }, list.Items.OfType<SideRow>().Select(row => row.Path));
            }
        }
        finally { MotionPolicy.SystemAnimationsReader = reader; MotionPolicy.Apply(false); }
    }

    [AvaloniaFact]
    public async Task Membership_pin_and_rename_updates_keep_unaffected_library_rows()
    {
        using var f = new DesktopFixture();
        var first = await f.WriteAsync("first.md", "# First\n");
        var second = await f.WriteAsync("second.md", "# Second\n");
        await f.Window.ShowNavAsync("Recent");
        await f.Shell.OpenFileAsync(first);
        await f.Shell.OpenFileAsync(second);
        await f.Window.ShowNavAsync("Files");
        Pump(f);
        var list = f.Window.SideList;
        var original = list.Items.OfType<SideRow>().ToArray();
        var third = await f.WriteAsync("third.md", "# Third\n");
        await f.Shell.OpenFileAsync(third);
        Pump(f);
        var thirdRow = Assert.Single(list.Items.OfType<SideRow>(), row => row.Path == third);
        Assert.Equal(new[] { second, first, third }, list.Items.OfType<SideRow>().Select(row => row.Path));
        Assert.All(original, row => Assert.Same(row, Assert.Single(list.Items.OfType<SideRow>(), current => current.Path == row.Path)));

        Assert.True(f.Shell.Recent.SetPinned(RecentKind.File, third, true));
        Pump(f);
        Assert.Equal(new[] { third, second, first }, list.Items.OfType<SideRow>().Select(row => row.Path));
        Assert.True(f.Shell.Recent.Remove(RecentKind.File, second));
        Assert.True(await f.Shell.CloseTabAsync(Assert.Single(f.Shell.Tabs, tab => tab.FilePath == second)));
        Pump(f);
        Assert.Equal(new[] { third, first }, list.Items.OfType<SideRow>().Select(row => row.Path));
        var renamed = Path.Combine(f.Docs, "renamed.md");
        File.Move(first, renamed);
        f.Shell.Recent.Rename(RecentKind.File, first, renamed);
        Assert.True(await f.Shell.CloseTabAsync(Assert.Single(f.Shell.Tabs, tab => tab.FilePath == first)));
        Pump(f);
        Assert.Equal(new[] { third, renamed }, list.Items.OfType<SideRow>().Select(row => row.Path));
        Assert.Equal("renamed.md", list.Items.OfType<SideRow>().Last().Title);
        Assert.Same(thirdRow, list.Items.OfType<SideRow>().First());
    }

    [AvaloniaFact]
    public async Task Explicit_case_only_rename_updates_library_spelling_even_when_tab_closes_in_recent_view()
    {
        using var f = new DesktopFixture();
        var original = await f.WriteAsync("README.md", "# Readme\n");
        var tab = (await f.Shell.OpenFileAsync(original))!;
        var kept = await f.WriteAsync("kept.md", "# Keep\n");
        await f.Shell.OpenFileAsync(kept);
        Pump(f);
        var list = f.Window.SideList;
        var rows = list.Items.OfType<SideRow>().ToArray();
        var index = Array.FindIndex(rows, row => row.Path == original);
        var keptRow = Assert.Single(rows, row => row.Path == kept);
        await f.Window.ShowNavAsync("Recent");

        Assert.True(await f.Shell.RenameDocumentAsync(tab, "readme.md"));
        var renamed = Path.Combine(f.Docs, "readme.md");
        Assert.Equal(renamed, tab.FilePath);
        Assert.True(await f.Shell.CloseTabAsync(tab));
        await f.Window.ShowNavAsync("Files");
        Pump(f);

        var current = list.Items.OfType<SideRow>().ToArray();
        Assert.Equal(renamed, current[index].Path);
        Assert.Equal("readme.md", current[index].Title);
        Assert.Same(keptRow, Assert.Single(current, row => row.Path == kept));
        Assert.Equal("# Readme\n", await File.ReadAllTextAsync(renamed));
        f.Shell.Recent.AddFile(renamed.ToUpperInvariant());
        Pump(f);
        Assert.Same(current[index], list.Items.OfType<SideRow>().ElementAt(index));
        Assert.Equal(renamed, list.Items.OfType<SideRow>().ElementAt(index).Path);
    }

    [AvaloniaFact]
    public async Task Explicit_reference_rename_spelling_uses_provenance_without_existing_file_metadata()
    {
        using var f = new DesktopFixture();
        var original = Path.Combine(f.Docs, "unavailable", "README.md");
        var renamed = Path.Combine(f.Docs, "unavailable", "readme.md");
        f.Shell.Recent.AddFile(original);
        Pump(f);
        var originalRow = Assert.Single(f.Window.SideList.Items.OfType<SideRow>());
        Assert.True(originalRow.IsMissing);
        f.Shell.Recent.AddFile(original.ToUpperInvariant());
        Pump(f);
        Assert.Same(originalRow, Assert.Single(f.Window.SideList.Items.OfType<SideRow>()));

        await f.Window.ShowNavAsync("Recent");
        f.Shell.Recent.Rename(RecentKind.File, original, renamed);
        await f.Window.ShowNavAsync("Files");
        Pump(f);

        var row = Assert.Single(f.Window.SideList.Items.OfType<SideRow>());
        Assert.Equal(renamed, row.Path);
        Assert.EndsWith("readme.md", row.Title, StringComparison.Ordinal);
        Assert.True(row.IsMissing);
        Assert.False(Directory.Exists(Path.GetDirectoryName(original)));
        f.Window.RefreshLibraryContents();
        Pump(f);
        Assert.Same(row, Assert.Single(f.Window.SideList.Items.OfType<SideRow>()));
    }

    [AvaloniaFact]
    public async Task Recent_updates_preserve_scroll_focused_action_and_canonical_reference_membership()
    {
        using var f = new DesktopFixture();
        var workspace = Path.Combine(f.Docs, "workspace");
        Directory.CreateDirectory(workspace);
        Assert.True(await f.Shell.OpenFolderAsync(workspace));
        var paths = new List<string>();
        for (var index = 0; index < 20; index++)
            paths.Add(await f.WriteAsync($"document-{index:00}.md", "# Document\n"));
        foreach (var path in paths) f.Shell.Recent.AddFile(path);
        f.Shell.Recent.AddUrl("https://EXAMPLE.test/A.md#heading");
        f.Shell.Recent.AddUrl("https://example.test/a.md");
        Pump(f);
        var list = f.Window.SideList;
        var rows = list.Items.OfType<SideRow>().ToArray();
        Assert.Equal(22, rows.Length);
        var scroll = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>());
        scroll.Offset = new Avalonia.Vector(0, 120);
        Pump(f);
        Assert.True(scroll.Offset.Y > 0);
        var action = list.GetVisualDescendants().OfType<Button>().First(button => button.IsEffectivelyVisible
            && Avalonia.Automation.AutomationProperties.GetName(button) == "File reference actions"
            && button.DataContext is SideRow { IsOnline: false });
        Assert.True(action.Focus());
        Pump(f);
        var focusedRow = Assert.IsType<SideRow>(action.DataContext);
        var offset = scroll.Offset;
        f.Shell.Recent.AddFile(paths[0].ToUpperInvariant());
        f.Shell.Recent.AddUrl("https://example.test/A.md#other");
        Pump(f);

        Assert.Equal(rows.Select(row => row.Path), list.Items.OfType<SideRow>().Select(row => row.Path));
        Assert.Same(focusedRow, action.DataContext);
        Assert.True(action.IsFocused);
        Assert.Equal(offset, scroll.Offset);
        Assert.Equal(2, list.Items.OfType<SideRow>().Count(row => row.IsOnline));
    }

    private static async Task Until(DesktopFixture f, Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        { await Task.Delay(10, TestContext.Current.CancellationToken); Pump(f); }
        Assert.True(condition());
    }

    private static void Pump(DesktopFixture f)
    { Dispatcher.UIThread.RunJobs(); f.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
}
