using Avalonia;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Tern.Workspaces;

namespace Tern.Desktop.ViewModels;

public sealed record OutlineRow(string Text, Thickness Indent, int Line, string Anchor, int Level)
{
    public string LevelLabel => $"H{Level}";
    public override string ToString() => Text;
}

public sealed record LinkRowView(string Text, string Destination, int Line, bool IsBroken, bool IsExternal)
{
    public string Status => IsBroken ? "Broken" : IsExternal ? "External" : "Internal";
}

/// <summary>One line in the sidebar list panels (recent files, search hits, backlinks, tasks...).</summary>
public sealed record SideRow(string Title, string Detail, string? Path, int Line, string Glyph = "", bool IsMissing = false, bool CanRemoveReference = false, bool IsOnline = false)
{
    public bool IsWorkspace { get; init; }
    public bool IsRecentEntry { get; init; }
    public bool IsDocument => !IsWorkspace && !IsGroup && SearchQuery is null;
    public string OpenLabel => IsWorkspace ? "Open Workspace" : "Open";
    public string RemoveLabel => IsRecentEntry || IsWorkspace ? "Forget recent entry" : "Close and forget file reference";
    public string RemoveHint => IsWorkspace ? "Remove this workspace from Recent; keep its files and any open workspace."
        : "Close document and remove Library reference; saved files stay on disk. In Recent, only forget the reference.";
    public string RemoveAccessibleName => "File reference actions";
    public string IconPath => IsWorkspace ? LibraryIcons.Workspace : LibraryIcons.Document;
    public Tern.App.Documents.DocumentTab? OpenTab { get; init; }
    public Tern.App.Panels.TaskEntry? Task { get; init; }
    public bool IsTask => Task is not null;
    public bool TaskChecked => Task?.Checked == true;
    public string TaskAccessibleName => IsTask ? "Toggle task " + Title : "Task completion";
    public string? SearchQuery { get; init; }
    public bool IsGroup { get; init; }
    public bool HasIcon => !IsTask && !IsGroup;
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    public bool CanRename => IsDocument && CanRemoveReference && !IsOnline && !IsMissing;
}

