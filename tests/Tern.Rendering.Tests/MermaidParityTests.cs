using Tern.Rendering.Mermaid;

namespace Tern.Rendering.Tests;

public sealed class MermaidParityTests
{
    [Fact]
    public void Readme_study_map_wraps_consistent_nodes_and_routes_orthogonally()
    {
        const string source = """
            flowchart LR
            Index[README and documentation index] --> Language[Python or C# fundamentals]
            Language --> SQL[SQL and application data]
            SQL --> Cloud[Azure decisions and resources]
            Index --> Tools[Git, Docker and Podman references]
            Index --> Lab[Local AI chat lab]
            """;
        Assert.True(MermaidDiagram.TryParse(source, out var parsed));
        var layout = parsed.Flowchart!.Layout();
        Assert.Equal(6, layout.Nodes.Count);
        Assert.Equal(5, layout.Edges.Count);
        Assert.Single(layout.Nodes.Select(node => node.Bounds.Width).Distinct());
        Assert.Contains(layout.Nodes, node => node.Node.Label.Contains('\n', StringComparison.Ordinal));
        Assert.All(layout.Nodes, node => Assert.InRange(node.Bounds.Width, 120, 220));
        Assert.All(layout.Edges, AssertOrthogonal);
        foreach (var original in parsed.Flowchart.Nodes)
        {
            var rendered = layout.Nodes.Single(node => node.Node.Id == original.Id).Node.Label;
            Assert.Equal(original.Label.Replace(" ", "", StringComparison.Ordinal), rendered.Replace(" ", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Azure_hierarchy_keeps_the_chain_centered_and_fanout_at_one_rank()
    {
        const string source = """
            flowchart TB
            T[Microsoft Entra tenant] --> MG[Management Groups]
            MG --> S[Subscriptions]
            S --> RG[Resource Groups]
            RG --> R[Resources]
            R --> C[Compute]
            R --> D[Data]
            R --> N[Networking]
            R --> I[Identity]
            R --> O[Observability]
            """;
        Assert.True(MermaidDiagram.TryParse(source, out var parsed));
        var layout = parsed.Flowchart!.Layout();
        var chain = new[] { "T", "MG", "S", "RG", "R" }.Select(id => layout.Nodes.Single(node => node.Node.Id == id)).ToArray();
        Assert.All(chain, node => Assert.InRange(Math.Abs(node.Bounds.Center.X - chain[0].Bounds.Center.X), 0, 0.1));
        var leaves = new[] { "C", "D", "N", "I", "O" }.Select(id => layout.Nodes.Single(node => node.Node.Id == id)).ToArray();
        Assert.Single(leaves.Select(node => node.Layer).Distinct());
        Assert.InRange(layout.Height, 250, 700);
        Assert.All(layout.Edges, AssertOrthogonal);
        var fanout = layout.Edges.Where(edge => edge.Edge.From == "R").ToArray();
        Assert.Equal(5, fanout.Select(edge => edge.Points[0].X).Distinct().Count());
        var branchTracks = fanout.SelectMany(edge => Enumerable.Range(1, edge.Points.Count - 1)
            .Where(index => Math.Abs(edge.Points[index].Y - edge.Points[index - 1].Y) < 0.001 && Math.Abs(edge.Points[index].X - edge.Points[index - 1].X) > 1)
            .Select(index => edge.Points[index].Y)).Distinct().ToArray();
        Assert.True(branchTracks.Length >= 2, "Outer and inner relationships must use separate horizontal tracks.");
    }

    [Fact]
    public void Sql_entities_keep_every_relationship_and_cardinality()
    {
        const string source = """
            erDiagram
            categories ||--o{ products : "groups"
            suppliers ||--o{ products : "supplies"
            customers ||--o{ orders : "places"
            employees ||--o{ orders : "handles"
            employees ||--o{ employees : "manages"
            orders ||--o{ order_items : "contains"
            products ||--o{ order_items : "appears on"
            orders ||--o{ payments : "is paid by"
            """;
        Assert.True(MermaidDiagram.TryParse(source, out var parsed), parsed.ErrorMessage);
        Assert.Equal(MermaidResultKind.Flowchart, parsed.Kind);
        var diagram = parsed.Flowchart!;
        Assert.Equal(8, diagram.Nodes.Count);
        Assert.Equal(8, diagram.Edges.Count);
        Assert.All(diagram.Nodes, node => Assert.Equal(FlowNodeShape.Entity, node.Shape));
        Assert.All(diagram.Edges, edge =>
        {
            Assert.Equal(FlowArrowHead.One, edge.StartHead);
            Assert.Equal(FlowArrowHead.ZeroOrMany, edge.EndHead);
        });
        Assert.Contains(diagram.Edges, edge => edge.From == "employees" && edge.To == "employees" && edge.Label == "manages");
        var layout = diagram.Layout();
        Assert.Equal(8, layout.Edges.Count);
        Assert.All(layout.Edges, edge => { Assert.Equal(2, edge.StartArrow.Count); Assert.Equal(2, edge.EndArrow.Count); });
    }

    [Fact]
    public void Entity_attributes_aliases_optional_cardinalities_and_nonidentifying_lines_survive()
    {
        const string source = """
            erDiagram
            CUSTOMER["Customer record"] {
                int id PK "Primary identifier"
                string email UK
            }
            ORDER {
                int customer_id FK
            }
            CUSTOMER |o..|{ ORDER : places
            """;
        Assert.True(MermaidDiagram.TryParse(source, out var parsed), parsed.ErrorMessage);
        var customer = parsed.Flowchart!.Nodes.Single(node => node.Id == "CUSTOMER");
        Assert.Equal("Customer record", customer.Label);
        Assert.Equal(["int id [PK] — Primary identifier", "string email [UK]"], customer.Attributes);
        var edge = Assert.Single(parsed.Flowchart.Edges);
        Assert.Equal(FlowLineStyle.Dotted, edge.Style);
        Assert.Equal(FlowArrowHead.ZeroOrOne, edge.StartHead);
        Assert.Equal(FlowArrowHead.Many, edge.EndHead);
        Assert.True(parsed.Flowchart.Layout().Nodes.Single(node => node.Node.Id == "CUSTOMER").Bounds.Height > 80);
    }

    [Fact]
    public void Mermaid_entity_escapes_are_decoded_as_inert_label_text()
    {
        Assert.True(MermaidDiagram.TryParse("graph LR\nA[\"iter#40;#41; and #quot;text#quot;\"] --> B", out var parsed));
        Assert.Equal("iter() and \"text\"", parsed.Flowchart!.Nodes[0].Label);
    }

    [Theory]
    [InlineData("A -.weakest.-> B", "weakest", FlowLineStyle.Dotted)]
    [InlineData("A --uses--> B", "uses", FlowLineStyle.Solid)]
    [InlineData("A --calls#40;#41;--> B", "calls()", FlowLineStyle.Solid)]
    [InlineData("A ==strong==> B", "strong", FlowLineStyle.Thick)]
    [InlineData("A -->|iter#40;#41; returns a fresh one| B", "iter() returns a fresh one", FlowLineStyle.Solid)]
    [InlineData("A -->|Car#40;...#41;| B", "Car(...)", FlowLineStyle.Solid)]
    public void Corpus_inline_labels_and_entity_semicolons_remain_single_edge_statements(string statement, string label, FlowLineStyle style)
    {
        Assert.True(MermaidDiagram.TryParse("graph LR\n" + statement, out var parsed), parsed.ErrorMessage);
        var edge = Assert.Single(parsed.Flowchart!.Edges);
        Assert.Equal(("A", "B", label, style), (edge.From, edge.To, edge.Label, edge.Style));
    }

    private static void AssertOrthogonal(LaidOutEdge edge)
    {
        for (var index = 1; index < edge.Points.Count; index++)
        {
            var previous = edge.Points[index - 1];
            var point = edge.Points[index];
            Assert.True(Math.Abs(previous.X - point.X) < 0.001 || Math.Abs(previous.Y - point.Y) < 0.001);
        }
    }
}
