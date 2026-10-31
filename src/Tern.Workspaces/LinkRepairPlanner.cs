using Tern.Domain.Text;
using Tern.Markdown;

namespace Tern.Workspaces;

/// <summary>
/// Works out which links must change when a file or folder is renamed or moved, or when a heading is renamed.
/// Planning reads documents (through the supplied provider, so unsaved buffers count) and the current
/// <see cref="LinkGraph"/>, and returns the edits; it never writes. Plan <em>before</em> moving files and before
/// calling <see cref="LinkGraph.Rename"/>: the graph must still describe the old layout.
/// </summary>
public sealed class LinkRepairPlanner
{
    private readonly IDocumentTextProvider _provider;
    private readonly MarkdownProfile _profile;

    public LinkRepairPlanner(LinkGraph graph, IDocumentTextProvider provider, MarkdownProfile profile = MarkdownProfile.Extended)
    {
        Graph = graph;
        _provider = provider;
        _profile = profile;
    }

    public LinkGraph Graph { get; }

    /// <summary>Edits for every link affected by moving <paramref name="oldPath"/> (a file or folder) to <paramref name="newPath"/>.</summary>
    public async Task<RepairPlan> PlanRenameAsync(string oldPath, string newPath, CancellationToken cancellationToken = default)
    {
        var from = WorkspacePaths.Normalize(oldPath);
        var to = WorkspacePaths.Normalize(newPath);
        if (WorkspacePaths.IsUnder(from, to))
        {
            throw new ArgumentException("A folder cannot be moved into itself.", nameof(newPath));
        }

        if (from == to)
        {
            return RepairPlan.Empty;
        }

        string Map(string path)
        {
            if (!WorkspacePaths.IsSameOrUnder(from, path))
            {
                return path;
            }

            var normalized = WorkspacePaths.Normalize(path);
            return to + normalized[from.Length..];
        }

        var documents = Graph.Documents;
        var stemCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in documents)
        {
            var stem = Path.GetFileNameWithoutExtension(Map(document));
            stemCounts[stem] = stemCounts.GetValueOrDefault(stem) + 1;
        }

        var candidates = new List<string>();
        foreach (var document in documents)
        {
            var moved = !string.Equals(Map(document), document, StringComparison.Ordinal);
            var affected = moved || Graph.GetOutgoing(document).Any(l => l.TargetPath is not null && !string.Equals(Map(l.TargetPath), l.TargetPath, StringComparison.Ordinal));
            if (affected)
            {
                candidates.Add(document);
            }
        }

        var warnings = new List<string>();
        var repairs = new List<FileRepair>();
        foreach (var document in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await _provider.ReadTextAsync(document, cancellationToken).ConfigureAwait(false);
            if (text is null)
            {
                warnings.Add($"Could not read {document}; its links were not checked.");
                continue;
            }

            var analysis = MarkdownAnalyzer.Analyze(text, _profile);
            var newDocumentPath = Map(document);
            var edits = LinkRewriter.Rewrite(text, analysis, link =>
            {
                var resolved = Graph.Resolve(document, link);
                if (resolved.Status == LinkStatus.AmbiguousWikiLink)
                {
                    if (resolved.Candidates.Any(c => !string.Equals(Map(c), c, StringComparison.Ordinal)))
                    {
                        warnings.Add($"{document}:{link.Line}: ambiguous link [[{link.Destination}]] was left unchanged.");
                    }

                    return null;
                }

                if (resolved.TargetPath is null || resolved.Status is not (LinkStatus.Ok or LinkStatus.MissingAnchor))
                {
                    return null;
                }

                var target = resolved.TargetPath;
                var newTarget = Map(target);
                return link.Kind == LinkKind.WikiLink
                    ? RewriteWiki(link, resolved, newTarget, stemCounts, warnings, document)
                    : RewritePath(link, resolved, document, newDocumentPath, target, newTarget);
            });

            if (edits.Count > 0 || !string.Equals(newDocumentPath, document, StringComparison.Ordinal))
            {
                repairs.Add(new FileRepair(newDocumentPath, newDocumentPath == document ? null : document, text, edits, BuildPreviews(text, analysis.Lines, edits)));
            }
        }

