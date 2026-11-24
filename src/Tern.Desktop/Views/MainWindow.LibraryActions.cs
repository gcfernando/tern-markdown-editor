using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
        if (!RequireNetwork()) return;
        var key = "download:" + url;
        if (_onlineRequests.ContainsKey(key)) return;
        using var operation = new CancellationTokenSource();
        _onlineRequests.Add(key, operation);
        using var loader = CreateOnlineLoader();
        using var statusOperation = _shell.BeginStatusOperation("Fetching online download", cancel: operation.Cancel);
        try
        {
            _shell.StatusMessage = "Downloading online file…";
            statusOperation.Report("Checking the local URL cache");
            var document = await MarkdownUrlCache.ReadAsync(MarkdownUrlCache.Root(_shell.Paths.StateDirectory), url, operation.Token);
            if (document is null)
            {
                statusOperation.Report("Fetching the permitted Markdown and available assets");
                document = await loader.LoadBundleAsync(url, operation.Token);
            }
            if (!_windowClosed && !operation.IsCancellationRequested)
            {
                statusOperation.Complete(outcome: "Downloaded source ready; choose local save destination");
                await DownloadUrlDocumentAsync(document);
            }
            else statusOperation.Cancel();
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        { statusOperation.Cancel(); if (!_windowClosed) _shell.StatusMessage = "Online download canceled."; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OperationCanceledException or IOException or UnauthorizedAccessException)
        { statusOperation.Fail(ex.Message); if (!_windowClosed) _shell.SetStatus("Online download could not finish: " + Tern.App.Shell.UserProblem.Plain(ex), Tern.App.Shell.StatusSeverity.Error); }
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
        Tern.App.Documents.DocumentTab? open;
        try { open = await _shell.FindOpenTabAsync(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            if (!_windowClosed) _shell.SetStatus("Tern could not check the open document. Save your work, then try again: " + _shell.PlainProblem(ex), Tern.App.Shell.StatusSeverity.Error);
            return;
        }
        if (_windowClosed) return;
        if (open is not null) { await RenameDocumentFromContextAsync(open); return; }
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
        if (e.Key != Key.Delete || e.KeyModifiers != KeyModifiers.None || e.Source is not Control source) return;
        // Selection may have been cleared during activation or refer to a different
        // row. Only the owning row of this Library-local key event is its target.
        var ancestors = source.GetSelfAndVisualAncestors().TakeWhile(visual => !ReferenceEquals(visual, sender)).ToArray();
        if (ancestors.Any(visual => visual is TextBox or Button or ComboBox or RangeBase or SelectableTextBlock)) return;
        var container = ancestors.OfType<Control>().FirstOrDefault(control => control is ListBoxItem or TreeViewItem);
        if (container is null || !container.GetVisualAncestors().Any(visual => ReferenceEquals(visual, sender))) return;
        var selected = container.DataContext;
        if (!IsRecyclableLibraryItem(selected)) return;
        e.Handled = true;
        await DeleteLibraryItemAsync(selected);
    }

    private static bool IsRecyclableLibraryItem(object? item) => item switch
    {
        SideRow { CanRename: true, Path: not null } => true,
        TreeRow { IsOnlineOnly: false } tree => tree.IsRecyclable,
        _ => false
    };

    private bool _libraryDeleting;
    internal Func<Tern.App.Shell.ILocalFileDeletion>? LibraryDeletionFactory { get; set; }
    private async Task DeleteLibraryItemAsync(object? selected)
    {
        if (_libraryDeleting || !IsRecyclableLibraryItem(selected)) return;
        if (selected is TreeRow { IsFolderEntry: true } folder)
        {
            _libraryDeleting = true;
            try
            {
                var folderService = LibraryDeletionFactory?.Invoke() ?? new WindowsRecycleBinService(TryGetPlatformHandle()?.Handle ?? 0);
                await _shell.DeleteFolderAsync(folder.FullPath, folderService);
                RefreshWorkspace();
            }
            finally { _libraryDeleting = false; }
            return;
        }
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
