using Tern.Workspaces;

namespace Tern.App.Shell;

public enum IdentityChangeKind { Rename, Move, Duplicate, SaveAs }

/// <summary>A file or folder that now lives at <see cref="NewPath"/>. For a duplicate or Save As the original stays in place.</summary>
internal sealed record IdentityChange(string OldPath, string NewPath, IdentityChangeKind Kind, bool IsFolder = false);

public sealed partial class ShellViewModel
{
    private const int MaxIdentityFiles = 50_000;

    /// <summary>
    /// SB-06: the one place that carries everything keyed by a document's path to its new path after Rename, Move, Duplicate
    /// or Save As (Recent, view states, reopen-closed-tab stack, open tabs, link graph, linked-image permissions, saved-text
    /// history and recovery checkpoints). Duplicates and Save As copy only what is safe to copy; history stays with the original.
    /// Every step is isolated, so one failure becomes a warning and never blocks the others. Returns the warnings.
    /// </summary>
    internal async Task<IReadOnlyList<string>> ApplyIdentityChangeAsync(IdentityChange change)
    {
        var warnings = new List<string>();
        var from = Path.GetFullPath(change.OldPath);
        var to = Path.GetFullPath(change.NewPath);
        var relocated = change.Kind is IdentityChangeKind.Rename or IdentityChangeKind.Move;
        string? Map(string path) => MapPath(path, from, to, change.IsFolder);
        var pairs = new List<(string From, string To)>();

        void Step(string what, Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { warnings.Add(what + " could not be updated: " + PlainProblem(ex)); }
        }

        async Task StepAsync(string what, Func<Task> action)
        {
            try { await action().ConfigureAwait(true); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { warnings.Add(what + " could not be updated: " + PlainProblem(ex)); }
        }

        Step("The list of affected files", () =>
        {
            if (!change.IsFolder) { pairs.Add((from, to)); return; }
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
            foreach (var file in Directory.EnumerateFiles(to, "*", options).Take(MaxIdentityFiles))
                pairs.Add((from + file[to.TrimEnd(Path.DirectorySeparatorChar).Length..], file));
        });

        if (relocated)
        {
            Step("Reopen closed tab", () =>
            {
                var remapped = _closedTabs.Reverse().Select(p => Map(p) ?? p).ToList();
                _closedTabs.Clear();
                foreach (var p in remapped) _closedTabs.Push(p);
            });
            Step("Recent items", () =>
            {
                foreach (var kind in new[] { RecentKind.File, RecentKind.Workspace })
                    foreach (var entry in Recent.Get(kind).ToList())
                        if (Map(entry.Path) is { } moved) Recent.Rename(kind, entry.Path, moved);
                if (Recent.LastSaveError is { } error) warnings.Add("Recent could not be persisted: " + PlainProblem(error));
            });
            Step("Caret and scroll positions", () =>
            {
                foreach (var key in _viewStates.Keys.ToList())
                    if (Map(key) is { } moved && _viewStates.Remove(key, out var state)) _viewStates[moved] = state;
            });
            Step("Open tabs", () =>
            {
                foreach (var tab in Tabs.ToList())
                {
                    if (tab.FilePath is { } open && Map(open) is { } moved) tab.Session.UpdateFilePath(moved);
                    if (tab.RecoveredFromPath is { } recovered && Map(recovered) is { } recoveredMoved) tab.RecoveredFromPath = recoveredMoved;
                }
            });
            Step("The link graph", () =>
            {
                foreach (var workspace in OpenWorkspaces)
                {
                    foreach (var pair in pairs) workspace.ClearUnsavedText(pair.From);
                    if (workspace.IsInside(from)) workspace.Graph.Rename(from, to);
                }
            });
        }
        else
        {
            Step("Caret and scroll positions", () =>
            {
                foreach (var (source, target) in pairs)
                    if (_viewStates.TryGetValue(source, out var state)) _viewStates[target] = state;
            });
        }

        Step("Linked-image permissions", () =>
        {
            if (!_linkedImages.Remap(pairs, keepOriginal: !relocated, relocated && change.IsFolder ? image => Map(image) ?? image : null))
                warnings.Add("Linked-image permissions could not be saved: " + _linkedImages.LastSaveError);
        });

        if (relocated)
        {
            await StepAsync("Saved-text history", async () =>
            {
                foreach (var (source, target) in pairs) await HistoryStore.RenameAsync(source, target).ConfigureAwait(true);
            }).ConfigureAwait(true);
            await StepAsync("Crash-recovery checkpoints", async () =>
            {
                foreach (var tab in Tabs.Where(t => t.IsDirty && (t.FilePath is { } p && pairs.Any(x => SamePath(x.To, p)) || t.FilePath is null && t.RecoveredFromPath is { } r && pairs.Any(x => SamePath(x.To, r)))).ToList())
                    await _recovery.RetryAsync(tab.Session).ConfigureAwait(true);
                await RecoveryStore.RewriteOriginalPathsAsync(Map).ConfigureAwait(true);
            }).ConfigureAwait(true);
        }
        return warnings;
    }

    private static string? MapPath(string path, string from, string to, bool isFolder)
    {
        var full = Path.GetFullPath(path);
        if (string.Equals(full, from, StringComparison.OrdinalIgnoreCase)) return to;
        if (!isFolder) return null;
        var prefix = from.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? to.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + full[prefix.Length..]
            : null;
    }
}
