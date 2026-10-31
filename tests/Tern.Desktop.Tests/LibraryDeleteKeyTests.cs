using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using Tern.App.Shell;
using Tern.Desktop.ViewModels;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class LibraryDeleteKeyTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Library_delete_key_confirms_and_only_recycles_after_approval(bool confirm)
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("local.md", "# Keep these bytes\n");
        var tab = await f.Shell.OpenFileAsync(path);
        f.Dialogs.ConfirmDelete = confirm;
        var recycle = new FakeRecycle(Path.Combine(f.AppData, "test-recycle"));
        f.Window.LibraryDeletionFactory = () => recycle;
        await f.Window.ShowNavAsync("Recent"); Pump(f);
        var list = f.Window.FindControl<ListBox>("SideList")!;
        var row = Assert.Single(list.Items.OfType<SideRow>(), item => !item.IsOnline && item.Path == path);
        var container = Assert.IsType<ListBoxItem>(list.ContainerFromItem(row));
        list.SelectedItem = row; Pump(f);
        container.Focus();
        container.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete });
        await Until(f, () => f.Dialogs.DeletePrompts == 1 && (!confirm || f.Shell.Tabs.Count == 0));
        Assert.Equal(1, f.Dialogs.DeletePrompts);
        Assert.Equal(confirm ? 1 : 0, recycle.Calls);
        Assert.Equal(!confirm, File.Exists(path));
        if (confirm)
        {
            Assert.Empty(f.Shell.Tabs);
            Assert.DoesNotContain(f.Shell.Recent.Files, entry => entry.Path == path);
            Assert.Equal("# Keep these bytes\n", await File.ReadAllTextAsync(recycle.Destination));
        }
        else Assert.Same(tab, Assert.Single(f.Shell.Tabs));
    }

    [AvaloniaFact]
    public async Task Online_library_delete_key_never_requests_physical_deletion()
    {
        using var f = new DesktopFixture();
        var factoryCalls = 0;
        f.Window.LibraryDeletionFactory = () => { factoryCalls++; return new FakeRecycle(Path.Combine(f.AppData, "test-recycle")); };
        f.Shell.Recent.AddUrl("https://example.com/online.md");
        await f.Window.ShowNavAsync("Recent"); Pump(f);
        var list = f.Window.FindControl<ListBox>("SideList")!;
        var row = Assert.Single(list.Items.OfType<SideRow>(), item => item.IsOnline);
        var container = Assert.IsType<ListBoxItem>(list.ContainerFromItem(row));
        container.Focus();
        container.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete });
        Pump(f);
        Assert.Equal(0, factoryCalls);
        Assert.Equal(0, f.Dialogs.DeletePrompts);
        Assert.Single(f.Shell.Recent.Urls);
    }

    [AvaloniaFact]
    public async Task Editor_delete_key_edits_text_without_recycling_its_file()
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("editing.md", "abc");
        var tab = await f.Shell.OpenFileAsync(path); Pump(f);
        var calls = 0;
        f.Window.LibraryDeletionFactory = () => { calls++; return new FakeRecycle(Path.Combine(f.AppData, "test-recycle")); };
        f.Window.Editor.TextArea.Focus();
        f.Window.Editor.CaretOffset = 0;
        f.Window.Editor.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete });
        Pump(f);
        Assert.Equal("bc", tab!.CurrentText);
        Assert.Equal("abc", await File.ReadAllTextAsync(path));
        Assert.Equal(0, calls);
        Assert.Equal(0, f.Dialogs.DeletePrompts);
    }

    [AvaloniaFact]
    public void Main_window_and_url_dialog_use_the_real_packaged_application_icon()
    {
        using var f = new DesktopFixture();
        Assert.NotNull(f.Window.Icon);
        var url = new OpenUrlWindow();
        try { Assert.NotNull(url.Icon); }
        finally { url.Close(); }
        using var asset = AssetLoader.Open(new Uri("avares://Tern/Assets/tern.ico"));
        var header = new byte[4];
        Assert.Equal(4, asset.Read(header));
        Assert.Equal(new byte[] { 0, 0, 1, 0 }, header);
    }

    private static async Task Until(DesktopFixture f, Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        { await Task.Delay(10, TestContext.Current.CancellationToken); Pump(f); }
        Assert.True(condition());
    }
    private static void Pump(DesktopFixture f)
    { Dispatcher.UIThread.RunJobs(); f.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }

    private sealed class FakeRecycle(string directory) : ILocalFileDeletion
    {
        public int Calls { get; private set; }
        public string Destination { get; private set; } = string.Empty;
        public Task MoveToRecycleBinAsync(string path)
        {
            Calls++;
            Directory.CreateDirectory(directory);
            Destination = Path.Combine(directory, Path.GetFileName(path));
            File.Move(path, Destination);
            return Task.CompletedTask;
        }
    }
}
