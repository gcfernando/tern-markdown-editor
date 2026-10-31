using Tern.Rendering.Mermaid;

namespace Tern.Rendering.Tests;

public sealed class FlowchartBranchRoutingTests
{
    private const string Dispatch = """
        START["You write: a + b"] --> L["Python calls<br/>type#40;a#41;.__add__#40;a, b#41;"]
        L -->|returns a value| DONE["Result"]
        L -->|returns NotImplemented| R["Python calls<br/>type#40;b#41;.__radd__#40;b, a#41;"]
        R -->|returns a value| DONE
        R -->|returns NotImplemented| ERR["TypeError:<br/>unsupported operand type#40;s#41;"]
        """;

    [Theory]
    [InlineData("TD")]
    [InlineData("BT")]
    [InlineData("LR")]
    [InlineData("RL")]
    public void Operator_fallback_keeps_branch_order_and_clear_labels_in_every_direction(string direction)
    {
        Assert.True(MermaidDiagram.TryParse("graph " + direction + "\n" + Dispatch, out var parsed), parsed.ErrorMessage);
        // Multiline measurement approximates the actual 16 px reading font rather
        // than treating an explicit <br/> as one unusually wide line.
        var layout = parsed.Flowchart!.Layout(text =>
        {
            var lines = text.Split('\n');
            return (lines.Max(line => line.Length) * 8d, lines.Length * 20d);
        });
        var horizontal = direction is "LR" or "RL";
        var result = layout.Nodes.Single(node => node.Node.Id == "DONE").Bounds;
        var error = layout.Nodes.Single(node => node.Node.Id == "ERR").Bounds;
        Assert.True(horizontal ? result.Center.Y < error.Center.Y : result.Center.X < error.Center.X,
            "The first declared success branch must precede the later failure branch on the cross axis.");
        var reflected = layout.Nodes.Single(node => node.Node.Id == "R").Bounds;
        var skip = layout.Edges.Single(edge => edge.Edge.From == "L" && edge.Edge.To == "DONE");
        Assert.Contains(skip.Points, point => horizontal ? point.Y < reflected.Y - 8 : point.X < reflected.X - 8);
        foreach (var edge in layout.Edges)
        {
            for (var index = 1; index < edge.Points.Count; index++)
            {
                var a = edge.Points[index - 1];
                var b = edge.Points[index];
                Assert.True(Math.Abs(a.X - b.X) < 0.001 || Math.Abs(a.Y - b.Y) < 0.001);
                foreach (var node in layout.Nodes.Where(node => node.Node.Id != edge.Edge.From && node.Node.Id != edge.Edge.To))
                    Assert.False(Intersects(node.Bounds, a, b), $"{edge.Edge.From}->{edge.Edge.To} crosses {node.Node.Id}");
            }
            if (edge.LabelBounds is not { } label) continue;
            Assert.DoesNotContain('\n', edge.Edge.Label!);
            Assert.All(layout.Nodes, node => Assert.False(label.Overlaps(node.Bounds), $"Label {edge.Edge.Label} overlaps {node.Node.Id}"));
            foreach (var other in layout.Edges.Where(other => !ReferenceEquals(other, edge)))
            {
                if (other.LabelBounds is { } otherLabel) Assert.False(label.Overlaps(otherLabel));
                for (var index = 1; index < other.Points.Count; index++)
                    Assert.False(Intersects(label, other.Points[index - 1], other.Points[index]), $"Label {edge.Edge.Label} masks {other.Edge.From}->{other.Edge.To}");
            }
        }
    }

    [Fact]
    public void Rank_corridors_keep_skip_edges_outside_tall_intermediate_nodes()
    {
        const string source = "graph TD\nA -->|complete| D\nA --> B[\"one<br/>two<br/>three<br/>four<br/>five<br/>six<br/>seven<br/>eight\"]\nB --> C\nC --> D\n";
        Assert.True(MermaidDiagram.TryParse(source, out var parsed));
        var layout = parsed.Flowchart!.Layout();
        var skip = layout.Edges.Single(edge => edge.Edge.From == "A" && edge.Edge.To == "D");
        foreach (var node in layout.Nodes.Where(node => node.Node.Id is "B" or "C"))
            for (var index = 1; index < skip.Points.Count; index++)
                Assert.False(Intersects(node.Bounds, skip.Points[index - 1], skip.Points[index]));
    }

    private static bool Intersects(DiagramRect rectangle, DiagramPoint a, DiagramPoint b) =>
        Math.Abs(a.X - b.X) < 0.001
            ? a.X > rectangle.X + 0.01 && a.X < rectangle.Right - 0.01 && Math.Max(a.Y, b.Y) > rectangle.Y + 0.01 && Math.Min(a.Y, b.Y) < rectangle.Bottom - 0.01
            : a.Y > rectangle.Y + 0.01 && a.Y < rectangle.Bottom - 0.01 && Math.Max(a.X, b.X) > rectangle.X + 0.01 && Math.Min(a.X, b.X) < rectangle.Right - 0.01;
}
