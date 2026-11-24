using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.App.Shell;
using Tern.App.Editing;
using Tern.App.Palette;
using Tern.App.Settings;
using Tern.Desktop.Services;
using Tern.Markdown;
using Tern.Search;
using Tern.Workspaces;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private QuickPicker? _picker;
    private Control? _overlayReturnFocus;

    internal bool IsOverlayOpen => OverlayLayer.IsVisible;

    internal QuickPicker? Picker => _picker;

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && IsOverlayOpen && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
        {
            if (OverlayLayer.Children.FirstOrDefault() is AIEditReview review)
            {
                review.MoveTabFocus(e.KeyModifiers == KeyModifiers.Shift);
                e.Handled = true;
            }
            return;
        }

        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && IsOverlayOpen)
        {
            if (OverlayLayer.Children.FirstOrDefault() is not IDismissibleOverlay { DismissWithEscape: false })
            {
                HideOverlay();
                e.Handled = true;
            }
            return;
        }

        if (IsOverlayOpen && (e.Key == Key.F10 || e.KeyModifiers == KeyModifiers.Alt || e.Key is Key.LeftAlt or Key.RightAlt && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Alt)
            && !OverlayLayer.Children.OfType<AIPromptPanel>().Any())
        {
            e.Handled = true;
            return;
        }

        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return;
        }

        var modifiers = e.KeyModifiers;
        var gesture = new Gesture(
            modifiers.HasFlag(KeyModifiers.Control),
            modifiers.HasFlag(KeyModifiers.Alt),
            modifiers.HasFlag(KeyModifiers.Shift),
            modifiers.HasFlag(KeyModifiers.Meta),
            e.Key.ToString());
        var parsed = Gesture.TryParse(gesture.ToString(), out var normalized);
        if (parsed && AltGrGuard.ShouldIgnore(normalized))
        {
            // AltGr character input: leave the key to the editor / text box instead of running a command.
            return;
        }
        var commandId = parsed ? _shell.Registry.FindCommandFor(normalized) : null;
        if (commandId is CommandIds.Cut or CommandIds.Copy or CommandIds.Paste or CommandIds.SelectAll
            && e.Source is Control clipboardSource && (clipboardSource is SelectableTextBlock
                || clipboardSource.GetVisualAncestors().OfType<SelectableTextBlock>().Any()))
            return;
        var textBoxInput = e.Source is Control source && (source is TextBox || source.GetVisualAncestors().OfType<TextBox>().Any());
        if ((textBoxInput || ReferenceEditor.IsKeyboardFocusWithin) && commandId is { } editingCommand
            && (editingCommand is CommandIds.Undo or CommandIds.Redo or CommandIds.Cut or CommandIds.Copy or CommandIds.Paste or CommandIds.PastePlain or CommandIds.PasteTable or CommandIds.SelectAll or CommandIds.ChangeEncoding or CommandIds.ChangeLineEnding
                or CommandIds.InsertImage or CommandIds.PasteImage or CommandIds.ToggleTaskAtCaret
                || editingCommand.StartsWith("format.", StringComparison.Ordinal) || editingCommand.StartsWith("insert.", StringComparison.Ordinal)))
            return;
        if (IsOverlayOpen)
        {
            // SB-03: registry shortcuts are suspended while an overlay is open; only the prompt panel's own toggle stays live.
            if (commandId == CommandIds.AskAI && OverlayLayer.Children.OfType<AIPromptPanel>().Any()) { HideOverlay(); e.Handled = true; }
            return;
        }
        // Avalonia can name Enter as Return. Use the same alias normalization
        // as saved bindings and the shortcut recorder before looking it up.
        if (commandId is { } id)
        {
            e.Handled = true;
            _ = RunCommandAsync(id);
            return;
        }

        if (FindBar.IsVisible && !IsOverlayOpen && e.Key == Key.F3
            && modifiers is KeyModifiers.None or KeyModifiers.Shift)
        {
            FindNext(modifiers == KeyModifiers.None);
            e.Handled = true;
            return;
        }

        if (!IsOverlayOpen && e.Key == Key.F6 && modifiers is KeyModifiers.None or KeyModifiers.Shift)
        {
            e.Handled = FocusNextRegion(modifiers == KeyModifiers.Shift);
            return;
        }

        if (e.Key == Key.Escape && modifiers == KeyModifiers.None && _focusMode && !IsOverlayOpen && !FindBar.IsVisible)
        {
            ToggleFocusMode();
            e.Handled = true;
            return;
        }
    }

    private bool FocusNextRegion(bool backward)
    {
        var tabEntry = TabStrip.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(item => item.IsSelected)
            ?? (Control?)this.FindControl<Button>("NewTabButton");
        var inspectorEntry = InspectorContent.Items.OfType<TabItem>().FirstOrDefault(item => item.IsSelected);
        (Control Region, Control? Entry)[] regions =
        [
            (TitleBar, SearchBox),
            (Sidebar, WorkspaceAddButton),
            ((Control)TabStrip.Parent!, tabEntry),
            (FindBar, FindBox),
            (EditorHost, Editor.TextArea),
            (PreviewHost, Preview),
            (ReferenceEditor, ReferenceEditor.TextArea),
            (RightPanel, inspectorEntry)
        ];
        var available = regions.Where(region => region.Entry is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true }).ToArray();
        if (available.Length == 0) return false;
        var current = Array.FindIndex(available, region => region.Region.IsKeyboardFocusWithin);
        var direction = backward ? -1 : 1;
        if (current < 0) current = backward ? 0 : -1;
        for (var step = 1; step <= available.Length; step++)
        {
            var index = (current + step * direction + available.Length) % available.Length;
            if (available[index].Entry!.Focus(NavigationMethod.Tab)) return true;
        }
        return false;
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (_shown is null || !_contexts.TryGetValue(_shown, out var context))
        {
            return;
        }

        if (e.Key == Key.Insert && e.KeyModifiers == KeyModifiers.Shift)
        {
            e.Handled = true;
            _ = PasteEditorAsync();
            return;
        }
        if (e.Key == Key.Tab && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift
            && !_shown.IsReadOnly && !_shell.IsBusy && !IsOverlayOpen)
        {
            var line = Editor.TextArea.Caret.Line;
            if (_shown.Analysis?.Tables.Any(table => line >= table.Line && line <= table.Line + table.Rows) == true)
            {
                e.Handled = true;
                _ = NavigateTableAsync(e.KeyModifiers == KeyModifiers.Shift);
                return;
            }
            if (IsListSelection())
            {
                e.Handled = true;
                RunFormatting(e.KeyModifiers == KeyModifiers.Shift ? CommandIds.Outdent : CommandIds.Indent);
                return;
            }
        }
        if (e.KeyModifiers is KeyModifiers.Control or (KeyModifiers.Control | KeyModifiers.Shift)
            && e.Key is Key.Z or Key.Y)
        {
            // Registered history shortcuts were handled by the window. Suppress
            // AvaloniaEdit's built-in bindings when those keys are unassigned.
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && !_shown.IsReadOnly)
        {
            var text = Editor.Document.Text;
            var newline = _shown.Session.Format.NewLineText;
            var result = Editor.TextArea.Selection.IsEmpty
                ? ListContinuation.OnEnter(text, Editor.CaretOffset, newline, _shown.Analysis?.CodeBlocks) : null;
            if (result is not null)
            {
                ApplyEdit(result);
                e.Handled = true;
            }
            else if (!text.Contains('\r') && !text.Contains('\n'))
            {
                var start = Editor.SelectionStart;
                var indent = new string([.. text.Take(start).TakeWhile(c => c is ' ' or '\t')]);
                var insertion = newline + indent;
                ApplyEdit(new EditResult([new Tern.Domain.Text.TextEdit(start, Editor.SelectionLength, insertion)], start + insertion.Length, 0));
                e.Handled = true;
            }
        }
    }

    private void ApplyEdit(EditResult result)
    {
        if (_shown is null || !_contexts.TryGetValue(_shown, out var context))
        {
            return;
        }

        context.Buffer.Apply(result.Edits);
        Editor.Select(result.SelectionStart, result.SelectionLength);
        Editor.TextArea.Caret.BringCaretToView();
    }

    /// <summary>Runs a command by id. Exposed so the palette, menus, and tests share one path with the key bindings.</summary>
    internal async Task RunCommandAsync(string id)
    {
        try
        {
            if (CommandExecutorOverride is { } executor) await executor(id);
            else await RunCommandCoreAsync(id);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var title = _shell.Registry.TryGet(id, out var command) ? command.Title : id;
            _shell.ReportUnexpectedError(title, ex);
        }
    }

    /// <summary>Test seam: replaces command execution so error handling can be exercised with a throwing command.</summary>
    internal Func<string, Task>? CommandExecutorOverride { get; set; }

    private async Task RunCommandCoreAsync(string id)
    {
        if (_windowClosed) return;
        if (!CanRunCommand(id))
        {
            _shell.SetStatus(CommandDisabledReason(id) ?? "This action is unavailable in the current context.", Tern.App.Shell.StatusSeverity.Info);
            return;
        }
        if (TableEditingService.Handles(id)) { await EditTableAsync(id); return; }
        if (id is CommandIds.OpenUrl or CommandIds.AskAI && !RequireNetwork()) return;
        if (id is CommandIds.NewFile or CommandIds.OpenFile or CommandIds.OpenFolder or CommandIds.OpenUrl) _welcomeIsOpen = false;
        if (id is CommandIds.CloseWorkspace or CommandIds.CloseAllWorkspaces) RememberCurrentDocumentView();
        if (id == CommandIds.InsertTable)
        {
            await InsertTableAsync();
            return;
        }
        if (id == CommandIds.InsertLink) { await InsertLinkAsync(); return; }
        if (FormattingService.IsFormattingCommand(id))
        {
            RunFormatting(id);
            return;
        }

        switch (id)
        {
            case CommandIds.CreateWorkspace: await CreateWorkspaceFromPickerAsync(); break;
            case CommandIds.PromoteFolder: await PromoteFolderFromUiAsync(null); break;
            case CommandIds.CloseAllWorkspaces: await _shell.CloseAllWorkspacesAsync(); break;
            case CommandIds.LibraryFiles: await ShowNavAsync("Files"); break;
            case CommandIds.LibraryRecent: await ShowNavAsync("Recent"); break;
            case CommandIds.LibrarySearch: await ShowNavAsync("Search"); break;
            case CommandIds.LibraryBacklinks: await ShowNavAsync("Backlinks"); break;
            case CommandIds.LibraryBrokenLinks: await ShowNavAsync("Broken"); break;
            case CommandIds.LibraryTasks: await ShowNavAsync("Tasks"); break;
            case CommandIds.LibraryTags: await ShowNavAsync("Tags"); break;
            case CommandIds.LibraryMentions: await ShowNavAsync("Mentions"); break;
            case CommandIds.LibraryGraph: await ShowNavAsync("Graph"); break;
            case CommandIds.Help:
                HelpWindow.ShowFor(this, () => CreateHelpWindow());
                break;
            case CommandIds.AIConnections: await ShowSettingsAsync("AI connections"); break;
            case CommandIds.AskAI: ShowAIWritingPrompt(); break;
            case CommandIds.AskKnowledge: ShowKnowledgeAssistant(); break;
            case CommandIds.Reset: await new ResetWindow(_shell).ShowDialog(this); ApplySettings(); ApplyTheme(); RefreshWorkspace(); break;
            case CommandIds.ClearRecent:
                if (await PromptWindow.AskAsync(this, "Clear recent history?",
                    "Clear unpinned recent files, workspaces, URLs and the Reopen Closed Tab list? Pinned references are kept. "
                    + "Documents, open tabs and workspace contents stay unchanged.\n\n"
                    + "To remove pinned references too, use Reset Tern and explicitly reset Recent history (including pinned references).",
                    ["Clear recent history", "Cancel"], 1, 1, destructiveIndex: 0) == 0)
                    _shell.ClearRecentHistory();
                break;
            case CommandIds.Rename: await RenameActiveDocumentAsync(); break;
            case CommandIds.MoveDocument when _shell.ActiveTab?.FilePath is { } movePath: await MoveWithPickerAsync(movePath); break;
            case CommandIds.DuplicateDocument when _shell.ActiveTab?.FilePath is { } copyPath:
                await RunRelocationAsync(() => _shell.DuplicatePathAsync(copyPath), "Cannot duplicate"); break;
            case CommandIds.NewFile: _shell.NewDocument(); break;
            case CommandIds.NewFromTemplate: await ShowStarterTemplatesAsync(); break;
            case CommandIds.OpenTodaysNote: await _shell.OpenTodaysNoteAsync(); break;
            case CommandIds.OpenUrl:
                var urlWindow = new OpenUrlWindow(CreateOnlineLoader(), cacheRoot: MarkdownUrlCache.Root(_shell.Paths.StateDirectory), activities: _shell.Activities);
                if (await urlWindow.ShowDialog<MarkdownUrlDocument?>(this) is { } downloaded)
                {
                    if (urlWindow.DownloadRequested) await DownloadUrlDocumentAsync(downloaded);
                    else await OpenUrlDocumentAsync(downloaded, Tern.App.Shell.DuplicateOpenBehavior.Ask);
                }
                break;
            case CommandIds.NewWorkspaceFile: await CreateWorkspaceEntryAsync(false); break;
            case CommandIds.NewFolder: await CreateWorkspaceEntryAsync(true); break;
            case CommandIds.Print: await PrintDocumentAsync(); break;
            case CommandIds.InsertImage: await InsertImageFromPickerAsync(); break;
            case CommandIds.PasteImage: await PasteImageAsync(); break;
            case CommandIds.Undo: if (_shown is { IsReadOnly: false } undoTab) ContextFor(undoTab).Buffer.Undo(); break;
            case CommandIds.Redo: if (_shown is { IsReadOnly: false } redoTab) ContextFor(redoTab).Buffer.Redo(); break;
            case CommandIds.Cut: await CopyEditorAsync(cut: true); break;
            case CommandIds.Copy: await CopyEditorAsync(cut: false); break;
            case CommandIds.Paste: Editor.TextArea.Focus(); await PasteEditorAsync(); break;
            case CommandIds.PastePlain: Editor.TextArea.Focus(); await PasteEditorAsync(EditorPasteMode.Plain); break;
            case CommandIds.PasteTable: Editor.TextArea.Focus(); await PasteEditorAsync(EditorPasteMode.Table); break;
            case CommandIds.SelectAll: Editor.TextArea.Focus(); Editor.Select(0, Editor.Document.TextLength); break;
            case CommandIds.OpenFile:
                if (_dialogs is not null)
                {
                    foreach (var path in await _dialogs.PickFilesToOpenAsync())
                    {
                        await OpenDocumentPathAsync(path, askDuplicate: true);
                    }
                }

                break;
            case CommandIds.OpenFolder: await OpenFolderAsync(); break;
            case CommandIds.OpenWorkspace: await OpenExistingWorkspaceAsync(); break;
            case CommandIds.Save when _shell.ActiveTab is { } tab: await _shell.SaveTabAsync(tab); break;
            case CommandIds.SaveAs when _shell.ActiveTab is { } tab: await _shell.SaveTabAsAsync(tab); break;
            case CommandIds.SaveAll: await _shell.SaveAllAsync(); break;
            case CommandIds.CloseWorkspace: await _shell.CloseWorkspaceAsync(); break;
            case CommandIds.CloseDocuments: await _shell.CloseDocumentsAsync(); break;
            case CommandIds.CloseTab when _shell.ActiveTab is { } tab: await _shell.CloseTabAsync(tab); break;
            case CommandIds.NextTab: _shell.NextTab(); break;
            case CommandIds.PreviousTab: _shell.PreviousTab(); break;
            case CommandIds.ReopenClosedTab: await _shell.ReopenClosedTabAsync(); break;
            case CommandIds.QuickOpen: ShowQuickOpen(string.Empty); break;
            case CommandIds.GoToHeading: ShowQuickOpen("#"); break;
            case CommandIds.FoldSection:
            case CommandIds.MoveSectionUp:
            case CommandIds.MoveSectionDown:
            case CommandIds.PromoteSection:
            case CommandIds.DemoteSection: ApplyOutlineCommand(id); break;
            case CommandIds.CommandPalette: ShowPalette(); break;
            case CommandIds.GoToLine: ShowGoToLine(); break;
            case CommandIds.FindInWorkspace: ShowWorkspaceSearch(); break;
            case CommandIds.ReplaceInWorkspace: await ReplaceInWorkspaceAsync(); break;
            case CommandIds.WorkspacePrivacy: await ChangeWorkspacePrivacyAsync(); break;
            case CommandIds.ClearFileHistory: await ClearSavedHistoryAsync(false); break;
            case CommandIds.ClearWorkspaceHistory: await ClearSavedHistoryAsync(true); break;
            case CommandIds.RecentlyDiscarded: ShowDiscardedDrafts(); break;
            case CommandIds.GitDiff: await ShowGitDiffAsync(); break;
            case CommandIds.Find: ShowFindBar(false); break;
            case CommandIds.Replace: ShowFindBar(true); break;
            case CommandIds.ViewSource: SetMode(ViewMode.Source); break;
            case CommandIds.ViewSplit: SetMode(ViewMode.Split); break;
            case CommandIds.ViewReading: SetMode(ViewMode.Reading); break;
            case CommandIds.ReadCurrentLine: AnnounceDocumentInformation(currentLine: true); break;
            case CommandIds.ReadDocumentStatus: AnnounceDocumentInformation(currentLine: false); break;
            case CommandIds.ToggleSidebar: ToggleSidebar(); break;
            case CommandIds.ShowHistory: await ShowHistoryAsync(); break;
            case CommandIds.ToggleReference: ToggleReferencePane(); break;
            case CommandIds.ToggleFocus: ToggleFocusMode(); break;
            case CommandIds.ToggleRightPanel: SetInspectorVisibility(!RightPanel.IsVisible); break;
            case CommandIds.ResetLayout: ResetLayout(); break;
            case CommandIds.Welcome: ReplayWelcome(); break;
            case CommandIds.ToggleTheme: ToggleTheme(); break;
            case CommandIds.ZoomIn: Zoom(1); break;
            case CommandIds.ZoomOut: Zoom(-1); break;
            case CommandIds.ZoomReset: _shell.Settings.EditorFontSize = new AppSettings().EditorFontSize; ApplySettings(); break;
            case CommandIds.ExportHtml: await ShowExportAsync(ExportFormat.Html); break;
            case CommandIds.ExportAs: await ShowExportAsync(ExportFormat.Pdf); break;
            case CommandIds.ExportPdf: await ShowExportAsync(ExportFormat.Pdf); break;
            case CommandIds.Settings: await ShowSettingsAsync(); break;
            case CommandIds.Shortcuts: await ShowShortcutsAsync(); break;
            case CommandIds.About: await new AboutWindow().ShowDialog(this); break;
            case CommandIds.ReloadFromDisk when _shell.ActiveTab is { FilePath: not null } tab:
                await _shell.ReloadFromDiskAsync(tab);
                break;
            case CommandIds.RevealInExplorer when _shell.ActiveTab?.FilePath is { } path: _launcher.RevealInFileManager(path); break;
            case CommandIds.ReviewSaveFragments: await ShowSaveFragmentsAsync(); break;
            case CommandIds.ChangeEncoding: ShowEncodingPicker(false); break;
            case CommandIds.ReopenWithEncoding: ShowEncodingPicker(true); break;
            case CommandIds.ChangeLineEnding: OnLineEndingClick(this, new RoutedEventArgs()); break;
            case CommandIds.Exit: Close(); break;
        }
    }

    private void RunFormatting(string id)
    {
        if (_shown is null || _shown.IsReadOnly)
        {
            return;
        }

        var text = Editor.Document.Text;
        var result = FormattingService.Execute(id, text, Editor.SelectionStart, Editor.SelectionLength, _shown.Analysis, _shown.Session.Format.NewLineText,
            _shell.Settings.TabSize, _shell.Settings.InsertSpacesForTab);
        if (result is not null)
        {
            ApplyEdit(result);
            Editor.Focus();
            if (_shell.Registry.TryGet(id, out var command)) _shell.StatusMessage = command.Title + " applied.";
        }
    }

    private void SetMode(ViewMode mode)
    {
        var changed = _shell.ViewMode != mode;
        _shell.ViewMode = mode;
        ApplyViewMode();
        if (changed) _shell.StatusMessage = mode + " view";
    }

    private void ToggleSidebar()
    {
        if (_focusMode) ToggleFocusMode();
        if (_libraryAutoCollapsed) _libraryForcedOpen = !_libraryForcedOpen;
        else _shell.Settings.SidebarVisible = !_shell.Settings.SidebarVisible;
        ApplySettings();
    }

    private void ToggleTheme()
    {
        _shell.Settings.Theme = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? ThemeChoice.Paper : ThemeChoice.Midnight;
        ApplyTheme();
        RebuildPreview();
    }

    private void Zoom(int step)
    {
        _shell.Settings.EditorFontSize = Math.Clamp(_shell.Settings.EditorFontSize + step, 8, 40);
        ApplySettings();
    }

    // ---- Overlays -----------------------------------------------------------------------------------------------

    private void ShowPicker(string watermark, string initialText, Func<string, IReadOnlyList<PickerItem>> provider, Action<PickerItem> accept, string emptyText = "No matches", Func<string, string>? emptyTextProvider = null,
        Func<string, CancellationToken, Task<IReadOnlyList<PickerItem>>>? asyncProvider = null, Func<bool>? isCurrent = null, Func<string?>? errorProvider = null,
        Func<string?>? scopeStatusProvider = null)
    {
        _picker ??= new QuickPicker();
        ShowOverlay(_picker);
        _picker.Open(watermark, initialText, provider, accept, HideOverlay, emptyText, emptyTextProvider, asyncProvider, isCurrent, errorProvider, scopeStatusProvider);
    }

    private void ShowOverlay(Control panel)
    {
        if (!IsOverlayOpen) _overlayReturnFocus = FocusManager?.GetFocusedElement() as Control;
        OverlayLayer.Children.Clear();
        OverlayLayer.IsVisible = true;
        OverlayLayer.Children.Add(panel);
        Tern.Desktop.Services.MotionPolicy.Enter(OverlayLayer);
        Tern.Desktop.Services.MotionPolicy.Enter(panel);
    }

    internal void HideOverlay()
    {
        if (!IsOverlayOpen) return;
        _replayingWelcome = false;
        UpdateFirstNoteChecklist();
        var target = _overlayReturnFocus;
        _overlayReturnFocus = null;
        OverlayLayer.IsVisible = false;
        OverlayLayer.Children.Clear();
        if (target?.Focus() != true && _shown is not null)
        {
            FocusVisibleContent();
        }
    }

    private void OnOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, OverlayLayer))
        {
            HideOverlay();
        }
    }

    internal void ShowPalette() =>
        ShowPicker("Command palette: type a command…", string.Empty,
            query => [.. CommandPaletteModel.Filter(_shell.Registry, query).Select(c =>
            {
                var reason = CommandDisabledReason(c.CommandId);
                return new PickerItem(c.Title, reason ?? c.Category, c.Shortcut, c.CommandId,
                    IsEnabled: CanRunCommand(c.CommandId), DisabledReason: reason);
            })],
            item =>
            {
                if (!item.IsEnabled || item.Payload is string unavailable && !CanRunCommand(unavailable))
                {
                    if (item.Payload is string disabledId)
                        _shell.SetStatus(CommandDisabledReason(disabledId) ?? item.DisabledReason ?? "This command is unavailable.", Tern.App.Shell.StatusSeverity.Info);
                    return;
                }
                HideOverlay();
                if (item.Payload is string id)
                {
                    Dispatcher.UIThread.Post(async () => await RunCommandAsync(id));
                }
            });

    internal void ShowQuickOpen(string initial)
    {
        var workspace = _shell.Workspace;
        var files = (workspace is null ? _shell.Recent.Files.Select(f => f.Path) : workspace.Snapshot.Files.Select(f => f.RelativePath)).ToArray();
        var headings = (workspace is not null ? HeadingCandidate.FromGraph(workspace.Graph)
            : _shell.Tabs.Where(t => t.FilePath is not null).SelectMany(t => (t.Analysis?.Headings ?? []).Select(h => new HeadingCandidate(t.FilePath!, h.Text, h.Level, h.Line)))).ToArray();
        bool IsCurrentScope() => ReferenceEquals(workspace, _shell.Workspace) && (workspace is null || _shell.OpenWorkspaces.Contains(workspace));
        void Forget(string path) { RemoveQuickOpenReference(path); files = files.Where(f => !string.Equals(f, path, StringComparison.OrdinalIgnoreCase)).ToArray(); }
        ShowPicker("Go to file…  (# for headings)", initial, QuickOpenItems, item =>
        {
            if (!IsCurrentScope()) return;
            HideOverlay();
            switch (item.Payload)
            {
                case HeadingCandidate heading:
                    _ = OpenDocumentPathAsync(heading.Path, heading.Line);
                    break;
                case string path:
                    _ = OpenDocumentPathAsync(path);
                    break;
            }
        }, asyncProvider: files.Length <= 500 && headings.Length <= 500 ? null : (query, token) => Task.Run<IReadOnlyList<PickerItem>>(() =>
        {
            token.ThrowIfCancellationRequested();
            var result = Tern.Search.QuickOpen.Search(query, files, headings, 40);
            if (result.Mode == QuickOpenMode.Headings)
                return result.Headings.Select(h => new PickerItem(h.Item.Text, Path.GetFileName(h.Item.Path), "H" + h.Item.Level, h.Item)).ToArray();
            return result.Files.Select(f =>
            {
                var full = workspace is null ? f.Item : workspace.ToFullPath(f.Item);
                return new PickerItem(Path.GetFileName(f.Item), Path.GetDirectoryName(f.Item) ?? "", "", full,
                    workspace is null ? () => Forget(full) : null);
            }).ToArray();
        }, token), isCurrent: IsCurrentScope);
    }

    private IReadOnlyList<PickerItem> QuickOpenItems(string query)
    {
        var workspace = _shell.Workspace;
        IEnumerable<string> files = workspace is null
            ? _shell.Recent.Files.Select(f => f.Path)
            : workspace.Snapshot.Files.Select(f => f.RelativePath);
        IEnumerable<HeadingCandidate> headings = workspace is not null
            ? HeadingCandidate.FromGraph(workspace.Graph)
            : _shell.Tabs.Where(t => t.FilePath is not null).SelectMany(t => (t.Analysis?.Headings ?? []).Select(h => new HeadingCandidate(t.FilePath!, h.Text, h.Level, h.Line)));

        var results = Tern.Search.QuickOpen.Search(query, files, headings, 40);
        if (results.Mode == QuickOpenMode.Headings)
        {
            return [.. results.Headings.Select(h => new PickerItem(h.Item.Text, Path.GetFileName(h.Item.Path), "H" + h.Item.Level, h.Item))];
        }

        return
        [
            .. results.Files.Select(f =>
            {
                var full = workspace is null ? f.Item : workspace.ToFullPath(f.Item);
                return new PickerItem(Path.GetFileName(f.Item), Path.GetDirectoryName(f.Item) ?? string.Empty, string.Empty, full,
                    workspace is null ? () => RemoveQuickOpenReference(full) : null);
            }),
        ];
    }

    private void RemoveQuickOpenReference(string path)
    {
        if (!_shell.RemoveRecent(RecentKind.File, path)) return;
        _shell.StatusMessage = _shell.Recent.LastSaveError is { } error
            ? "Removed for this session; the file list could not be saved: " + _shell.PlainProblem(error)
            : "Reference removed. The file and any open edits are kept.";
    }

    private (string Url, BannerState? Banner)? _onlineOpenFailure;

    private async Task OpenStoredUrlAsync(string url)
    {
        if (!RequireNetwork()) return;
        string canonical;
        try { canonical = MarkdownUrlLoader.GetFetchAddress(url).AbsoluteUri; }
        catch (ArgumentException ex) { ShowOnlineOpenFailure(url, _shell.PlainProblem(ex)); return; }
        if (await _shell.FindOpenSourceAsync(canonical, online: true) is { } existing)
        { _shell.ActiveTab = existing; return; }
        if (_onlineRequests.ContainsKey(canonical)) return;
        using var operation = new CancellationTokenSource();
        _onlineRequests.Add(canonical, operation);
        using var loader = CreateOnlineLoader();
        using var statusOperation = _shell.BeginStatusOperation("Fetching online document", new Uri(canonical).IdnHost, operation.Cancel);
        try
        {
            _shell.StatusMessage = "Opening online file…";
            statusOperation.Report("Checking the local URL cache");
            var document = await MarkdownUrlCache.ReadAsync(MarkdownUrlCache.Root(_shell.Paths.StateDirectory), canonical, operation.Token, includeAssets: false);
            if (document is null)
            {
                statusOperation.Report("Fetching the permitted online document");
                document = await loader.LoadBundleAsync(canonical, operation.Token);
            }
            if (!_windowClosed && !operation.IsCancellationRequested)
            {
                statusOperation.Complete(outcome: "Online source ready; opening cached document");
                await OpenUrlDocumentAsync(document);
                if (await _shell.FindOpenSourceAsync(canonical, online: true) is not null && _onlineOpenFailure is { } failure && failure.Url == canonical)
                {
                    if (ReferenceEquals(_shell.Banner, failure.Banner)) _shell.DismissBanner();
                    _onlineOpenFailure = null;
                }
            }
            else statusOperation.Cancel();
        }
        catch (OperationCanceledException) { statusOperation.Cancel(); if (!_windowClosed) _shell.StatusMessage = "Online open canceled."; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { statusOperation.Fail(ex.Message); if (!_windowClosed) ShowOnlineOpenFailure(canonical, Tern.App.Shell.UserProblem.Plain(ex)); }
        finally { _onlineRequests.Remove(canonical); }
    }

    private void ShowOnlineOpenFailure(string url, string message)
    {
        _shell.SetStatus("Online file could not be opened: " + message, StatusSeverity.Error);
        _shell.ShowBanner("Online file could not be opened: " + message, StatusSeverity.Error,
            new BannerAction("Retry", async () => { if (!_windowClosed) await OpenStoredUrlAsync(url); }),
            new BannerAction("Open from URL", async () => { if (!_windowClosed) await RunCommandAsync(CommandIds.OpenUrl); }));
        _onlineOpenFailure = (url, _shell.Banner);
    }

    internal async Task OpenUrlDocumentAsync(MarkdownUrlDocument downloaded, Tern.App.Shell.DuplicateOpenBehavior behavior = Tern.App.Shell.DuplicateOpenBehavior.Activate)
    {
        using var statusOperation = _shell.BeginStatusOperation("Opening cached online document", downloaded.SuggestedFileName);
        try
        {
            statusOperation.Report("Preparing the downloaded Markdown and cache identity");
            var source = downloaded.SourceUrl.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                ? MarkdownUrlLoader.GetFetchAddress(downloaded.SourceUrl.AbsoluteUri).AbsoluteUri
                : new UriBuilder(downloaded.SourceUrl) { Fragment = string.Empty }.Uri.AbsoluteUri;
            var existing = await _shell.FindOpenSourceAsync(source, online: true);
            if (existing is not null && behavior == Tern.App.Shell.DuplicateOpenBehavior.Activate)
            { _shell.ActiveTab = existing; statusOperation.Complete(outcome: "Existing online document activated"); return; }
            if (existing is not null && behavior == Tern.App.Shell.DuplicateOpenBehavior.Ask)
                statusOperation.WaitForUser("Choose how to handle the already-open online document");
            var choice = existing is not null && behavior == Tern.App.Shell.DuplicateOpenBehavior.Ask
                ? await _shell.AskDuplicateOpenAsync(existing) : Tern.App.Shell.DuplicateOpenChoice.NewTab;
            if (choice == Tern.App.Shell.DuplicateOpenChoice.Cancel) { statusOperation.Cancel(); return; }
            statusOperation.Report("Writing the local immutable URL cache");
            try
            {
                if (downloaded.CachedPath is null)
                    downloaded = await MarkdownUrlCache.WriteAsync(MarkdownUrlCache.Root(_shell.Paths.StateDirectory), downloaded.RequestedUrl ?? source, downloaded);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                statusOperation.Fail(ex.Message);
                _shell.StatusMessage = "Online document was not opened: its local cache could not be written.";
                _dialogs?.ShowMessage("Cannot cache online document", _shell.PlainProblem(ex) + " Choose Download and Open to use another folder."); return;
            }
            Tern.App.Documents.DocumentTab? opened;
            if (choice == Tern.App.Shell.DuplicateOpenChoice.Replace && existing is not null)
            {
                statusOperation.WaitForUser("Review unsaved changes before replacing the online tab");
                opened = await _shell.ReplaceOnlineDocumentAsync(existing, source, downloaded.SuggestedFileName, downloaded.Text, downloaded.Format);
                if (opened is null) { statusOperation.Cancel(); return; }
            }
            else opened = _shell.OpenOnlineDocument(source, downloaded.SuggestedFileName, downloaded.Text, downloaded.Format);
            statusOperation.Report("Online document opened; attaching cached resources and preparing preview");
            opened.ResourceDocumentPath = downloaded.CachedPath;
            opened.IsCachedSource = downloaded.WasCached;
            opened.SourceWarnings = downloaded.Warnings;
            if (downloaded.CachedPath is { } cachePath)
            {
                if (_cacheLeases.Remove(opened, out var oldLease)) oldLease.Dispose();
                _cacheLeases[opened] = MarkdownUrlCache.AcquireLease(cachePath);
            }
            // The immutable cache is the resource backing store. Keep compressed asset
            // bytes only until publication, rather than for the lifetime of every tab.
            opened.ResourceFiles = new Dictionary<string, byte[]>();
            UpdateChrome();
            await RebuildPreviewAsync();
            try { _shell.Recent.AddUrl(downloaded.SourceUrl.AbsoluteUri); }
            catch (ArgumentException)
            {
                statusOperation.Complete("The online document is open, but its address is too long to store in Recent.", true, "Online document opened with Recent warning");
                _shell.StatusMessage = "Opened the online file, but its address is too long to keep in Recent. Use Save As to keep the document.";
                return;
            }
            _shell.StatusMessage = _shell.Recent.LastSaveError is { } error
                ? "Opened the online file, but Recent could not be saved: " + _shell.PlainProblem(error)
                : "Online file opened from a local cache. Use Download or Save As to keep a portable copy.";
            if (downloaded.Warnings.Count > 0)
            {
                _shell.StatusMessage = $"Opened with {downloaded.Warnings.Count} missing asset(s). Retry Open from URL to fetch them again.";
                _dialogs?.ShowMessage("Download partly completed", "The Markdown was opened, but these assets remain online. Retry Open from URL after correcting access or connection problems.\n\n" + string.Join("\n\n", downloaded.Warnings));
            }
            var warning = downloaded.Warnings.Count > 0 || _shell.Recent.LastSaveError is not null;
            statusOperation.Complete(downloaded.Warnings.Count > 0 ? $"{downloaded.Warnings.Count} assets remain online; retry after fixing access." : _shell.Recent.LastSaveError?.Message,
                warning, warning ? "Online document opened with warning" : "Online document opened from local cache");
        }
        catch (OperationCanceledException) { statusOperation.Cancel(); throw; }
        catch (Exception ex) { statusOperation.Fail(ex.Message); throw; }
    }

    internal async Task DownloadUrlDocumentAsync(MarkdownUrlDocument document)
    {
        var saved = await _shell.DownloadOnlineDocumentAsync(document.SourceUrl.AbsoluteUri, document.SuggestedFileName, document.Text, document.Format, document.Assets);
        if (saved is null)
        { _shell.StatusMessage = "Download canceled or not saved. Your documents were kept."; UpdateChrome(); await RebuildPreviewAsync(); return; }
        using var statusOperation = _shell.BeginStatusOperation("Preparing downloaded document preview", saved.DisplayName);
        try
        {
            statusOperation.Report("Preparing the saved local document preview");
            if (saved is not null && document.Warnings.Count > 0)
                _dialogs?.ShowMessage("Download partly completed", "The Markdown was saved; these assets could not be downloaded and retain their online addresses. Retry the download to a new folder.\n\n" + string.Join("\n\n", document.Warnings));
            UpdateChrome();
            await RebuildPreviewAsync();
            statusOperation.Complete(document.Warnings.Count > 0 ? $"{document.Warnings.Count} assets remain online; retry to a new folder." : null,
                document.Warnings.Count > 0, document.Warnings.Count > 0 ? "Downloaded document ready with missing assets" : "Downloaded document ready");
        }
        catch (OperationCanceledException) { statusOperation.Cancel(); throw; }
        catch (Exception ex) { statusOperation.Fail("The downloaded file was saved, but preview preparation failed: " + ex.Message); throw; }
    }

    private void ShowGoToLine() =>
        ShowPicker("Line number", string.Empty,
            query => int.TryParse(query, out var line) && line > 0
                ? [new PickerItem($"Go to line {Math.Min(line, Editor.Document.LineCount)}", string.Empty, string.Empty, Math.Min(line, Editor.Document.LineCount))]
                : [],
            item =>
            {
                HideOverlay();
                if (item.Payload is int line)
                {
                    GoToLine(line, 1);
                }
            },
            "Type a line number");

    private void ShowWorkspaceSearch()
    {
        if (!_shell.HasWorkspace)
        {
            _shell.StatusMessage = "Open a workspace to search its document content. Use the Library tree to browse folders and Quick Open for filenames.";
            return;
        }

        string? searchError = null;
        var searchWorkspace = _shell.Workspace!;
        bool CurrentWorkspace() => ReferenceEquals(searchWorkspace, _shell.Workspace) && _shell.OpenWorkspaces.Contains(searchWorkspace);
        IReadOnlyList<PickerItem> PresentSearch(SearchPage page)
        {
            var previousError = searchError;
            searchError = page.Error;
            if (searchError is not null) _shell.StatusMessage = searchError;
            else if (previousError is not null && _shell.StatusMessage == previousError) _shell.StatusMessage = "Search completed";
            return [.. page.Hits.Select(h => new PickerItem(h.Title, h.RelativePath + "  —  " + h.Snippet, h.Line > 0 ? "line " + h.Line : string.Empty, h,
                TitleRanges: h.TitleHighlights, DetailRanges: h.Highlights.Select(r => r with { Start = r.Start + h.RelativePath.Length + 5 }).ToArray()))];
        }
        IReadOnlyList<PickerItem> MatchesSmall(string query) => CurrentWorkspace() && !string.IsNullOrWhiteSpace(query)
            ? PresentSearch(searchWorkspace.Search(query, 40)) : [];
        async Task<IReadOnlyList<PickerItem>> Matches(string query, CancellationToken token)
        {
            if (!CurrentWorkspace() || string.IsNullOrWhiteSpace(query)) return [];
            using var operation = _shell.Activities.Begin("Searching workspace content", searchWorkspace.Root);
            try
            {
                operation.Report("Searching indexed text and unsaved buffers");
                var page = await Task.Run(() => searchWorkspace.Search(query, 40, cancellationToken: token), token);
                token.ThrowIfCancellationRequested();
                if (!CurrentWorkspace()) throw new OperationCanceledException(token);
                if (page.Error is { } error) operation.Fail(error);
                else operation.Complete(outcome: page.Hits.Count == 0 ? "Search complete; no matches" : "Search results ready");
                return PresentSearch(page);
            }
            catch (OperationCanceledException) { operation.Cancel("Search superseded or closed; documents were not changed."); throw; }
            catch (Exception ex) { operation.Fail(ex.Message); throw; }
        }
        ShowPicker("Search document content in " + _shell.Workspace!.Name + "…", _lastSearch, MatchesSmall,
            item =>
            {
                var query = _picker?.Input.Text ?? string.Empty;
                HideOverlay();
                ShowSearchResults(query);
                if (item.Payload is SearchHit hit)
                {
                    _ = OpenDocumentPathAsync(hit.Path, Math.Max(1, hit.Line));
                }
            },
            "Type to search", query => searchError ?? (!searchWorkspace.IsContentReady
                ? searchWorkspace.ContentStatus : string.IsNullOrWhiteSpace(query)
                ? "Type a word or phrase to search this workspace's documents."
                : "No matching documents. Search checks document text and paths; browse folder and workspace names in the Library tree."),
                asyncProvider: searchWorkspace.Snapshot.Files.Count > 500 || searchWorkspace.HasUnsavedSearchBuffers ? Matches : null,
                isCurrent: CurrentWorkspace, errorProvider: () => searchError,
                scopeStatusProvider: () => searchWorkspace.IsContentReady ? null : searchWorkspace.ContentStatus);
        _picker!.ShowSearchSyntax();
        _picker.AddAction("Save search", () => { if (CurrentWorkspace()) SaveWorkspaceSearch(_picker?.Input.Text ?? ""); });
        _picker.AddAction("Replace in workspace", () => { if (CurrentWorkspace()) { HideOverlay(); _ = RunCommandAsync(CommandIds.ReplaceInWorkspace); } });
        _picker.SetRecoveryAction("Rebuild this workspace's search index", async () =>
        {
            if (!CurrentWorkspace()) return;
            using var operation = _shell.BeginStatusOperation("Rebuilding search index", searchWorkspace.Root);
            try
            {
                var progress = new Progress<Tern.Search.IndexProgress>(p => operation.Report("Indexing workspace", p.Processed, p.Total));
                await Task.Run(() => searchWorkspace.RebuildSearchAsync(progress));
                if (!CurrentWorkspace() || _windowClosed) { operation.Cancel(); return; }
                searchError = null;
                _shell.StatusMessage = "This workspace's search index was rebuilt. Documents and other indexes were kept.";
                _picker.RefreshResults();
                operation.Complete(outcome: "Workspace search index rebuilt");
            }
            catch (OperationCanceledException) { operation.Cancel(); throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            { operation.Fail(ex.Message); if (CurrentWorkspace()) { searchError = "Rebuild failed: " + Tern.App.Shell.UserProblem.Plain(ex); _picker.RefreshResults(); } }
        });
    }

    // ---- Find / replace ----------------------------------------------------------------------------------------

    private InFileFindResult _found = InFileFindResult.Empty;
    private int _foundIndex = -1;
    private int _findGeneration;
    private Tern.App.Documents.DocumentTab? _findTab;
    private long _findRevision;
    private FindOptions? _findOptions;
    private FindOptions? _requestedFindOptions;
    private Tern.App.Documents.DocumentTab? _requestedFindTab;
    private long _requestedFindRevision;
    private readonly FindMatchRenderer _findHighlight = new();
    private CancellationTokenSource? _findCancellation;
    private bool _updatingFindResults;
    private int _findNavigationGeneration;
    private int _findCloseGeneration;
    private bool _findWrapped;
    private bool _findSourceOnly;
    private bool _replaceAllRunning;
    private readonly Flyout _findOptionsFlyout = DialogParts.PanelFlyout();

    private void OnFindOptionsClick(object? sender, RoutedEventArgs e) => _findOptionsFlyout.ShowAt(FindOptionsButton);

    private void UpdateFindLayout(bool compact)
    {
        FindBox.Width = compact ? 160 : 200;
        FindOptionsButton.IsVisible = compact;
        FindOptionsPanel.Margin = compact ? new Avalonia.Thickness(12) : default;
        FindResultsToggle.Content = compact ? new VectorIcon { Kind = "List" } : "Results";
        FindReplaceToggle.Content = compact ? new VectorIcon { Kind = "Swap" } : "Replace";
        FindResultsToggle.Classes.Set("icon-button", compact);
        FindReplaceToggle.Classes.Set("icon-button", compact);
        if (compact && FindControls.Children.Contains(FindOptionsPanel))
        {
            FindControls.Children.Remove(FindOptionsPanel);
            _findOptionsFlyout.Content = FindOptionsPanel;
        }
        else if (!compact && !FindControls.Children.Contains(FindOptionsPanel))
        {
            _findOptionsFlyout.Hide();
            _findOptionsFlyout.Content = null;
            FindControls.Children.Insert(1, FindOptionsPanel);
        }
    }

    private sealed record FindResultRow(int Index, string Label)
    {
        public override string ToString() => Label;
    }

    private bool HasCurrentFind => _shown is { } tab && ReferenceEquals(_findTab, tab)
        && tab.Session.IsCurrent(_findRevision) && _findOptions == CurrentFindOptions();

    private void OnFindResultsClick(object? sender, RoutedEventArgs e) => FindResults.IsVisible = FindResultsToggle.IsChecked == true;

    private void OnFindReplaceToggleClick(object? sender, RoutedEventArgs e) => ReplaceRow.IsVisible = FindReplaceToggle.IsChecked == true;

    private void OnFindResultSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (!_updatingFindResults && HasCurrentFind && FindResults.SelectedItem is FindResultRow row)
        {
            _findWrapped = false;
            SelectMatch(row.Index);
        }
    }

    private void OnFindResultsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { OnFindCloseClick(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Enter && FindResults.SelectedItem is FindResultRow row && HasCurrentFind)
        { SelectMatch(row.Index); FindBox.Focus(); e.Handled = true; }
    }

    private void InvalidateFind()
    {
        _findGeneration++;
        _findCancellation?.Cancel();
        _found = InFileFindResult.Empty;
        _foundIndex = -1;
        SetFindHighlight(null);
        if (FindBar.IsVisible) _ = RunFindAsync(false);
    }

    private FindOptions CurrentFindOptions() => new(FindBox.Text ?? string.Empty, ReplaceBox.Text ?? string.Empty,
        CaseToggle.IsChecked == true, WordToggle.IsChecked == true, RegexToggle.IsChecked == true);

    internal void ShowFindBar(bool withReplace)
    {
        if (_shown is null)
        {
            return;
        }

        FindBar.IsVisible = true;
        ReplaceRow.IsVisible = withReplace;
        FindReplaceToggle.IsChecked = withReplace;
        if (_shell.ViewMode != ViewMode.Reading && !Editor.TextArea.Selection.IsEmpty && !Editor.SelectedText.Contains('\n', StringComparison.Ordinal))
        {
            FindBox.Text = Editor.SelectedText;
        }

        FindBox.Focus();
        FindBox.SelectAll();
        _ = RunFindAsync(true);
    }

    private void OnFindCloseClick(object? sender, RoutedEventArgs e)
    {
        FindBar.IsVisible = false;
        _findCloseGeneration++;
        _findGeneration++;
        _findCancellation?.Cancel();
        _findNavigationGeneration++;
        SetFindHighlight(null);
        FocusVisibleContent();
    }

    private void OnFindTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!FindBar.IsVisible) return;
        if (ReferenceEquals(_requestedFindTab, _shown) && _shown?.Session.IsCurrent(_requestedFindRevision) == true
            && _requestedFindOptions == CurrentFindOptions()) return;
        _ = RunFindAsync(true);
    }

    private void ApplyFindPreferences()
    {
        var settings = _shell.Settings;
        var changed = CaseToggle.IsChecked != settings.FindMatchCase || WordToggle.IsChecked != settings.FindWholeWord
            || RegexToggle.IsChecked != settings.FindUseRegex;
        CaseToggle.IsChecked = settings.FindMatchCase;
        WordToggle.IsChecked = settings.FindWholeWord;
        RegexToggle.IsChecked = settings.FindUseRegex;
        if (changed && FindBar.IsVisible) InvalidateFind();
    }

    private void OnFindOptionClick(object? sender, RoutedEventArgs e)
    {
        _shell.Settings.FindMatchCase = CaseToggle.IsChecked == true;
        _shell.Settings.FindWholeWord = WordToggle.IsChecked == true;
        _shell.Settings.FindUseRegex = RegexToggle.IsChecked == true;
        _shell.SaveSettings();
        _ = RunFindAsync(true);
    }

    private async Task RunFindAsync(bool select)
    {
        _findCancellation?.Cancel();
        // Each in-flight search owns its source until its continuations finish.
        using var cancellation = new CancellationTokenSource();
        _findCancellation = cancellation;
        var generation = ++_findGeneration;
        _found = InFileFindResult.Empty;
        _foundIndex = -1;
        _findWrapped = false;
        _findSourceOnly = false;
        SetFindHighlight(null);
        var tab = _shown;
        FindStatus.Text = string.IsNullOrEmpty(FindBox.Text) ? string.Empty : "Searching…";
        if (tab is null) { _findCancellation = null; return; }
        var snapshot = tab.Session.GetSnapshot();
        var options = CurrentFindOptions();
        var anchor = _shell.ViewMode == ViewMode.Reading
            ? Editor.Document.GetLineByNumber(Math.Clamp(Preview.TopSourceLine, 1, Editor.Document.LineCount)).Offset
            : Editor.SelectionStart;
        _requestedFindOptions = options;
        _requestedFindTab = tab;
        _requestedFindRevision = snapshot.Revision;
        InFileFindResult result;
        using var activity = _shell.Activities.Begin("Finding document text", tab.DisplayName,
            cancel: () => { if (!cancellation.IsCancellationRequested) cancellation.Cancel(); }, queued: true);
        try
        {
            await Task.Delay(80, cancellation.Token);
            activity.Report("Searching the current document snapshot");
            result = await InFileFinder.FindAsync(snapshot.Text, options, cancellation.Token);
            if (generation != _findGeneration || !ReferenceEquals(tab, _shown) || !tab.Session.IsCurrent(snapshot.Revision))
            {
                activity.Cancel("Find results superseded; document unchanged.");
                return;
            }

            _found = result;
            _findTab = tab;
            _findRevision = snapshot.Revision;
            _findOptions = options;
            _foundIndex = _found.IndexAtOrAfter(anchor);
            _updatingFindResults = true;
            FindResults.ItemsSource = _found.Matches.Select((m, i) => new FindResultRow(i,
                $"Line {m.Line}, column {m.Column}   {ResultSnippet(snapshot.Text, m)}")).ToArray();
            FindResults.SelectedIndex = _foundIndex;
            _updatingFindResults = false;
            SetFindHighlight(_foundIndex >= 0 ? _found.Matches[_foundIndex] : null);
            ShowFindStatus();
            if (select && _foundIndex >= 0)
            {
                SelectMatch(_foundIndex);
            }
            if (result.Error is { } error) activity.Fail(error);
            else activity.Complete(result.Truncated ? "Search reached its result limit; narrow the query." : null, result.Truncated,
                result.Truncated ? "Find results limited" : result.Matches.Count == 0 ? "Find complete; no matches" : "Find results ready");
        }
        catch (OperationCanceledException) { activity.Cancel("Find canceled or superseded; document unchanged."); }
        catch (Exception ex) { activity.Fail(ex.Message); throw; }
        finally { if (ReferenceEquals(_findCancellation, cancellation)) _findCancellation = null; }
    }

    internal async Task SearchAsync(string text)
    {
        FindBox.Text = text;
        await RunFindAsync(true);
    }

    internal string FindStatusText => FindStatus.Text ?? string.Empty;

    private static string ResultSnippet(string source, FindMatch match)
    {
        var lineStart = match.Start == 0 ? 0 : source.LastIndexOf('\n', match.Start - 1) + 1;
        var lineEnd = source.IndexOf('\n', match.Start);
        if (lineEnd < 0) lineEnd = source.Length;
        var start = Math.Max(lineStart, match.Start - 35);
        var end = Math.Min(lineEnd, start + 130);
        return (start > lineStart ? "…" : "") + source[start..end].TrimEnd('\r')
            + (end < lineEnd ? "…" : "");
    }

    private void ShowFindStatus()
    {
        var ready = HasCurrentFind && _found.Matches.Count > 0 && _found.Error is null;
        FindPreviousButton.IsEnabled = FindNextButton.IsEnabled = ready;
        ReplaceCurrentButton.IsEnabled = ready && !_replaceAllRunning && _shown?.IsReadOnly == false;
        ReplaceAllButton.IsEnabled = ReplaceCurrentButton.IsEnabled && !_found.Truncated;
        UpdateReplaceAllHint();
        FindSourceButton.IsVisible = _findSourceOnly && _shell.ViewMode != ViewMode.Source;
        FindStatus.Text = _found.Error is { } error ? error
            : string.IsNullOrEmpty(FindBox.Text) ? string.Empty
            : _found.Matches.Count == 0 ? "No results"
            : $"{_foundIndex + 1} of {_found.Matches.Count}{(_found.Truncated ? "+ (limited)" : string.Empty)}"
                + (_findWrapped ? " · Wrapped" : "") + (_findSourceOnly ? " · Source-only match" : "");
    }

    private async void OnFindSourceClick(object? sender, RoutedEventArgs e)
    {
        var index = _foundIndex;
        await RunCommandAsync(CommandIds.ViewSource);
        if (HasCurrentFind) SelectMatch(index);
        FindBox.Focus();
    }

    private void SetFindHighlight(FindMatch? match)
    {
        if (match is null) FindSourceButton.IsVisible = false;
        _findHighlight.Match = match;
        _findHighlight.Matches = FindBar.IsVisible && HasCurrentFind ? _found.Matches : [];
        if (_found.Matches.Count == 0)
        {
            _updatingFindResults = true;
            FindResults.ItemsSource = null;
            _updatingFindResults = false;
        }
        FindPreviousButton.IsEnabled = FindNextButton.IsEnabled = match is not null && HasCurrentFind;
        ReplaceCurrentButton.IsEnabled = match is not null && HasCurrentFind && !_replaceAllRunning && _found.Error is null && _shown?.IsReadOnly == false;
        ReplaceAllButton.IsEnabled = ReplaceCurrentButton.IsEnabled && !_found.Truncated && _found.Error is null;
        UpdateReplaceAllHint();
        Editor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Selection);
        RefreshPreviewFindHighlight();
    }

    private void RefreshPreviewFindHighlight()
    {
        var valid = FindBar.IsVisible && HasCurrentFind && _shown is { } tab && ReferenceEquals(_renderedPreviewTab, tab)
            && tab.Session.IsCurrent(_renderedPreviewRevision);
        Preview.HighlightSearchMatches(valid ? _found.Matches.Select(m => (m.Start, m.Length)).ToArray() : [],
            valid ? _foundIndex : -1, valid ? _shown!.CurrentText : string.Empty);
    }

    private void QueueFindReveal()
    {
        var navigation = ++_findNavigationGeneration;
        var generation = _findGeneration;
        var index = _foundIndex;
        Dispatcher.UIThread.Post(() =>
        {
            if (navigation != _findNavigationGeneration || generation != _findGeneration || !FindBar.IsVisible
                || !HasCurrentFind || index < 0 || index != _foundIndex || _shell.ViewMode == ViewMode.Source
                || !ReferenceEquals(_renderedPreviewTab, _shown) || _renderedPreviewRevision != _findRevision) return;
            var match = _found.Matches[index];
            _findSourceOnly = !Preview.RevealSearchMatch(match.Line);
            ShowFindStatus();
        }, DispatcherPriority.Loaded);
    }

    private void SelectMatch(int index)
    {
        if (!HasCurrentFind || index < 0 || index >= _found.Matches.Count)
        {
            return;
        }

        var match = _found.Matches[index];
        _foundIndex = index;
        _findSourceOnly = false;
        _updatingFindResults = true;
        FindResults.SelectedIndex = index;
        _updatingFindResults = false;
        Editor.Select(match.Start, match.Length);
        SetFindHighlight(FindBar.IsVisible ? match : null);
        var line = Editor.Document.GetLineByOffset(match.Start);
        if (_shell.ViewMode == ViewMode.Reading)
        {
            NavigateReadingPreviewTo(line.LineNumber);
        }
        else
            Editor.ScrollTo(line.LineNumber, match.Start - line.Offset + 1);
        if (_shell.ViewMode != ViewMode.Source)
        {
            ContextFor(_shown!).ReadingLine = line.LineNumber;
            QueueFindReveal();
        }
        ShowFindStatus();
    }

    internal void FindNext(bool forward)
    {
        if (!HasCurrentFind || _found.Matches.Count == 0 || _found.Error is not null)
        {
            return;
        }

        var next = forward ? (_foundIndex + 1) % _found.Matches.Count
            : (_foundIndex - 1 + _found.Matches.Count) % _found.Matches.Count;
        _findWrapped = forward ? next <= _foundIndex : next >= _foundIndex;
        SelectMatch(next);
    }

    private void OnFindNextClick(object? sender, RoutedEventArgs e) => FindNext(true);

    private void OnFindPreviousClick(object? sender, RoutedEventArgs e) => FindNext(false);

    private void UpdateReplaceAllHint()
    {
        ToolTip.SetShowOnDisabled(ReplaceAllButton, true);
        ToolTip.SetTip(ReplaceAllButton, HasCurrentFind && _found.Truncated
            ? "Replace all is unavailable because the results are limited. Narrow the search to include every match."
            : "Replace all matches (Ctrl+Alt+Enter while focus is in Find or Replace)");
    }

    private async void OnFindBarKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != (KeyModifiers.Control | KeyModifiers.Alt)) return;
        e.Handled = true;
        if (ReplaceAllButton.IsEnabled) await ReplaceAllAsync();
    }

    private void OnFindKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (ReferenceEquals(sender, ReplaceBox))
            {
                ReplaceCurrent();
            }
            else
            {
                if (!HasCurrentFind) SearchThenNavigate(!e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                else FindNext(!e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            OnFindCloseClick(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private async void SearchThenNavigate(bool forward)
    {
        // The first Enter during the debounce selects the first current result;
        // subsequent Enter presses advance normally instead of using stale offsets.
        await RunFindAsync(true);
        if (!forward && HasCurrentFind && _found.Matches.Count > 0) SelectMatch(_found.Matches.Count - 1);
    }

    private void OnReplaceClick(object? sender, RoutedEventArgs e) => ReplaceCurrent();

    internal void ReplaceCurrent()
    {
        if (_replaceAllRunning || _found.Error is not null || _shown is null || _shown.IsReadOnly || !ReferenceEquals(_findTab, _shown) || !_shown.Session.IsCurrent(_findRevision) || _findOptions != CurrentFindOptions()
            || _foundIndex < 0 || _foundIndex >= _found.Matches.Count || !_contexts.TryGetValue(_shown, out var context))
        {
            return;
        }

        var match = _found.Matches[_foundIndex];
        context.Buffer.Apply([new Tern.Domain.Text.TextEdit(match.Start, match.Length, match.Replacement)]);
        _ = RunFindAsync(true);
    }

    private async void OnReplaceAllClick(object? sender, RoutedEventArgs e) => await ReplaceAllAsync();

    internal async Task ReplaceAllAsync()
    {
        if (_replaceAllRunning || _shown is not { IsReadOnly: false } tab || !_contexts.TryGetValue(tab, out var context))
        {
            return;
        }

        var snapshot = tab.Session.GetSnapshot();
        var options = CurrentFindOptions();
        var closeGeneration = _findCloseGeneration;
        _replaceAllRunning = true;
        ShowFindStatus();
        try
        {
            var result = await InFileFinder.FindAsync(snapshot.Text, options);
            if (closeGeneration != _findCloseGeneration || !FindBar.IsVisible || !ReferenceEquals(tab, _shown) || !tab.Session.IsCurrent(snapshot.Revision) || options != CurrentFindOptions()
                || result.Matches.Count == 0 || result.Truncated || result.Error is not null)
            {
                return;
            }

            context.Buffer.Apply([.. result.Matches.Select(m => new Tern.Domain.Text.TextEdit(m.Start, m.Length, m.Replacement))]);
            _shell.StatusMessage = $"Replaced {result.Matches.Count} matches.";
            await RunFindAsync(false);
        }
        finally
        {
            _replaceAllRunning = false;
            ShowFindStatus();
        }
    }

    // ---- Dialog windows -----------------------------------------------------------------------------------------

    private async Task ShowExportAsync(ExportFormat format)
    {
        if (_shown is null)
        {
            return;
        }

        await new ExportWindow(_shown.DisplayName, _shown.FilePath ?? _shown.ResourceDocumentPath, Editor.Document.Text, _shown.Profile, WorkspaceRootFor(_shown), format,
    _shell.Tabs.Select(t => t.FilePath).OfType<string>().ToArray(), ExportImageLoader(_shown), _shown.LinkedImageFiles,
    _shell.Activities, Tern.Desktop.Services.MotionPolicy.IsReduced, _shell.Settings.ExportPreferences, _shell.SaveSettings).ShowDialog(this);
    }

    private async Task ShowSettingsAsync(string? initialCategory = null)
    {
        await new SettingsWindow(_shell.Settings, _shell.Registry, RunCommandAsync, AskGuideAIAsync,
            configureAI: ShowAIConnectionsAsync, aiProfiles: () => _aiWorkspace.Profiles, initialCategory: initialCategory,
            answerAI: AnswerGuideQuestionAsync, workspaceConnections: () => _aiWorkspace.Connections,
            commit: () =>
            {
                if (!_shell.SaveSettings()) throw new IOException("Settings could not be persisted. Earlier settings remain active.");
                ApplyTheme(); ApplySettings(); RefreshCommandHints(); RebuildPreview();
            }).ShowDialog(this);
        ReloadAIConnections();
        ApplyTheme();
        ApplySettings();
        RebuildPreview();
    }

    private async Task ShowShortcutsAsync()
    {
        await new ShortcutsWindow(_shell.Registry, RunCommandAsync, AskGuideAIAsync, AnswerGuideQuestionAsync,
            commit: () =>
            {
                var previous = _shell.Settings.KeyBindings;
                if (!_shell.SaveSettings())
                {
                    _shell.Settings.KeyBindings = previous;
                    throw new IOException("Shortcut settings could not be persisted. Earlier assignments remain active.");
                }
                SearchHint.Text = Gesture.Describe(_shell.Registry.GetGesture(CommandIds.QuickOpen));
                RefreshCommandHints();
            }).ShowDialog(this);
    }
}