/// <summary>A node of the sidebar tree, with a dirty marker that follows the open tab for that file.</summary>
public sealed partial class TreeRow : ObservableObject
{
    private readonly ObservableCollection<TreeRow> _children;
    private readonly ObservableCollection<TreeRow> _presentedChildren;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGitChange))]
    private string _gitState = "";
    public bool HasGitChange => GitState.Length > 0;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _isChangedOnDisk;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCloseAction))]
    private bool _isOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExpandedFolder))]
    [NotifyPropertyChangedFor(nameof(SectionToggleText))]
    [NotifyPropertyChangedFor(nameof(SectionToggleHint))]
    [NotifyPropertyChangedFor(nameof(ShowCloseAction))]
    [NotifyPropertyChangedFor(nameof(IsLoadingChildren))]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    public TreeRow(string name, string fullPath, bool isDirectory, IReadOnlyList<TreeRow> children)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        _children = new ObservableCollection<TreeRow>(children);
        _presentedChildren = new ObservableCollection<TreeRow>(children.Take(32));
    }

    public string Name { get; }
    public bool IsOnlineOnly { get; private init; }
    public string DisplayName => IsOnlineOnly ? Name + " (online-only)" : Name;

    public string FullPath { get; }

    public bool IsDirectory { get; }
    public bool CanRename => !IsDirectory;
    /// <summary>A plain folder inside a section: it can be renamed, moved, duplicated and recycled (workspace roots cannot).</summary>
    public bool IsFolderEntry => IsDirectory && !IsWorkspace && !IsSection;
    public bool IsRecyclable => CanRename || IsFolderEntry;
    public bool IsWorkspace { get; private init; }
    public bool IsSection { get; private init; }
    [ObservableProperty]
    private bool _isSectionOpen;
    public bool IsTreeEntry => !IsSection;
    public bool IsEmptyWorkspace => IsSection && IsWorkspace && Children.Count == 0;
    public string SectionKind => IsWorkspace ? "WORKSPACE" : "FOLDER";
    public string SectionCapability => IsWorkspace ? "Content search and workspace questions" : "Browse files · Quick Open searches filenames · Make it a workspace for content search";
    /// <summary>True for a folder that is not part of a workspace and can be made one.</summary>
    public bool CanPromote { get; set; }
    public string SectionAccessibleName => (IsWorkspace ? "Workspace section: " : "Folder section: ") + Name;
    public string SectionCount { get; private set; } = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSectionNotice))]
    private string _sectionNotice = string.Empty;
    [ObservableProperty]
    private bool _isPreparingContent;
    [ObservableProperty]
    private double _contentProgressFraction;
    [ObservableProperty]
    private bool _isContentProgressIndeterminate;
    public bool HasSectionNotice => SectionNotice.Length > 0;
    public string SectionToggleText => IsExpanded ? "Collapse" : "Expand";
    public string SectionToggleHint => SectionToggleText + " " + (IsWorkspace ? "workspace " : "folder ") + Name;
    public bool IsExpandedFolder => IsDirectory && !IsWorkspace && IsExpanded;
    public bool ShowCloseAction => IsOpen || IsWorkspace || IsExpandedFolder;
    public string CloseActionName => IsWorkspace ? "Close this workspace" : IsDirectory ? "Close folder" : "Close document";
    public string CloseActionHint => IsDirectory && !IsWorkspace ? "Close folder documents and collapse" : CloseActionName;

    public IReadOnlyList<TreeRow> Children => _children;
    public IReadOnlyList<TreeRow> PresentedChildren => _presentedChildren;
    public bool IsLoadingChildren => IsExpanded && _presentedChildren.Count < _children.Count;
    public string ChildrenLoadingText => $"Loading Library entries · {_presentedChildren.Count:N0} of {_children.Count:N0}";

    internal bool PresentNextChildren()
    {
        Synchronize(_presentedChildren, _children.Take(_presentedChildren.Count + 32).ToArray());
        OnPropertyChanged(nameof(IsLoadingChildren));
        OnPropertyChanged(nameof(ChildrenLoadingText));
        return _presentedChildren.Count < _children.Count;
    }

    public string IconPath => IsWorkspace ? LibraryIcons.Workspace : IsDirectory ? LibraryIcons.Folder : LibraryIcons.Document;
    public string SectionIconName => IsWorkspace ? "Workspace" : "Folder";

    public static TreeRow From(FileTreeNode node, bool workspaceRoot = false, IReadOnlySet<string>? namedRoots = null, bool rootSection = false) =>
        new(node.Name, node.FullPath, node.IsDirectory, [.. node.Children.Select(child => From(child, namedRoots: namedRoots))])
        {
            IsOnlineOnly = node.IsOnlineOnly,
            IsWorkspace = workspaceRoot || !rootSection && node.IsWorkspaceRoot || namedRoots?.Contains(node.FullPath) == true,
            IsSection = rootSection || node.IsWorkspaceRoot || namedRoots?.Contains(node.FullPath) == true,
            SectionCount = $"{node.FileCount:N0} document(s) · {node.Children.Count(child => child.IsDirectory):N0} folder(s)",
            SectionNotice = node.IsDirectory && node.Children.Count == 0 ? "Empty — use Create File or Create Folder from this section's menu." : string.Empty,
        };

    internal static TreeRow Reconcile(TreeRow? current, FileTreeNode node, bool workspaceRoot = false,
        IReadOnlySet<string>? namedRoots = null, bool rootSection = false)
    {
        var workspace = workspaceRoot || !rootSection && node.IsWorkspaceRoot || namedRoots?.Contains(node.FullPath) == true;
        var section = rootSection || node.IsWorkspaceRoot || namedRoots?.Contains(node.FullPath) == true;
        if (current is null || current.FullPath != node.FullPath || current.Name != node.Name
            || current.IsDirectory != node.IsDirectory || current.IsOnlineOnly != node.IsOnlineOnly
            || current.IsWorkspace != workspace || current.IsSection != section)
            return From(node, workspaceRoot, namedRoots, rootSection);

        var previous = current.Children.GroupBy(child => child.FullPath, WorkspacePaths.Comparer)
            .ToDictionary(group => group.Key, group => group.First(), WorkspacePaths.Comparer);
        var children = node.Children.Select(child =>
            Reconcile(previous.GetValueOrDefault(child.FullPath), child, namedRoots: namedRoots)).ToArray();
        var wasEmpty = current.IsEmptyWorkspace;
        Synchronize(current._children, children);
        Synchronize(current._presentedChildren, children.Take(current._presentedChildren.Count).ToArray());
        current.OnPropertyChanged(nameof(IsLoadingChildren));
        current.OnPropertyChanged(nameof(ChildrenLoadingText));
        var count = $"{node.FileCount:N0} document(s) · {node.Children.Count(child => child.IsDirectory):N0} folder(s)";
        if (current.SectionCount != count)
        {
            current.SectionCount = count;
            current.OnPropertyChanged(nameof(SectionCount));
        }
        if (wasEmpty != current.IsEmptyWorkspace) current.OnPropertyChanged(nameof(IsEmptyWorkspace));
        current.SectionNotice = node.IsDirectory && node.Children.Count == 0
            ? "Empty — use Create File or Create Folder from this section's menu." : string.Empty;
        return current;
    }

    internal static void Synchronize<T>(ObservableCollection<T> rows, IReadOnlyList<T> updated) where T : class
    {
        for (var index = 0; index < updated.Count; index++)
        {
            if (index < rows.Count && ReferenceEquals(rows[index], updated[index])) continue;
            var previous = rows.IndexOf(updated[index]);
            if (previous >= 0) rows.Move(previous, index);
            else rows.Insert(index, updated[index]);
        }
        while (rows.Count > updated.Count) rows.RemoveAt(rows.Count - 1);
    }

    public IEnumerable<TreeRow> Flatten()
    {
        // Iterative pre-order: nested iterators cost O(depth) for every row of a large tree.
        var pending = new Stack<TreeRow>();
        pending.Push(this);
        while (pending.Count > 0)
        {
            var row = pending.Pop();
            yield return row;
            for (var index = row._children.Count - 1; index >= 0; index--) pending.Push(row._children[index]);
        }
    }
}

internal static class LibraryIcons
{
    internal const string Workspace = "M1,1 L7,1 L7,7 L1,7 Z M10,1 L16,1 L16,7 L10,7 Z M1,10 L7,10 L7,16 L1,16 Z M10,10 L16,10 L16,16 L10,16 Z";
    internal const string Folder = "M1,4 L6,4 L8,6 L15,6 L15,14 L1,14 Z M1,4 L1,2 L6,2 L8,4 L15,4 L15,6";
    internal const string Document = "M3,1 L10,1 L14,5 L14,15 L3,15 Z M10,1 L10,5 L14,5 M6,8 L11,8 M6,11 L11,11";
}
