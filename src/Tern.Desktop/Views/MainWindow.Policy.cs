using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Input;
using Avalonia.Threading;
using Tern.App.Commands;
using Tern.Desktop.Services;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private CommandControlIndex? _commandControls;
    private void OnFeatureHelpClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control control && Help.GetFeature(control) is { } featureId)
            HelpWindow.ShowFor(this, () => CreateHelpWindow(featureId), featureId);
    }
    private Task AskGuideAIAsync(string question)
    {
        HelpWindow.ShowFor(this, () => CreateHelpWindow(question: question), question: question);
        return Task.CompletedTask;
    }
    internal Task ShowContextGuidanceAsync(string topic, Window owner)
    {
        var feature = string.IsNullOrWhiteSpace(topic) ? null : Tern.App.Help.FeatureCatalog.Search(_shell.Registry, topic).FirstOrDefault();
        HelpWindow.ShowFor(owner, () => CreateHelpWindow(feature?.Id), feature?.Id);
        return Task.CompletedTask;
    }

    internal HelpWindow CreateHelpWindow(string? feature = null, string? question = null) => new(_shell.Registry, feature,
        RunCommandAsync, AskGuideAIAsync, AnswerGuideQuestionAsync, question, _aiWorkspace,
        () => ShowAIConnectionsAsync(this), CanRunCommand, text => _shell.NewDocument(text), BeginWalkthroughAsync,
        () => _shown is not null && Editor.SelectionStart + Editor.SelectionLength <= Editor.Document.TextLength ? Editor.SelectedText : null);
    private void OnTabRename(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (sender is TextBlock { Parent: StackPanel panel, DataContext: Tern.App.Documents.DocumentTab tab } label)
        {
            if (!tab.CanRename) return;
            _shell.ActiveTab = tab;
            var input = panel.Children.OfType<TextBox>().Single();
            input.Tag = new InlineRenameSession(tab.DisplayName);
            input.PropertyChanged -= OnInlineRenameInputChanged;
            input.PropertyChanged += OnInlineRenameInputChanged;
            SetInlineRenameError(input, null);
            input.Text = tab.DisplayName; label.IsVisible = false; input.IsVisible = true;
            input.Focus(); input.SelectAll(); e.Handled = true;
        }
    }

    private sealed class InlineRenameSession(string originalName)
    {
        public string OriginalName { get; } = originalName;
        public bool Committing { get; set; }
    }

    private async void OnInlineRenameLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    { if (sender is TextBox input) await CommitInlineRenameAsync(input); }

    private static void OnInlineRenameInputChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBox.TextProperty && sender is TextBox { Tag: InlineRenameSession { Committing: false } } input)
            SetInlineRenameError(input, null);
    }

    private static void SetInlineRenameError(TextBox input, string? message)
    {
        input.Classes.Set("rename-error", message is not null);
        ToolTip.SetTip(input, message);
        if (input.Parent is StackPanel panel && panel.Children.OfType<TextBlock>()
            .FirstOrDefault(text => text.Name == "TabRenameError") is { } error)
        {
            error.Text = message;
            error.IsVisible = message is not null;
        }
    }

    private static void EndInlineRename(TextBox input)
    {
        input.Tag = null;
        input.PropertyChanged -= OnInlineRenameInputChanged;
        SetInlineRenameError(input, null);
        input.IsVisible = false;
        if (input.Parent is StackPanel panel) panel.Children.OfType<TextBlock>().First().IsVisible = true;
    }

    private async void OnInlineRenameKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (sender is not TextBox input) return;
        if (e.Key == Avalonia.Input.Key.Escape)
        {
            e.Handled = true;
            if (input.Tag is InlineRenameSession { Committing: true })
                _shell.SetStatus("The rename is already in progress. Wait for it to finish.", Tern.App.Shell.StatusSeverity.Info);
            else EndInlineRename(input);
            return;
        }
        if (e.Key != Avalonia.Input.Key.Enter) return;
        e.Handled = true;
        await CommitInlineRenameAsync(input);
    }

    private async Task CommitInlineRenameAsync(TextBox input)
    {
        if (_windowClosed || input.Tag is not InlineRenameSession session || session.Committing
            || input.DataContext is not Tern.App.Documents.DocumentTab tab || !_shell.Tabs.Contains(tab)) return;
        var name = input.Text ?? string.Empty;
        if (name == session.OriginalName) { EndInlineRename(input); return; }
        session.Committing = true;
        input.IsReadOnly = true;
        var renamed = false;
        try
        {
            renamed = await _shell.RenameDocumentAsync(tab, name);
            if (_windowClosed) return;
            if (!renamed)
            {
                var message = _shell.StatusMessage ?? "The document could not be renamed. Choose a valid, unused name.";
                SetInlineRenameError(input, message);
                _shell.SetStatus(message, Tern.App.Shell.StatusSeverity.Error);
                return;
            }
            EndInlineRename(input);
            await ReviewRenameLinksAsync();
            if (!_windowClosed) RefreshWorkspace();
        }
        catch (Exception ex)
        {
            _shell.ReportUnexpectedError(renamed ? "review links after renaming" : "rename the document", ex);
            if (!renamed && !_windowClosed) SetInlineRenameError(input, _shell.StatusMessage);
        }
        finally
        {
            session.Committing = false;
            input.IsReadOnly = false;
        }
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
                ? "Reference removed for this session, but the file list could not be saved: " + _shell.PlainProblem(error) + " The file and open edits are kept."
                : "Reference removed from Library and Recent. The file and any open edits are kept.";
    }
    internal bool CanRunCommand(string id) => CanRunCommand(id, CurrentCommandContext());

    private bool CanRunCommand(string id, DocumentCommandContext context)
    {
        if (_shell.Settings.OfflineMode && id is CommandIds.OpenUrl or CommandIds.AskAI) return false;
        if (id == CommandIds.Rename) return _shown is { CanRename: true } && !_shell.IsBusy;
        if (Tern.App.Editing.TableEditingService.Handles(id))
            return DocumentCommandPolicy.CanExecute(id, context) && TableCommandDisabledReason(id) is null;
        return DocumentCommandPolicy.CanExecute(id, context);
    }

    private DocumentCommandContext CurrentCommandContext()
    {
        var undo = _shown is not null && _contexts.TryGetValue(_shown, out var context) ? context.Buffer.Document.UndoStack : null;
        return _shell.GetCommandContext(undo?.CanUndo == true, undo?.CanRedo == true);
    }

    private string? CommandDisabledReason(string id)
    {
        if (_shell.Settings.OfflineMode && id is CommandIds.OpenUrl or CommandIds.AskAI)
            return "Offline mode blocks this connection. Turn Offline off to connect.";
        if (id == CommandIds.Rename && _shown is { CanRename: false })
            return "Download a local copy before renaming this online note.";
        if (Tern.App.Editing.TableEditingService.Handles(id) && DocumentCommandPolicy.CanExecute(id, CurrentCommandContext()))
            return TableCommandDisabledReason(id);
        return DocumentCommandPolicy.DisabledReason(id, CurrentCommandContext());
    }

    private void RefreshCommandAvailability()
    {
        if (_windowClosed) return;
        _commandControls ??= new CommandControlIndex(this, control => control.Tag is string id && _shell.Registry.TryGet(id, out _));
        var context = CurrentCommandContext();
        foreach (var control in _commandControls.Snapshot())
            if (control.Tag is string id && _shell.Registry.TryGet(id, out var command))
            {
                control.IsEnabled = control.Classes.Contains("global-add") ? !_shell.IsBusy : CanRunCommand(id, context);
                if (_shell.Settings.OfflineMode && id is CommandIds.OpenUrl or CommandIds.AskAI)
                {
                    ToolTip.SetTip(control, "Offline mode blocks this connection. Turn Offline off to connect.");
                    ToolTip.SetShowOnDisabled(control, true);
                }
                else if (DocumentCommandPolicy.IsOutputCommand(id))
                {
                    ToolTip.SetTip(control, DocumentCommandPolicy.DisabledReason(id, context) ?? command.Title);
                    ToolTip.SetShowOnDisabled(control, true);
                }
            }
        SourceButton.IsEnabled = SplitButton.IsEnabled = ReadingButton.IsEnabled = _shown is not null && !_shell.IsBusy;
        StatusLineEnding.IsEnabled = CanRunCommand(CommandIds.ChangeLineEnding, context);
        StatusEncoding.IsEnabled = CanRunCommand(CommandIds.ChangeEncoding, context) || CanRunCommand(CommandIds.ReopenWithEncoding, context);
    }

    private void RefreshCommandHints()
    {
        foreach (var command in _shell.Registry.Commands) Resources["Tern.Command." + command.Id] = command.Title;
        RefreshMenuCommands(ApplicationMenu);
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
            if (control.ContextMenu is { } contextMenu) RefreshMenuCommands(contextMenu);
            if (control is Button { Flyout: MenuFlyout flyout })
                foreach (var item in flyout.Items.OfType<MenuItem>())
                {
                    RefreshMenuCommand(item);
                    RefreshMenuCommands(item);
                }
            if (control.Tag is not string id || !_shell.Registry.TryGet(id, out var command)) continue;
            var gesture = _shell.Registry.GetGesture(id);
            if (control is MenuItem menu) RefreshMenuCommand(menu);
            if (control is Button button) ToolTip.SetTip(button, command.Title + (gesture is null ? "" : " (" + Gesture.Describe(gesture) + ")"));
        }

    }

    private void RefreshMenuCommands(ItemsControl root)
    {
        foreach (var item in root.Items.OfType<MenuItem>())
        {
            RefreshMenuCommand(item);
            RefreshMenuCommands(item);
        }
    }

    private void RefreshMenuCommand(MenuItem menu)
    {
        if (menu.Tag is not string id || !_shell.Registry.TryGet(id, out var command)) return;
        var title = command.Title;
        if (menu.Header is string header && header.IndexOf('_') is var marker && marker >= 0 && marker + 1 < header.Length)
        {
            var index = title.IndexOf(header[marker + 1].ToString(), StringComparison.OrdinalIgnoreCase);
            if (index >= 0) title = title.Insert(index, "_");
        }
        menu.Header = title;
        menu.InputGesture = DesktopGesture(_shell.Registry.GetGesture(id));
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
        using var activity = _shell.Activities.Begin("Reviewing proposed link repairs");
        try
        {
            activity.WaitForUser("Compare proposed link edits; the completed move or rename is not undone by Cancel");
            if (!await new RenameLinksWindow(plan).ShowDialog<bool>(this))
            { activity.Cancel("No proposed link edits applied. The completed move or rename was not undone."); return; }
            activity.Complete(outcome: "Link-repair review approved; applying the selected plan");
            var result = await _shell.ApplyRenameRepairsAsync(plan);
            if (!result.Succeeded)
                await PromptWindow.AskAsync(this, "Some links were kept unchanged", string.Join("\n", result.Files
                    .Where(f => f.Status is Tern.Workspaces.RepairStatus.Conflict or Tern.Workspaces.RepairStatus.Failed)
                    .Select(f => Path.GetFileName(f.Repair.Path) + ": " + f.Message)), ["Continue"], 0, 0);
        }
        catch (OperationCanceledException) { activity.Cancel(); throw; }
        catch (Exception ex) { activity.Fail(ex.Message); throw; }
    }
}
