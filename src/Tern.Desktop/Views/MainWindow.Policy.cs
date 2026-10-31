using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Input;
using Avalonia.Threading;
using Tern.App.Commands;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private void OnFeatureHelpClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { Tag: string featureId }) new HelpWindow(_shell.Registry, featureId, RunCommandAsync, AskGuideAIAsync, AnswerGuideQuestionAsync).Show(this);
    }
    private Task AskGuideAIAsync(string question) => new HelpWindow(_shell.Registry, openFeature: RunCommandAsync,
        answerAI: AnswerGuideQuestionAsync, initialQuestion: question).ShowDialog(this);
    private void OnTabRename(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (sender is TextBlock { Parent: StackPanel panel, DataContext: Tern.App.Documents.DocumentTab tab } label)
        {
            if (!tab.CanRename) return;
            _shell.ActiveTab = tab;
            var input = panel.Children.OfType<TextBox>().Single();
            input.Text = tab.DisplayName; label.IsVisible = false; input.IsVisible = true;
            input.Focus(); input.SelectAll(); e.Handled = true;
        }
    }

    private static void OnInlineRenameLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    { if (sender is TextBox input) EndInlineRename(input); }

    private static void EndInlineRename(TextBox input)
    {
        input.IsVisible = false;
        if (input.Parent is StackPanel panel) panel.Children.OfType<TextBlock>().First().IsVisible = true;
    }

    private async void OnInlineRenameKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: Tern.App.Documents.DocumentTab tab } input) return;
        if (e.Key == Avalonia.Input.Key.Escape) { EndInlineRename(input); e.Handled = true; }
        if (e.Key != Avalonia.Input.Key.Enter) return;
        e.Handled = true;
        if (await _shell.RenameDocumentAsync(tab, input.Text ?? string.Empty)) { EndInlineRename(input); await ReviewRenameLinksAsync(); RefreshWorkspace(); }
        else Avalonia.Controls.ToolTip.SetTip(input, _shell.StatusMessage);
    }

    private async void OnRemoveRecentClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        if (_nav == "Recent" && sender is Control { DataContext: Tern.Desktop.ViewModels.SideRow { IsWorkspace: true, Path: { } workspacePath } })
        {
            if (_shell.RemoveRecent(Tern.Workspaces.RecentKind.Workspace, workspacePath))
                _shell.StatusMessage = _shell.Recent.LastSaveError is { } saveError
                    ? "Workspace reference removed for this session, but Recent could not be saved: " + saveError.Message
                    : "Workspace removed from Recent. Its files and any open workspace were kept.";
            return;
        }
        if (_nav == "Files" && sender is Control { DataContext: Tern.Desktop.ViewModels.SideRow { CanRemoveReference: true, Path: { } libraryPath } libraryRow })
        {
            await _shell.CloseLibraryReferenceAsync(libraryPath, libraryRow.IsOnline);
            return;
        }
        if ((_nav == "Recent" || (_nav == "Files" && _shell.Workspace is null))
            && sender is Control { DataContext: Tern.Desktop.ViewModels.SideRow { CanRemoveReference: true, Path: { } path } row }
            && _shell.RemoveRecent(row.IsWorkspace ? Tern.Workspaces.RecentKind.Workspace : row.IsOnline ? Tern.Workspaces.RecentKind.Url : Tern.Workspaces.RecentKind.File, path))
            _shell.StatusMessage = _shell.Recent.LastSaveError is { } error
                ? "Reference removed for this session, but the file list could not be saved: " + error.Message + " The file and open edits are kept."
                : "Reference removed from Library and Recent. The file and any open edits are kept.";
    }
    internal bool CanRunCommand(string id)
    {
        if (id == CommandIds.Rename) return _shown is { CanRename: true } && !_shell.IsBusy;
        var undo = _shown is not null && _contexts.TryGetValue(_shown, out var context) ? context.Buffer.Document.UndoStack : null;
        return DocumentCommandPolicy.CanExecute(id, _shell.GetCommandContext(undo?.CanUndo == true, undo?.CanRedo == true));
    }

    private void RefreshCommandAvailability()
    {
        foreach (var control in this.GetLogicalDescendants().OfType<Control>())
            if (control.Tag is string id && _shell.Registry.TryGet(id, out var command))
            {
                control.IsEnabled = control.Classes.Contains("global-add") ? !_shell.IsBusy : CanRunCommand(id);
                if (DocumentCommandPolicy.IsOutputCommand(id))
                {
                    ToolTip.SetTip(control, DocumentCommandPolicy.DisabledReason(id, _shell.GetCommandContext()) ?? command.Title);
                    ToolTip.SetShowOnDisabled(control, true);
                }
            }
        SourceButton.IsEnabled = SplitButton.IsEnabled = ReadingButton.IsEnabled = _shown is not null && !_shell.IsBusy;
        StatusLineEnding.IsEnabled = CanRunCommand(CommandIds.ChangeLineEnding);
        StatusEncoding.IsEnabled = CanRunCommand(CommandIds.ChangeEncoding) || CanRunCommand(CommandIds.ReopenWithEncoding);
    }

    private void RefreshCommandHints()
    {
        Resources["Tern.NewDocumentHint"] = CommandHint(CommandIds.NewFile);
        Resources["Tern.CloseDocumentHint"] = CommandHint(CommandIds.CloseTab);
        Resources["Tern.RenameDocumentHint"] = "Double-click to rename; " + CommandHint(CommandIds.Rename);
        foreach (var (button, id) in new[] { (SourceButton, CommandIds.ViewSource), (SplitButton, CommandIds.ViewSplit),
            (ReadingButton, CommandIds.ViewReading), (FocusButton, CommandIds.ToggleFocus) })
        {
            var shortcut = Gesture.Describe(_shell.Registry.GetGesture(id));
            var hint = _shell.Registry.TryGet(id, out var definition) ? definition.Title : id;
            ToolTip.SetTip(button, hint + (shortcut.Length == 0 ? "" : " (" + shortcut + ")")
                + (id == CommandIds.ToggleFocus ? "; Escape to return" : ""));
        }
        foreach (var control in this.GetLogicalDescendants().OfType<Control>())
        {
            if (control.Tag is not string id || !_shell.Registry.TryGet(id, out var command)) continue;
            var gesture = _shell.Registry.GetGesture(id);
            if (control is MenuItem menu) menu.InputGesture = DesktopGesture(gesture);
            if (control is Button button) ToolTip.SetTip(button, command.Title + (gesture is null ? "" : " (" + Gesture.Describe(gesture) + ")"));
        }
    }

    private string CommandHint(string id)
    {
        var shortcut = Gesture.Describe(_shell.Registry.GetGesture(id));
        var title = _shell.Registry.TryGet(id, out var command) ? command.Title : id;
        return title + (shortcut.Length == 0 ? "" : " (" + shortcut + ")");
    }

    internal static KeyGesture? DesktopGesture(string? text)
    {
        if (!Gesture.TryParse(text, out var gesture)) return null;
        var modifiers = KeyModifiers.None;
        if (gesture.Ctrl) modifiers |= KeyModifiers.Control;
        if (gesture.Alt) modifiers |= KeyModifiers.Alt;
        if (gesture.Shift) modifiers |= KeyModifiers.Shift;
        if (gesture.Meta) modifiers |= KeyModifiers.Meta;
        return new KeyGesture(Enum.Parse<Key>(gesture.Key), modifiers);
    }

    private void OnRecentChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (!_windowClosed) { ShowNav(_nav); if (IsOverlayOpen) _picker?.RefreshResults(); }
    });

    private async Task RenameActiveDocumentAsync()
    {
        if (_shown is not { } tab) return;
        await RenameDocumentFromContextAsync(tab);
    }

    internal async Task RenameDocumentFromContextAsync(Tern.App.Documents.DocumentTab tab)
    {
        if (!tab.CanRename || _shell.IsBusy || !_shell.Tabs.Contains(tab)) return;
        var name = await new FileNameWindow(false, tab.FilePath is null
            ? "Name this new document. The name is kept in memory until Save."
            : "Rename the file in " + Path.GetDirectoryName(tab.FilePath), true, tab.DisplayName,
            validateName: candidate => LocalRenameError(tab.FilePath, candidate)).ShowDialog<string?>(this);
        if (name is null) return;
        if (await _shell.RenameDocumentAsync(tab, name)) { await ReviewRenameLinksAsync(); RefreshWorkspace(); }
        else await PromptWindow.AskAsync(this, "Cannot rename", _shell.StatusMessage ?? "The file could not be renamed. Your file and edits were kept.", ["OK"], 0, 0);
    }

    internal static string? LocalRenameError(string? path, string name)
    {
        if (path is null) return null;
        var destination = Path.Combine(Path.GetDirectoryName(path)!, name);
        var same = string.Equals(Path.GetFullPath(path), Path.GetFullPath(destination), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        return Directory.Exists(destination) || (!same && File.Exists(destination)) ? "That name already exists in this folder. Choose another name." : null;
    }

    private async Task ReviewRenameLinksAsync()
    {
        if (_shell.LastRenameRepairPlan is not { TotalEdits: > 0 } plan) return;
        if (!await new RenameLinksWindow(plan).ShowDialog<bool>(this)) return;
        var result = await _shell.ApplyRenameRepairsAsync(plan);
        if (!result.Succeeded)
            await PromptWindow.AskAsync(this, "Some links were kept unchanged", string.Join("\n", result.Files
                .Where(f => f.Status is Tern.Workspaces.RepairStatus.Conflict or Tern.Workspaces.RepairStatus.Failed)
                .Select(f => Path.GetFileName(f.Repair.Path) + ": " + f.Message)), ["Continue"], 0, 0);
    }
}
