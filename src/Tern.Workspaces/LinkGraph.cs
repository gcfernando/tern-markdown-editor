using Tern.Markdown;

namespace Tern.Workspaces;

public enum LinkStatus
{
    /// <summary>Resolved to an existing file (and heading, if one was given).</summary>
    Ok,

    /// <summary>A URL such as https: or mailto:; never checked.</summary>
    External,

    /// <summary>No destination to check (<c>[text]()</c>) or an attachment reference that cannot be verified.</summary>
    Unchecked,

    MissingFile,

    /// <summary>The file exists but has no heading matching the fragment.</summary>
    MissingAnchor,

    /// <summary>A wiki link whose name matches several files.</summary>
    AmbiguousWikiLink,

    /// <summary>The destination points outside the workspace root.</summary>
    OutsideWorkspace,
}

/// <summary>A link after resolution against the workspace.</summary>
/// <param name="TargetPath">The file the link points at. Set for <see cref="LinkStatus.Ok"/> and <see cref="LinkStatus.MissingAnchor"/>, and for missing/outside files to the path that was looked up.</param>
/// <param name="Context">The source line the link appears on, trimmed, for display in backlink lists.</param>
/// <param name="Candidates">For ambiguous wiki links, every file the name could refer to.</param>
/// <param name="ResolvedByName">Wiki links only: matched by file name (true) rather than by title or alias (false).</param>
/// <param name="ExtensionOmitted">The destination had no extension and matched a document by appending one.</param>
public sealed record ResolvedLink(
    string SourcePath,
    LinkInfo Link,
    LinkStatus Status,
    string? TargetPath,
    string? Fragment,
    string Context,
    IReadOnlyList<string> Candidates,
    bool ResolvedByName = true,
    bool ExtensionOmitted = false)
{
    public int Line => Link.Line;

    public bool IsBroken => Status is LinkStatus.MissingFile or LinkStatus.MissingAnchor or LinkStatus.AmbiguousWikiLink or LinkStatus.OutsideWorkspace;
}

/// <summary>A reference to a document from another document.</summary>
public sealed record Backlink(string SourcePath, int Line, string Context, LinkKind Kind, ResolvedLink Link);

public sealed record GraphDocument(string Path, string Title, IReadOnlyList<HeadingInfo> Headings, IReadOnlyList<string> Aliases);

public sealed record GraphHeading(string Path, HeadingInfo Heading);

public sealed class LinkGraphOptions
{
    public MarkdownProfile Profile { get; init; } = MarkdownProfile.Extended;

    /// <summary>Tells whether a non-document path (image, PDF, folder) exists. Defaults to the real file system.</summary>
    public Func<string, bool>? PathExists { get; init; }

    /// <summary>Extensions removed from wiki link names before matching.</summary>
    public IReadOnlyList<string> DocumentExtensions { get; init; } = WorkspaceScanOptions.DefaultMarkdownExtensions;
}

/// <summary>
/// The link structure of a workspace: which document links where, resolved against the real folder layout.
/// Documents are fed in as text, so unsaved editor buffers can be overlaid with <see cref="Update"/>.
/// All members are thread-safe; query results are snapshots.
/// </summary>
public sealed class LinkGraph
{
    private const int MaxContextLength = 240;

    private readonly Lock _gate = new();
    private readonly LinkGraphOptions _options;
    private readonly Func<string, bool> _pathExists;
    private readonly HashSet<string> _documentExtensions;
    private readonly Dictionary<string, Entry> _docs = new(WorkspacePaths.Comparer);
    private readonly Dictionary<string, List<string>> _byStem = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _byTitle = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ResolvedLink>> _incoming = new(WorkspacePaths.Comparer);

    public LinkGraph(string root, LinkGraphOptions? options = null)
    {
        Root = WorkspacePaths.Normalize(root);
        _options = options ?? new LinkGraphOptions();
        _pathExists = _options.PathExists ?? (p => File.Exists(p) || Directory.Exists(p));
        _documentExtensions = new HashSet<string>(_options.DocumentExtensions, StringComparer.OrdinalIgnoreCase);
    }

    public string Root { get; }

    public int DocumentCount
    {
        get
        {
            lock (_gate)
            {
                return _docs.Count;
            }
        }
    }

