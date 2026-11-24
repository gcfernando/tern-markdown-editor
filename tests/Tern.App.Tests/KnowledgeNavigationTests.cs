using Tern.App.Panels;
using Tern.Markdown;
using Tern.Workspaces;

namespace Tern.App.Tests;

public sealed class KnowledgeNavigationTests
{
    [Fact]
    public void Wiki_candidates_preserve_aliases_headings_and_disambiguate_duplicate_filenames()
    {
        var root = Path.Combine(Path.GetTempPath(), "tern-wiki-candidates");
        var graph = new LinkGraph(root);
        var source = Path.Combine(root, "source.md");
        graph.Update(source, "# Source");
        graph.Update(Path.Combine(root, "alpha", "note.md"), "---\ntitle: First note\naliases: [Overview]\n---\n# First note\n## Details");
        graph.Update(Path.Combine(root, "beta", "note.md"), "# Second note");
        var candidates = KnowledgeNavigation.WikiLinks(graph, source);
        Assert.Contains(candidates, candidate => candidate.Label.Contains("Overview", StringComparison.Ordinal));
        Assert.Contains(candidates, candidate => candidate.Destination.EndsWith("#details", StringComparison.Ordinal));
        Assert.Equal(2, candidates.Where(candidate => candidate.Destination.EndsWith("note.md", StringComparison.Ordinal)).Select(candidate => candidate.Path).Distinct().Count());
        foreach (var candidate in candidates)
        {
            var resolved = graph.Resolve(source, new LinkInfo(LinkKind.WikiLink, candidate.Destination, default, 1, candidate.Label));
            Assert.Equal(LinkStatus.Ok, resolved.Status);
            Assert.Equal(candidate.Path, resolved.TargetPath);
        }
    }

    [Fact]
    public void Tags_count_documents_not_duplicate_metadata_values_and_follow_updates()
    {
        var root = Path.Combine(Path.GetTempPath(), "tern-tag-candidates");
        var graph = new LinkGraph(root);
        var first = Path.Combine(root, "one.md");
        graph.Update(first, "---\ntags: [Writing, writing, review]\n---\n# One");
        graph.Update(Path.Combine(root, "two.md"), "---\ntags: [writing]\n---\n# Two");
        Assert.Equal(2, KnowledgeNavigation.Tags(graph).Single(tag => tag.Name.Equals("writing", StringComparison.OrdinalIgnoreCase)).Documents);
        graph.Update(first, "---\ntags: [planning]\n---\n# One");
        Assert.Equal(1, KnowledgeNavigation.Tags(graph).Single(tag => tag.Name.Equals("writing", StringComparison.OrdinalIgnoreCase)).Documents);
        Assert.DoesNotContain(KnowledgeNavigation.Tags(graph), tag => tag.Name == "review");
    }
}
