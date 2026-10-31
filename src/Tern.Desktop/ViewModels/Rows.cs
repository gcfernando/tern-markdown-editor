using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using Tern.Workspaces;

namespace Tern.Desktop.ViewModels;

public sealed record OutlineRow(string Text, Thickness Indent, int Line, string Anchor)
{
    public override string ToString() => Text;
}

public sealed record LinkRowView(string Text, string Destination, int Line, bool IsBroken, string Glyph);

/// <summary>One line in the sidebar list panels (recent files, search hits, backlinks, tasks...).</summary>
public sealed record SideRow(string Title, string Detail, string? Path, int Line, string Glyph = "", bool IsMissing = false, bool CanRemoveReference = false, bool IsOnline = false)
{
    public bool IsWorkspace { get; init; }
    public bool IsDocument => !IsWorkspace;
    public string OpenLabel => IsWorkspace ? "Open Workspace" : "Open";
    public string RemoveLabel => IsWorkspace ? "Remove from Recent" : "Remove";
    public string RemoveHint => IsWorkspace ? "Remove this workspace from Recent; keep its files and any open workspace."
        : "Close document and remove Library reference; saved files stay on disk. In Recent, only forget the reference.";
    public string RemoveAccessibleName => IsWorkspace ? "Remove workspace reference" : "Remove file reference";
    public string IconPath => IsWorkspace ? LibraryIcons.Workspace : LibraryIcons.Document;
    public Tern.App.Documents.DocumentTab? OpenTab { get; init; }
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    public bool CanRename => IsDocument && CanRemoveReference && !IsOnline && !IsMissing;
}

/// <summary>A node of the sidebar tree, with a dirty marker that follows the open tab for that file.</summary>
public sealed partial class TreeRow : ObservableObject
{
    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExpandedFolder))]
    [NotifyPropertyChangedFor(nameof(SectionToggleText))]
    [NotifyPropertyChangedFor(nameof(SectionToggleHint))]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    public TreeRow(string name, string fullPath, bool isDirectory, IReadOnlyList<TreeRow> children)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Children = children;
    }

    public string Name { get; }

    public string FullPath { get; }

    public bool IsDirectory { get; }
    public bool CanRename => !IsDirectory;
    public bool IsWorkspace { get; private init; }
    public bool IsSection { get; private init; }
    public bool IsSectionOpen { get; set; }
    public bool IsTreeEntry => !IsSection;
    public string SectionKind => IsWorkspace ? "WORKSPACE" : "FOLDER · OUTSIDE WORKSPACES";
    public string SectionAccessibleName => (IsWorkspace ? "Workspace section: " : "Folder section: ") + Name;
    public string SectionCount { get; private init; } = string.Empty;
    public string SectionNotice { get; set; } = string.Empty;
    public bool HasSectionNotice => SectionNotice.Length > 0;
    public string SectionToggleText => IsExpanded ? "Collapse" : "Expand";
    public string SectionToggleHint => SectionToggleText + " " + (IsWorkspace ? "workspace " : "folder ") + Name;
    public bool IsExpandedFolder => IsDirectory && !IsWorkspace && IsExpanded;

    public IReadOnlyList<TreeRow> Children { get; }

    public string IconPath => IsWorkspace ? LibraryIcons.Workspace : IsDirectory ? LibraryIcons.Folder : LibraryIcons.Document;
    public string SectionIconName => IsWorkspace ? "Workspace" : "Folder";

    public static TreeRow From(FileTreeNode node, bool workspaceRoot = false, IReadOnlySet<string>? namedRoots = null, bool rootSection = false) =>
        new(node.Name, node.FullPath, node.IsDirectory, [.. node.Children.Select(child => From(child, namedRoots: namedRoots))])
        {
            IsWorkspace = workspaceRoot || !rootSection && node.IsWorkspaceRoot || namedRoots?.Contains(node.FullPath) == true,
            IsSection = rootSection || node.IsWorkspaceRoot || namedRoots?.Contains(node.FullPath) == true,
            SectionCount = $"{node.FileCount:N0} document(s) · {node.Children.Count(child => child.IsDirectory):N0} folder(s)",
            SectionNotice = node.IsDirectory && node.Children.Count == 0 ? "Empty — use Create File or Create Folder from this section's menu." : string.Empty,
        };

    public IEnumerable<TreeRow> Flatten()
    {
        yield return this;
        foreach (var child in Children.SelectMany(c => c.Flatten()))
        {
            yield return child;
        }
    }
}

internal static class LibraryIcons
{
    internal const string Workspace = "M1,1 L7,1 L7,7 L1,7 Z M10,1 L16,1 L16,7 L10,7 Z M1,10 L7,10 L7,16 L1,16 Z M10,10 L16,10 L16,16 L10,16 Z";
    internal const string Folder = "M1,4 L6,4 L8,6 L15,6 L15,14 L1,14 Z M1,4 L1,2 L6,2 L8,4 L15,4 L15,6";
    internal const string Document = "M3,1 L10,1 L14,5 L14,15 L3,15 Z M10,1 L10,5 L14,5 M6,8 L11,8 M6,11 L11,11";
}
