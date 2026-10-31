using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Panels;
using Tern.Desktop.ViewModels;
using Tern.Domain.Text;
using Tern.Workspaces;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private string _nav = "Files";
    private string _lastSearch = string.Empty;
    private IReadOnlyList<TreeRow> _treeRoots = [];
    private int _taskScanGeneration;
    private int _tasksOpen;
    private readonly HashSet<Tern.App.Shell.WorkspaceSession> _observedWorkspaces = [];

    private void OnWorkspaceFilesChanged(object? sender, WorkspaceChangeSet e) => Dispatcher.UIThread.Post(async () => { if (!_windowClosed) { RefreshWorkspace(); await CheckExternalChangesAsync(); } });

    internal string ActiveNav => _nav;

    private void OnRestoreLibraryClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_focusMode) ToggleFocusMode();
        _shell.Settings.SidebarVisible = true;
        ApplySettings();
        // Focusing a TreeView restores its selected descendant and expands collapsed ancestors.
        // Enter the restored panel at its visible header so every section keeps the user's expansion state.
        WorkspaceAddButton.Focus();
    }

    private void OnRestoreInspectorClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_focusMode) ToggleFocusMode();
        _compactInspector = false;
        RightPanel.IsVisible = true;
        Body.ColumnDefinitions[4].Width = new GridLength(260);
        UpdatePanelRestoreButtons();
        InspectorContent.Focus();
    }

    private void RefreshWorkspace()
    {
        var workspace = _shell.Workspace;
        foreach (var old in _observedWorkspaces.Where(w => !_shell.OpenWorkspaces.Contains(w)).ToArray())
        {
            old.FilesChanged -= OnWorkspaceFilesChanged;
            _observedWorkspaces.Remove(old);
        }
        foreach (var current in _shell.OpenWorkspaces)
            if (_observedWorkspaces.Add(current)) current.FilesChanged += OnWorkspaceFilesChanged;
        _syncingWorkspaces = true;
        WorkspaceSelector.ItemsSource = _shell.OpenWorkspaces.ToArray();
        WorkspaceSelector.SelectedItem = workspace;
        WorkspaceSelector.IsVisible = false;
        _syncingWorkspaces = false;

        BuildTree();
        SidebarTitle.Text = "LIBRARY";
        WorkspaceCard.IsVisible = false;
        WorkspaceTitle.Text = workspace is null ? string.Empty : (workspace.IsNamedWorkspace ? "Workspace · " : "Folder · ") + workspace.Name;
        WorkspaceLocation.Text = workspace?.Root ?? string.Empty;
        WorkspaceCount.Text = workspace is null ? string.Empty : $"{workspace.Snapshot.Files.Count:N0} documents · {_shell.OpenWorkspaces.Count(w => w.IsNamedWorkspace)} workspace(s) open";
        if (workspace?.Snapshot.Warnings.Count > 0) WorkspaceCount.Text += $" · {workspace.Snapshot.Warnings.Count} unavailable item(s)";
        ToolTip.SetTip(WorkspaceCount, workspace is null ? null : string.Join(Environment.NewLine, workspace.Notices));
        ToolTip.SetTip(WorkspaceLocation, workspace?.Root);
        ToolTip.SetTip(WorkspaceTitle, workspace?.Root);
        Avalonia.Controls.ToolTip.SetTip(SidebarTitle, workspace?.Root);
        UpdateBadges();
        ShowNav(_nav);
        UpdateChrome();
        RefreshDocumentWorkspaceContext();
    }

    private void BuildTree()
    {
        if (_shell.OpenWorkspaces.Count == 0)
        {
            _treeRoots = [];
            FileTree.ItemsSource = null;
            return;
        }

        var expanded = _treeRoots.SelectMany(r => r.Flatten()).GroupBy(r => r.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().IsExpanded, StringComparer.OrdinalIgnoreCase);
        var namedRoots = _shell.OpenWorkspaces.Where(w => w.IsNamedWorkspace).Select(w => w.Root).ToHashSet(WorkspacePaths.Comparer);
        static bool ContainsDirectory(FileTreeNode node, string path) => WorkspacePaths.PathsEqual(node.FullPath, path)
            || node.Children.Any(child => child.IsDirectory && ContainsDirectory(child, path));
        var top = _shell.OpenWorkspaces.Where(w => !_shell.OpenWorkspaces.Any(parent => parent != w && parent.IsInside(w.Root)
            && ContainsDirectory(parent.Snapshot.Tree, w.Root))).ToArray();
        _treeRoots = top.OrderByDescending(w => w.IsNamedWorkspace)
            .Select(w => TreeRow.From(w.Snapshot.Tree, workspaceRoot: w.IsNamedWorkspace, namedRoots, rootSection: true)).ToArray();
        foreach (var row in _treeRoots.SelectMany(r => r.Flatten())) row.IsExpanded = expanded.TryGetValue(row.FullPath, out var value) ? value : row.IsWorkspace || top.Any(w => WorkspacePaths.PathsEqual(w.Root, row.FullPath));
        foreach (var row in _treeRoots.SelectMany(r => r.Flatten()).Where(r => r.IsSection))
        {
            var owner = _shell.OpenWorkspaces.FirstOrDefault(w => WorkspacePaths.PathsEqual(w.Root, row.FullPath));
            row.IsSectionOpen = owner is not null;
            row.IsSelected = ReferenceEquals(owner, _shell.Workspace);
            if (owner?.Snapshot.Warnings.Count > 0) row.SectionNotice = $"{owner.Snapshot.Warnings.Count} unavailable item(s). " + string.Join(" ", owner.Notices);
            else if (owner is null && row.IsWorkspace) row.SectionNotice = "Workspace not open. Select its name to open it.";
        }
        FileTree.ItemsSource = _treeRoots;
        RefreshTreeDirtyState();
    }

    internal void RefreshLibraryContents()
    {
        RefreshWorkspace();
    }

    private void RevealOpenedWorkspacePath()
    {
        if (_shell.WorkspaceRevealPath is not { } path) return;
        foreach (var row in _treeRoots.SelectMany(root => root.Flatten()).Where(row => row.IsDirectory && WorkspacePaths.IsSameOrUnder(row.FullPath, path)))
            row.IsExpanded = true;
    }

    private void OnWorkspaceSectionToggleClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Control { DataContext: TreeRow row }) row.IsExpanded = !row.IsExpanded;
    }

    private async void OnWorkspaceSectionSelectClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Control { DataContext: TreeRow row }) return;
        if (row.IsWorkspace) await _shell.OpenWorkspaceAsync(row.FullPath);
        else if (_shell.OpenWorkspaces.FirstOrDefault(w => WorkspacePaths.PathsEqual(w.Root, row.FullPath)) is { } owner)
            _shell.SelectWorkspace(owner);
    }

    private async void OnWorkspaceSectionCloseClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Control { DataContext: TreeRow row }) return;
        RememberCurrentDocumentView();
        if (_shell.OpenWorkspaces.FirstOrDefault(w => WorkspacePaths.PathsEqual(w.Root, row.FullPath)) is { } owner)
            await _shell.CloseWorkspaceAsync(owner);
    }

    private void RefreshTreeDirtyState()
    {
        var open = _shell.Tabs.Where(t => t.FilePath is not null).Select(t => t.FilePath!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dirty = _shell.Tabs.Where(t => t.IsDirty && t.FilePath is not null).Select(t => t.FilePath!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _treeRoots.SelectMany(r => r.Flatten()))
        {
            row.IsDirty = !row.IsDirectory && dirty.Contains(row.FullPath);
            row.IsOpen = !row.IsDirectory && open.Contains(row.FullPath);
            row.IsActive = !row.IsDirectory && string.Equals(_shown?.FilePath, row.FullPath, StringComparison.OrdinalIgnoreCase);
        }
        if (_nav == "Files") RefreshOutsideLibrary();
    }

    private void UpdateBadges()
    {
        var broken = _shell.Workspace is { } current ? _shell.GetBrokenLinks(current).Count : 0;
        BrokenBadge.IsVisible = broken > 0;
        BrokenBadgeText.Text = broken.ToString(System.Globalization.CultureInfo.CurrentCulture);
        TasksBadge.IsVisible = _tasksOpen > 0;
        TasksBadgeText.Text = _tasksOpen.ToString(System.Globalization.CultureInfo.CurrentCulture);
    }

    private async Task ScanTasksAsync()
    {
        var generation = ++_taskScanGeneration;
        var workspace = _shell.Workspace;
        if (workspace is null)
        {
            _tasksOpen = 0;
            return;
        }

        var buffers = _shell.Tabs.Where(t => t.FilePath is not null).GroupBy(t => t.FilePath!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(t => ReferenceEquals(t, _shell.ActiveTab)).ThenByDescending(t => t.IsDirty).First().CurrentText, StringComparer.OrdinalIgnoreCase);
        var files = workspace.Snapshot.Files;
        var profile = _shell.Settings.DefaultProfile;
        var entries = await Task.Run(() => TaskIndex.ScanAsync(files, profile, buffers));
        if (generation == _taskScanGeneration)
        {
            _tasks = entries;
            _tasksOpen = TaskIndex.OpenCount(entries);
            UpdateBadges();
        }
    }

    private IReadOnlyList<TaskEntry> _tasks = [];

    internal async Task ShowNavAsync(string nav)
    {
        if (nav == "Tasks")
        {
            await ScanTasksAsync();
        }

        ShowNav(nav);
    }

    private void ShowNav(string nav)
    {
        _nav = nav;
        NavRecent.IsChecked = nav == "Recent";
        NavSearch.IsChecked = nav == "Search";
        NavFiles.IsChecked = nav == "Files";
        NavBacklinks.IsChecked = nav == "Backlinks";
        NavBroken.IsChecked = nav == "Broken";
        NavTasks.IsChecked = nav == "Tasks";

        var showTree = nav == "Files" && _shell.OpenWorkspaces.Count > 0;
        FileTree.IsVisible = showTree;
        LibraryContent.RowDefinitions[0].Height = nav == "Files" && !showTree ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        LibraryContent.RowDefinitions[2].Height = nav == "Files" && !showTree ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        Grid.SetRow(SideList, nav == "Files" ? 2 : 0);
        Grid.SetRowSpan(SideList, nav == "Files" ? 1 : 3);
        SideList.MaxHeight = showTree ? 220 : double.PositiveInfinity;
        OutsideLibraryHeader.IsVisible = false;
        NoWorkspaceText.IsVisible = false;
        if (nav == "Files")
        {
            RefreshOutsideLibrary();
            return;
        }

        SideList.IsVisible = true;
        SideList.ItemsSource = RowsFor(nav);
    }

    private IReadOnlyList<SideRow> RowsFor(string nav)
    {
        var workspace = _shell.Workspace;
        switch (nav)
        {
            case "Recent":
                return FileReferenceRows(includeWorkspaces: true);
            case "Search":
                if (workspace is null || _lastSearch.Length == 0)
                {
                    return [];
                }

                var page = workspace.Search(_lastSearch, 50);
                if (page.Error is { } searchError) _shell.StatusMessage = searchError;
                return [.. page.Hits.Select(h => new SideRow(h.Title, h.RelativePath + " — " + h.Snippet, h.Path, h.Line))];
            case "Backlinks":
                if (workspace is null || _shown?.FilePath is not { } current)
                {
                    return [];
                }

                return [.. workspace.Graph.GetBacklinks(current).Select(b => new SideRow(Path.GetFileName(b.SourcePath), $"line {b.Line}: {b.Context}", b.SourcePath, b.Line))];
            case "Broken":
                return workspace is null
                    ? []
                    : [.. _shell.GetBrokenLinks(workspace).Select(l => new SideRow(l.Link.Destination, $"{Path.GetFileName(l.SourcePath)}:{l.Line}", l.SourcePath, l.Line))];
            case "Tasks":
                return [.. _tasks.Where(t => !t.Checked).Select(t => new SideRow(t.Text, $"{t.RelativePath}:{t.Line}", t.Path, t.Line))];
            case "Files":
                return workspace is null ? FileReferenceRows() : [];
            default:
                return [];
        }
    }

    private IReadOnlyList<SideRow> FileReferenceRows(bool includeWorkspaces = false) => [.. _shell.Recent.Files.Select(r => (Entry: r, Kind: RecentKind.File))
        .Concat(_shell.Recent.Urls.Select(r => (Entry: r, Kind: RecentKind.Url)))
        .Concat(includeWorkspaces ? _shell.Recent.Workspaces.Select(r => (Entry: r, Kind: RecentKind.Workspace)) : [])
        .OrderByDescending(r => r.Entry.Pinned).ThenByDescending(r => r.Entry.LastOpenedUtc)
        .Select(r =>
        {
            if (r.Kind == RecentKind.Workspace) return RecentWorkspaceRow(r.Entry.Path);
            if (r.Kind == RecentKind.Url)
            {
                var uri = new Uri(r.Entry.Path);
                var name = Uri.UnescapeDataString(uri.Segments.LastOrDefault() ?? "").Trim('/');
                return new SideRow(string.IsNullOrWhiteSpace(name) ? uri.Host : name,
                    "Online · " + uri.AbsoluteUri, uri.AbsoluteUri, 0, CanRemoveReference: true, IsOnline: true);
            }
            var exists = File.Exists(r.Entry.Path);
            return new SideRow(exists ? Path.GetFileName(r.Entry.Path) : "⚠ Missing or inaccessible · " + Path.GetFileName(r.Entry.Path),
                exists ? Path.GetDirectoryName(r.Entry.Path) ?? string.Empty : r.Entry.Path + " · Open to retry, or remove reference",
                r.Entry.Path, 0, IsMissing: !exists, CanRemoveReference: true);
        })];

    private SideRow RecentWorkspaceRow(string path)
    {
        var exists = Directory.Exists(path);
        var name = _shell.OpenWorkspaces.FirstOrDefault(w => WorkspacePaths.PathsEqual(w.Root, path))?.Name
            ?? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        try { name = WorkspaceDefinition.Load(path)?.Name ?? name; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        return new SideRow(name, "Workspace · " + path + (exists ? string.Empty : " · Missing or inaccessible; open to retry"),
            path, 0, IsMissing: !exists, CanRemoveReference: true) { IsWorkspace = true };
    }

    private async Task OpenRecentWorkspaceAsync(string path)
    {
        if (await _shell.OpenWorkspaceAsync(path)) await ShowNavAsync("Files");
        else
        {
            _shell.StatusMessage = "Could not open workspace: " + path + ". Check that the folder is available, then retry. Its Recent entry was kept.";
        }
    }

    private IReadOnlyList<SideRow> OutsideLibraryRows()
    {
        var references = FileReferenceRows().Where(r => r.IsOnline || r.Path is { } path && _shell.WorkspaceForPath(path) is null).ToList();
        foreach (var tab in _shell.Tabs.Where(t => t.FilePath is { } path && _shell.WorkspaceForPath(path) is null))
        {
            if (references.Any(r => r.Path is { } path && WorkspacePaths.PathsEqual(path, tab.FilePath!))) continue;
            references.Add(new SideRow(tab.DisplayName, Path.GetDirectoryName(tab.FilePath!) ?? "Outside workspaces", tab.FilePath, 0, CanRemoveReference: true) { OpenTab = tab });
        }
        foreach (var tab in _shell.Tabs.Where(t => t.FilePath is null))
        {
            var online = tab.SourceUrl is null ? -1 : references.FindIndex(row => row.IsOnline && row.Path == tab.SourceUrl);
            if (online >= 0) references[online] = references[online] with { OpenTab = tab };
            else references.Add(new SideRow(tab.DisplayName, "Outside workspaces · unsaved draft", null, 0) { OpenTab = tab });
        }
        return references;
    }

    private void RefreshOutsideLibrary()
    {
        var outside = OutsideLibraryRows();
        SideList.IsVisible = outside.Count > 0;
        SideList.ItemsSource = outside;
        OutsideLibraryHeader.IsVisible = outside.Count > 0;
        NoWorkspaceText.IsVisible = _shell.OpenWorkspaces.Count == 0 && outside.Count == 0;
    }

    internal void ShowSearchResults(string query)
    {
        _lastSearch = query;
        ShowNav("Search");
    }

    private async void OnNavClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string tag })
        {
            await ShowNavAsync(tag);
        }
    }

    private async void OnOpenFolderClick(object? sender, RoutedEventArgs e) => await OpenFolderAsync();

    private async Task OpenFolderAsync()
    {
        if (_dialogs is null)
        {
            return;
        }

        if (await _dialogs.PickFolderAsync() is { } folder)
        {
            await OpenFolderSelectionAsync(folder);
        }
    }

    internal async Task OpenFolderSelectionAsync(string folder)
    {
        if (_shell.IsKnownWorkspace(folder))
        {
            var choice = await PromptWindow.AskAsync(this, "Open as workspace",
                "This folder is a workspace. Use Open Workspace to load it correctly.\n\n" + folder,
                ["Open Workspace", "Cancel"], 0, 1);
            if (choice == 0) await OpenRecentWorkspaceAsync(folder);
            return;
        }
        if (await _shell.OpenFolderAsync(folder)) await ShowNavAsync("Files");
    }

    private async void OnShowTreeClick(object? sender, RoutedEventArgs e) => await ShowNavAsync("Files");

    private async void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_libraryRightPress) { FileTree.SelectedItem = null; return; }
        if (FileTree.SelectedItem is TreeRow selected)
        {
            var owner = _shell.OpenWorkspaces.FirstOrDefault(w => WorkspacePaths.PathsEqual(w.Root, selected.FullPath))
                ?? _shell.WorkspaceForPath(selected.FullPath);
            if (owner is not null && !ReferenceEquals(owner, _shell.Workspace)) _shell.SelectWorkspace(owner);
        }
        if (FileTree.SelectedItem is TreeRow { IsDirectory: false } row)
        {
            await OpenDocumentPathAsync(row.FullPath);
        }
    }

    private async void OnSideListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_libraryRightPress) { SideList.SelectedItem = null; return; }
        if (SideList.SelectedItem is SideRow { OpenTab: { } tab })
        { _shell.ActiveTab = tab; SideList.SelectedItem = null; return; }
        if (SideList.SelectedItem is not SideRow { Path: { } path } row)
        {
            return;
        }

        SideList.SelectedItem = null;
        if (row.IsWorkspace) await OpenRecentWorkspaceAsync(path);
        else if (row.IsOnline) await OpenStoredUrlAsync(path);
        else await OpenDocumentPathAsync(path, Math.Max(1, row.Line));
    }

    private void OnOutlineSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (OutlineList.SelectedItem is OutlineRow row)
        {
            GoToLine(row.Line, 1);
            Preview.ScrollToAnchor(row.Anchor);
            OutlineList.SelectedItem = null;
        }
    }

    private void OnLinkListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (LinkList.SelectedItem is LinkRowView row)
        {
            GoToLine(row.Line, 1);
            RepairLinkButton.IsEnabled = row.IsBroken && _shown is { IsReadOnly: false, FilePath: { } path }
                && _shell.WorkspaceForPath(path) is not null;
        }
    }

    private void RefreshPanels()
    {
        RepairLinkButton.IsEnabled = false;
        var owner = _shown?.FilePath is { } path ? _shell.WorkspaceForPath(path) : _shell.Workspace;
        var panel = DocumentPanel.Build(_shown, owner);
        OutlineEmpty.IsVisible = panel.Outline.Count == 0;
        LinksEmpty.IsVisible = panel.Links.Count == 0;
        TagsEmpty.IsVisible = panel.Tags.Count == 0;
        RepairLinkButton.IsVisible = panel.Links.Any(link => link.IsBroken);
        OutlineList.ItemsSource = panel.Outline.Select(o => new OutlineRow(o.Text, new Thickness(Math.Max(0, o.Level - 1) * 12, 2, 0, 2), o.Line, o.Anchor)).ToList();
        LinkList.ItemsSource = panel.Links.Select(l => new LinkRowView(l.Label, l.Destination, l.Line, l.IsBroken, l.IsBroken ? "✕" : l.IsExternal ? "↗" : "✓")).ToList();
        LinksSummary.Text = panel.LinksChecked == 0 ? string.Empty : panel.LinksBroken == 0 ? $"{panel.LinksChecked} checked" : $"{panel.LinksChecked} checked · {panel.LinksBroken} broken";
        InfoWords.Text = panel.Info.Words.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        InfoRead.Text = panel.Info.ReadMinutes == 0 ? "–" : $"{panel.Info.ReadMinutes} min";
        InfoModified.Text = _shown is null ? "–" : DocumentPanel.DescribeAge(panel.Info.ModifiedUtc, DateTime.UtcNow);
        TagList.ItemsSource = panel.Tags;
        UpdateBadges();
        if (_nav is "Backlinks" or "Broken")
        {
            SideList.ItemsSource = RowsFor(_nav);
        }
    }

    private void UpdateStatus()
    {
        var tab = _shown;
        StatusEncoding.IsVisible = StatusLineEnding.IsVisible = StatusProfile.IsVisible = tab is not null;
        if (tab is null)
        {
            StatusPath.Text = string.Empty;
            StatusPosition.Text = string.Empty;
            StatusWords.Text = string.Empty;
            StatusEncoding.Content = string.Empty;
            StatusLineEnding.Content = string.Empty;
            StatusProfile.Content = string.Empty;
            StatusSaved.Text = string.Empty;
            return;
        }

        var caret = Editor.TextArea.Caret;
        StatusPath.Text = tab.FilePath ?? (tab.RecoveredFromPath is { } original
            ? $"Recovered copy of {original} (not saved)" : tab.DisplayName + " (not saved)");
        StatusPosition.Text = $"Ln {caret.Line}, Col {caret.Column}";
        StatusWords.Text = $"{tab.Analysis?.WordCount ?? 0:N0} words";
        StatusEncoding.Content = tab.EncodingText;
        StatusLineEnding.Content = tab.LineEndingText;
        StatusProfile.Content = tab.Profile switch
        {
            Tern.Markdown.MarkdownProfile.Gfm => "GFM",
            Tern.Markdown.MarkdownProfile.Extended => "Extended",
            _ => "CommonMark",
        };
        StatusSaved.Text = tab.IsReadOnly ? "Read-only"
            : tab.IsDirty ? (tab.IsRecovered && tab.IsUntitled ? "Recovered · " : string.Empty) + (tab.RecoveryError is not null ? "Unsaved · recovery failed"
                : tab.CheckpointRevision == tab.Session.Revision ? "Unsaved · recovery checkpointed" : "Unsaved · recovery pending"
                )
            : tab.IsUntitled ? "Not saved to file" : "Saved locally";
    }

    private void OnEncodingClick(object? sender, RoutedEventArgs e) => ShowEncodingPicker(_shown?.IsReadOnly == true);

    private void ShowEncodingPicker(bool reopen)
    {
        var command = reopen ? Tern.App.Commands.CommandIds.ReopenWithEncoding : Tern.App.Commands.CommandIds.ChangeEncoding;
        if (!CanRunCommand(command)) return;
        if (_shown is not { } tab)
        {
            return;
        }

        var options = Tern.Filesystem.TextFileCodec.AvailableEncodings
            .OrderByDescending(o => o.Kind == tab.Session.Format.Encoding && o.HasBom == tab.Session.Format.HasBom)
            .ThenBy(o => o.CodePage == 65001 ? 0 : o.CodePage is 1200 or 1201 or 12000 or 12001 ? 1 : 2)
            .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        ShowPicker(reopen ? "Reopen existing bytes with encoding" : "Choose encoding for the next save", string.Empty,
            query => [.. options.Where(o => string.IsNullOrWhiteSpace(query) || o.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(o => new PickerItem(o.Name, reopen ? "Read the file using this encoding" : "Convert on Save", string.Empty, o))], async item =>
        {
            HideOverlay();
            if (item.Payload is Tern.Filesystem.EncodingOption option && ReferenceEquals(tab, _shown) && CanRunCommand(command))
            {
                try
                {
                    if (reopen)
                    {
                        if (!await tab.ReopenWithEncodingAsync(option.Kind))
                            _shell.StatusMessage = tab.LastSaveError ?? "The document changed while reopening. Your changes were kept; choose the encoding again.";
                    }
                    else if (!tab.TryConvertEncoding(option.Kind, option.HasBom))
                        _shell.StatusMessage = "Some characters cannot be saved in " + option.Name + ". Your text was kept.";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { _shell.StatusMessage = "The file could not be reopened: " + ex.Message; }
            }

            UpdateStatus();
        });
    }

    private void OnProfileClick(object? sender, RoutedEventArgs e)
    {
        if (_shown is not { } tab) return;
        ShowPicker("Markdown profile for this document", string.Empty,
            _ => [.. Enum.GetValues<Tern.Markdown.MarkdownProfile>().Select(profile => new PickerItem(profile.ToString(), "Changes rendering only; source stays unchanged", string.Empty, profile))],
            item =>
            {
                HideOverlay();
                if (item.Payload is Tern.Markdown.MarkdownProfile profile && ReferenceEquals(tab, _shown))
                {
                    tab.SetProfile(profile);
                    UpdateStatus();
                    RebuildPreview();
                }
            });
    }

    private void OnLineEndingClick(object? sender, RoutedEventArgs e)
    {
        if (!CanRunCommand(Tern.App.Commands.CommandIds.ChangeLineEnding)) return;
        if (_shown is not { } tab)
        {
            return;
        }

        ShowPicker("Choose line ending", string.Empty,
            query => new[] { (LineEnding.CrLf, "CRLF (Windows)"), (LineEnding.Lf, "LF (Unix / macOS)"), (LineEnding.Cr, "CR (classic Mac)") }
                .Where(o => string.IsNullOrWhiteSpace(query) || o.Item2.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(o => new PickerItem(o.Item2, "Convert now; keep this choice when saving", string.Empty, o.Item1)).ToArray(), item =>
        {
            HideOverlay();
            if (item.Payload is LineEnding ending && ReferenceEquals(tab, _shown) && CanRunCommand(Tern.App.Commands.CommandIds.ChangeLineEnding))
            {
                tab.ConvertLineEndings(ending);
            }

            UpdateStatus();
        });
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var items = e.DataTransfer.TryGetFiles();
        if (items is null)
        {
            e.Handled = true;
            _shell.StatusMessage = "Drop local Markdown files or folders to open them, or PNG, JPEG, GIF, WebP or BMP images to insert them. Paste text with Ctrl+V.";
            return;
        }

        e.Handled = true;
        e.DragEffects = DragDropEffects.Copy;
        var paths = items.Select(i => i.TryGetLocalPath()).ToArray();
        var inSource = e.Source is Visual visual && (ReferenceEquals(visual, Editor) || visual.GetVisualAncestors().Contains(Editor));
        await OpenDroppedAsync([.. paths.OfType<string>()], inSource);
        if (paths.Any(p => p is null)) _shell.StatusMessage += " Non-local items were not opened. Save them locally first.";
    }

    internal async Task OpenDroppedAsync(IReadOnlyList<string> paths, bool allowImageInsertion = true)
    {
        var problems = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    if (!await _shell.OpenDirectoryAsync(path)) problems.Add(Path.GetFileName(path) + ": folder was not opened.");
                }
                else if (File.Exists(path) && IsMarkdownPath(path))
                {
                    if (await _shell.OpenFileAsync(path, duplicateBehavior: Tern.App.Shell.DuplicateOpenBehavior.Ask) is null)
                        problems.Add(Path.GetFileName(path) + ": document was not opened (cancelled or unavailable).");
                }
                else if (File.Exists(path) && IsRasterImagePath(path))
                {
                    if (!allowImageInsertion)
                    { problems.Add(Path.GetFileName(path) + ": drop images into the writable Source editor, or choose Format → Insert Local Image."); continue; }
                    if (!await ImportImageFileAsync(path)) problems.Add(Path.GetFileName(path) + ": " + _shell.StatusMessage);
                }
                else
                {
                    problems.Add(Path.GetFileName(path) + ": unsupported or missing item. Drop Markdown or text (.md, .markdown, .mdown, .txt) or folders to open; PNG, JPEG, GIF, WebP or BMP images insert attachments.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                problems.Add(Path.GetFileName(path) + ": " + ex.Message);
            }
        }
        if (problems.Count > 0) _shell.StatusMessage = string.Join(" · ", problems);
        else if (paths.Count == 0) _shell.StatusMessage = "No local files were dropped. Save the item locally, then try again.";
    }

    private static bool IsMarkdownPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown" or ".mdown" or ".txt";

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File)) return;
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }
}
