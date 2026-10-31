using Tern.Domain.Text;
using Tern.Filesystem;

namespace Tern.Workspaces;

/// <summary>What planned edits to one line look like, for showing the user before anything is written.</summary>
public sealed record LinkEditPreview(int Line, string Before, string After, int EditCount);

/// <summary>The edits planned for one file. <see cref="Path"/> is where the file lives once the move has happened.</summary>
/// <param name="OriginalPath">Set when the file itself is being moved: its location before the move.</param>
/// <param name="OriginalText">The exact text the edit offsets refer to; applying is refused if the file no longer has this text.</param>
public sealed record FileRepair(
    string Path,
    string? OriginalPath,
    string OriginalText,
    IReadOnlyList<TextEdit> Edits,
    IReadOnlyList<LinkEditPreview> Previews)
{
    public bool WasMoved => OriginalPath is not null;

    /// <summary>The text after applying every edit.</summary>
    public string ResultText => TextEdit.ApplyAll(OriginalText, Edits);
}

public enum RepairStatus
{
    Applied,

    /// <summary>The file changed since the plan was made (or on disk during saving); it was left untouched.</summary>
    Conflict,

    Failed,

    /// <summary>Nothing to do, or the caller excluded the file.</summary>
    Skipped,
}

public sealed record FileRepairResult(FileRepair Repair, RepairStatus Status, string? Message);

public sealed record RepairResult(IReadOnlyList<FileRepairResult> Files)
{
    public bool Succeeded => Files.All(f => f.Status is RepairStatus.Applied or RepairStatus.Skipped);

    public int AppliedCount => Files.Count(f => f.Status == RepairStatus.Applied);
}

/// <summary>
/// The result of planning a link repair. Planning never writes anything; <see cref="ApplyAsync"/> does, one file at a
/// time, each through the safe-save path so a file is either fully updated or left exactly as it was.
/// </summary>
public sealed record RepairPlan(IReadOnlyList<FileRepair> Files, IReadOnlyList<string> Warnings)
{
    public static RepairPlan Empty { get; } = new([], []);

    public int TotalEdits => Files.Sum(f => f.Edits.Count);

    /// <param name="filter">Return false for files that should not be written, e.g. documents with unsaved editor changes that the editor will update itself.</param>
    public async Task<RepairResult> ApplyAsync(DocumentFileService files, Func<FileRepair, bool>? filter = null, CancellationToken cancellationToken = default)
    {
        var results = new List<FileRepairResult>(Files.Count);
        foreach (var repair in Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (repair.Edits.Count == 0 || (filter is not null && !filter(repair)))
            {
                results.Add(new FileRepairResult(repair, RepairStatus.Skipped, null));
                continue;
            }

            var (status, message) = await SafeEditApplier.ApplyAsync(files, repair.Path, repair.OriginalText, repair.Edits, cancellationToken).ConfigureAwait(false);
            results.Add(new FileRepairResult(repair, status, message));
        }

        return new RepairResult(results);
    }
}

/// <summary>Applies text edits to a file on disk only if it still has the text the edits were computed from.</summary>
public static class SafeEditApplier
{
    public static async Task<(RepairStatus Status, string? Message)> ApplyAsync(
        DocumentFileService files,
        string path,
        string expectedText,
        IReadOnlyList<TextEdit> edits,
        CancellationToken cancellationToken = default)
    {
        LoadedFile loaded;
        try
        {
            if (!File.Exists(path))
            {
                return (RepairStatus.Failed, "The file no longer exists.");
            }

            loaded = await files.LoadAsync(path, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (RepairStatus.Failed, ex.Message);
        }

        if (loaded.ShouldOpenReadOnly)
        {
            return (RepairStatus.Failed, "The file cannot be re-encoded exactly as it is, so Tern will not rewrite it.");
        }

        if (!string.Equals(loaded.Text, expectedText, StringComparison.Ordinal))
        {
            return (RepairStatus.Conflict, "The file changed after the repair was planned.");
        }

        string updated;
        try
        {
            updated = TextEdit.ApplyAll(loaded.Text, edits);
        }
        catch (ArgumentException ex)
        {
            return (RepairStatus.Failed, ex.Message);
        }

        if (string.Equals(updated, loaded.Text, StringComparison.Ordinal))
        {
            return (RepairStatus.Skipped, null);
        }

        var format = LineEndingAnalyzer.Describe(loaded.Format.Encoding, loaded.Format.HasBom, updated)
            with { DominantLineEnding = loaded.Format.DominantLineEnding };
        var outcome = await files.SaveAsync(path, updated, format, loaded.Baseline, overwriteExisting: true, cancellationToken).ConfigureAwait(false);
        return outcome switch
        {
            SaveSucceeded => (RepairStatus.Applied, null),
            SaveConflict conflict => (RepairStatus.Conflict, $"The file was modified on disk while saving ({conflict.Kind})."),
            SaveFailed failed => (RepairStatus.Failed, failed.Reason),
            _ => (RepairStatus.Failed, "Unknown save outcome."),
        };
    }
}
