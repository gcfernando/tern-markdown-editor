using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Shell;
using Tern.Desktop.Services;
using Tern.Desktop.ViewModels;
using Tern.Desktop.Views;
using Tern.Domain.Text;
using Tern.Workspaces;

namespace Tern.Desktop.Tests;

public sealed class LibraryDeleteKeyTests
{
    [AvaloniaFact]
    public async Task Context_row_real_confirmation_button_recycles_B_while_A_stays_active()
    {
        using var f = new DesktopFixture();
        var b = await f.WriteAsync("context.md", "Context bytes");
        f.Shell.Recent.AddFile(b);
        var a = await f.WriteAsync("active.md", "Active bytes");
        var active = await f.Shell.OpenFileAsync(a);
        f.Dialogs.DeleteResponse = new DesktopDialogs { Owner = f.Window }.ConfirmDeleteAsync;
        var recycle = new FakeRecycle(Path.Combine(f.AppData, "test-recycle"));
        f.Window.LibraryDeletionFactory = () => recycle;
        await f.Window.ShowNavAsync("Recent"); Pump(f);
        var list = f.Window.FindControl<ListBox>("SideList")!;
        var row = Assert.Single(list.Items.OfType<SideRow>(), item => item.Path == b);
        var container = Assert.IsType<ListBoxItem>(list.ContainerFromItem(row));
        var host = Assert.Single(container.GetVisualDescendants().OfType<Control>(), control => control.ContextMenu is not null);
        host.ContextMenu!.Open(host); Pump(f);
        var item = Assert.Single(host.ContextMenu.Items.OfType<MenuItem>(), item => item.Header?.ToString()?.StartsWith("Move File to Recycle Bin") == true);
        Assert.Same(row, item.DataContext);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(f, () => f.Window.OwnedWindows.OfType<PromptWindow>().Any());
        var prompt = Assert.Single(f.Window.OwnedWindows.OfType<PromptWindow>());
        var confirm = Assert.Single(prompt.GetVisualDescendants().OfType<Button>(), button => button.Classes.Contains("danger"));
        prompt.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        using (var frame = prompt.CaptureRenderedFrame())
        {
            Assert.NotNull(frame);
            var output = Path.Combine(Screenshots.Folder, "reported-recycle");
            Directory.CreateDirectory(output);
            using var stream = File.Create(Path.Combine(output, "owned-file-confirmation.png"));
            frame.Save(stream);
        }
        Click(prompt, confirm);
        await Until(f, () => recycle.Calls == 1 && !File.Exists(b));
        Assert.Equal("Context bytes", await File.ReadAllTextAsync(recycle.Destination));
        Assert.Equal("Active bytes", await File.ReadAllTextAsync(a));
        Assert.Same(active, f.Shell.ActiveTab);
    }

