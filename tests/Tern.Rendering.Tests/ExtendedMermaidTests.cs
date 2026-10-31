using Tern.Rendering.Mermaid;

namespace Tern.Rendering.Tests;

public sealed class ExtendedMermaidTests
{
    private static MermaidParseResult Parse(string source)
    {
        Assert.True(MermaidDiagram.TryParse(source, out var parsed), parsed.ErrorMessage);
        return parsed;
    }

    [Fact]
    public void State_graph_preserves_labels_transitions_and_terminal_markers()
    {
        var graph = Parse("stateDiagram-v2\n[*] --> Idle\nIdle --> Running : begin\nRunning --> Idle : stop\nRunning --> [*]").Flowchart!;
        Assert.Equal(GraphDiagramKind.State, graph.Kind);
        Assert.Equal(4, graph.Nodes.Count);
        Assert.Equal(4, graph.Edges.Count);
        Assert.Contains(graph.Nodes, node => node.Shape == FlowNodeShape.InitialState);
        Assert.Contains(graph.Nodes, node => node.Shape == FlowNodeShape.FinalState);
        Assert.Contains(graph.Edges, edge => edge.Label == "begin");
        Assert.True(graph.Layout().Height > 0);
    }

    [Fact]
    public void Class_graph_preserves_compartments_multiplicities_and_Uml_markers()
    {
        var graph = Parse("classDiagram\nclass Basket {\n+string owner\n+total() decimal\n}\nBasket \"1\" *-- \"0..*\" Item : contains\nService ..> Basket : reads\nBase <|-- Service").Flowchart!;
        Assert.Equal(GraphDiagramKind.Class, graph.Kind);
        var basket = Assert.Single(graph.Nodes, node => node.Id == "Basket");
        Assert.Equal(["+string owner"], basket.Attributes);
        Assert.Equal(["+total() decimal"], basket.Methods);
        var composition = graph.Edges[0];
        Assert.Equal(FlowArrowHead.FilledDiamond, composition.StartHead);
        Assert.Equal("1", composition.StartLabel);
        Assert.Equal("0..*", composition.EndLabel);
        Assert.Equal(FlowLineStyle.Dotted, graph.Edges[1].Style);
        Assert.Equal(FlowArrowHead.HollowTriangle, graph.Edges[2].StartHead);
        var layout = graph.Layout();
        Assert.NotNull(layout.Edges[0].StartLabelBounds);
        Assert.NotNull(layout.Edges[0].EndLabelBounds);
        Assert.All(layout.Nodes, node => Assert.True(node.Bounds.Height >= 22));
    }

    [Fact]
    public void Mindmap_retains_tree_and_has_nonoverlapping_two_sided_branches()
    {
        var graph = Parse("mindmap\n  root((Research))\n    Evidence\n      Papers\n      Experiments\n    Delivery\n      Reports\n      Presentations\n    Review\n      Quality").Flowchart!;
        Assert.Equal(GraphDiagramKind.Mindmap, graph.Kind);
        Assert.Equal(9, graph.Nodes.Count);
        Assert.Equal(8, graph.Edges.Count);
        var layout = graph.Layout();
        foreach (var a in layout.Nodes)
        foreach (var b in layout.Nodes.Where(b => a.Node.Id != b.Node.Id)) Assert.False(a.Bounds.Overlaps(b.Bounds));
        var root = layout.Nodes[0].Bounds.Center.X;
        Assert.Contains(layout.Nodes, node => node.Bounds.Center.X < root);
        Assert.Contains(layout.Nodes, node => node.Bounds.Center.X > root);
    }

    [Theory]
    [InlineData("TD", 7.2, 18)]
    [InlineData("BT", 9, 24)]
    [InlineData("LR", 11, 26)]
    [InlineData("RL", 14, 32)]
    public void Class_cardinality_paint_stays_clear_of_relationship_labels_and_nodes(string direction, double glyphWidth, double lineHeight)
    {
        var graph = Parse("classDiagram\ndirection " + direction + "\nclass ShoppingCart {\n+addItem(item)\n+total() Decimal\n}\nclass CartItem {\n+String name\n+Decimal price\n+int quantity\n}\nclass CheckoutService {\n+checkout(cart)\n}\nShoppingCart \"1\" *-- \"0..*\" CartItem : contains\nCheckoutService ..> ShoppingCart : reads").Flowchart!;
        var layout = graph.Layout(text => (text.Split('\n').Max(line => line.Length) * glyphWidth, text.Split('\n').Length * lineHeight));
        var centerLabels = layout.Edges.Where(edge => edge.LabelBounds is not null).Select(edge => edge.LabelBounds!.Value).ToArray();
        var endLabels = layout.Edges.SelectMany(edge => new[] { edge.StartLabelBounds, edge.EndLabelBounds }).Where(bounds => bounds is not null).Select(bounds => bounds!.Value).ToArray();
        Assert.Equal(2, endLabels.Length);
        foreach (var endpoint in endLabels)
        {
            var paint = new DiagramRect(endpoint.X - 2, endpoint.Y - 2, endpoint.Width + 4, endpoint.Height + 4);
            Assert.InRange(paint.X, 0, layout.Width);
            Assert.InRange(paint.Y, 0, layout.Height);
            Assert.True(paint.Right <= layout.Width && paint.Bottom <= layout.Height);
            foreach (var label in centerLabels)
                Assert.False(paint.Overlaps(new(label.X - 2, label.Y - 2, label.Width + 4, label.Height + 4)));
            foreach (var node in layout.Nodes) Assert.False(paint.Overlaps(node.Bounds));
        }
        Assert.False(endLabels[0].Overlaps(endLabels[1]));
    }

