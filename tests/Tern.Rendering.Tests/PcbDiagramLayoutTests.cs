using System.Text.RegularExpressions;
using Tern.Rendering.Mermaid;

namespace Tern.Rendering.Tests;

public sealed class PcbDiagramLayoutTests
{
    [Fact]
    public void Supplied_pcb_diagrams_keep_labels_clear_of_nodes_labels_and_foreign_edges()
    {
        var markdown = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MarkdownSamples", "complex_pcb_circuit_network_design.md"));
        var diagrams = Regex.Matches(markdown, @"```mermaid\s*\r?\n(.*?)```", RegexOptions.Singleline);
        Assert.Equal(10, diagrams.Count);
        var failures = new List<string>();
        foreach (Match match in diagrams)
        {
            Assert.True(MermaidDiagram.TryParse(match.Groups[1].Value, out var parsed), parsed.ErrorMessage);
            if (parsed.Flowchart is not { } graph) continue;
            var layout = graph.Layout();
            foreach (var frame in layout.Subgraphs)
            {
                var header = new DiagramRect(frame.Bounds.X, frame.Bounds.Y, frame.Bounds.Width, 24);
                foreach (var node in layout.Nodes)
                    if (header.Overlaps(node.Bounds)) failures.Add($"Group title {frame.Subgraph.Title} overlaps node {node.Node.Id}");
            }
            foreach (var edge in layout.Edges)
            {
                foreach (var segment in DiagramEdgePath.Build(edge.Points))
                {
                    if (segment.Control is not { } control) continue;
                    for (var step = 0; step <= 20; step++)
                    {
                        var t = step / 20d; var u = 1 - t;
                        var point = new DiagramPoint(u * u * segment.Start.X + 2 * u * t * control.X + t * t * segment.End.X,
                            u * u * segment.Start.Y + 2 * u * t * control.Y + t * t * segment.End.Y);
                        foreach (var node in layout.Nodes.Where(node => node.Node.Id != edge.Edge.From && node.Node.Id != edge.Edge.To))
                            if (node.Bounds.Contains(point, -0.01)) failures.Add($"Curved edge {edge.Edge.From}->{edge.Edge.To} enters {node.Node.Id}");
                        foreach (var other in layout.Edges.Where(other => !ReferenceEquals(edge, other)))
                            if (other.LabelBounds is { } otherLabel && otherLabel.Contains(point, -0.01)) failures.Add($"Curve masks label {other.Edge.Label}");
                    }
                }
                foreach (var node in layout.Nodes.Where(node => node.Node.Id != edge.Edge.From && node.Node.Id != edge.Edge.To))
                    for (var i = 1; i < edge.Points.Count; i++)
                        if (Intersects(node.Bounds, edge.Points[i - 1], edge.Points[i])) failures.Add($"Edge {edge.Edge.From}->{edge.Edge.To} crosses node {node.Node.Id}");
                if (edge.LabelBounds is not { } label) continue;
                var onRoute = Enumerable.Range(1, edge.Points.Count - 1).Any(i => Intersects(label, edge.Points[i - 1], edge.Points[i]));
                if (!onRoute && edge.LabelLeader.Count < 2) failures.Add($"Detached label {edge.Edge.Label} has no leader");
                for (var i = 1; i < edge.LabelLeader.Count; i++)
                {
                    foreach (var node in layout.Nodes)
                        if (Intersects(node.Bounds, edge.LabelLeader[i - 1], edge.LabelLeader[i])) failures.Add($"Label leader {edge.Edge.Label} crosses {node.Node.Id}");
                    foreach (var other in layout.Edges.Where(other => !ReferenceEquals(edge, other)))
                        if (other.LabelBounds is { } otherLabel && Intersects(otherLabel, edge.LabelLeader[i - 1], edge.LabelLeader[i])) failures.Add($"Leader {edge.Edge.Label} crosses label {other.Edge.Label}");
                }
                foreach (var node in layout.Nodes)
                    if (label.Overlaps(node.Bounds)) failures.Add($"{edge.Edge.Label} overlaps node {node.Node.Id}");
                foreach (var other in layout.Edges.Where(other => !ReferenceEquals(other, edge)))
                {
                    if (other.LabelBounds is { } otherLabel && label.Overlaps(otherLabel)) failures.Add($"Labels {edge.Edge.Label} / {other.Edge.Label} overlap");
                    for (var i = 1; i < other.Points.Count; i++)
                        if (Intersects(label, other.Points[i - 1], other.Points[i])) failures.Add($"{edge.Edge.Label} masks {other.Edge.From}->{other.Edge.To}");
                }
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void Curves_keep_exact_endpoints_and_tangent_continuity_without_zero_length_segments()
    {
        DiagramPoint[] route = [new(0, 0), new(0, 0), new(0, 30), new(0, 60), new(100, 60), new(100, 120)];
        var path = DiagramEdgePath.Build(route);
        Assert.Equal(route[0], path[0].Start);
        Assert.Equal(route[^1], path[^1].End);
        Assert.Equal(2, path.Count(segment => segment.Control is not null));
        for (var i = 0; i < path.Count; i++)
        {
            Assert.NotEqual(path[i].Start, path[i].End);
            if (i > 0) Assert.Equal(path[i - 1].End, path[i].Start);
            if (path[i].Control is not { } control || i == 0 || i + 1 == path.Count) continue;
            var before = path[i - 1]; var after = path[i + 1];
            Assert.Equal(0, Cross(before.Start, before.End, path[i].Start, control), 6);
            Assert.Equal(0, Cross(control, path[i].End, after.Start, after.End), 6);
        }
    }

    private static double Cross(DiagramPoint a, DiagramPoint b, DiagramPoint c, DiagramPoint d) =>
        (b.X - a.X) * (d.Y - c.Y) - (b.Y - a.Y) * (d.X - c.X);

    private static bool Intersects(DiagramRect r, DiagramPoint a, DiagramPoint b) => Math.Abs(a.X - b.X) < 0.001
        ? a.X > r.X && a.X < r.Right && Math.Max(a.Y, b.Y) > r.Y && Math.Min(a.Y, b.Y) < r.Bottom
        : a.Y > r.Y && a.Y < r.Bottom && Math.Max(a.X, b.X) > r.X && Math.Min(a.X, b.X) < r.Right;
}
