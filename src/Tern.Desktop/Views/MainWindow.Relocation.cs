using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tern.Desktop.Services;
using Tern.Desktop.ViewModels;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private string? _treeDragPath;
    private Point _treeDragStart;
    private PointerPressedEventArgs? _treeDragTrigger;
    private bool _relocating;

    private static string? LibraryPathOf(object? context) => context switch
    {
        TreeRow { IsSection: false } row => row.FullPath,
        _ => null,
    };

    private async Task RunRelocationAsync(Func<Task<bool>> operation, string failureTitle)
    {
        if (_relocating || _shell.IsBusy) return;
        _relocating = true;
        try
        {
            var succeeded = await operation();
            if (succeeded) await ReviewRenameLinksAsync();
            else if (_shell.StatusMessage is { Length: > 0 } message && _shell.StatusSeverity != Tern.App.Shell.StatusSeverity.Info)
                await PromptWindow.AskAsync(this, failureTitle, message, ["OK"], 0, 0);
            RefreshWorkspace();
        }
        finally { _relocating = false; }
    }

    private async void OnLibraryMoveClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _libraryRightPress = false;
        if (LibraryPathOf((sender as Control)?.DataContext) is { } path) await MoveWithPickerAsync(path);
    }

    internal async Task MoveWithPickerAsync(string path)
    {
        var workspace = _shell.WorkspaceForPath(path);
        if (workspace is null) { _shell.SetStatus("Only items inside an open workspace can be moved.", Tern.App.Shell.StatusSeverity.Warning); return; }
        var destinations = _shell.MoveDestinations(path);
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        var destination = await new MoveToWindow(name, workspace.Root, destinations).ShowDialog<string?>(this);
        if (destination is null) return;
        await RunRelocationAsync(() => _shell.MovePathAsync(path, destination), "Cannot move");
    }

    private async void OnLibraryDuplicateClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _libraryRightPress = false;
        if (LibraryPathOf((sender as Control)?.DataContext) is { } path)
            await RunRelocationAsync(() => _shell.DuplicatePathAsync(path), "Cannot duplicate");
    }

    private async void OnLibraryRevealClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _libraryRightPress = false;
        if (LibraryPathOf((sender as Control)?.DataContext) is { } path) _launcher.RevealInFileManager(path);
    }

    private async void OnFolderRenameClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _libraryRightPress = false;
        if (LibraryPathOf((sender as Control)?.DataContext) is not { } path) return;
        var parent = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar)) ?? path;
        var name = await new FileNameWindow(true, "Rename the folder in " + parent, true, Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)), "Rename folder",
            candidate => string.Equals(candidate, Path.GetFileName(path), StringComparison.Ordinal) || !Directory.Exists(Path.Combine(parent, candidate)) && !File.Exists(Path.Combine(parent, candidate))
                ? null : "That name already exists in this folder. Choose another name.").ShowDialog<string?>(this);
        if (name is null) return;
        await RunRelocationAsync(() => _shell.RenameFolderAsync(path, name), "Cannot rename");
    }

    private async void OnFolderRecycleClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _libraryRightPress = false;
        await DeleteLibraryItemAsync((sender as Control)?.DataContext);
    }

    private void OnTreeRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control && e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) { _treeDragStart = e.GetPosition(this); _treeDragTrigger = e; }
        else _treeDragStart = default;
    }

    private async void OnTreeRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_treeDragPath is not null || _relocating || _treeDragStart == default || _treeDragTrigger is not { } trigger) return;
        if (sender is not Control { DataContext: TreeRow { IsSection: false } row } || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var delta = e.GetPosition(this) - _treeDragStart;
        if (Math.Abs(delta.X) < 8 && Math.Abs(delta.Y) < 8) return;
        _treeDragStart = default;
        _treeDragPath = row.FullPath;
        try
        {
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateText(row.FullPath));
            await DragDrop.DoDragDropAsync(trigger, transfer, DragDropEffects.Move);
        }
        finally { _treeDragPath = null; }
    }

    private void OnTreeRowDragOver(object? sender, DragEventArgs e)
    {
        if (_treeDragPath is not { } source) return;
        e.Handled = true;
        e.DragEffects = sender is Control { DataContext: TreeRow { IsDirectory: true } target } && CanDropOnto(source, target.FullPath)
            ? DragDropEffects.Move : DragDropEffects.None;
    }

    private async void OnTreeRowDrop(object? sender, DragEventArgs e)
    {
        if (_treeDragPath is not { } source) return;
        e.Handled = true;
        if (sender is not Control { DataContext: TreeRow { IsDirectory: true } target } || !CanDropOnto(source, target.FullPath)) return;
        await RunRelocationAsync(() => _shell.MovePathAsync(source, target.FullPath), "Cannot move");
    }

    private bool CanDropOnto(string source, string folder)
    {
        var parent = Path.GetDirectoryName(source.TrimEnd(Path.DirectorySeparatorChar));
        if (parent is not null && string.Equals(Path.GetFullPath(parent), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase)) return false;
        var prefix = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return !Path.GetFullPath(folder).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !string.Equals(Path.GetFullPath(source), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase);
    }
}
