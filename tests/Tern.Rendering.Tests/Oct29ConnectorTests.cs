using Tern.Rendering.Mermaid;

namespace Tern.Rendering.Tests;

public sealed class Oct29ConnectorTests
{
    [Theory]
    [InlineData("LR")]
    [InlineData("RL")]
    [InlineData("TD")]
    [InlineData("BT")]
    public void Handbook_main_chain_stays_straight_when_feedback_uses_the_opposite_face(string direction)
    {
        var source = $"flowchart {direction}\nA[Problem] --> B[Collect] --> C[Clean] --> D[EDA] --> E[Features]\nE --> F[Split] --> G[Train] --> H[Evaluate] --> I[Deploy] --> J[Monitor]\nJ -. drift / new data .-> B";
        Assert.True(MermaidDiagram.TryParse(source, out var parsed), parsed.ErrorMessage);
        var layout = parsed.Flowchart!.Layout();
        var incoming = layout.Edges.Where(edge => edge.Edge.To == "B").ToArray();
        Assert.Equal(2, incoming.Length);
        Assert.NotEqual(incoming[0].Points[^1], incoming[1].Points[^1]);
        var outgoing = Assert.Single(layout.Edges, edge => edge.Edge.From == "B" && edge.Edge.To == "C");
        Assert.NotEqual(Assert.Single(incoming, edge => edge.Edge.From == "J").Points[^1], outgoing.Points[0]);
        var main = Assert.Single(incoming, edge => edge.Edge.From == "A");
        var cross = direction is "LR" or "RL" ? main.Points[0].Y : main.Points[0].X;
        Assert.All(main.Points, point => Assert.InRange(Math.Abs((direction is "LR" or "RL" ? point.Y : point.X) - cross), 0, .001));
        foreach (var edge in incoming)
            for (var i = 1; i < edge.Points.Count; i++)
                Assert.True(Math.Abs(edge.Points[i].X - edge.Points[i - 1].X) < .001
                    || Math.Abs(edge.Points[i].Y - edge.Points[i - 1].Y) < .001,
                    "A routed segment must remain horizontal or vertical before rounded corner painting.");
        Assert.Equal(10, layout.Nodes.Count);
        Assert.Equal(10, layout.Edges.Count);
    }

    [Theory]
    [InlineData("LR")]
    [InlineData("RL")]
    [InlineData("TD")]
    [InlineData("BT")]
    public void Opposing_main_and_feedback_arrows_use_separate_ports_on_the_same_face(string direction)
    {
        Assert.True(MermaidDiagram.TryParse($"flowchart {direction}\nA[Problem] --> B[Collect] --> C[Check]\nC -. feedback .-> B", out var parsed));
        var layout = parsed.Flowchart!.Layout();
        var main = Assert.Single(layout.Edges, edge => edge.Edge.From == "B" && edge.Edge.To == "C");
        var feedback = Assert.Single(layout.Edges, edge => edge.Edge.From == "C" && edge.Edge.To == "B");
        Assert.NotEqual(main.Points[0], feedback.Points[^1]);
        Assert.NotEqual(main.Points[^1], feedback.Points[0]);
        var cross = direction is "LR" or "RL" ? main.Points[0].Y : main.Points[0].X;
        Assert.All(main.Points, point => Assert.InRange(Math.Abs((direction is "LR" or "RL" ? point.Y : point.X) - cross), 0, .001));
        Assert.All(feedback.Points, point => Assert.True(Math.Abs((direction is "LR" or "RL" ? point.Y : point.X) - cross) > 1,
            "Feedback must remain on a separate track instead of painting over the main arrow."));
    }
}