    [Fact]
    public void Pie_uses_actual_values_and_produces_finite_geometry_even_for_huge_numbers()
    {
        var pie = Assert.IsType<PieDiagram>(Parse("pie showData\ntitle Results\n\"Pass\" : 3\n\"Fail\" : 1").Chart);
        Assert.Equal(3, pie.Categories[0].Value);
        var layout = pie.Layout();
        Assert.Contains(layout.Primitives.OfType<ChartText>(), text => text.Text.Contains("75%", StringComparison.Ordinal));
        Assert.Equal(2, layout.Primitives.OfType<ChartPolygon>().Count());
        var huge = Parse("pie\n\"First\" : 1e308\n\"Second\" : 1e308").Chart!.Layout();
        Assert.All(huge.Primitives.OfType<ChartPolygon>().SelectMany(p => p.Points), point => Assert.True(double.IsFinite(point.X) && double.IsFinite(point.Y)));
    }

    [Fact]
    public void Gantt_resolves_dates_dependencies_sections_and_milestone()
    {
        var chart = Assert.IsType<GanttDiagram>(Parse("gantt\ndateFormat YYYY-MM-DD\nsection Plan\nDraft :draft, 2027-03-01, 2d\nReview :review, after draft, 1w\nsection Deliver\nPublish :milestone, after review, 0d").Chart);
        Assert.Equal(new DateOnly(2027, 3, 3), chart.Tasks[1].Start);
        Assert.Equal(new DateOnly(2027, 3, 10), chart.Tasks[2].Start);
        Assert.True(chart.Tasks[2].Milestone);
        Assert.Equal("Deliver", chart.Tasks[2].Section);
        Assert.Single(chart.Layout().Primitives.OfType<ChartPolygon>());
    }

    [Theory]
    [InlineData("pie title Pets", false)]
    [InlineData("pie showData title Pets", true)]
    [InlineData("pie\tshowData\ttitle\tPets", true)]
    public void Pie_accepts_same_line_title_with_optional_data_flag(string header, bool showData)
    {
        var pie = Assert.IsType<PieDiagram>(Parse(header + "\n\"Dogs\" : 5").Chart);
        Assert.Equal("Pets", pie.Title);
        Assert.Equal(showData, pie.ShowData);
        Assert.Single(pie.Categories);
    }

    [Theory]
    [InlineData("pie\n\"bad\" : -1")]
    [InlineData("pie\n\"zero\" : 0")]
    [InlineData("pie\n\"bad\" : 1e999")]
    [InlineData("pie unknown title Pets\n\"Dogs\" : 5")]
    [InlineData("classDiagram\nclass Open {\n+member")]
    [InlineData("mindmap\n root\n otherRoot")]
    [InlineData("stateDiagram-v2\nstate X {")]
    [InlineData("gantt\nA :a, after b, 1d\nB :b, after a, 1d")]
    [InlineData("gantt\nA :after missing, 1d")]
    [InlineData("gantt\nA :9999-12-31, 2d")]
    public void Malformed_or_unimplemented_statements_fail_truthfully(string source)
    {
        Assert.False(MermaidDiagram.TryParse(source, out var parsed));
        Assert.Equal(MermaidResultKind.Error, parsed.Kind);
        Assert.NotEmpty(parsed.ErrorMessage!);
    }

    [Fact]
    public void New_grammars_obey_node_and_depth_budgets()
    {
        var pie = "pie\n" + string.Join('\n', Enumerable.Range(0, 501).Select(i => $"\"Category {i}\" : 1"));
        Assert.False(MermaidDiagram.TryParse(pie, out _));
        var mind = "mindmap\n" + string.Join('\n', Enumerable.Range(0, 66).Select(i => new string(' ', i + 1) + "Node"));
        Assert.False(MermaidDiagram.TryParse(mind, out _));
    }
}
