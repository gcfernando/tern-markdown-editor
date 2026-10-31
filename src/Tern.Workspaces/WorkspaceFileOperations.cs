using System.Text;

namespace Tern.Workspaces;

public enum FileOperationError
{
    None,
    InvalidName,
    AlreadyExists,
    NotFound,
    OutsideWorkspace,
    InvalidTarget,
    Failed,
}

public enum CollisionPolicy
{
    /// <summary>Report <see cref="FileOperationError.AlreadyExists"/>.</summary>
    Fail,

    /// <summary>Pick a free name by appending " (2)", " (3)", ...</summary>
    AutoRename,
}

/// <summary>Outcome of a file operation. For renames and moves, <see cref="Plan"/> holds the link repairs to apply next.</summary>
public sealed record FileOperationResult(bool Succeeded, string? Path, RepairPlan? Plan, FileOperationError Error, string? Message)
{
    internal static FileOperationResult Ok(string path, RepairPlan? plan = null) => new(true, path, plan, FileOperationError.None, null);

    internal static FileOperationResult Fail(FileOperationError error, string message) => new(false, null, null, error, message);
}

/// <summary>
/// Creates, renames, moves and duplicates files and folders inside a workspace. Names are validated, nothing is ever
/// overwritten, and a rename or move returns the <see cref="RepairPlan"/> for the links it breaks (computed before the
/// move, applied by the caller). It does not touch any file content itself.
/// </summary>
public sealed class WorkspaceFileOperations
{
    private readonly string _root;
    private readonly LinkRepairPlanner? _planner;

    public WorkspaceFileOperations(string root, LinkRepairPlanner? planner = null)
    {
        _root = WorkspacePaths.Normalize(root);
        _planner = planner;
    }