        return new RepairPlan([.. repairs.Where(r => r.Edits.Count > 0 || r.WasMoved)], warnings);
    }

    /// <summary>
    /// Edits for links whose <c>#fragment</c> pointed at a heading that has been renamed. <paramref name="oldText"/> and
    /// <paramref name="newText"/> are the document before and after the edit; headings are paired by position.
    /// </summary>
    public async Task<RepairPlan> PlanHeadingRenameAsync(string documentPath, string oldText, string newText, CancellationToken cancellationToken = default)
    {
        var path = WorkspacePaths.Normalize(documentPath);
        var oldHeadings = MarkdownAnalyzer.Analyze(oldText, _profile).Headings;
        var newHeadings = MarkdownAnalyzer.Analyze(newText, _profile).Headings;
        var warnings = new List<string>();
        if (oldHeadings.Count != newHeadings.Count)
        {
            warnings.Add("Headings were added or removed in the same edit; heading links were not repaired.");
            return new RepairPlan([], warnings);
        }

        var renames = new List<(HeadingInfo Old, HeadingInfo New)>();
        for (var i = 0; i < oldHeadings.Count; i++)
        {
            if (oldHeadings[i].Anchor != newHeadings[i].Anchor || oldHeadings[i].Text != newHeadings[i].Text)
            {
                renames.Add((oldHeadings[i], newHeadings[i]));
            }
        }

        if (renames.Count == 0)
        {
            return RepairPlan.Empty;
        }

        var sources = new HashSet<string>(Graph.GetLinksTo(path).Select(l => l.SourcePath), WorkspacePaths.Comparer) { path };
        var repairs = new List<FileRepair>();
        foreach (var source in sources.Order(WorkspacePaths.Comparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await _provider.ReadTextAsync(source, cancellationToken).ConfigureAwait(false);
            if (text is null)
            {
                warnings.Add($"Could not read {source}; its heading links were not checked.");
                continue;
            }

            var analysis = MarkdownAnalyzer.Analyze(text, _profile);
            var edits = LinkRewriter.Rewrite(text, analysis, link =>
            {
                var resolved = Graph.Resolve(source, link);
                if (resolved.TargetPath is null || !WorkspacePaths.PathsEqual(resolved.TargetPath, path) || string.IsNullOrEmpty(resolved.Fragment))
                {
                    return null;
                }

                return RewriteFragment(link, resolved.Fragment, renames);
            });

            if (edits.Count > 0)
            {
                repairs.Add(new FileRepair(source, null, text, edits, BuildPreviews(text, analysis.Lines, edits)));
            }
        }

        return new RepairPlan(repairs, warnings);
    }

    private static string? RewriteFragment(LinkInfo link, string fragment, List<(HeadingInfo Old, HeadingInfo New)> renames)
    {
        foreach (var (old, renamed) in renames)
        {
            var viaAnchor = fragment.Equals(old.Anchor, StringComparison.OrdinalIgnoreCase);
            var viaText = link.Kind == LinkKind.WikiLink && fragment.Equals(old.Text, StringComparison.OrdinalIgnoreCase);
            if (!viaAnchor && !viaText)
            {
                continue;
            }

            var replacement = viaText ? renamed.Text : renamed.Anchor;
            var hash = link.Destination.IndexOf('#', StringComparison.Ordinal);
            if (hash < 0)
            {
                return null;
            }

            var encoded = link.Kind == LinkKind.WikiLink ? replacement : replacement.Replace(" ", "%20", StringComparison.Ordinal);
            return link.Destination[..(hash + 1)] + encoded;
        }

        return null;
    }

    private string? RewritePath(LinkInfo link, ResolvedLink resolved, string document, string newDocument, string target, string newTarget)
    {
        var parsed = LinkTarget.Parse(link.Destination);
        var targetMoved = !string.Equals(target, newTarget, StringComparison.Ordinal);
        var raw = link.Destination;
        var suffixStart = raw.IndexOfAny(['#', '?']);
        var suffix = suffixStart >= 0 ? raw[suffixStart..] : string.Empty;

        switch (parsed.Kind)
        {
            case LinkTargetKind.RootRelative when targetMoved:
                return "/" + LinkTarget.FormatPath(WorkspacePaths.GetRelative(Graph.Root, newTarget)) + suffix;
            case LinkTargetKind.Absolute when targetMoved:
                return LinkTarget.FormatPath(newTarget) + suffix;
            case LinkTargetKind.Relative:
                var oldRelative = WorkspacePaths.GetRelativeFromDirectory(Path.GetDirectoryName(document)!, target);
                var newRelative = WorkspacePaths.GetRelativeFromDirectory(Path.GetDirectoryName(newDocument)!, newTarget);
                if (string.Equals(oldRelative, newRelative, StringComparison.Ordinal))
                {
                    return null;
                }

                if (resolved.ExtensionOmitted && Path.HasExtension(newRelative))
                {
                    newRelative = newRelative[..^Path.GetExtension(newRelative).Length];
                }

                var prefix = raw.StartsWith("./", StringComparison.Ordinal) && !newRelative.StartsWith("../", StringComparison.Ordinal) ? "./" : string.Empty;
                return prefix + LinkTarget.FormatPath(newRelative) + suffix;
            default:
                return null;
        }
    }

    private string? RewriteWiki(LinkInfo link, ResolvedLink resolved, string newTarget, Dictionary<string, int> stemCounts, List<string> warnings, string document)
    {
        if (!resolved.ResolvedByName || resolved.TargetPath is null)
        {
            return null;
        }

        var raw = link.Destination;
        var hash = raw.IndexOf('#', StringComparison.Ordinal);
        var suffix = hash >= 0 ? raw[hash..] : string.Empty;
        var name = (hash >= 0 ? raw[..hash] : raw).Trim();
        if (name.Length == 0)
        {
            return null;
        }

        var extension = Path.GetExtension(name);
        var keepExtension = extension.Length > 0 && WorkspaceScanOptions.DefaultMarkdownExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        var usesPath = name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal);
        var newStem = Path.GetFileNameWithoutExtension(newTarget);
        var newExtension = keepExtension ? Path.GetExtension(newTarget) : string.Empty;

        string newName;
        if (usesPath)
        {
            newName = WorkspacePaths.GetRelative(Graph.Root, newTarget);
            if (!keepExtension && Path.GetExtension(newName).Length > 0)
            {
                newName = newName[..^Path.GetExtension(newName).Length];
            }
        }
        else if (stemCounts.GetValueOrDefault(newStem) > 1)
        {
            warnings.Add($"{document}:{link.Line}: another file is named \"{newStem}\"; [[{raw}]] was rewritten with its folder path to stay unambiguous.");
            newName = WorkspacePaths.GetRelative(Graph.Root, newTarget);
            if (!keepExtension && Path.GetExtension(newName).Length > 0)
            {
                newName = newName[..^Path.GetExtension(newName).Length];
            }
        }
        else
        {
            newName = newStem + newExtension;
        }

        return string.Equals(newName, name, StringComparison.Ordinal) ? null : newName + suffix;
    }

    private static IReadOnlyList<LinkEditPreview> BuildPreviews(string text, LineIndex lines, IReadOnlyList<TextEdit> edits)
    {
        var previews = new List<LinkEditPreview>();
        foreach (var group in edits.GroupBy(e => lines.LineOf(e.Start)).OrderBy(g => g.Key))
        {
            var start = lines.LineStart(group.Key);
            var end = lines.LineEnd(text, group.Key);
            var before = text[start..end];
            var relative = group.Select(e => new TextEdit(e.Start - start, Math.Min(e.Length, before.Length - (e.Start - start)), e.NewText)).ToList();
            previews.Add(new LinkEditPreview(group.Key, before, TextEdit.ApplyAll(before, relative), relative.Count));
        }

        return previews;
    }
}