    [AvaloniaFact]
    public async Task Real_confirmation_button_recycles_focused_row_after_activation_clears_selection()
    {
        using var f = new DesktopFixture();
        var a = await f.WriteAsync("active.md", "Active bytes");
        var b = await f.WriteAsync("focused.md", "Focused bytes");
        f.Shell.Recent.AddFile(b);
        var active = await f.Shell.OpenFileAsync(a);
        f.Dialogs.DeleteResponse = new DesktopDialogs { Owner = f.Window }.ConfirmDeleteAsync;
        var recycle = new FakeRecycle(Path.Combine(f.AppData, "test-recycle"));
        f.Window.LibraryDeletionFactory = () => recycle;
        await f.Window.ShowNavAsync("Recent"); Pump(f);
        var list = f.Window.FindControl<ListBox>("SideList")!;
        var rowA = Assert.Single(list.Items.OfType<SideRow>(), item => item.Path == a);
        var rowB = Assert.Single(list.Items.OfType<SideRow>(), item => item.Path == b);
        list.SelectedItem = rowA; Pump(f);
        rowB = Assert.Single(list.Items.OfType<SideRow>(), item => item.Path == b);
        var container = Assert.IsType<ListBoxItem>(list.ContainerFromItem(rowB));
        container.Focus();
        Assert.Same(rowB, container.DataContext);
        Assert.True(container.IsFocused);
        Assert.Same(active, f.Shell.ActiveTab);
        container.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete });
        await Until(f, () => f.Window.OwnedWindows.OfType<PromptWindow>().Any());
        var prompt = Assert.Single(f.Window.OwnedWindows.OfType<PromptWindow>());
        var message = Assert.Single(prompt.GetVisualDescendants().OfType<SelectableTextBlock>()).Text!;
        Assert.Contains(b, message);
        var confirm = Assert.Single(prompt.GetVisualDescendants().OfType<Button>(), button => button.Classes.Contains("danger"));
        Assert.False(confirm.IsDefault);
        Assert.True(Assert.Single(prompt.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Cancel")).IsDefault);
        Click(prompt, confirm);
        await Until(f, () => recycle.Calls == 1 && !File.Exists(b));
        Assert.Equal("Focused bytes", await File.ReadAllTextAsync(recycle.Destination));
        Assert.Equal("Active bytes", await File.ReadAllTextAsync(a));
        Assert.Same(active, f.Shell.ActiveTab);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Focused_tree_file_overrides_empty_or_other_selected_row(bool staleSelection)
    {
        using var f = new DesktopFixture();
        var a = await f.WriteAsync(@"workspace\active.md", "Active bytes");
        var b = await f.WriteAsync(@"workspace\focused.md", "Focused bytes");
        var root = Path.GetDirectoryName(b)!;
        WorkspaceDefinition.Create(root, "Test workspace");
        Assert.True(await f.Shell.OpenWorkspaceAsync(root));
        var active = await f.Shell.OpenFileAsync(a);
        f.Dialogs.ConfirmDelete = true;
        var recycle = new FakeRecycle(Path.Combine(f.AppData, "test-recycle"));
        f.Window.LibraryDeletionFactory = () => recycle;
        await f.Window.ShowNavAsync("Files"); Pump(f);
        Assert.Single(f.Window.FileTree.Items.OfType<TreeRow>()).IsExpanded = true; Pump(f);
        var rowA = Assert.Single(f.Window.FileTree.Items.OfType<TreeRow>().SelectMany(row => row.Flatten()), row => WorkspacePaths.PathsEqual(row.FullPath, a));
        f.Window.FileTree.SelectedItem = staleSelection ? rowA : null; Pump(f);
        var container = Assert.Single(f.Window.FileTree.GetVisualDescendants().OfType<TreeViewItem>(),
            item => item.DataContext is TreeRow row && WorkspacePaths.PathsEqual(row.FullPath, b));
        container.Focus();
        Assert.True(container.IsFocused);
        Assert.NotSame(container.DataContext, f.Window.FileTree.SelectedItem);
        container.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete });
        await Until(f, () => recycle.Calls == 1 && !File.Exists(b));
        Assert.Equal("Focused bytes", await File.ReadAllTextAsync(recycle.Destination));
        Assert.Equal("Active bytes", await File.ReadAllTextAsync(a));
        Assert.Same(active, f.Shell.ActiveTab);
    }

    [AvaloniaTheory]
    [InlineData("cancel")]
    [InlineData("enter")]
    [InlineData("escape")]
    [InlineData("close")]
    public async Task Real_confirmation_dismissal_keeps_disk_dirty_buffer_and_references(string dismissal)
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("keep.md", "Disk bytes");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "Unsaved ")]);
        await f.Shell.HistoryStore.CaptureAsync(path, "Previous history");
        f.Dialogs.DeleteResponse = new DesktopDialogs { Owner = f.Window }.ConfirmDeleteAsync;
        var recycle = new FakeRecycle(Path.Combine(f.AppData, "test-recycle"));
        f.Window.LibraryDeletionFactory = () => recycle;
        await f.Window.ShowNavAsync("Recent"); Pump(f);
        var row = Assert.Single(f.Window.SideList.Items.OfType<SideRow>(), item => item.Path == path);
        var container = Assert.IsType<ListBoxItem>(f.Window.SideList.ContainerFromItem(row));
        container.Focus();
        container.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete });
        await Until(f, () => f.Window.OwnedWindows.OfType<PromptWindow>().Any());
        var prompt = Assert.Single(f.Window.OwnedWindows.OfType<PromptWindow>());
        var cancel = Assert.Single(prompt.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Cancel"));
        Assert.True(cancel.IsDefault);
        Assert.True(cancel.IsFocused);
        var message = Assert.Single(prompt.GetVisualDescendants().OfType<SelectableTextBlock>()).Text!;
        Assert.Contains(path, message);
        Assert.Contains("1 open tab(s) have unsaved edits", message);
        var waiting = Assert.Single(f.Shell.Activities.Items, activity => activity.Kind == "Recycling Library file");
        Assert.Equal(ActivityState.WaitingForUser, waiting.State);
        Assert.False(waiting.IsIndeterminate);
        Assert.False(f.Shell.IsBusy);
        if (dismissal == "cancel") Click(prompt, cancel);
        else if (dismissal == "close") prompt.Close();
        else
        {
            var key = dismissal == "enter" ? Key.Enter : Key.Escape;
            prompt.KeyPress(key, RawInputModifiers.None, default, null);
        }
        await Until(f, () => f.Shell.Activities.Items.Any(activity => activity.Kind == "Recycling Library file" && activity.State == ActivityState.Canceled));
        Assert.Equal(0, recycle.Calls);
        Assert.Equal("Disk bytes", await File.ReadAllTextAsync(path));
        Assert.Equal("Unsaved Disk bytes", tab.CurrentText);
        Assert.True(tab.IsDirty);
        Assert.Same(tab, Assert.Single(f.Shell.Tabs));
        Assert.Contains(f.Shell.Recent.Files, entry => entry.Path == path);
        Assert.Single(await f.Shell.HistoryStore.ReadAsync(path));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interactive_row_descendants_do_not_turn_Delete_into_file_recycling(bool editable)
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("kept.md", "Keep bytes");
        await f.Shell.OpenFileAsync(path);
        await f.Window.ShowNavAsync("Recent"); Pump(f);
        var row = Assert.Single(f.Window.SideList.Items.OfType<SideRow>(), item => item.Path == path);
        var container = Assert.IsType<ListBoxItem>(f.Window.SideList.ContainerFromItem(row));
        Control interactive = editable ? new TextBox { Text = "abc" } : new Button { Content = new TextBlock { Text = "Action" } };
        container.ContentTemplate = null;
        container.Content = interactive; Pump(f);
        Control source = editable
            ? Assert.Single(interactive.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.TextPresenter>())
            : Assert.IsType<TextBlock>(((Button)interactive).Content);
        var key = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete };
        source.RaiseEvent(key); Pump(f);
        Assert.Equal(0, f.Dialogs.DeletePrompts);
        Assert.Equal("Keep bytes", await File.ReadAllTextAsync(path));
    }

    [AvaloniaTheory]
    [InlineData("online")]
    [InlineData("missing")]
    [InlineData("draft")]
    [InlineData("root")]
    public async Task Ineligible_row_does_not_consume_Delete_or_create_a_recycle_service(string kind)
    {
        using var f = new DesktopFixture();
        var calls = 0;
        f.Window.LibraryDeletionFactory = () => { calls++; return new FakeRecycle(Path.Combine(f.AppData, "test-recycle")); };
        await f.Window.ShowNavAsync(kind == "root" ? "Files" : "Recent"); Pump(f);
        Control container;
        if (kind == "root")
        {
            WorkspaceDefinition.Create(f.Docs, "Test workspace");
            Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));
            f.Window.RefreshLibraryContents(); Pump(f);
            container = Assert.Single(f.Window.FileTree.GetVisualDescendants().OfType<TreeViewItem>());
        }
        else
        {
            var row = kind switch
            {
                "online" => new SideRow("Online", "", "https://example.test/online.md", 1, IsOnline: true, CanRemoveReference: true),
                "missing" => new SideRow("Missing", "", Path.Combine(f.Docs, "missing.md"), 1, IsMissing: true, CanRemoveReference: true),
                _ => new SideRow("Draft", "", null, 1, CanRemoveReference: true)
            };
            f.Window.SideList.ItemsSource = new[] { row }; Pump(f);
            container = Assert.IsType<ListBoxItem>(f.Window.SideList.ContainerFromItem(row));
        }
        container.Focus();
        var key = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete };
        container.RaiseEvent(key); Pump(f);
        Assert.False(key.Handled);
        Assert.Equal(0, calls);
        Assert.Equal(0, f.Dialogs.DeletePrompts);
        Assert.True(Directory.Exists(f.Docs));
    }

    [AvaloniaFact]
    public async Task Slow_failed_recycling_is_visible_blocks_duplicates_and_allows_explicit_retry()
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("retry.md", "Keep until recycled");
        var tab = await f.Shell.OpenFileAsync(path);
        f.Dialogs.ConfirmDelete = true;
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recycle = new FakeRecycle(Path.Combine(f.AppData, "test-recycle"))
        { BeforeMove = () => resume.Task, Failure = new IOException("Synthetic recycle unavailable") };
        var factoryCalls = 0;
        f.Window.LibraryDeletionFactory = () => { factoryCalls++; return recycle; };
        await f.Window.ShowNavAsync("Recent"); Pump(f);
        ListBoxItem Row()
        {
            var row = Assert.Single(f.Window.SideList.Items.OfType<SideRow>(), item => item.Path == path);
            return Assert.IsType<ListBoxItem>(f.Window.SideList.ContainerFromItem(row));
        }
        void Delete(ListBoxItem row) => row.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete });
        var container = Row(); container.Focus(); Delete(container);
        await Until(f, () => recycle.Calls == 1);
        try
        {
            Assert.True(f.Shell.IsBusy);
            var running = Assert.Single(f.Shell.Activities.Items, activity => activity.Kind == "Recycling Library file");
            Assert.Equal(ActivityState.Active, running.State);
            Assert.Contains("Windows Recycle Bin", running.Phase);
            Assert.True(running.IsIndeterminate);
            Assert.True(File.Exists(path));
            Assert.Same(tab, Assert.Single(f.Shell.Tabs));
            Delete(container); Pump(f);
            Assert.Equal(1, factoryCalls);
            Assert.Equal(1, f.Dialogs.DeletePrompts);
        }
        finally { resume.TrySetResult(); }
        await Until(f, () => !f.Shell.IsBusy && f.Shell.Activities.Items.Any(activity => activity.Kind == "Recycling Library file" && activity.State == ActivityState.Failed));
        Assert.Equal(StatusSeverity.Error, f.Shell.StatusSeverity);
        Assert.Equal("Keep until recycled", await File.ReadAllTextAsync(path));
        Assert.Same(tab, Assert.Single(f.Shell.Tabs));
        recycle.BeforeMove = null; recycle.Failure = null;
        container = Row(); container.Focus(); Delete(container);
        await Until(f, () => recycle.Calls == 2 && !File.Exists(path));
        Assert.Equal(2, factoryCalls);
        Assert.Equal(2, f.Dialogs.DeletePrompts);
        Assert.Equal("Keep until recycled", await File.ReadAllTextAsync(recycle.Destination));
    }

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

    private static void Click(PromptWindow prompt, Button button)
    {
        prompt.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), prompt)!.Value;
        prompt.MouseDown(point, MouseButton.Left);
        prompt.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class FakeRecycle(string directory) : ILocalFileDeletion
    {
        public int Calls { get; private set; }
        public string Destination { get; private set; } = string.Empty;
        public Func<Task>? BeforeMove { get; set; }
        public Exception? Failure { get; set; }
        public async Task MoveToRecycleBinAsync(string path)
        {
            Calls++;
            if (BeforeMove is { } before) await before();
            if (Failure is { } failure) throw failure;
            Directory.CreateDirectory(directory);
            Destination = Path.Combine(directory, Path.GetFileName(path));
            File.Move(path, Destination);
        }
    }
}