    /// <summary>Reads and analyses the given documents in parallel and resolves every link once at the end.</summary>
    public async Task LoadAsync(IEnumerable<string> paths, IDocumentTextProvider provider, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var entries = new System.Collections.Concurrent.ConcurrentBag<Entry>();
        var done = 0;
        var options = new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };
        await Parallel.ForEachAsync(paths, options, async (path, ct) =>
        {
            if (!WorkspacePaths.IsPhysicallyContained(Root, path)) return;
            var text = await provider.ReadTextAsync(path, ct).ConfigureAwait(false);
            if (text is not null)
            {
                entries.Add(BuildEntry(WorkspacePaths.Normalize(path), text));
            }

            progress?.Report(Interlocked.Increment(ref done));
        }).ConfigureAwait(false);

        lock (_gate)
        {
            foreach (var entry in entries)
            {
                _docs[entry.Path] = entry;
            }

            RebuildAll();
        }
    }

    /// <summary>Adds or replaces a document from its current text (saved or not).</summary>
    public void Update(string path, string text)
    {
        var entry = BuildEntry(WorkspacePaths.Normalize(path), text);
        lock (_gate)
        {
            var structuralChange = !_docs.TryGetValue(entry.Path, out var previous) || previous.Signature != entry.Signature;
            if (previous is not null)
            {
                _docs.Remove(previous.Path);
            }

            _docs[entry.Path] = entry;
            if (structuralChange)
            {
                RebuildAll();
            }
            else
            {
                RemoveIncoming(entry.Path);
                ResolveEntry(entry);
            }
        }
    }

    /// <summary>Forgets a document, or every document beneath a folder path.</summary>
    public void Remove(string path)
    {
        var normalized = WorkspacePaths.Normalize(path);
        lock (_gate)
        {
            var doomed = _docs.Keys.Where(k => WorkspacePaths.IsSameOrUnder(normalized, k)).ToList();
            if (doomed.Count == 0)
            {
                return;
            }

            foreach (var key in doomed)
            {
                _docs.Remove(key);
            }

            RebuildAll();
        }
    }

    /// <summary>
    /// Records that a file or folder moved. The text of each document is unchanged, so its links are re-resolved from the
    /// new location: they stay broken until a repair plan has been applied and <see cref="Update"/> called with the result.
    /// </summary>
    public void Rename(string oldPath, string newPath)
    {
        var from = WorkspacePaths.Normalize(oldPath);
        var to = WorkspacePaths.Normalize(newPath);
        lock (_gate)
        {
            var moving = _docs.Values.Where(e => WorkspacePaths.IsSameOrUnder(from, e.Path)).ToList();
            foreach (var entry in moving)
            {
                _docs.Remove(entry.Path);
            }

            foreach (var entry in moving)
            {
                var remainder = entry.Path.Length > from.Length ? entry.Path[from.Length..] : string.Empty;
                var moved = entry with { Path = to + remainder, Stem = Path.GetFileNameWithoutExtension(to + remainder) };
                _docs[moved.Path] = moved;
            }

            RebuildAll();
        }
    }

    public bool Contains(string path)
    {
        lock (_gate)
        {
            return _docs.ContainsKey(WorkspacePaths.Normalize(path));
        }
    }

    public IReadOnlyList<string> Documents
    {
        get
        {
            lock (_gate)
            {
                return [.. _docs.Values.Select(e => e.Path).Order(WorkspacePaths.Comparer)];
            }
        }
    }

    public GraphDocument? GetDocument(string path)
    {
        lock (_gate)
        {
            return _docs.TryGetValue(WorkspacePaths.Normalize(path), out var e) ? new GraphDocument(e.Path, e.Title, e.Headings, e.Aliases) : null;
        }
    }

    public IReadOnlyList<GraphHeading> GetHeadings()
    {
        lock (_gate)
        {
            return [.. _docs.Values.OrderBy(e => e.Path, WorkspacePaths.Comparer).SelectMany(e => e.Headings.Select(h => new GraphHeading(e.Path, h)))];
        }
    }

    public IReadOnlyList<ResolvedLink> GetOutgoing(string path)
    {
        lock (_gate)
        {
            return _docs.TryGetValue(WorkspacePaths.Normalize(path), out var e) ? [.. e.Resolved] : [];
        }
    }

    /// <summary>Every link, from any document, that points at <paramref name="targetPath"/> (a document or any other file).</summary>
    public IReadOnlyList<ResolvedLink> GetLinksTo(string targetPath)
    {
        lock (_gate)
        {
            return _incoming.TryGetValue(WorkspacePaths.Normalize(targetPath), out var list) ? [.. list] : [];
        }
    }

    /// <summary>Links from other documents to <paramref name="path"/>, with the line each appears on, ordered by source then line.</summary>
    public IReadOnlyList<Backlink> GetBacklinks(string path)
    {
        var normalized = WorkspacePaths.Normalize(path);
        return [.. GetLinksTo(normalized)
            .Where(l => !WorkspacePaths.PathsEqual(l.SourcePath, normalized))
            .OrderBy(l => l.SourcePath, WorkspacePaths.Comparer)
            .ThenBy(l => l.Line)
            .Select(l => new Backlink(l.SourcePath, l.Line, l.Context, l.Link.Kind, l))];
    }

    /// <summary>All links that do not resolve, across the workspace or limited to one document.</summary>
    public IReadOnlyList<ResolvedLink> GetBrokenLinks(string? path = null)
    {
        lock (_gate)
        {
            IEnumerable<Entry> source = path is null
                ? _docs.Values.OrderBy(e => e.Path, WorkspacePaths.Comparer)
                : _docs.TryGetValue(WorkspacePaths.Normalize(path), out var e) ? [e] : [];
            return [.. source.SelectMany(x => x.Resolved).Where(l => l.IsBroken)];
        }
    }

    /// <summary>
    /// Documents that no other document links to and, unless <paramref name="allowOutgoing"/> is set, that do not link to
    /// any other document either.
    /// </summary>
    public IReadOnlyList<string> GetOrphans(bool allowOutgoing = false)
    {
        lock (_gate)
        {
            var result = new List<string>();
            foreach (var entry in _docs.Values.OrderBy(e => e.Path, WorkspacePaths.Comparer))
            {
                var hasIncoming = _incoming.TryGetValue(entry.Path, out var list)
                    && list.Any(l => !WorkspacePaths.PathsEqual(l.SourcePath, entry.Path));
                var hasOutgoing = entry.Resolved.Any(l =>
                    l.TargetPath is not null && !WorkspacePaths.PathsEqual(l.TargetPath, entry.Path) && _docs.ContainsKey(l.TargetPath));
                if (!hasIncoming && (allowOutgoing || !hasOutgoing))
                {
                    result.Add(entry.Path);
                }
            }

            return result;
        }
    }

    /// <summary>Resolves a link that was parsed from <paramref name="sourcePath"/>'s current text against the graph.</summary>
    public ResolvedLink Resolve(string sourcePath, LinkInfo link)
    {
        lock (_gate)
        {
            return ResolveLink(WorkspacePaths.Normalize(sourcePath), link, string.Empty);
        }
    }

    private Entry BuildEntry(string path, string text)
    {
        var analysis = MarkdownAnalyzer.Analyze(text, _options.Profile);
        var front = FrontMatterReader.Read(analysis.FrontMatter?.Yaml);
        var stem = Path.GetFileNameWithoutExtension(path);
        var title = !string.IsNullOrWhiteSpace(front.Title)
            ? front.Title!
            : analysis.Headings.Count > 0 && analysis.Headings[0].Text.Length > 0 ? analysis.Headings[0].Text : stem;

        var links = analysis.Links.Select(l => (Link: l, Context: ContextFor(text, analysis.Lines, l))).ToList();
        var explicitAnchors = analysis.ExplicitAnchors.Keys.ToHashSet(StringComparer.Ordinal);
        var signature = string.Join('\u0001', [title, .. front.Aliases, .. analysis.Headings.Select(h => h.Anchor + "\u0002" + h.Text), .. explicitAnchors.Order(StringComparer.Ordinal)]);
        return new Entry(path, stem, title, front.Aliases, analysis.Headings, explicitAnchors, links, [], signature);
    }

    private static string ContextFor(string text, LineIndex lines, LinkInfo link)
    {
        var start = lines.LineStart(link.Line);
        var end = lines.LineEnd(text, link.Line);
        var line = text[start..end].Trim();
        return line.Length > MaxContextLength ? line[..MaxContextLength] + "…" : line;
    }

    // Caller holds the lock.
    private void RebuildAll()
    {
        _byStem.Clear();
        _byTitle.Clear();
        _incoming.Clear();
        foreach (var entry in _docs.Values)
        {
            Index(_byStem, entry.Stem, entry.Path);
            Index(_byTitle, entry.Title, entry.Path);
            foreach (var alias in entry.Aliases)
            {
                Index(_byTitle, alias, entry.Path);
            }
        }

        foreach (var entry in _docs.Values)
        {
            ResolveEntry(entry);
        }
    }

    private static void Index(Dictionary<string, List<string>> index, string key, string path)
    {
        if (!index.TryGetValue(key, out var list))
        {
            index[key] = list = [];
        }

        if (!list.Contains(path, WorkspacePaths.Comparer))
        {
            list.Add(path);
        }
    }

    private void RemoveIncoming(string sourcePath)
    {
        foreach (var list in _incoming.Values)
        {
            list.RemoveAll(l => WorkspacePaths.PathsEqual(l.SourcePath, sourcePath));
        }
    }

    private void ResolveEntry(Entry entry)
    {
        entry.Resolved.Clear();
        foreach (var (link, context) in entry.Links)
        {
            var resolved = ResolveLink(entry.Path, link, context);
            entry.Resolved.Add(resolved);
            foreach (var target in TargetsOf(resolved))
            {
                if (!_incoming.TryGetValue(target, out var list))
                {
                    _incoming[target] = list = [];
                }

                list.Add(resolved);
            }
        }
    }

    private static IEnumerable<string> TargetsOf(ResolvedLink link)
    {
        if (link.Status == LinkStatus.AmbiguousWikiLink)
        {
            return link.Candidates;
        }

        return link.Status is LinkStatus.Ok or LinkStatus.MissingAnchor && link.TargetPath is not null ? [link.TargetPath] : [];
    }

    private ResolvedLink ResolveLink(string sourcePath, LinkInfo link, string context)
    {
        ResolvedLink Make(LinkStatus status, string? target = null, string? fragment = null, IReadOnlyList<string>? candidates = null, bool byName = true, bool noExt = false) =>
            new(sourcePath, link, status, target, fragment, context, candidates ?? [], byName, noExt);

        if (link.Kind == LinkKind.WikiLink)
        {
            return ResolveWiki(sourcePath, link, Make);
        }

        var target = LinkTarget.Parse(link.Destination);
        switch (target.Kind)
        {
            case LinkTargetKind.Empty:
                return Make(LinkStatus.Unchecked);
            case LinkTargetKind.External:
                return Make(LinkStatus.External);
            case LinkTargetKind.Anchor:
                return CheckFragment(sourcePath, target.Fragment, Make);
        }

        string path;
        try
        {
            path = target.Kind switch
            {
                LinkTargetKind.RootRelative => WorkspacePaths.Normalize(Path.Combine(Root, target.Path.TrimStart('/', '\\'))),
                LinkTargetKind.Absolute => WorkspacePaths.Normalize(target.Path),
                _ => WorkspacePaths.Normalize(Path.Combine(Path.GetDirectoryName(sourcePath) ?? Root, target.Path)),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Make(LinkStatus.MissingFile, null, target.Fragment);
        }

        if (!WorkspacePaths.IsSameOrUnder(Root, path))
        {
            return Make(LinkStatus.OutsideWorkspace, path, target.Fragment);
        }

        if (_docs.TryGetValue(path, out var doc))
        {
            return CheckFragment(doc.Path, target.Fragment, Make);
        }

        if (_pathExists(path))
        {
            return Make(LinkStatus.Ok, path, target.Fragment);
        }

        if (!Path.HasExtension(path))
        {
            foreach (var extension in _options.DocumentExtensions)
            {
                if (_docs.TryGetValue(path + extension, out var withExtension))
                {
                    var checkedLink = CheckFragment(withExtension.Path, target.Fragment, Make);
                    return checkedLink with { ExtensionOmitted = true };
                }
            }
        }

        return Make(LinkStatus.MissingFile, path, target.Fragment);
    }

    private ResolvedLink CheckFragment(
        string documentPath,
        string? fragment,
        Func<LinkStatus, string?, string?, IReadOnlyList<string>?, bool, bool, ResolvedLink> make)
    {
        if (string.IsNullOrEmpty(fragment) || !_docs.TryGetValue(documentPath, out var doc))
        {
            return make(LinkStatus.Ok, documentPath, fragment, null, true, false);
        }

        return HasHeading(doc, fragment, allowText: false)
            ? make(LinkStatus.Ok, doc.Path, fragment, null, true, false)
            : make(LinkStatus.MissingAnchor, doc.Path, fragment, null, true, false);
    }

    private static bool HasHeading(Entry doc, string fragment, bool allowText)
    {
        if (doc.ExplicitAnchors.Contains(fragment)) return true;
        if (fragment.StartsWith('^'))
        {
            return true;
        }

        var slug = HeadingAnchors.Slugify(fragment);
        foreach (var heading in doc.Headings)
        {
            if (heading.Anchor.Equals(fragment, StringComparison.OrdinalIgnoreCase)
                || heading.Anchor.Equals(slug, StringComparison.OrdinalIgnoreCase)
                || (allowText && heading.Text.Equals(fragment.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private ResolvedLink ResolveWiki(
        string sourcePath,
        LinkInfo link,
        Func<LinkStatus, string?, string?, IReadOnlyList<string>?, bool, bool, ResolvedLink> make)
    {
        var raw = link.Destination.Trim();
        string? fragment = null;
        var hash = raw.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            fragment = raw[(hash + 1)..].Trim();
            raw = raw[..hash].Trim();
        }

        ResolvedLink Finish(Entry doc, bool byName)
        {
            var ok = string.IsNullOrEmpty(fragment) || HasHeading(doc, fragment, allowText: true);
            return make(ok ? LinkStatus.Ok : LinkStatus.MissingAnchor, doc.Path, fragment, null, byName, false);
        }

        if (raw.Length == 0)
        {
            return _docs.TryGetValue(sourcePath, out var self) ? Finish(self, true) : make(LinkStatus.Unchecked, null, fragment, null, true, false);
        }

        var name = raw.Replace('\\', '/');
        var extension = Path.GetExtension(name);
        if (extension.Length > 0 && _documentExtensions.Contains(extension))
        {
            name = name[..^extension.Length];
        }
        else if (extension.Length > 0 && extension.Length <= 6)
        {
            return ResolveAttachment(sourcePath, name, fragment, make);
        }

        var stemKey = name[(name.LastIndexOf('/') + 1)..];
        var candidates = new List<string>();
        if (_byStem.TryGetValue(stemKey, out var byStem))
        {
            candidates.AddRange(name.Contains('/', StringComparison.Ordinal) ? byStem.Where(p => EndsWithPath(p, name)) : byStem);
        }

        var byName = true;
        if (candidates.Count == 0 && !name.Contains('/', StringComparison.Ordinal) && _byTitle.TryGetValue(name, out var byTitle))
        {
            candidates.AddRange(byTitle);
            byName = false;
        }

        return candidates.Count switch
        {
            0 => make(LinkStatus.MissingFile, null, fragment, null, true, false),
            1 => Finish(_docs[candidates[0]], byName),
            _ => make(LinkStatus.AmbiguousWikiLink, null, fragment, candidates.Order(WorkspacePaths.Comparer).ToList(), byName, false),
        };
    }

    private ResolvedLink ResolveAttachment(
        string sourcePath,
        string name,
        string? fragment,
        Func<LinkStatus, string?, string?, IReadOnlyList<string>?, bool, bool, ResolvedLink> make)
    {
        foreach (var baseDirectory in new[] { Path.GetDirectoryName(sourcePath) ?? Root, Root })
        {
            try
            {
                var candidate = WorkspacePaths.Normalize(Path.Combine(baseDirectory, name));
                if (WorkspacePaths.IsSameOrUnder(Root, candidate) && _pathExists(candidate))
                {
                    return make(LinkStatus.Ok, candidate, fragment, null, true, false);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                break;
            }
        }

        // Obsidian resolves attachments by name anywhere in the vault; without a file index we cannot tell.
        return make(LinkStatus.Unchecked, null, fragment, null, true, false);
    }

    private bool EndsWithPath(string documentPath, string name)
    {
        var relative = WorkspacePaths.GetRelative(Root, documentPath);
        var extension = Path.GetExtension(relative);
        var withoutExtension = extension.Length > 0 ? relative[..^extension.Length] : relative;
        return withoutExtension.Equals(name, StringComparison.OrdinalIgnoreCase)
            || withoutExtension.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record Entry(
        string Path,
        string Stem,
        string Title,
        IReadOnlyList<string> Aliases,
        IReadOnlyList<HeadingInfo> Headings,
        IReadOnlySet<string> ExplicitAnchors,
        List<(LinkInfo Link, string Context)> Links,
        List<ResolvedLink> Resolved,
        string Signature);
}
