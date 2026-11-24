using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Tern.App.Documents;
using Tern.App.Panels;
using Tern.App.Shell;
using Tern.Desktop.ViewModels;
using Tern.Domain.Text;
using Tern.Markdown;
using Tern.Workspaces;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    internal bool TryShowWikiCompletion()
    {
        if (_windowClosed || IsOverlayOpen || _shell.IsBusy || _shown is not { IsReadOnly: false, FilePath: { } path } tab
            || _shell.WorkspaceForPath(path) is not { } workspace || Editor.SelectionLength != 0 || Editor.CaretOffset < 2
            || Editor.Document.GetText(Editor.CaretOffset - 2, 2) != "[[") return false;
        var revision = tab.Session.Revision;
        var start = Editor.CaretOffset - 2;
        var candidates = KnowledgeNavigation.WikiLinks(workspace.Graph, path);
        ShowPicker("Link to a workspace note or #heading", "", query => candidates
            .Where(item => item.Label.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Destination.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(60).Select(item => new PickerItem(item.Label, item.Destination, "Wiki link", item)).ToArray(),
            item =>
            {
                HideOverlay();
                if (item.Payload is not WikiLinkCandidate candidate) return;
                if (_windowClosed || !ReferenceEquals(tab, _shown) || !tab.Session.IsCurrent(revision) || tab.IsReadOnly || _shell.IsBusy
                    || Editor.CaretOffset != start + 2 || Editor.SelectionLength != 0
                    || tab.FilePath is not { } currentPath || !WorkspacePaths.PathsEqual(currentPath, path)
                    || !ReferenceEquals(workspace, _shell.WorkspaceForPath(path))
                    || workspace.Graph.Resolve(path, new LinkInfo(LinkKind.WikiLink, candidate.Destination, default, 1, candidate.Label)).Status != LinkStatus.Ok)
                { _shell.SetStatus("The note or workspace changed. Type [[ again to choose a current target.", StatusSeverity.Info); return; }
                var wiki = "[[" + candidate.Destination + "]]";
                ApplyEdit(new EditResult([new TextEdit(start, 2, wiki)], start + wiki.Length, 0));
            }, "No matching notes. Wait for workspace preparation or change the search.");
        return true;
    }

    private void RefreshSavedSearches()
    {
        SavedSearchList.Children.Clear();
        if (_shell.Workspace is not { } workspace) return;
        var entry = _shell.Settings.SavedWorkspaceSearches.FirstOrDefault(pair => WorkspacePaths.Comparer.Equals(pair.Key, workspace.Root));
        foreach (var query in (entry.Value ?? []).Where(query => !string.IsNullOrWhiteSpace(query)))
        {
            var row = new Grid { ColumnDefinitions = new("*,Auto") };
            var open = new Button { Content = new TextBlock { Text = query, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis },
                Classes = { "flat" }, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            AutomationProperties.SetName(open, "Run saved search " + query);
            ToolTip.SetTip(open, query);
            open.Click += (_, _) => { if (!ReferenceEquals(workspace, _shell.Workspace)) return; _lastSearch = query; ShowWorkspaceSearch(); };
            var remove = new Button { Content = new VectorIcon { Kind = "Close" }, Classes = { "flat", "icon-button" } };
            AutomationProperties.SetName(remove, "Forget saved search " + query);
            ToolTip.SetTip(remove, "Forget this search; keep all notes");
            remove.Click += (_, _) =>
            {
                if (entry.Value is null) return;
                var index = entry.Value.IndexOf(query);
                if (index < 0) return;
                entry.Value.RemoveAt(index);
                if (!_shell.SaveSettings()) entry.Value.Insert(index, query);
                RefreshSavedSearches();
            };
            row.Children.Add(open); row.Children.Add(remove); Grid.SetColumn(remove, 1);
            SavedSearchList.Children.Add(row);
        }
    }

    internal bool SaveWorkspaceSearch(string query)
    {
        if (_shell.Workspace is not { } workspace || string.IsNullOrWhiteSpace(query) || query.Length > 4096)
        { _shell.SetStatus("Enter a workspace search of 1 to 4,096 characters before saving.", StatusSeverity.Info); return false; }
        var settings = _shell.Settings.SavedWorkspaceSearches;
        var key = settings.Keys.FirstOrDefault(root => WorkspacePaths.Comparer.Equals(root, workspace.Root)) ?? workspace.Root;
        if (!settings.TryGetValue(key, out var saved) || saved is null) settings[key] = saved = [];
        if (saved.Contains(query, StringComparer.Ordinal)) { _shell.SetStatus("This search is already in Library Shortcuts.", StatusSeverity.Info); return true; }
        saved.Add(query);
        if (!_shell.SaveSettings()) { saved.Remove(query); return false; }
        RefreshSavedSearches();
        _shell.SetStatus("Search saved in Library Shortcuts for this workspace.", StatusSeverity.Success);
        return true;
    }

    private IReadOnlyList<SideRow> TaskRows()
    {
        var path = TaskPathFilter.Text?.Trim().Replace('\\', '/') ?? "";
        var tag = TaskTagFilter.Text?.Trim().TrimStart('#') ?? "";
        var tasks = _tasks.Where(task => (TaskFilter.SelectedIndex == 2 || task.Checked == (TaskFilter.SelectedIndex == 1))
            && task.RelativePath.Contains(path, StringComparison.OrdinalIgnoreCase)
            && (tag.Length == 0 || task.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)));
        var rows = new List<SideRow>();
        foreach (var group in tasks.GroupBy(task => task.RelativePath).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (TaskGroupByFile.IsChecked == true) rows.Add(new SideRow(group.Key, "", null, 0) { IsGroup = true });
            rows.AddRange(group.Select(task => new SideRow(task.Text, $"{(task.Checked ? "Completed" : "Open")} - {task.RelativePath}:{task.Line}", task.Path, task.Line) { Task = task }));
        }
        return rows;
    }

    private void OnTaskQueryChanged(object? sender, TextChangedEventArgs e) => RefreshTaskRows();
    private void OnTaskGroupChanged(object? sender, RoutedEventArgs e)
    {
        _shell.Settings.TaskGroupByFile = TaskGroupByFile.IsChecked == true;
        _shell.SaveSettings();
        RefreshTaskRows();
    }
    private void RefreshTaskRows()
    {
        if (_nav != "Tasks" || SideList is null) return;
        SideList.ItemsSource = TaskRows();
        TaskScopeCount.Text = $"{_shell.Workspace?.Root ?? "No workspace"} - {_tasks.Count(task => !task.Checked)} open - {_tasks.Count(task => task.Checked)} completed";
    }

    private CancellationTokenSource? _taskRefreshCancellation;
    private void ScheduleTaskRefresh()
    {
        if (_windowClosed || _nav != "Tasks") return;
        _taskRefreshCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _taskRefreshCancellation = cancellation;
        _ = RefreshAsync();
        async Task RefreshAsync()
        {
            try
            {
                await Task.Delay(400, cancellation.Token);
                await ScanTasksAsync(cancellation.Token);
                if (!_windowClosed && !cancellation.IsCancellationRequested) RefreshTaskRows();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex) { if (!_windowClosed) _shell.ReportUnexpectedError("Refresh workspace tasks", ex); }
            finally
            {
                if (ReferenceEquals(_taskRefreshCancellation, cancellation)) _taskRefreshCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private async void OnTaskToggleClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not CheckBox { DataContext: SideRow { Task: { } task } } checkbox) return;
        checkbox.IsEnabled = false;
        try { await ToggleWorkspaceTaskAsync(task); }
        catch (Exception ex) { _shell.ReportUnexpectedError("Toggle workspace task", ex); }
        finally { checkbox.IsEnabled = true; checkbox.IsChecked = checkbox.DataContext is SideRow row && row.TaskChecked; }
    }

    internal async Task<bool> ToggleWorkspaceTaskAsync(TaskEntry entry)
    {
        var workspace = _shell.Workspace;
        if (_windowClosed || _shell.IsBusy || workspace is null || !workspace.IsInside(entry.Path)
            || !workspace.Snapshot.Files.Any(file => WorkspacePaths.PathsEqual(file.FullPath, entry.Path)))
        { _shell.SetStatus("The workspace changed. Refresh Tasks and try again.", StatusSeverity.Warning); return false; }
        var tab = _shell.Tabs.Where(tab => tab.FilePath is { } path && WorkspacePaths.PathsEqual(path, entry.Path))
            .OrderByDescending(tab => ReferenceEquals(tab, _shell.ActiveTab)).ThenByDescending(tab => tab.IsDirty).FirstOrDefault()
            ?? await _shell.OpenFileAsync(entry.Path, activate: false, recordRecent: false);
        if (_windowClosed || !ReferenceEquals(workspace, _shell.Workspace) || tab is null || tab.IsReadOnly || _shell.IsBusy || !_shell.Tabs.Contains(tab))
        { _shell.SetStatus("The task needs a writable document in the current workspace.", StatusSeverity.Warning); return false; }
        var snapshot = tab.Session.GetSnapshot();
        var profile = tab.Profile;
        var analysis = await Task.Run(() => MarkdownAnalyzer.Analyze(snapshot.Text, profile));
        var task = analysis.Tasks.FirstOrDefault(task => task.Line == entry.Line && task.Checked == entry.Checked && task.Text == entry.Text);
        var edit = task is null ? null : TaskEdits.ToggleOnLine(snapshot.Text, analysis, entry.Line);
        if (_windowClosed || !ReferenceEquals(workspace, _shell.Workspace) || !tab.Session.IsCurrent(snapshot.Revision) || tab.Profile != profile
            || tab.IsReadOnly || _shell.IsBusy || !_shell.Tabs.Contains(tab) || tab.FilePath is not { } currentPath
            || !WorkspacePaths.PathsEqual(currentPath, entry.Path) || edit is null)
        { _shell.SetStatus("This task changed or cannot be edited. Refresh Tasks and try again.", StatusSeverity.Warning); return false; }
        tab.ApplyEdits([edit.Value]);
        await ScanTasksAsync();
        RefreshTaskRows();
        _shell.SetStatus("Task updated in " + tab.DisplayName + ". Save to keep it; Undo in that document to revert.", StatusSeverity.Info);
        return true;
    }

    private readonly Dictionary<DocumentTab, (string Text, string Signature, int Count)> _headingBaselines = [];
    private readonly Dictionary<DocumentTab, CancellationTokenSource> _headingRepairs = [];
    internal Func<RepairPlan, Task<bool>>? RepairReview { get; set; }

    private void ScheduleHeadingRepair(DocumentTab tab)
    {
        if (_windowClosed || tab.Analysis is not { } analysis || tab.AnalysisRevision != tab.Session.Revision
            || tab.FilePath is not { } path || _shell.WorkspaceForPath(path) is not { } workspace) return;
        if (_headingRepairs.Remove(tab, out var pending)) pending.Cancel();
        var signature = string.Join("\n", analysis.Headings.Select(heading => heading.Anchor + "\t" + heading.Text));
        var text = tab.CurrentText;
        if (!_headingBaselines.TryGetValue(tab, out var baseline) || baseline.Signature == signature || baseline.Count != analysis.Headings.Count)
        { _headingBaselines[tab] = (text, signature, analysis.Headings.Count); return; }
        var revision = tab.Session.Revision;
        var cancellation = new CancellationTokenSource();
        _headingRepairs[tab] = cancellation;
        _ = PlanAsync();
        async Task PlanAsync()
        {
            try
            {
                await Task.Delay(1000, cancellation.Token);
                var plan = await Task.Run(() => workspace.CreateLinkRepairPlanner().PlanHeadingRenameAsync(path, baseline.Text, text, cancellation.Token), cancellation.Token);
                if (_windowClosed || cancellation.IsCancellationRequested || !_shell.Tabs.Contains(tab) || !tab.Session.IsCurrent(revision)
                    || tab.FilePath is not { } currentPath || !WorkspacePaths.PathsEqual(currentPath, path)
                    || !ReferenceEquals(workspace, _shell.WorkspaceForPath(path))) return;
                if (plan.TotalEdits == 0)
                {
                    _headingBaselines[tab] = (text, signature, analysis.Headings.Count);
                    if (plan.Warnings.Count > 0) _shell.SetStatus(string.Join(" ", plan.Warnings), StatusSeverity.Warning);
                    return;
                }
                _shell.ShowBanner($"You renamed a heading. {plan.TotalEdits} link(s) to it may need updating.", StatusSeverity.Info,
                    new BannerAction("Review anchor updates", async () =>
                    {
                        bool CurrentTarget() => !_windowClosed && _shell.Tabs.Contains(tab) && tab.Session.IsCurrent(revision)
                            && tab.FilePath is { } current && WorkspacePaths.PathsEqual(current, path)
                            && ReferenceEquals(workspace, _shell.WorkspaceForPath(path));
                        if (!CurrentTarget())
                        { _shell.SetStatus("The heading changed again. Check the newest suggestion.", StatusSeverity.Warning); return; }
                        try { await ReviewWorkspaceEditsAsync(workspace, plan, isCurrent: CurrentTarget); }
                        catch (Exception ex) { if (!_windowClosed) _shell.ReportUnexpectedError("Review heading link updates", ex); }
                        if (CurrentTarget())
                            _headingBaselines[tab] = (text, signature, analysis.Headings.Count);
                    }));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex) { if (!_windowClosed) _shell.ReportUnexpectedError("Plan heading link updates", ex); }
            finally
            {
                if (_headingRepairs.TryGetValue(tab, out var current) && ReferenceEquals(current, cancellation)) _headingRepairs.Remove(tab);
                cancellation.Dispose();
            }
        }
    }

    private async Task ReviewWorkspaceEditsAsync(WorkspaceSession workspace, RepairPlan plan, bool replacement = false, Func<bool>? isCurrent = null)
    {
        var accepted = RepairReview is { } review ? await review(plan)
            : await new RenameLinksWindow(plan, replacement ? "workspace replacements" : "heading link updates").ShowDialog<bool>(this);
        if (!accepted) return;
        if (_windowClosed || !_shell.OpenWorkspaces.Contains(workspace) || isCurrent?.Invoke() == false)
        { _shell.SetStatus("The workspace or note changed, so nothing was updated. Try again.", StatusSeverity.Warning); return; }
        var result = await _shell.ApplyRenameRepairsAsync(plan, replacement ? "workspace replacement" : "link update");
        if (!result.Succeeded)
            _shell.SetStatus("Some reviewed edits were not applied: " + string.Join(" ", result.Files
                .Where(file => file.Status is RepairStatus.Conflict or RepairStatus.Failed).Select(file => file.Repair.Path + ": " + file.Message)), StatusSeverity.Warning);
    }

    private CancellationTokenSource? _mentionCancellation;
    private IReadOnlyList<Tern.Desktop.ViewModels.SideRow> _mentionRows = [];

    private async void OnKnowledgeNavClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string nav })
        {
            try { await ShowNavAsync(nav); }
            catch (Exception ex) { _shell.ReportUnexpectedError("Explore workspace links", ex); }
        }
    }

    private async Task ShowUnlinkedMentionsAsync()
    {
        _mentionCancellation?.Cancel();
        _mentionRows = [];
        ShowNav("Mentions");
        if (_shown is not { FilePath: { } path } tab || _shell.WorkspaceForPath(path) is not { } workspace)
        { _shell.SetStatus("Open a saved workspace note to find unlinked mentions.", StatusSeverity.Info); return; }
        var cancellation = new CancellationTokenSource();
        _mentionCancellation = cancellation;
        using var operation = _shell.BeginStatusOperation("Finding unlinked title and alias mentions", path, cancellation.Cancel);
        try
        {
            var mentions = await Task.Run(() => workspace.FindUnlinkedMentionsAsync(path, cancellation.Token), cancellation.Token);
            if (_windowClosed || cancellation.IsCancellationRequested || _nav != "Mentions" || !ReferenceEquals(tab, _shown)
                || !ReferenceEquals(workspace, _shell.WorkspaceForPath(path))) return;
            _mentionRows = mentions.Select(mention => new Tern.Desktop.ViewModels.SideRow(Path.GetFileName(mention.Path),
                $"line {mention.Line}: {mention.Context}", mention.Path, mention.Line)).ToArray();
            SideList.ItemsSource = _mentionRows;
            _shell.SetStatus($"{mentions.Count} unlinked document mention(s). Linked documents are excluded; open a result to review its wording.", StatusSeverity.Info);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { operation.Fail(ex.Message); if (!_windowClosed) _shell.SetStatus("Unlinked mentions could not be read: " + Tern.App.Shell.UserProblem.Plain(ex), StatusSeverity.Warning); }
        finally
        {
            if (ReferenceEquals(_mentionCancellation, cancellation)) _mentionCancellation = null;
            cancellation.Dispose();
        }
    }

    private IReadOnlyList<Tern.Desktop.ViewModels.SideRow> GraphRows()
    {
        if (_shown is not { FilePath: { } path } || _shell.WorkspaceForPath(path) is not { } workspace) return [];
        var incoming = workspace.Graph.GetBacklinks(path).Select(link => link.SourcePath).ToHashSet(WorkspacePaths.Comparer);
        var outgoing = workspace.Graph.GetOutgoing(path).Where(link => link.Status is LinkStatus.Ok or LinkStatus.MissingAnchor && link.TargetPath is not null)
            .Select(link => link.TargetPath!).Where(workspace.Graph.Contains).ToHashSet(WorkspacePaths.Comparer);
        var neighbours = incoming.Union(outgoing, WorkspacePaths.Comparer).Where(neighbour => !WorkspacePaths.PathsEqual(neighbour, path))
            .Order(WorkspacePaths.Comparer).Select(neighbour => new Tern.Desktop.ViewModels.SideRow(
                workspace.Graph.GetDocument(neighbour)?.Title ?? Path.GetFileName(neighbour),
                (incoming.Contains(neighbour) && outgoing.Contains(neighbour) ? "Linked both ways with "
                    : incoming.Contains(neighbour) ? "Links to " : "Linked from ") + Path.GetFileName(path), neighbour, 1)).ToArray();
        return [new SideRow("Local graph: " + (workspace.Graph.GetDocument(path)?.Title ?? Path.GetFileName(path)),
            neighbours.Length == 0 ? "No linked workspace neighbours." : "One-hop incoming and outgoing workspace neighbours", null, 0) { IsGroup = true }, .. neighbours];
    }
}