    public FileOperationResult CreateFile(string directory, string name, string content = "", CollisionPolicy collision = CollisionPolicy.Fail)
    {
        var prepared = PrepareChild(directory, name, collision);
        if (prepared.Error is not null)
        {
            return prepared.Error;
        }

        try
        {
            // CreateNew makes the existence check atomic: a file created meanwhile is never overwritten.
            using var stream = new FileStream(prepared.Path!, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var bytes = new UTF8Encoding(false).GetBytes(content);
            stream.Write(bytes);
            return FileOperationResult.Ok(prepared.Path!);
        }
        catch (IOException) when (File.Exists(prepared.Path))
        {
            return FileOperationResult.Fail(FileOperationError.AlreadyExists, "A file with that name already exists.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FileOperationResult.Fail(FileOperationError.Failed, ex.Message);
        }
    }

    public FileOperationResult CreateFolder(string directory, string name, CollisionPolicy collision = CollisionPolicy.Fail)
    {
        var prepared = PrepareChild(directory, name, collision);
        if (prepared.Error is not null)
        {
            return prepared.Error;
        }

        try
        {
            Directory.CreateDirectory(prepared.Path!);
            return FileOperationResult.Ok(prepared.Path!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FileOperationResult.Fail(FileOperationError.Failed, ex.Message);
        }
    }

    /// <summary>Renames a file or folder within its current folder.</summary>
    public async Task<FileOperationResult> RenameAsync(string path, string newName, CancellationToken cancellationToken = default)
    {
        var source = WorkspacePaths.Normalize(path);
        var check = CheckSource(source);
        if (check is not null)
        {
            return check;
        }

        var validation = FileNameValidator.Validate(newName);
        if (!validation.IsValid)
        {
            return FileOperationResult.Fail(FileOperationError.InvalidName, validation.Message!);
        }

        var target = Path.Combine(Path.GetDirectoryName(source)!, newName);
        var caseOnly = string.Equals(source, target, StringComparison.OrdinalIgnoreCase) && WorkspacePaths.IsCaseInsensitive;
        if (string.Equals(source, target, StringComparison.Ordinal))
        {
            return FileOperationResult.Ok(source);
        }

        if (!caseOnly && Exists(target))
        {
            return FileOperationResult.Fail(FileOperationError.AlreadyExists, "Something with that name already exists in this folder.");
        }

        return await MoveCoreAsync(source, target, caseOnly, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Moves a file or folder into <paramref name="destinationDirectory"/>, keeping its name.</summary>
    public async Task<FileOperationResult> MoveAsync(string path, string destinationDirectory, CollisionPolicy collision = CollisionPolicy.Fail, CancellationToken cancellationToken = default)
    {
        var source = WorkspacePaths.Normalize(path);
        var check = CheckSource(source);
        if (check is not null)
        {
            return check;
        }

        var directory = WorkspacePaths.Normalize(destinationDirectory);
        if (!WorkspacePaths.IsSameOrUnder(_root, directory))
        {
            return FileOperationResult.Fail(FileOperationError.OutsideWorkspace, "The destination is outside the workspace.");
        }

        if (!Directory.Exists(directory))
        {
            return FileOperationResult.Fail(FileOperationError.NotFound, "The destination folder does not exist.");
        }

        if (!IsContained(directory))
        {
            return FileOperationResult.Fail(FileOperationError.OutsideWorkspace, "The destination is a link that leaves the workspace.");
        }

        if (WorkspacePaths.IsSameOrUnder(source, directory) && Directory.Exists(source))
        {
            return FileOperationResult.Fail(FileOperationError.InvalidTarget, "A folder cannot be moved into itself.");
        }

        var name = Path.GetFileName(source);
        var target = Path.Combine(directory, name);
        if (WorkspacePaths.PathsEqual(source, target))
        {
            return FileOperationResult.Ok(source);
        }

        if (Exists(target))
        {
            if (collision == CollisionPolicy.Fail)
            {
                return FileOperationResult.Fail(FileOperationError.AlreadyExists, "Something with that name already exists in the destination.");
            }

            target = FreeName(directory, name);
        }

        return await MoveCoreAsync(source, target, false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Copies a file or folder next to the original as "name copy.ext", "name copy 2.ext", ... Links inside copied folders are not followed.</summary>
    public FileOperationResult Duplicate(string path)
    {
        var source = WorkspacePaths.Normalize(path);
        var check = CheckSource(source);
        if (check is not null)
        {
            return check;
        }

        var directory = Path.GetDirectoryName(source)!;
        var isDirectory = Directory.Exists(source);
        if (isDirectory && new DirectoryInfo(source).LinkTarget is not null)
        {
            return FileOperationResult.Fail(FileOperationError.InvalidTarget, "A link to a folder cannot be duplicated.");
        }
        var stem = isDirectory ? Path.GetFileName(source) : Path.GetFileNameWithoutExtension(source);
        var extension = isDirectory ? string.Empty : Path.GetExtension(source);

        string target;
        var attempt = 1;
        do
        {
            var suffix = attempt == 1 ? " copy" : $" copy {attempt}";
            target = Path.Combine(directory, stem + suffix + extension);
            attempt++;
        }
        while (Exists(target));

        try
        {
            if (isDirectory)
            {
                CopyDirectory(new DirectoryInfo(source), target);
            }
            else
            {
                File.Copy(source, target, overwrite: false);
            }

            return FileOperationResult.Ok(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FileOperationResult.Fail(FileOperationError.Failed, ex.Message);
        }
    }

    private async Task<FileOperationResult> MoveCoreAsync(string source, string target, bool caseOnly, CancellationToken ct)
    {
        RepairPlan? plan = null;
        if (_planner is not null)
        {
            try
            {
                plan = await _planner.PlanRenameAsync(source, target, ct).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                return FileOperationResult.Fail(FileOperationError.InvalidTarget, ex.Message);
            }
        }

        try
        {
            if (Directory.Exists(source))
            {
                if (caseOnly)
                {
                    // Directory.Move refuses a destination that differs only by case, so hop through a unique name.
                    var hop = Path.Combine(Path.GetDirectoryName(source)!, $".{Guid.NewGuid():N}.tern-rename");
                    Directory.Move(source, hop);
                    Directory.Move(hop, target);
                }
                else
                {
                    Directory.Move(source, target);
                }
            }
            else
            {
                File.Move(source, target, overwrite: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FileOperationResult.Fail(FileOperationError.Failed, ex.Message);
        }

        return FileOperationResult.Ok(target, plan);
    }

    private (string? Path, FileOperationResult? Error) PrepareChild(string directory, string name, CollisionPolicy collision)
    {
        var parent = WorkspacePaths.Normalize(directory);
        if (!WorkspacePaths.IsSameOrUnder(_root, parent))
        {
            return (null, FileOperationResult.Fail(FileOperationError.OutsideWorkspace, "The folder is outside the workspace."));
        }

        if (!Directory.Exists(parent))
        {
            return (null, FileOperationResult.Fail(FileOperationError.NotFound, "The folder does not exist."));
        }

        if (!IsContained(parent))
        {
            return (null, FileOperationResult.Fail(FileOperationError.OutsideWorkspace, "The folder is a link that leaves the workspace."));
        }

        var validation = FileNameValidator.Validate(name);
        if (!validation.IsValid)
        {
            return (null, FileOperationResult.Fail(FileOperationError.InvalidName, validation.Message!));
        }

        var target = Path.Combine(parent, name);
        if (Exists(target))
        {
            if (collision == CollisionPolicy.Fail)
            {
                return (null, FileOperationResult.Fail(FileOperationError.AlreadyExists, "Something with that name already exists in this folder."));
            }

            target = FreeName(parent, name);
        }

        return (target, null);
    }

    private FileOperationResult? CheckSource(string source)
    {
        if (!WorkspacePaths.IsUnder(_root, source))
        {
            return FileOperationResult.Fail(FileOperationError.OutsideWorkspace, "Only items inside the workspace can be changed.");
        }

        if (!IsContained(Path.GetDirectoryName(source)!))
        {
            return FileOperationResult.Fail(FileOperationError.OutsideWorkspace, "The item is reached through a link that leaves the workspace.");
        }

        return Exists(source) ? null : FileOperationResult.Fail(FileOperationError.NotFound, "The item no longer exists.");
    }

    /// <summary>
    /// True when no folder on the way from the workspace root down to <paramref name="directory"/> is a junction or
    /// symlink whose target leaves the workspace. The check is by real location, so a lexically inside path cannot escape.
    /// </summary>
    private bool IsContained(string directory, int depth = 0)
    {
        if (depth > 16)
        {
            return false;
        }

        var current = WorkspacePaths.Normalize(directory);
        while (!string.IsNullOrEmpty(current) && !WorkspacePaths.PathsEqual(current, _root) && WorkspacePaths.IsUnder(_root, current))
        {
            var info = new DirectoryInfo(current);
            if (info.LinkTarget is not null)
            {
                FileSystemInfo? resolved;
                try
                {
                    resolved = info.ResolveLinkTarget(returnFinalTarget: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return false;
                }

                if (resolved is null)
                {
                    return false;
                }

                var real = WorkspacePaths.Normalize(resolved.FullName);
                if (!WorkspacePaths.IsSameOrUnder(_root, real) || !IsContained(real, depth + 1))
                {
                    return false;
                }
            }

            current = Path.GetDirectoryName(current);
        }

        return true;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string FreeName(string directory, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        // ".gitignore"-style names have no stem; treat the whole name as the stem.
        if (stem.Length == 0)
        {
            stem = name;
            extension = string.Empty;
        }

        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({n}){extension}");
            if (!Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static void CopyDirectory(DirectoryInfo source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var entry in source.EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null)
            {
                continue;
            }

            var destination = Path.Combine(target, entry.Name);
            if (entry is DirectoryInfo directory)
            {
                CopyDirectory(directory, destination);
            }
            else if (entry is FileInfo file)
            {
                file.CopyTo(destination, overwrite: false);
            }
        }
    }
}
