using Tern.Markdown;
using Tern.Workspaces;

namespace Tern.App.Panels;

public sealed record WikiLinkCandidate(string Label, string Destination, string Path);
public sealed record WorkspaceTag(string Name, int Documents);

public static class KnowledgeNavigation
{
    public static IReadOnlyList<WikiLinkCandidate> WikiLinks(LinkGraph graph, string source)
    {
        var candidates = new List<WikiLinkCandidate>();
        foreach (var path in graph.Documents)
        {
            if (graph.GetDocument(path) is not { } document) continue;
            var destination = WorkspacePaths.GetRelative(graph.Root, path).Replace('\\', '/');
            if (!Resolves(destination, path))
                destination = Path.GetFileName(graph.Root.TrimEnd(Path.DirectorySeparatorChar)) + "/" + destination;
            if (!Resolves(destination, path)) continue;
            candidates.Add(new(document.Title, destination, path));
            foreach (var alias in document.Aliases.Distinct(StringComparer.OrdinalIgnoreCase))
                candidates.Add(new(alias + " (alias)", Resolves(alias, path) ? alias : destination, path));
            foreach (var heading in document.Headings)
                if (Resolves(destination + "#" + heading.Anchor, path))
                    candidates.Add(new(document.Title + " #" + heading.Text, destination + "#" + heading.Anchor, path));
        }
        return candidates;

        bool Resolves(string value, string target) =>
            !value.Any(c => c is '[' or ']' or '|' || char.IsControl(c))
            && graph.Resolve(source, new LinkInfo(LinkKind.WikiLink, value, default, 1, value)) is { Status: LinkStatus.Ok, TargetPath: { } resolved }
            && WorkspacePaths.PathsEqual(resolved, target);
    }

    public static IReadOnlyList<WorkspaceTag> Tags(LinkGraph graph) => graph.Documents
        .SelectMany(path => graph.GetDocument(path)?.Tags.Distinct(StringComparer.OrdinalIgnoreCase) ?? [])
        .GroupBy(tag => tag, StringComparer.OrdinalIgnoreCase)
        .Select(group => new WorkspaceTag(group.Key, group.Count()))
        .OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase).ToArray();
}
