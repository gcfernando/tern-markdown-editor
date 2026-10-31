using Tern.Markdown;
using Tern.Workspaces;

namespace Tern.App.Panels;

public sealed record TaskEntry(string Path, string RelativePath, int Line, bool Checked, string Text);

/// <summary>Collects task-list items across a workspace for the Tasks panel and its badge.</summary>
public static class TaskIndex
{
    /// <param name="openBuffers">Unsaved text by full path; it wins over what is on disk.</param>
    public static async Task<IReadOnlyList<TaskEntry>> ScanAsync(
        IEnumerable<WorkspaceFile> files,
        MarkdownProfile profile,
        IReadOnlyDictionary<string, string>? openBuffers = null,
        CancellationToken cancellationToken = default)
    {
        var disk = new DiskDocumentTextProvider();
        var entries = new List<TaskEntry>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = openBuffers is not null && openBuffers.TryGetValue(file.FullPath, out var buffered)
                ? buffered
                : await disk.ReadTextAsync(file.FullPath, cancellationToken).ConfigureAwait(false);
            if (text is null)
            {
                continue;
            }

            foreach (var task in MarkdownAnalyzer.Analyze(text, profile).Tasks)
            {
                entries.Add(new TaskEntry(file.FullPath, file.RelativePath, task.Line, task.Checked, task.Text));
            }
        }

        return entries;
    }

    public static int OpenCount(IEnumerable<TaskEntry> entries) => entries.Count(e => !e.Checked);
}
