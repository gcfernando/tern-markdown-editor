using Tern.Workspaces;

namespace Tern.Workspaces.Tests;

public sealed class HtmlAnchorGraphTests
{
    [Fact]
    public void Explicit_id_name_and_raw_heading_targets_are_not_reported_as_broken()
    {
        using var directory = new TempDirectory();
        var graph = new LinkGraph(directory.Path);
        var source = directory.Combine("index.md");
        var target = directory.Combine("guide.md");
        graph.Update(source, "[toc](guide.md#toc) [legacy](guide.md#europe) [play](guide.md#how-to-play) [generated](guide.md#raw-title) [unicode](guide.md#caf%C3%A9) [missing](guide.md#missing)");
        graph.Update(target, "<a id=\"toc\"></a>\n\n<a name=\"europe\"></a>\n\n<h1 id=\"how-to-play\">Play</h1>\n\n<h2>Raw title</h2>\n\n<a id=\"café\"></a>");
        var outgoing = graph.GetOutgoing(source);
        Assert.Equal(5, outgoing.Count(l => l.Status == LinkStatus.Ok));
        Assert.Equal("missing", Assert.Single(graph.GetBrokenLinks()).Fragment);
        graph.Update(target, "# Replaced\n");
        Assert.Equal(6, graph.GetBrokenLinks(source).Count);
    }

    [Fact]
    public void Targets_mentioned_only_in_code_or_script_stay_missing()
    {
        using var directory = new TempDirectory();
        var graph = new LinkGraph(directory.Path);
        var source = directory.Combine("index.md");
        var target = directory.Combine("guide.md");
        graph.Update(source, "[hidden](guide.md#secret)");
        graph.Update(target, "```html\n<a id=\"secret\"></a>\n```\n\n<script><a id=\"secret\"></a></script>");
        Assert.Equal(LinkStatus.MissingAnchor, Assert.Single(graph.GetOutgoing(source)).Status);
    }
}
