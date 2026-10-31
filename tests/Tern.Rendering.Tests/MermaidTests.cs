using System.Diagnostics;
using Tern.Rendering.Mermaid;

namespace Tern.Rendering.Tests;

public sealed class MermaidFlowchartTests
{
    private static FlowchartDiagram Parse(string source)
    {
        Assert.True(MermaidDiagram.TryParse(source, out var result), result.ErrorMessage ?? result.UnsupportedType);
        Assert.Equal(MermaidResultKind.Flowchart, result.Kind);
        return result.Flowchart!;
    }

    private static MermaidParseResult ParseAny(string source)
    {
        _ = MermaidDiagram.TryParse(source, out var result);
        return result;
    }

    private static FlowNode Node(FlowchartDiagram diagram, string id) => diagram.Nodes.Single(n => n.Id == id);

    [Theory]
    [InlineData("graph TD", FlowDirection.TopDown)]
    [InlineData("graph TB", FlowDirection.TopDown)]
    [InlineData("flowchart LR", FlowDirection.LeftRight)]
    [InlineData("flowchart RL", FlowDirection.RightLeft)]
    [InlineData("graph BT", FlowDirection.BottomUp)]
    [InlineData("flowchart", FlowDirection.TopDown)]
    public void Directions_are_parsed(string header, FlowDirection expected)
    {
        Assert.Equal(expected, Parse(header + "\n  A --> B").Direction);
    }

    [Fact]
    public void Node_shapes_and_labels_are_recognised()
    {
        var diagram = Parse("graph TD\n  A[Rect] --> B(Round)\n  B --> C{Decision}\n  C --> D((Circle))\n  D --> E([Stadium])\n  E --> F[[Sub]]\n  F --> G[(DB)]\n  G --> H{{Hex}}");

        Assert.Equal((FlowNodeShape.Rectangle, "Rect"), (Node(diagram, "A").Shape, Node(diagram, "A").Label));
        Assert.Equal((FlowNodeShape.Rounded, "Round"), (Node(diagram, "B").Shape, Node(diagram, "B").Label));
        Assert.Equal((FlowNodeShape.Diamond, "Decision"), (Node(diagram, "C").Shape, Node(diagram, "C").Label));
        Assert.Equal((FlowNodeShape.Circle, "Circle"), (Node(diagram, "D").Shape, Node(diagram, "D").Label));
        Assert.Equal((FlowNodeShape.Stadium, "Stadium"), (Node(diagram, "E").Shape, Node(diagram, "E").Label));
        Assert.Equal(FlowNodeShape.Subroutine, Node(diagram, "F").Shape);
        Assert.Equal(FlowNodeShape.Cylinder, Node(diagram, "G").Shape);
        Assert.Equal(FlowNodeShape.Hexagon, Node(diagram, "H").Shape);
    }

    [Fact]
    public void Quoted_labels_may_contain_special_characters()
    {
        var diagram = Parse("graph TD\n  A[\"Say (hi) & [bye]\"] --> B");

        Assert.Equal("Say (hi) & [bye]", Node(diagram, "A").Label);
    }

    [Fact]
    public void A_bare_identifier_is_a_rectangle_labelled_with_its_id()
    {
        var diagram = Parse("graph TD\n  Start --> End");

        Assert.Equal((FlowNodeShape.Rectangle, "Start"), (Node(diagram, "Start").Shape, Node(diagram, "Start").Label));
    }

    [Fact]
    public void A_later_definition_gives_a_node_its_label()
    {
        var diagram = Parse("graph TD\n  A --> B\n  A[Alpha]");

        Assert.Equal("Alpha", Node(diagram, "A").Label);
        Assert.Equal(2, diagram.Nodes.Count);
    }

    [Theory]
    [InlineData("A --> B", FlowLineStyle.Solid, FlowArrowHead.None, FlowArrowHead.Arrow)]
    [InlineData("A --- B", FlowLineStyle.Solid, FlowArrowHead.None, FlowArrowHead.None)]
    [InlineData("A -.-> B", FlowLineStyle.Dotted, FlowArrowHead.None, FlowArrowHead.Arrow)]
    [InlineData("A -.- B", FlowLineStyle.Dotted, FlowArrowHead.None, FlowArrowHead.None)]
    [InlineData("A ==> B", FlowLineStyle.Thick, FlowArrowHead.None, FlowArrowHead.Arrow)]
    [InlineData("A === B", FlowLineStyle.Thick, FlowArrowHead.None, FlowArrowHead.None)]
    [InlineData("A <--> B", FlowLineStyle.Solid, FlowArrowHead.Arrow, FlowArrowHead.Arrow)]
    [InlineData("A --o B", FlowLineStyle.Solid, FlowArrowHead.None, FlowArrowHead.Circle)]
    [InlineData("A --x B", FlowLineStyle.Solid, FlowArrowHead.None, FlowArrowHead.Cross)]
    [InlineData("A ~~~ B", FlowLineStyle.Invisible, FlowArrowHead.None, FlowArrowHead.None)]
    [InlineData("A ----> B", FlowLineStyle.Solid, FlowArrowHead.None, FlowArrowHead.Arrow)]
    public void Edge_operators_map_to_style_and_heads(string statement, FlowLineStyle style, FlowArrowHead start, FlowArrowHead end)
    {
        var edge = Assert.Single(Parse("graph LR\n  " + statement).Edges);

        Assert.Equal(("A", "B"), (edge.From, edge.To));
        Assert.Equal((style, start, end), (edge.Style, edge.StartHead, edge.EndHead));
        Assert.Null(edge.Label);
    }

