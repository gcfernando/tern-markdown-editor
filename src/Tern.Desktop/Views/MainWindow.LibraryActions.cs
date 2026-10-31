using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Tern.Desktop.Services;
using Tern.Desktop.ViewModels;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private bool _libraryRightPress;
    private readonly Dictionary<string, CancellationTokenSource> _onlineRequests = new(StringComparer.Ordinal);
    internal Func<MarkdownUrlLoader> OnlineLoaderFactory { get; set; } = () => new MarkdownUrlLoader();
    private void OnLibraryContextClosed(object? sender, RoutedEventArgs e) => _libraryRightPress = false;

    private void TrackLibraryPointer(object? sender, PointerPressedEventArgs e)
        => _libraryRightPress = e.GetCurrentPoint(this).Properties.IsRightButtonPressed;

    private async void OnLibraryOpenContextClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _libraryRightPress = false;
        if (sender is Control { DataContext: SideRow { Path: { } path } row })
        { if (row.IsWorkspace) await OpenRecentWorkspaceAsync(path); else if (row.IsOnline) await OpenStoredUrlAsync(path); else await OpenDocumentPathAsync(path); }
        else if (sender is Control { DataContext: TreeRow { IsDirectory: false } tree }) await OpenDocumentPathAsync(tree.FullPath);
    }

    private async void OnLibraryDownloadClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Control { DataContext: SideRow { IsOnline: true, Path: { } url } }) return;
        _libraryRightPress = false;
        var key = "download:" + url;
        if (_onlineRequests.ContainsKey(key)) return;
        using var operation = new CancellationTokenSource();
        _onlineRequests.Add(key, operation);
        using var loader = OnlineLoaderFactory();
        using var statusOperation = _shell.BeginStatusOperation();
        try
        {
            _shell.StatusMessage = "Downloading online file…";
            var document = await MarkdownUrlCache.ReadAsync(MarkdownUrlCache.Root(_shell.Paths.StateDirectory), url, operation.Token)
                ?? await loader.LoadBundleAsync(url, operation.Token);
            if (!_windowClosed && !operation.IsCancellationRequested) await DownloadUrlDocumentAsync(document);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OperationCanceledException)
        { if (!_windowClosed) _shell.StatusMessage = ex.Message; }
        finally { _onlineRequests.Remove(key); }
    }

    private async void OnLibraryRenameClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _libraryRightPress = false;
        var path = sender is Control control ? control.DataContext switch
        {
            SideRow { CanRename: true, Path: { } local } => local,
            TreeRow { IsDirectory: false } tree => tree.FullPath,
            _ => null
        } : null;
        if (path is null) return;
        if (_shell.FindOpenSource(path) is { } open) { await RenameDocumentFromContextAsync(open); return; }
        var name = await new FileNameWindow(false, "Rename the file in " + Path.GetDirectoryName(path), true, Path.GetFileName(path),
            validateName: candidate => LocalRenameError(path, candidate)).ShowDialog<string?>(this);
        if (name is null) return;
        var tab = await _shell.OpenFileAsync(path, activate: false, recordRecent: false);
        if (tab is null) return;
        var renamed = await _shell.RenameDocumentAsync(tab, name);
        if (!tab.IsDirty) await _shell.CloseTabAsync(tab);
        if (!renamed) await PromptWindow.AskAsync(this, "Cannot rename", _shell.StatusMessage ?? "The file could not be renamed. Your file was kept.", ["OK"], 0, 0);
        await ReviewRenameLinksAsync();
        RefreshWorkspace();
    }

    private async void OnLibraryDeleteClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _libraryRightPress = false;
        await DeleteLibraryItemAsync((sender as Control)?.DataContext);
    }

    private async void OnLibraryDeleteKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || e.KeyModifiers != KeyModifiers.None || e.Source is TextBox) return;
        var selected = sender switch { ListBox list => list.SelectedItem, TreeView tree => tree.SelectedItem, _ => null };
        if (selected is null && e.Source is Control source)
            selected = (source as ListBoxItem ?? source.FindAncestorOfType<ListBoxItem>())?.DataContext;
        if (selected is not (SideRow or TreeRow)) return;
        e.Handled = true;
        await DeleteLibraryItemAsync(selected);
    }

    private bool _libraryDeleting;
    internal Func<Tern.App.Shell.ILocalFileDeletion>? LibraryDeletionFactory { get; set; }
    private async Task DeleteLibraryItemAsync(object? selected)
    {
        if (_libraryDeleting) return;
        var path = selected switch
        {
            SideRow { CanRename: true, Path: { } local } => local,
            TreeRow { IsDirectory: false } tree => tree.FullPath,
            _ => null
        };
        if (path is null) return;
        _libraryDeleting = true;
        try
        {
            var service = LibraryDeletionFactory?.Invoke() ?? new WindowsRecycleBinService(TryGetPlatformHandle()?.Handle ?? 0);
            await _shell.DeleteFileAsync(path, service);
            RefreshWorkspace();
        }
        finally { _libraryDeleting = false; }
    }
}
