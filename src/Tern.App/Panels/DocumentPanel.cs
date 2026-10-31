using Tern.App.Documents;
using Tern.App.Shell;
using Tern.Markdown;
using Tern.Workspaces;

namespace Tern.App.Panels;

public sealed record OutlineItem(int Level, string Text, string Anchor, int Line);

public sealed record LinkRow(string Label, string Destination, int Line, bool IsBroken, bool IsExternal);

public sealed record DocumentInfo(int Words, int Characters, int ReadMinutes, DateTime? ModifiedUtc);

/// <summary>Everything the right-hand panel shows for one document, derived from its latest analysis.</summary>
public sealed record DocumentPanel(
    IReadOnlyList<OutlineItem> Outline,
    IReadOnlyList<LinkRow> Links,
    int LinksChecked,
    int LinksBroken,
    DocumentInfo Info,
    IReadOnlyList<string> Tags)
{
    public static DocumentPanel Empty { get; } = new([], [], 0, 0, new DocumentInfo(0, 0, 0, null), []);

    private const int WordsPerMinute = 220;

    public static DocumentPanel Build(DocumentTab? tab, WorkspaceSession? workspace)
    {
        if (tab?.Analysis is not { } analysis)
        {
            return Empty;
        }

        var outline = analysis.Headings.Select(h => new OutlineItem(h.Level, h.Text, h.Anchor, h.Line)).ToList();

        // With a workspace the graph knows which local links resolve; without one only external links can be judged.
        var resolved = tab.FilePath is { } path && workspace is not null
            ? workspace.Graph.GetOutgoing(path).GroupBy(r => (r.Line, r.Link.Destination), new LinkKeyComparer()).ToDictionary(g => g.Key, g => g.First(), new LinkKeyComparer())
            : [];

        var links = new List<LinkRow>();
        foreach (var link in analysis.Links)
        {
            var external = link.Destination.Contains("://", StringComparison.Ordinal) || link.Destination.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
            var broken = resolved.TryGetValue((link.Line, link.Destination), out var r) && r.IsBroken;
            if (broken && tab.FilePath is { } source && LinkedImageAccess.IsAvailable(source, link, tab.LinkedImageFiles)) broken = false;
            links.Add(new LinkRow(string.IsNullOrWhiteSpace(link.Label) ? link.Destination : link.Label, link.Destination, link.Line, broken, external));
        }

        var checkedCount = links.Count(l => !l.IsExternal);
        var brokenCount = links.Count(l => l.IsBroken);

        var words = analysis.WordCount;
        var minutes = words == 0 ? 0 : Math.Max(1, (int)Math.Ceiling(words / (double)WordsPerMinute));
        DateTime? modified = null;
        if (tab.FilePath is { } file && File.Exists(file))
        {
            modified = File.GetLastWriteTimeUtc(file);
        }

        var tags = analysis.FrontMatter is { } fm ? FrontMatterReader.Read(fm.Yaml).Tags.ToList() : [];
        return new DocumentPanel(outline, links, checkedCount, brokenCount, new DocumentInfo(words, analysis.CharacterCount, minutes, modified), tags);
    }

    /// <summary>"just now", "5 minutes ago", "3 days ago": the modified time in words.</summary>
    public static string DescribeAge(DateTime? modifiedUtc, DateTime nowUtc)
    {
        if (modifiedUtc is not { } m)
        {
            return "Not saved yet";
        }

        var span = nowUtc - m;
        return span.TotalMinutes < 1 ? "just now"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago"
            : span.TotalDays < 60 ? $"{(int)span.TotalDays} days ago"
            : m.ToLocalTime().ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture);
    }

    private sealed class LinkKeyComparer : IEqualityComparer<(int, string)>
    {
        public bool Equals((int, string) x, (int, string) y) => x.Item1 == y.Item1 && string.Equals(x.Item2, y.Item2, StringComparison.Ordinal);

        public int GetHashCode((int, string) obj) => HashCode.Combine(obj.Item1, obj.Item2);
    }
}
