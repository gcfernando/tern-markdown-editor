using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Collections.ObjectModel;
using Tern.App.Panels;
using Tern.App.Presentation;
using Tern.App.Shell;
using Tern.Desktop.ViewModels;
using Tern.Domain.Text;
using Tern.Workspaces;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private string _nav = "Files";
    private string _lastSearch = string.Empty;
    private readonly ObservableCollection<TreeRow> _treeRoots = [];
    private readonly ObservableCollection<SideRow> _outsideLibraryRows = [];
    private readonly HashSet<TreeRow> _observedTreeDirectories = [];
    private bool _treePresentationPending;
    private IReadOnlyList<OutlineItem> _outline = [];
    private IReadOnlyList<LinkRowView> _documentLinks = [];
    private int _taskScanGeneration;
    private int _tasksOpen;
    private readonly HashSet<Tern.App.Shell.WorkspaceSession> _observedWorkspaces = [];

    private void OnWorkspaceFilesChanged(object? sender, WorkspaceChangeSet e) => Dispatcher.UIThread.Post(async () =>
    {
        if (_windowClosed) return;
        try
        {
            RefreshWorkspace();
            await _shell.OfferFollowForMovedFilesAsync(e);
            if (!_windowClosed) await CheckExternalChangesAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { if (!_windowClosed) _shell.ReportUnexpectedError("check moved workspace files", ex); }
    });
    private void OnWorkspaceGraphChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_windowClosed) return;
        RefreshPanels();
        UpdateBadges();
        if (_nav is "Backlinks" or "Broken" or "Tags" or "Graph") ShowNav(_nav);
        ScheduleTaskRefresh();
    });

    internal string ActiveNav => _nav;

    private void OnRestoreLibraryClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_focusMode) ToggleFocusMode();
        if (_libraryAutoCollapsed) _libraryForcedOpen = true;
        else _shell.Settings.SidebarVisible = true;
        ApplySettings();
        // Focusing a TreeView restores its selected descendant and expands collapsed ancestors.
        // Enter the restored panel at its visible header so every section keeps the user's expansion state.
        WorkspaceAddButton.Focus();
    }

    private void OnRestoreInspectorClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_focusMode) ToggleFocusMode();
        SetInspectorVisibility(true);
        InspectorContent.Focus();
    }

    private void OnWorkspaceProblemReported(object? sender, string reason) { if (!_windowClosed) RefreshWorkspace(); }

    private void OnWorkspaceContentStatusChanged(object? sender, EventArgs e)
    {
        if (_windowClosed || sender is not WorkspaceSession workspace) return;
        // Runs repeatedly during indexing, so follow the path to the section instead of walking every file.
        foreach (var root in _treeRoots)
            if (FindDirectoryRow(root, workspace.Root) is { IsSection: true } row) UpdateContentStatus(row, workspace);
        if (_picker is not null && ReferenceEquals(workspace, _shell.Workspace)) _picker.RefreshResults();
    }

    private static TreeRow? FindDirectoryRow(TreeRow row, string path)
    {
        if (WorkspacePaths.PathsEqual(row.FullPath, path)) return row;
        if (!WorkspacePaths.IsSameOrUnder(row.FullPath, path)) return null;
        foreach (var child in row.Children)
            if (child.IsDirectory && FindDirectoryRow(child, path) is { } found) return found;
        return null;
    }

    /// <summary>Shows Files as soon as the tree is ready; indexing continues in the background and never pulls the user back.</summary>
    private async Task<bool> OpenThenShowFilesAsync(Func<Action, Task<bool>> open)
    {
        var shown = false;
        var opened = await open(() =>
        {
            shown = true;
            Dispatcher.UIThread.Post(async () => { if (!_windowClosed) await ShowNavAsync("Files"); });
        });
        if (opened && !shown && !_windowClosed) await ShowNavAsync("Files");
        return opened;
    }

    private static void UpdateContentStatus(TreeRow row, WorkspaceSession workspace)
    {
        row.IsPreparingContent = workspace.IsPreparingContent;
        row.IsContentProgressIndeterminate = workspace.ContentProgress is not { Total: > 0 };
        row.ContentProgressFraction = workspace.ContentProgress is { Total: > 0 } p ? Math.Clamp((double)p.Processed / p.Total, 0, 1) : 0;
        if (workspace.IsPreparingContent || !workspace.IsContentReady) row.SectionNotice = workspace.ContentStatus;
        else if (workspace.UpdatesPausedReason is null) row.SectionNotice = workspace.Snapshot.Warnings.Count > 0
            ? $"{workspace.Snapshot.Warnings.Count} unavailable item(s). " + string.Join(" ", workspace.Notices)
            : string.Empty;
    }

    private void OnCancelWorkspacePreparationClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Control { DataContext: TreeRow row })
            _shell.OpenWorkspaces.FirstOrDefault(w => WorkspacePaths.PathsEqual(w.Root, row.FullPath))?.CancelContentPreparation();
    }

    private void RefreshWorkspace()
    {
        var workspace = _shell.Workspace;
        foreach (var old in _observedWorkspaces.Where(w => !_shell.OpenWorkspaces.Contains(w)).ToArray())
        {
            old.FilesChanged -= OnWorkspaceFilesChanged;
            old.GraphChanged -= OnWorkspaceGraphChanged;
            old.ProblemReported -= OnWorkspaceProblemReported;
            old.ContentStatusChanged -= OnWorkspaceContentStatusChanged;
            _observedWorkspaces.Remove(old);
        }
        foreach (var current in _shell.OpenWorkspaces)
            if (_observedWorkspaces.Add(current)) { current.FilesChanged += OnWorkspaceFilesChanged; current.GraphChanged += OnWorkspaceGraphChanged; current.ProblemReported += OnWorkspaceProblemReported; current.ContentStatusChanged += OnWorkspaceContentStatusChanged; }
        _syncingWorkspaces = true;
        WorkspaceSelector.ItemsSource = _shell.OpenWorkspaces.ToArray();
        WorkspaceSelector.SelectedItem = workspace;
        WorkspaceSelector.IsVisible = false;
        _syncingWorkspaces = false;

        BuildTree();
        SidebarTitle.Text = "LIBRARY";
        WorkspaceCard.IsVisible = false;
        WorkspaceTitle.Text = workspace is null ? string.Empty : (workspace.IsNamedWorkspace ? "Workspace · " : "Folder · ") + workspace.Name + (workspace.IsPrivate ? " · Private" : "");
        WorkspaceLocation.Text = workspace?.Root ?? string.Empty;
        WorkspaceCount.Text = workspace is null ? string.Empty : $"{workspace.Snapshot.Files.Count:N0} documents · {_shell.OpenWorkspaces.Count(w => w.IsNamedWorkspace)} workspace(s) open";
        if (workspace?.Snapshot.Warnings.Count > 0) WorkspaceCount.Text += $" · {workspace.Snapshot.Warnings.Count} unavailable item(s)";
        ToolTip.SetTip(WorkspaceCount, workspace is null ? null : string.Join(Environment.NewLine, workspace.Notices));
        ToolTip.SetTip(WorkspaceLocation, workspace?.Root);
        ToolTip.SetTip(WorkspaceTitle, workspace?.Root);
        Avalonia.Controls.ToolTip.SetTip(SidebarTitle, workspace?.Root);
        UpdateBadges();
        RefreshSavedSearches();
        ShowNav(_nav);
        UpdateChrome();
        RefreshDocumentWorkspaceContext();
    }

    private void BuildTree()
    {
        if (_shell.OpenWorkspaces.Count == 0)
        {
            _treeRoots.Clear();
            RefreshTreePresentation();
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
        var previous = _treeRoots.ToDictionary(row => row.FullPath, WorkspacePaths.Comparer);
        var roots = top.OrderByDescending(w => w.IsNamedWorkspace)
            .Select(w => TreeRow.Reconcile(previous.GetValueOrDefault(w.Root), w.Snapshot.Tree,
                workspaceRoot: w.IsNamedWorkspace, namedRoots, rootSection: true)).ToArray();
        TreeRow.Synchronize(_treeRoots, roots);
        foreach (var row in _treeRoots.SelectMany(r => r.Flatten())) row.IsExpanded = expanded.TryGetValue(row.FullPath, out var value) ? value : row.IsWorkspace || top.Any(w => WorkspacePaths.PathsEqual(w.Root, row.FullPath));
        foreach (var row in _treeRoots.SelectMany(r => r.Flatten()).Where(r => r.IsSection))
        {
            var owner = _shell.OpenWorkspaces.FirstOrDefault(w => WorkspacePaths.PathsEqual(w.Root, row.FullPath));
            row.IsSectionOpen = owner is not null;
            row.IsSelected = ReferenceEquals(owner, _shell.Workspace);
            if (owner?.UpdatesPausedReason is { } paused) row.SectionNotice = paused + " · Use Rescan in the banner to resume.";
            else if (owner?.Snapshot.Warnings.Count > 0) row.SectionNotice = $"{owner.Snapshot.Warnings.Count} unavailable item(s). " + string.Join(" ", owner.Notices);
            else if (owner is null && row.IsWorkspace) row.SectionNotice = "Workspace not open. Select its name to open it.";
            if (owner is not null) UpdateContentStatus(row, owner);
            if (owner?.IsPrivate == true) row.SectionNotice = "Private · no History or recovery copies · search stays in memory" +
                (row.SectionNotice.Length > 0 ? " · " + row.SectionNotice : "");
        }
        static void MarkPromotable(TreeRow row, bool insideWorkspace)
        {
            insideWorkspace |= row.IsWorkspace;
            row.CanPromote = row.IsDirectory && !insideWorkspace;
            foreach (var child in row.Children) MarkPromotable(child, insideWorkspace);
        }
        foreach (var root in _treeRoots) MarkPromotable(root, false);
        RefreshTreePresentation();
        FileTree.ItemsSource = _treeRoots;
        RefreshTreeDirtyState();
        RefreshGitMarkers();
    }

    private void RefreshTreePresentation()
    {
        var directories = _treeRoots.SelectMany(row => row.Flatten()).Where(row => row.IsDirectory).ToHashSet();
        foreach (var removed in _observedTreeDirectories.Except(directories).ToArray())
        {
            removed.PropertyChanged -= OnTreeDirectoryChanged;
            _observedTreeDirectories.Remove(removed);
        }
        foreach (var directory in directories)
            if (_observedTreeDirectories.Add(directory)) directory.PropertyChanged += OnTreeDirectoryChanged;
        QueueTreePresentation();
    }

    private void OnTreeDirectoryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TreeRow.IsExpanded)) QueueTreePresentation();
    }

    private void QueueTreePresentation()
    {
        if (_windowClosed || _treePresentationPending || !_observedTreeDirectories.Any(row => row.IsLoadingChildren)) return;
        _treePresentationPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _treePresentationPending = false;
            if (_windowClosed) return;
            // One bounded realization batch per dispatcher turn lets input outrank large trees.
            _observedTreeDirectories.FirstOrDefault(row => row.IsLoadingChildren)?.PresentNextChildren();
            QueueTreePresentation();
        }, DispatcherPriority.Background);
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
        var changed = _shell.Tabs.Where(t => t.IsChangedOnDisk && t.FilePath is not null).Select(t => t.FilePath!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _treeRoots.SelectMany(r => r.Flatten()))
        {
            row.IsDirty = !row.IsDirectory && dirty.Contains(row.FullPath);
            row.IsChangedOnDisk = !row.IsDirectory && changed.Contains(row.FullPath);
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

    private async Task ScanTasksAsync(CancellationToken cancellationToken = default)
    {
        var generation = ++_taskScanGeneration;
        var workspace = _shell.Workspace;
        if (workspace is null)
        {
            _tasksOpen = 0;
            _tasks = [];
            return;
        }

        var buffers = _shell.Tabs.Where(t => t.FilePath is not null).GroupBy(t => t.FilePath!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(t => ReferenceEquals(t, _shell.ActiveTab)).ThenByDescending(t => t.IsDirty).First().CurrentText, StringComparer.OrdinalIgnoreCase);
        var files = workspace.Snapshot.Files;
        var profile = _shell.Settings.DefaultProfile;
        var entries = await Task.Run(() => TaskIndex.ScanAsync(files, profile, buffers, cancellationToken), cancellationToken);
        if (generation == _taskScanGeneration && ReferenceEquals(workspace, _shell.Workspace) && _shell.OpenWorkspaces.Contains(workspace))
        {
            _tasks = entries;
            _tasksOpen = TaskIndex.OpenCount(entries);
            UpdateBadges();
        }
    }

    private IReadOnlyList<TaskEntry> _tasks = [];

    private void OnTaskFilterChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_nav == "Tasks" && SideList is not null) SideList.ItemsSource = RowsFor("Tasks");
    }

    internal async Task ShowNavAsync(string nav)
    {
        if (nav == "Tasks")
        {
            await ScanTasksAsync();
        }
        if (nav == "Mentions")
        {
            await ShowUnlinkedMentionsAsync();
            return;
        }

        ShowNav(nav);
    }

    private void ShowNav(string nav)
    {
        if (nav != "Mentions") _mentionCancellation?.Cancel();
        _nav = nav;
        TaskFilterBar.IsVisible = nav == "Tasks";
        TaskScopeCount.Text = $"{_shell.Workspace?.Root ?? "No workspace"} · {_tasks.Count(t => !t.Checked)} open · {_tasks.Count(t => t.Checked)} completed";
        NavRecent.IsChecked = nav == "Recent";
        NavSearch.IsChecked = nav == "Search";
        NavTags.IsChecked = nav == "Tags";
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
                return FileReferenceRows(includeWorkspaces: true).Select(row => row with { IsRecentEntry = true }).ToArray();
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
                return TaskRows();
            case "Tags":
                return workspace is null ? [] : KnowledgeNavigation.Tags(workspace.Graph)
                    .Select(tag => new SideRow("#" + tag.Name, $"{tag.Documents} document(s)", null, 0)
                    { SearchQuery = "tag:\"" + tag.Name.Replace('"', ' ') + "\"" }).ToArray();
            case "Graph":
                return GraphRows();
            case "Mentions":
                return _mentionRows;
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
            path, 0, IsMissing: !exists, CanRemoveReference: true)
        { IsWorkspace = true };
    }

    private async Task OpenRecentWorkspaceAsync(string path)
    {
        if (!await OpenThenShowFilesAsync(treeReady => _shell.OpenWorkspaceAsync(path, treeReady: treeReady)))
        {
            _shell.StatusMessage = "Could not open workspace: " + path + ". Check that the folder is available, then retry. Its Recent entry was kept.";
        }
    }

    private IReadOnlyList<SideRow> OutsideLibraryRows()
    {
        var references = FileReferenceRows().Where(r => r.IsOnline || r.Path is { } path && _shell.WorkspaceForPath(path) is null).ToList();
        var localPaths = references.Where(row => !row.IsOnline && row.Path is not null)
            .Select(row => WorkspacePaths.Normalize(row.Path!)).ToHashSet(WorkspacePaths.Comparer);
        foreach (var tab in _shell.Tabs.Where(t => t.FilePath is { } path && _shell.WorkspaceForPath(path) is null))
        {
            if (!localPaths.Add(WorkspacePaths.Normalize(tab.FilePath!))) continue;
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
        static object Key(SideRow row) => row.Path is { } path
            ? (row.IsOnline ? RecentKind.Url : RecentKind.File,
                row.IsOnline ? RecentItemsStore.NormalizeUrl(path) : WorkspacePaths.Normalize(path).ToUpperInvariant())
            : (object)row.OpenTab!;
        var pending = outside.ToDictionary(Key);
        var updated = new List<SideRow>(outside.Count);
        // Library positions belong to the displayed references, not their changing Recent timestamps.
        foreach (var previous in _outsideLibraryRows)
        {
            if (!pending.Remove(Key(previous), out var current)) continue;
            if (current != previous && !current.IsOnline && current.Path is not null && previous.Path is not null)
            {
                current = current with
                {
                    Path = previous.Path,
                    Title = string.Equals(current.Title, previous.Title, StringComparison.OrdinalIgnoreCase) ? previous.Title : current.Title,
                    Detail = string.Equals(current.Detail, previous.Detail, StringComparison.OrdinalIgnoreCase) ? previous.Detail : current.Detail
                };
            }
            updated.Add(current == previous ? previous : current);
        }
        updated.AddRange(outside.Where(row => pending.Remove(Key(row))));
        var pinned = _shell.Recent.Files.Where(entry => entry.Pinned)
            .Select(entry => Key(new SideRow("", "", entry.Path, 0)))
            .Concat(_shell.Recent.Urls.Where(entry => entry.Pinned)
                .Select(entry => Key(new SideRow("", "", entry.Path, 0, IsOnline: true)))).ToHashSet();
        TreeRow.Synchronize(_outsideLibraryRows, updated.OrderByDescending(row => pinned.Contains(Key(row))).ToArray());
        SideList.IsVisible = outside.Count > 0;
        if (!ReferenceEquals(SideList.ItemsSource, _outsideLibraryRows)) SideList.ItemsSource = _outsideLibraryRows;
        OutsideLibraryHeader.IsVisible = outside.Count > 0;
        NoWorkspaceText.IsVisible = _shell.OpenWorkspaces.Count == 0 && outside.Count == 0;
    }

    private void OnRecentRenamed(object? sender, RecentRename rename) => Dispatcher.UIThread.Post(() =>
    {
        if (_windowClosed || rename.Kind != RecentKind.File
            || !WorkspacePaths.PathsEqual(rename.OldPath, rename.NewPath)) return;
        for (var index = 0; index < _outsideLibraryRows.Count; index++)
        {
            var row = _outsideLibraryRows[index];
            if (row.IsOnline || row.Path is not { } path || !WorkspacePaths.PathsEqual(path, rename.OldPath)) continue;
            _outsideLibraryRows[index] = row with
            {
                Path = rename.NewPath,
                Title = Path.GetFileName(rename.NewPath),
                Detail = Path.GetDirectoryName(rename.NewPath) ?? string.Empty
            };
        }
        if (_nav == "Files") RefreshOutsideLibrary();
    });

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
        await OpenThenShowFilesAsync(treeReady => _shell.OpenDirectoryAsync(folder, treeReady));
    }


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
        if (SideList.SelectedItem is SideRow { SearchQuery: { } query })
        { SideList.SelectedItem = null; _lastSearch = query; ShowWorkspaceSearch(); return; }
        if (SideList.SelectedItem is SideRow { OpenTab: { } tab })
        { _shell.SelectTab(tab, Tern.App.Shell.TabActivationReason.User); SideList.SelectedItem = null; return; }
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
            GoToLine(row.Line, 1, focusEditor: false);
            Preview.ScrollToAnchor(row.Anchor);
        }
    }

    private void OnOutlineSearchChanged(object? sender, TextChangedEventArgs e) => RefreshOutline();

    private void RefreshOutline()
    {
        var query = OutlineSearchBox.Text?.Trim() ?? string.Empty;
        var rows = _outline.Where(item => item.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(item => new OutlineRow(item.Text, new Thickness(Math.Max(0, item.Level - 1) * 8, 2, 0, 2), item.Line, item.Anchor, item.Level))
            .ToList();
        OutlineList.ItemsSource = rows;
        this.FindControl<TextBlock>("OutlineCount")!.Text = query.Length == 0 ? $"{rows.Count} headings" : $"{rows.Count} of {_outline.Count} headings";
        OutlineEmpty.IsVisible = rows.Count == 0;
        OutlineEmpty.Text = _outline.Count == 0 ? "Add # headings in Source to create an outline." : "No headings match your search.";
    }

    private void OnLinkListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        RepairLinkButton.IsEnabled = false;
        LinkDetails.IsVisible = LinkList.SelectedItem is LinkRowView;
        if (LinkList.SelectedItem is LinkRowView row)
        {
            LinkDetails.Text = $"{row.Status}\nSource: {_shown?.FilePath ?? "Unsaved note"}, line {row.Line}\nDestination: {row.Destination}\n" + (row.IsBroken ? "The workspace could not resolve this file or heading. Choose the correct target below." : row.IsExternal ? "External target; opening is a separate action." : "Local target or heading; checked within the workspace when available.");
            GoToLine(row.Line, 1, focusEditor: false);
            RepairLinkButton.IsEnabled = row.IsBroken && _shown is { IsReadOnly: false, FilePath: { } path }
                && _shell.WorkspaceForPath(path) is not null;
        }
    }

    private void OnLinksSearchChanged(object? sender, TextChangedEventArgs e) => RefreshLinks();

    private void OnTagSearchClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: string tag }) return;
        if (_shown?.FilePath is { } path && _shell.WorkspaceForPath(path) is { } owner) _shell.SelectWorkspace(owner);
        _lastSearch = "tag:\"" + tag.Replace('"', ' ') + "\"";
        ShowWorkspaceSearch();
    }

    private async void OnInspectorBacklinkSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (InspectorBacklinks.SelectedItem is SideRow { Path: { } path } row)
            await OpenDocumentPathAsync(path, Math.Max(1, row.Line));
    }

    private void RefreshLinks()
    {
        var query = this.FindControl<TextBox>("LinksSearchBox")!.Text?.Trim() ?? string.Empty;
        var rows = _documentLinks.Where(row => row.Text.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Destination.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        RepairLinkButton.IsEnabled = false;
        LinkDetails.IsVisible = false;
        LinkList.SelectedItem = null;
        LinkList.ItemsSource = rows;
        this.FindControl<TextBlock>("LinksCount")!.Text = query.Length == 0 ? $"{rows.Length} links" : $"{rows.Length} of {_documentLinks.Count} links";
        LinksEmpty.IsVisible = rows.Length == 0;
        LinksEmpty.Text = _documentLinks.Count == 0 ? "No links found. Insert a link from the source toolbar." : "No links match your search.";
    }

    private void RefreshPanels()
    {
        RefreshHeadingFolds();
        RefreshHybridSource();
        RepairLinkButton.IsEnabled = false;
        var owner = _shown?.FilePath is { } path ? _shell.WorkspaceForPath(path) : _shell.Workspace;
        RelatedNotesButton.IsVisible = owner is not null && _shown is { IsReadOnly: false, FilePath: not null };
        var panel = DocumentPanel.Build(_shown, owner);
        _outline = panel.Outline;
        RefreshOutline();
        TagsEmpty.IsVisible = panel.Tags.Count == 0;
        EditTagsButton.IsEnabled = _shown is { IsReadOnly: false };
        RepairLinkButton.IsVisible = panel.Links.Any(link => link.IsBroken);
        _documentLinks = panel.Links.Select(l => new LinkRowView(l.Label, l.Destination, l.Line, l.IsBroken, l.IsExternal)).ToArray();
        RefreshLinks();
        var backlinks = owner is not null && _shown?.FilePath is { } current
            ? owner.Graph.GetBacklinks(current).Select(link => new SideRow(Path.GetFileName(link.SourcePath),
                $"line {link.Line}: {link.Context}", link.SourcePath, link.Line)).ToArray() : [];
        InspectorBacklinks.ItemsSource = backlinks;
        InspectorBacklinksTitle.Text = $"Linked from ({backlinks.Length})";
        InspectorBacklinksEmpty.IsVisible = backlinks.Length == 0;
        LinksSummary.Text = panel.LinksChecked == 0 ? string.Empty : panel.LinksBroken == 0 ? $"{panel.LinksChecked} checked" : $"{panel.LinksChecked} checked · {panel.LinksBroken} broken";
        InfoWords.Text = panel.Info.Words.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        InfoSessionWords.Text = _shown is null ? "–" : _shown.WordsWrittenThisSession.ToString("+0;-0;0", System.Globalization.CultureInfo.CurrentCulture);
        var target = _shown?.Analysis?.FrontMatter is { } frontMatter ? FrontMatterReader.Read(frontMatter.Yaml).TargetWords : null;
        InfoWordTarget.Text = target is { } goal ? $"{panel.Info.Words:N0} / {goal:N0}" : "Not set";
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
        ContextGuidance.Message = tab is null ? "Open a note or choose File → New document to start writing."
            : tab.IsReadOnly ? "This note is read-only. Use Save As to create an editable copy, or Export as to share it."
            : _shell.ViewMode == Tern.App.Settings.ViewMode.Reading ? "Use Outline or Find to move through this note. Choose Source or Split to edit it."
            : tab.IsDirty ? "Review your changes in Reading or Split, then choose File → Save to keep them."
            : "Write in Source, check the result in Reading, or use Find to locate text. Export as creates a separate sharing copy.";
        UpdateCurrentSection();
        UpdateProtection(tab);
        UpdateFirstNoteChecklist();
        RefreshAuthoringState();
        StatusEncoding.IsVisible = StatusLineEnding.IsVisible = StatusProfile.IsVisible = tab is not null;
        if (tab is null)
        {
            StatusPath.Text = string.Empty;
            StatusPosition.Text = string.Empty;
            Avalonia.Automation.AutomationProperties.SetName(StatusPosition, "Cursor position");
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
        Avalonia.Automation.AutomationProperties.SetName(StatusPosition, CurrentSourceLineAnnouncement());
        StatusWords.Text = Editor.SelectionLength > 0
            ? $"{Tern.Markdown.MarkdownAnalyzer.CountWords(Editor.SelectedText):N0} selected · {tab.Analysis?.WordCount ?? 0:N0} words"
            : $"{tab.Analysis?.WordCount ?? 0:N0} words";
        StatusEncoding.Content = "Encoding: " + tab.EncodingText;
        StatusLineEnding.Content = "Line endings: " + tab.LineEndingText;
        StatusProfile.Content = tab.Profile.DisplayName();
        UpdateResponsiveStatus();
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

        var suggested = reopen && tab.IsReadOnly && tab.CurrentText.Contains('\0')
            ? Tern.Domain.Text.TextEncodingKind.Utf16LittleEndian : tab.Session.Format.Encoding;
        var options = Tern.Filesystem.TextFileCodec.AvailableEncodings
            .OrderByDescending(o => o.Kind == suggested && o.HasBom == tab.Session.Format.HasBom)
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
                { _shell.StatusMessage = "The file could not be reopened: " + _shell.PlainProblem(ex); }
            }

            UpdateStatus();
        });
    }

    private void OnProfileClick(object? sender, RoutedEventArgs e)
    {
        if (_shown is not { } tab) return;
        ShowPicker("Markdown profile for this document", string.Empty,
            _ => [.. Enum.GetValues<Tern.Markdown.MarkdownProfile>().Select(profile => new PickerItem(profile.DisplayName(), "Changes rendering only; source stays unchanged", string.Empty, profile))],
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

        var menu = new ContextMenu();
        foreach (var (ending, label) in new[] { (LineEnding.CrLf, "CRLF (Windows)"), (LineEnding.Lf, "LF (Unix / macOS)"), (LineEnding.Cr, "CR (classic Mac)") })
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) =>
            {
                if (ReferenceEquals(tab, _shown) && CanRunCommand(Tern.App.Commands.CommandIds.ChangeLineEnding)) tab.ConvertLineEndings(ending);
                UpdateStatus();
            };
            menu.Items.Add(item);
        }
        StatusLineEnding.ContextMenu = menu;
        menu.Open(StatusLineEnding);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (_treeDragPath is not null) return;
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
                problems.Add(Path.GetFileName(path) + ": " + _shell.PlainProblem(ex));
            }
        }
        if (problems.Count > 0) _shell.StatusMessage = string.Join(" · ", problems);
        else if (paths.Count == 0) _shell.StatusMessage = "No local files were dropped. Save the item locally, then try again.";
    }

    private static bool IsMarkdownPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown" or ".mdown" or ".txt";

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (_treeDragPath is not null || !e.DataTransfer.Contains(DataFormat.File)) return;
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }
}