    [Theory]
    [InlineData("A -->|yes| B", "yes")]
    [InlineData("A -- yes --> B", "yes")]
    [InlineData("A -- two words --> B", "two words")]
    [InlineData("A -.->|maybe| B", "maybe")]
    [InlineData("A -. maybe .-> B", "maybe")]
    [InlineData("A ==>|big| B", "big")]
    [InlineData("A == big ==> B", "big")]
    [InlineData("A -->|\"quoted | pipe\"| B", "quoted | pipe")]
    public void Edge_labels_are_read_in_all_notations(string statement, string label)
    {
        var edge = Assert.Single(Parse("graph TD\n  " + statement).Edges);

        Assert.Equal(label, edge.Label);
        Assert.Equal(("A", "B"), (edge.From, edge.To));
    }

    [Fact]
    public void Chained_edges_create_one_edge_per_link()
    {
        var diagram = Parse("graph TD\n  A --> B --> C -.-> D");

        Assert.Equal([("A", "B"), ("B", "C"), ("C", "D")], diagram.Edges.Select(e => (e.From, e.To)));
        Assert.Equal(FlowLineStyle.Dotted, diagram.Edges[2].Style);
    }

    [Fact]
    public void Ampersand_fans_out_to_every_combination()
    {
        var diagram = Parse("graph TD\n  A & B --> C & D");

        Assert.Equal(4, diagram.Edges.Count);
        Assert.Equal(
            [("A", "C"), ("A", "D"), ("B", "C"), ("B", "D")],
            diagram.Edges.Select(e => (e.From, e.To)).OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal));
    }

    [Fact]
    public void Semicolons_separate_statements()
    {
        var diagram = Parse("graph TD; A-->B; B-->C;");

        Assert.Equal(2, diagram.Edges.Count);
    }

    [Fact]
    public void Subgraphs_collect_their_nodes_and_may_nest()
    {
        var diagram = Parse("graph TD\n  subgraph one [First Group]\n    A --> B\n    subgraph inner\n      C\n    end\n  end\n  B --> D\n");

        var one = diagram.Subgraphs.Single(s => s.Id == "one");
        Assert.Equal("First Group", one.Title);
        Assert.Contains("A", one.NodeIds);
        Assert.Contains("B", one.NodeIds);
        Assert.DoesNotContain("D", one.NodeIds);
        Assert.Contains("C", diagram.Subgraphs.Single(s => s.Id == "inner").NodeIds);
        Assert.Equal(4, diagram.Nodes.Count);
    }

    [Fact]
    public void Styling_directives_comments_and_front_matter_are_ignored_gracefully()
    {
        var diagram = Parse("---\ntitle: Demo\n---\n%% a comment\ngraph TD\n  A --> B\n  classDef green fill:#9f6,stroke:#333\n  class A green\n  style B fill:#f9f\n  linkStyle 0 stroke:red\n  click A callback\n  A:::green\n");

        Assert.Equal(2, diagram.Nodes.Count);
        Assert.Single(diagram.Edges);
    }

    [Fact]
    public void Html_breaks_in_labels_become_newlines_and_html_is_not_kept()
    {
        var diagram = Parse("graph TD\n  A[\"line one<br/>line two\"] --> B[<b>x</b><script>alert(1)</script>]");

        Assert.Equal("line one\nline two", Node(diagram, "A").Label);
        Assert.DoesNotContain("<script", Node(diagram, "B").Label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parsing_reports_unsupported_diagram_types_honestly()
    {
        foreach (var type in new[] { "journey", "timeline", "gitGraph", "quadrantChart", "requirementDiagram" })
        {
            var parsed = MermaidDiagram.TryParse(type + "\n  something", out var result);

            Assert.False(parsed);
            Assert.Equal(MermaidResultKind.Unsupported, result.Kind);
            Assert.Equal(type, result.UnsupportedType);
            Assert.Null(result.Flowchart);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\n")]
    [InlineData("notADiagram\n A --> B")]
    [InlineData("graph TD\n  A -->")]
    [InlineData("graph TD\n  --> B")]
    [InlineData("graph TD\n  A[unterminated --> B")]
    [InlineData("graph TD\n  subgraph x\n  A --> B")]
    [InlineData("graph TD\n  end")]
    [InlineData("graph XX\n  A --> B")]
    public void Malformed_input_returns_an_error_with_a_line_number(string source)
    {
        var parsed = MermaidDiagram.TryParse(source, out var result);

        Assert.False(parsed);
        Assert.Equal(MermaidResultKind.Error, result.Kind);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.True(result.ErrorLine >= 0);
    }

    [Fact]
    public void Error_line_numbers_point_at_the_offending_line()
    {
        var result = ParseAny("graph TD\n  A --> B\n\n  C -->\n  D --> E");

        Assert.Equal(MermaidResultKind.Error, result.Kind);
        Assert.Equal(4, result.ErrorLine);
    }

    [Fact]
    public void Null_source_is_an_error_not_an_exception()
    {
        Assert.False(MermaidDiagram.TryParse(null, out var result));
        Assert.Equal(MermaidResultKind.Error, result.Kind);
    }

    [Fact]
    public void Node_budget_is_enforced()
    {
        var within = "graph TD\n" + string.Join('\n', Enumerable.Range(0, MermaidLimits.MaxNodes - 1).Select(i => $"  N{i} --> N{i + 1}"));
        var over = "graph TD\n" + string.Join('\n', Enumerable.Range(0, MermaidLimits.MaxNodes + 1).Select(i => $"  N{i} --> N{i + 1}"));

        Assert.True(MermaidDiagram.TryParse(within, out _));
        var parsed = MermaidDiagram.TryParse(over, out var result);
        Assert.False(parsed);
        Assert.Equal(MermaidResultKind.Error, result.Kind);
        Assert.Contains("node", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Edge_budget_is_enforced()
    {
        var edges = string.Join('\n', Enumerable.Range(0, MermaidLimits.MaxEdges + 1).Select(i => $"  A{i % 50} --> B{i % 50}"));

        var parsed = MermaidDiagram.TryParse("graph TD\n" + edges, out var result);

        Assert.False(parsed);
        Assert.Equal(MermaidResultKind.Error, result.Kind);
        Assert.Contains("edge", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Source_size_budget_is_enforced()
    {
        var source = "graph TD\n  A[" + new string('x', MermaidLimits.MaxSourceLength) + "] --> B";

        Assert.False(MermaidDiagram.TryParse(source, out var result));
        Assert.Equal(MermaidResultKind.Error, result.Kind);
    }

    [Fact]
    public void Overlong_lines_are_rejected()
    {
        var source = "graph TD\n  A[" + new string('x', MermaidLimits.MaxLineLength + 10) + "] --> B";

        Assert.False(MermaidDiagram.TryParse(source, out var result));
        Assert.Equal(2, result.ErrorLine);
    }

    [Fact]
    public void Random_input_never_throws()
    {
        var random = new Random(424242);
        string[] fragments =
        [
            "graph TD\n", "flowchart LR\n", "sequenceDiagram\n", "A", "B", "C1", " --> ", " --- ", " -.-> ", " ==> ", "-->|x|", "[", "]", "(", ")", "{", "}", "((", "))", "([", "])",
            "\"", "'", "&", ";", "\n", "  ", "subgraph s\n", "end\n", "classDef a b\n", "%%", "<br/>", "|", ":::", "A->>B: hi\n", "participant X\n", "Note over A: t\n", "loop x\n", "😀", "\u0000", "\t",
        ];

        for (var i = 0; i < 3000; i++)
        {
            var source = string.Concat(Enumerable.Range(0, random.Next(1, 30)).Select(_ => fragments[random.Next(fragments.Length)]));

            _ = MermaidDiagram.TryParse(source, out var result);

            Assert.True(Enum.IsDefined(result.Kind));
            if (result.Kind == MermaidResultKind.Flowchart)
            {
                var layout = result.Flowchart!.Layout();
                Assert.True(layout.Width >= 0 && layout.Height >= 0);
            }
            else if (result.Kind == MermaidResultKind.Sequence)
            {
                var layout = result.Sequence!.Layout();
                Assert.True(layout.Width >= 0 && layout.Height >= 0);
            }
        }
    }

    [Fact]
    public void Parsing_pathological_input_is_fast()
    {
        var sources = new[]
        {
            "graph TD\n" + string.Concat(Enumerable.Repeat("A-", 2000)),
            "graph TD\n  A" + string.Concat(Enumerable.Repeat(" --> B", 900)),
            "graph TD\n  A[" + string.Concat(Enumerable.Repeat("[", 1500)),
            "graph TD\n" + string.Concat(Enumerable.Repeat("subgraph s\n", 400)),
            "graph TD\n  A -- " + string.Concat(Enumerable.Repeat("- ", 1500)),
            "graph TD\n" + string.Concat(Enumerable.Repeat("A -->|", 600)),
        };

        var watch = Stopwatch.StartNew();
        foreach (var source in sources)
        {
            _ = MermaidDiagram.TryParse(source, out _);
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    // ---- layout ----

    private static FlowchartLayout Layout(string source, Func<string, (double, double)>? measurer = null) =>
        Parse(source).Layout(measurer is null ? null : text => measurer(text));

    private static double DistanceToBorder(DiagramRect rect, DiagramPoint p)
    {
        var dx = Math.Min(Math.Abs(p.X - rect.X), Math.Abs(p.X - rect.Right));
        var dy = Math.Min(Math.Abs(p.Y - rect.Y), Math.Abs(p.Y - rect.Bottom));
        var insideX = p.X >= rect.X - 0.01 && p.X <= rect.Right + 0.01;
        var insideY = p.Y >= rect.Y - 0.01 && p.Y <= rect.Bottom + 0.01;
        if (insideX && insideY)
        {
            return Math.Min(dx, dy);
        }

        return insideX ? dy : insideY ? dx : Math.Min(dx, dy);
    }

    private static void AssertLayoutInvariants(FlowchartLayout layout)
    {
        foreach (var node in layout.Nodes)
        {
            var b = node.Bounds;
            Assert.True(b.Width > 0 && b.Height > 0, node.Node.Id);
            Assert.True(b.X >= 0 && b.Y >= 0 && b.Right <= layout.Width + 0.01 && b.Bottom <= layout.Height + 0.01, $"{node.Node.Id} outside canvas");
        }

        foreach (var layer in layout.Nodes.GroupBy(n => n.Layer))
        {
            var members = layer.ToList();
            for (var i = 0; i < members.Count; i++)
            {
                for (var j = i + 1; j < members.Count; j++)
                {
                    Assert.False(members[i].Bounds.Overlaps(members[j].Bounds), $"{members[i].Node.Id} overlaps {members[j].Node.Id} in layer {layer.Key}");
                }
            }
        }

        var byId = layout.Nodes.ToDictionary(n => n.Node.Id, StringComparer.Ordinal);
        foreach (var edge in layout.Edges.Where(e => e.Edge.Style != FlowLineStyle.Invisible))
        {
            Assert.True(edge.Points.Count >= 2, $"{edge.Edge.From}->{edge.Edge.To} has no polyline");
            var from = byId[edge.Edge.From].Bounds;
            var to = byId[edge.Edge.To].Bounds;
            if (edge.Edge.From == edge.Edge.To)
            {
                continue;
            }

            Assert.True(from.Contains(edge.Points[0], 1.5) || DistanceToBorder(from, edge.Points[0]) <= 1.5, $"{edge.Edge.From}->{edge.Edge.To} does not start at its source");
            Assert.True(to.Contains(edge.Points[^1], 1.5) || DistanceToBorder(to, edge.Points[^1]) <= 1.5, $"{edge.Edge.From}->{edge.Edge.To} does not end at its target");
            foreach (var point in edge.Points)
            {
                Assert.True(double.IsFinite(point.X) && double.IsFinite(point.Y));
                Assert.True(point.X >= -0.01 && point.Y >= -0.01 && point.X <= layout.Width + 0.01 && point.Y <= layout.Height + 0.01, "edge point outside canvas");
            }
        }
    }

    [Fact]
    public void A_simple_chain_flows_top_down_in_increasing_layers()
    {
        var layout = Layout("graph TD\n  A --> B --> C");

        var a = layout.Nodes.Single(n => n.Node.Id == "A");
        var b = layout.Nodes.Single(n => n.Node.Id == "B");
        var c = layout.Nodes.Single(n => n.Node.Id == "C");
        Assert.True(a.Layer < b.Layer && b.Layer < c.Layer);
        Assert.True(a.Bounds.Bottom <= b.Bounds.Y && b.Bounds.Bottom <= c.Bounds.Y);
        AssertLayoutInvariants(layout);
    }

    [Theory]
    [InlineData("TD")]
    [InlineData("LR")]
    [InlineData("RL")]
    [InlineData("BT")]
    public void Every_direction_lays_out_a_branching_graph_without_overlap(string direction)
    {
        var layout = Layout($"graph {direction}\n  A[Start] --> B{{Check}}\n  B -->|yes| C[Do it]\n  B -->|no| D[Skip]\n  C --> E((End))\n  D --> E\n  A --> E");

        AssertLayoutInvariants(layout);
        Assert.Equal(5, layout.Nodes.Count);
        Assert.Equal(6, layout.Edges.Count);
    }

    [Fact]
    public void Direction_controls_the_axis_of_the_layers()
    {
        var td = Layout("graph TD\n  A --> B");
        var lr = Layout("graph LR\n  A --> B");
        var bt = Layout("graph BT\n  A --> B");
        var rl = Layout("graph RL\n  A --> B");

        Assert.True(td.Nodes[0].Bounds.Y < td.Nodes[1].Bounds.Y);
        Assert.True(lr.Nodes[0].Bounds.X < lr.Nodes[1].Bounds.X);
        Assert.True(bt.Nodes[0].Bounds.Y > bt.Nodes[1].Bounds.Y);
        Assert.True(rl.Nodes[0].Bounds.X > rl.Nodes[1].Bounds.X);
        Assert.True(lr.Width > lr.Height);
        Assert.True(td.Height > td.Width);
    }

    [Fact]
    public void Siblings_share_a_layer_and_sit_side_by_side()
    {
        var layout = Layout("graph TD\n  A --> B\n  A --> C\n  A --> D");

        var children = layout.Nodes.Where(n => n.Node.Id != "A").ToList();
        Assert.Single(children.Select(c => c.Layer).Distinct());
        Assert.Equal(1, Math.Round(children.Select(c => c.Bounds.Y).Distinct().Count() / 1.0));
        AssertLayoutInvariants(layout);
    }

    [Fact]
    public void Cycles_are_tolerated_and_every_node_is_placed()
    {
        var layout = Layout("graph TD\n  A --> B --> C --> A\n  C --> C\n  B --> A");

        Assert.Equal(3, layout.Nodes.Count);
        Assert.Equal(5, layout.Edges.Count);
        AssertLayoutInvariants(layout);
    }

    [Fact]
    public void Disconnected_components_are_all_laid_out()
    {
        var layout = Layout("graph LR\n  A --> B\n  C --> D\n  E");

        Assert.Equal(5, layout.Nodes.Count);
        AssertLayoutInvariants(layout);
    }

    [Fact]
    public void Long_edges_skipping_layers_are_routed_with_extra_points()
    {
        var layout = Layout("graph TD\n  A --> B --> C --> D\n  A --> D");

        var long_ = layout.Edges.Single(e => e.Edge.From == "A" && e.Edge.To == "D");
        Assert.True(long_.Points.Count >= 2);
        AssertLayoutInvariants(layout);
    }

    [Fact]
    public void Edge_arrowheads_are_geometry_near_the_target_end()
    {
        var layout = Layout("graph TD\n  A --> B\n  C --- D");

        var arrow = layout.Edges.Single(e => e.Edge.From == "A");
        var plain = layout.Edges.Single(e => e.Edge.From == "C");
        Assert.True(arrow.EndArrow.Count >= 3);
        Assert.Empty(arrow.StartArrow);
        Assert.Empty(plain.EndArrow);
        var target = layout.Nodes.Single(n => n.Node.Id == "B").Bounds;
        Assert.All(arrow.EndArrow, p => Assert.True(Math.Abs(p.Y - target.Y) < 20));
    }

    [Fact]
    public void Edge_labels_get_bounds_that_do_not_cover_nodes()
    {
        var layout = Layout("graph TD\n  A -->|a fairly long label| B");

        var edge = Assert.Single(layout.Edges);
        Assert.NotNull(edge.LabelBounds);
        foreach (var node in layout.Nodes)
        {
            Assert.False(edge.LabelBounds.Value.Overlaps(node.Bounds), $"label covers {node.Node.Id}");
        }
    }

    [Fact]
    public void Subgraph_frames_contain_their_member_nodes()
    {
        var layout = Layout("graph TD\n  subgraph g [Group]\n    A --> B\n  end\n  B --> C");

        var frame = Assert.Single(layout.Subgraphs).Bounds;
        foreach (var id in new[] { "A", "B" })
        {
            var node = layout.Nodes.Single(n => n.Node.Id == id).Bounds;
            Assert.True(node.X >= frame.X && node.Y >= frame.Y && node.Right <= frame.Right && node.Bottom <= frame.Bottom, $"{id} not inside frame");
        }
    }

    [Fact]
    public void Node_size_follows_the_text_measurer()
    {
        var narrow = Layout("graph TD\n  A[Hello]", _ => (10, 10));
        var wide = Layout("graph TD\n  A[Hello]", _ => (200, 40));

        Assert.True(wide.Nodes[0].Bounds.Width > narrow.Nodes[0].Bounds.Width);
        Assert.True(wide.Nodes[0].Bounds.Height > narrow.Nodes[0].Bounds.Height);
        Assert.True(wide.Nodes[0].Bounds.Width >= 200);
    }

    [Fact]
    public void Multiline_labels_make_taller_nodes()
    {
        var layout = Layout("graph TD\n  A[\"one\"] --> B[\"one<br/>two<br/>three\"]");

        var a = layout.Nodes.Single(n => n.Node.Id == "A").Bounds;
        var b = layout.Nodes.Single(n => n.Node.Id == "B").Bounds;
        Assert.True(b.Height > a.Height);
    }

    [Fact]
    public void A_measurer_returning_garbage_cannot_break_the_layout()
    {
        var layout = Layout("graph TD\n  A --> B", _ => (double.NaN, -5));

        AssertLayoutInvariants(layout);
    }

    [Fact]
    public void Layout_is_deterministic()
    {
        const string source = "graph TD\n  A --> B & C\n  B --> D\n  C --> D\n  D --> A\n  subgraph s\n    E --> F\n  end\n  F --> A";

        var first = Layout(source);
        var second = Layout(source);

        Assert.Equal(first.Width, second.Width);
        Assert.Equal(first.Height, second.Height);
        Assert.Equal(first.Nodes.Select(n => (n.Node.Id, n.Bounds, n.Layer)), second.Nodes.Select(n => (n.Node.Id, n.Bounds, n.Layer)));
        Assert.Equal(first.Edges.Select(e => e.Points), second.Edges.Select(e => e.Points), new PointListComparer());
    }

    [Fact]
    public void Input_order_does_not_change_connectivity_geometry_validity()
    {
        var forward = Layout("graph TD\n  A --> B\n  A --> C\n  B --> D\n  C --> D");
        var reversed = Layout("graph TD\n  C --> D\n  B --> D\n  A --> C\n  A --> B");

        AssertLayoutInvariants(forward);
        AssertLayoutInvariants(reversed);
        Assert.Equal(forward.Nodes.Max(n => n.Layer), reversed.Nodes.Max(n => n.Layer));
    }

    [Fact]
    public void A_maximum_size_graph_lays_out_in_reasonable_time()
    {
        var source = "graph TD\n" + string.Join('\n', Enumerable.Range(1, 400).Select(i => $"  N{i / 2} --> N{i}")) + "\n" + string.Join('\n', Enumerable.Range(1, 200).Select(i => $"  N{i} --> N{(i * 7) % 400}"));
        var diagram = Parse(source);

        var watch = Stopwatch.StartNew();
        var layout = diagram.Layout();
        watch.Stop();

        Assert.Equal(diagram.Nodes.Count, layout.Nodes.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"layout took {watch.Elapsed}");
        AssertLayoutInvariants(layout);
    }

    [Fact]
    public void Rectangle_edge_endpoints_lie_exactly_on_the_node_border()
    {
        var layout = Layout("graph TD\n  A[Top] --> B[Bottom]");

        var edge = Assert.Single(layout.Edges);
        var a = layout.Nodes.Single(n => n.Node.Id == "A").Bounds;
        var b = layout.Nodes.Single(n => n.Node.Id == "B").Bounds;
        Assert.InRange(DistanceToBorder(a, edge.Points[0]), 0, 0.5);
        Assert.InRange(DistanceToBorder(b, edge.Points[^1]), 0, 0.5);
        Assert.True(Math.Abs(edge.Points[0].Y - a.Bottom) < 0.5);
        Assert.True(Math.Abs(edge.Points[^1].Y - b.Y) < 0.5);
    }

    private sealed class PointListComparer : IEqualityComparer<IReadOnlyList<DiagramPoint>>
    {
        public bool Equals(IReadOnlyList<DiagramPoint>? x, IReadOnlyList<DiagramPoint>? y) => x is not null && y is not null && x.SequenceEqual(y);

        public int GetHashCode(IReadOnlyList<DiagramPoint> obj) => obj.Count;
    }
}

public sealed class MermaidSequenceTests
{
    private static SequenceDiagram Parse(string source)
    {
        Assert.True(MermaidDiagram.TryParse(source, out var result), result.ErrorMessage ?? result.UnsupportedType);
        Assert.Equal(MermaidResultKind.Sequence, result.Kind);
        return result.Sequence!;
    }

    [Fact]
    public void Participants_and_actors_are_declared_with_aliases()
    {
        var diagram = Parse("sequenceDiagram\n  participant A as Alice\n  actor B as Bob the Builder\n  participant C\n");

        Assert.Equal([("A", "Alice", false), ("B", "Bob the Builder", true), ("C", "C", false)], diagram.Participants.Select(p => (p.Id, p.Label, p.IsActor)));
    }

    [Fact]
    public void Participants_are_created_implicitly_in_order_of_appearance()
    {
        var diagram = Parse("sequenceDiagram\n  Alice->>Bob: Hi\n  Bob-->>Carol: Forward\n");

        Assert.Equal(["Alice", "Bob", "Carol"], diagram.Participants.Select(p => p.Id));
    }

    [Theory]
    [InlineData("A->>B: t", false, SequenceHead.Filled)]
    [InlineData("A-->>B: t", true, SequenceHead.Filled)]
    [InlineData("A->B: t", false, SequenceHead.None)]
    [InlineData("A-->B: t", true, SequenceHead.None)]
    [InlineData("A-xB: t", false, SequenceHead.Cross)]
    [InlineData("A--xB: t", true, SequenceHead.Cross)]
    [InlineData("A-)B: t", false, SequenceHead.Open)]
    [InlineData("A--)B: t", true, SequenceHead.Open)]
    public void Message_arrows_map_to_style_and_head(string statement, bool dashed, SequenceHead head)
    {
        var message = Assert.IsType<SequenceMessage>(Assert.Single(Parse("sequenceDiagram\n  " + statement).Events));

        Assert.Equal(("A", "B", "t"), (message.From, message.To, message.Text));
        Assert.Equal((dashed, head), (message.IsDashed, message.Head));
    }

    [Fact]
    public void Notes_support_all_placements()
    {
        var diagram = Parse("sequenceDiagram\n  A->>B: x\n  Note left of A: l\n  Note right of B: r\n  Note over A,B: both\n");

        var notes = diagram.Events.OfType<SequenceNote>().ToList();
        Assert.Equal([SequenceNotePlacement.LeftOf, SequenceNotePlacement.RightOf, SequenceNotePlacement.Over], notes.Select(n => n.Placement));
        Assert.Equal(["A", "B"], notes[2].Participants);
        Assert.Equal("both", notes[2].Text);
    }

    [Fact]
    public void Frames_nest_and_carry_dividers()
    {
        var diagram = Parse("sequenceDiagram\n  A->>B: go\n  alt ok\n    B->>A: yes\n  else bad\n    loop retry\n      B->>A: no\n    end\n  end\n");

        var kinds = diagram.Events.Select(e => e.GetType().Name).ToList();
        Assert.Equal(2, diagram.Events.OfType<SequenceFrameStart>().Count());
        Assert.Equal(2, diagram.Events.OfType<SequenceFrameEnd>().Count());
        Assert.Single(diagram.Events.OfType<SequenceFrameDivider>());
        Assert.Equal("alt", diagram.Events.OfType<SequenceFrameStart>().First().Kind);
        Assert.Equal("ok", diagram.Events.OfType<SequenceFrameStart>().First().Label);
        Assert.NotEmpty(kinds);
    }

    [Fact]
    public void Titles_autonumber_and_activation_statements_are_accepted()
    {
        var diagram = Parse("sequenceDiagram\n  title Checkout\n  autonumber\n  A->>B: one\n  activate B\n  B-->>A: two\n  deactivate B\n");

        Assert.Equal("Checkout", diagram.Title);
        var numbers = diagram.Events.OfType<SequenceMessage>().Select(m => m.Number).ToList();
        Assert.Equal([1, 2], numbers);
    }

    [Fact]
    public void A_message_without_text_is_accepted_with_empty_text()
    {
        var message = Assert.IsType<SequenceMessage>(Assert.Single(Parse("sequenceDiagram\n  A->>B").Events));

        Assert.Equal(string.Empty, message.Text);
    }

    [Theory]
    [InlineData("sequenceDiagram\n  this is not a statement")]
    [InlineData("sequenceDiagram\n  end")]
    [InlineData("sequenceDiagram\n  loop x\n  A->>B: t")]
    [InlineData("sequenceDiagram\n  else x")]
    [InlineData("sequenceDiagram\n  Note over : text")]
    public void Malformed_sequences_return_errors(string source)
    {
        Assert.False(MermaidDiagram.TryParse(source, out var result));
        Assert.Equal(MermaidResultKind.Error, result.Kind);
        Assert.True(result.ErrorLine >= 1);
    }

    [Fact]
    public void Participant_and_message_budgets_are_enforced()
    {
        var participants = "sequenceDiagram\n" + string.Join('\n', Enumerable.Range(0, MermaidLimits.MaxNodes + 1).Select(i => $"  participant P{i}"));
        var messages = "sequenceDiagram\n" + string.Join('\n', Enumerable.Range(0, MermaidLimits.MaxEdges + 5).Select(_ => "  A->>B: m"));

        Assert.False(MermaidDiagram.TryParse(participants, out var p));
        Assert.False(MermaidDiagram.TryParse(messages, out var m));
        Assert.Equal(MermaidResultKind.Error, p.Kind);
        Assert.Equal(MermaidResultKind.Error, m.Kind);
    }

    [Fact]
    public void Layout_places_lifelines_left_to_right_and_messages_top_to_bottom()
    {
        var layout = Parse("sequenceDiagram\n  Alice->>Bob: first\n  Bob-->>Carol: second\n  Carol->>Alice: third").Layout();

        var centres = layout.Participants.Select(p => p.CenterX).ToList();
        Assert.Equal(centres.OrderBy(x => x), centres);
        Assert.Equal(centres.Distinct().Count(), centres.Count);
        var ys = layout.Messages.Select(m => m.Points[0].Y).ToList();
        Assert.Equal(ys.OrderBy(y => y), ys);
        Assert.Equal(ys.Distinct().Count(), ys.Count);
    }

    [Fact]
    public void Message_lines_run_between_the_lifelines_of_their_participants()
    {
        var layout = Parse("sequenceDiagram\n  A->>B: hello\n  B->>A: back").Layout();

        var a = layout.Participants.Single(p => p.Participant.Id == "A");
        var b = layout.Participants.Single(p => p.Participant.Id == "B");
        var first = layout.Messages[0];
        var second = layout.Messages[1];
        Assert.Equal(a.CenterX, first.Points[0].X, 1);
        Assert.Equal(b.CenterX, first.Points[^1].X, 1);
        Assert.Equal(b.CenterX, second.Points[0].X, 1);
        Assert.Equal(a.CenterX, second.Points[^1].X, 1);
        Assert.All(layout.Messages, m => Assert.InRange(m.Points[0].Y, a.LifelineTop, a.LifelineBottom));
        Assert.NotEmpty(first.HeadMarker);
    }

    [Fact]
    public void Self_messages_loop_back_to_their_own_lifeline()
    {
        var layout = Parse("sequenceDiagram\n  A->>A: think").Layout();

        var message = Assert.Single(layout.Messages);
        Assert.True(message.Points.Count >= 3);
        Assert.Equal(layout.Participants[0].CenterX, message.Points[0].X, 1);
    }

    [Fact]
    public void Headers_do_not_overlap_and_footers_mirror_them()
    {
        var layout = Parse("sequenceDiagram\n  participant A as A very long participant name\n  participant B\n  A->>B: a message with plenty of text in it").Layout();

        for (var i = 1; i < layout.Participants.Count; i++)
        {
            Assert.False(layout.Participants[i - 1].Header.Overlaps(layout.Participants[i].Header));
        }

        Assert.All(layout.Participants, p =>
        {
            Assert.Equal(p.Header.Width, p.Footer.Width, 1);
            Assert.True(p.Footer.Y > p.Header.Bottom);
            Assert.Equal(p.CenterX, p.Header.Center.X, 1);
        });
    }

    [Fact]
    public void Wide_message_text_widens_the_gap_between_participants()
    {
        var narrow = Parse("sequenceDiagram\n  A->>B: x").Layout();
        var wide = Parse("sequenceDiagram\n  A->>B: " + new string('w', 80)).Layout();

        Assert.True(wide.Participants[1].CenterX - wide.Participants[0].CenterX > narrow.Participants[1].CenterX - narrow.Participants[0].CenterX);
    }

    [Fact]
    public void Everything_fits_inside_the_reported_size()
    {
        var layout = Parse("sequenceDiagram\n  A->>B: hi\n  Note over A,B: shared note\n  alt x\n    B->>C: y\n  else z\n    C-->>A: w\n  end\n  Note right of C: tail").Layout();

        foreach (var rect in layout.Participants.SelectMany(p => new[] { p.Header, p.Footer }).Concat(layout.Notes.Select(n => n.Bounds)).Concat(layout.Frames.Select(f => f.Bounds)))
        {
            Assert.True(rect.X >= -0.01 && rect.Y >= -0.01 && rect.Right <= layout.Width + 0.01 && rect.Bottom <= layout.Height + 0.01, $"{rect} outside {layout.Width}x{layout.Height}");
        }

        Assert.All(layout.Messages.SelectMany(m => m.Points), p => Assert.True(p.X >= -0.01 && p.X <= layout.Width + 0.01 && p.Y >= -0.01 && p.Y <= layout.Height + 0.01));
    }

    [Fact]
    public void Frames_are_laid_out_around_their_messages_with_dividers_inside()
    {
        var layout = Parse("sequenceDiagram\n  A->>B: before\n  alt ok\n    A->>B: inside\n  else bad\n    B->>A: also inside\n  end\n  A->>B: after").Layout();

        var frame = Assert.Single(layout.Frames);
        Assert.Equal("alt", frame.Kind);
        Assert.Equal("ok", frame.Label);
        var inside = layout.Messages.Where(m => m.Message.Text.Contains("inside", StringComparison.Ordinal)).ToList();
        Assert.All(inside, m => Assert.InRange(m.Points[0].Y, frame.Bounds.Y, frame.Bounds.Bottom));
        var before = layout.Messages.Single(m => m.Message.Text == "before");
        var after = layout.Messages.Single(m => m.Message.Text == "after");
        Assert.True(before.Points[0].Y < frame.Bounds.Y);
        Assert.True(after.Points[0].Y > frame.Bounds.Bottom);
        var divider = Assert.Single(frame.Dividers);
        Assert.InRange(divider.Y, frame.Bounds.Y, frame.Bounds.Bottom);
        Assert.Equal("bad", divider.Label);
    }

    [Fact]
    public void Sequence_layout_is_deterministic()
    {
        const string source = "sequenceDiagram\n  A->>B: x\n  Note over A,B: n\n  loop l\n    B-->>A: y\n  end";

        var first = Parse(source).Layout();
        var second = Parse(source).Layout();

        Assert.Equal(first.Width, second.Width);
        Assert.Equal(first.Height, second.Height);
        Assert.Equal(first.Participants.Select(p => (p.Header, p.Footer, p.CenterX)), second.Participants.Select(p => (p.Header, p.Footer, p.CenterX)));
        Assert.Equal(first.Messages.SelectMany(m => m.Points), second.Messages.SelectMany(m => m.Points));
    }

    [Fact]
    public void A_custom_measurer_is_used_for_sizing()
    {
        var small = Parse("sequenceDiagram\n  A->>B: hi").Layout(_ => (5, 5));
        var big = Parse("sequenceDiagram\n  A->>B: hi").Layout(_ => (300, 60));

        Assert.True(big.Width > small.Width);
        Assert.True(big.Height > small.Height);
    }
}
