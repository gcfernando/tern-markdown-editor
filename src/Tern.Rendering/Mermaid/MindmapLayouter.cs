namespace Tern.Rendering.Mermaid;

/// <summary>Two-sided tree layout with disjoint subtree bands and readable labels.</summary>
internal static class MindmapLayouter
{
    public static FlowchartLayout Layout(FlowchartDiagram diagram, Func<string, (double Width, double Height)> measure)
    {
        var nodes = diagram.Nodes.Select(node => node with { Label = MermaidDiagram.WrapLabel(node.Label, measure, 180) }).ToArray();
        var indexes = nodes.Select((node, index) => (node.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index, StringComparer.Ordinal);
        var children = Enumerable.Range(0, nodes.Length).Select(_ => new List<int>()).ToArray();
        foreach (var edge in diagram.Edges) children[indexes[edge.From]].Add(indexes[edge.To]);
        var sizes = nodes.Select(node => { var size = measure(node.Label); var width = size.Width + 28; var height = size.Height + 20; return node.Shape == FlowNodeShape.Circle ? (W: Math.Max(width, height), H: Math.Max(width, height)) : (W: width, H: height); }).ToArray();
        var heights = new double[nodes.Length];
        double Height(int index)
        {
            heights[index] = Math.Max(sizes[index].H, children[index].Sum(Height) + Math.Max(0, children[index].Count - 1) * 24);
            return heights[index];
        }
        Height(0);
        var rectangles = new DiagramRect[nodes.Length];
        var levels = new int[nodes.Length];
        rectangles[0] = new(0, -sizes[0].H / 2, sizes[0].W, sizes[0].H);
        var sides = new[] { new List<int>(), new List<int>() };
        foreach (var child in children[0]) sides[sides[0].Sum(i => heights[i]) <= sides[1].Sum(i => heights[i]) ? 0 : 1].Add(child);
        for (var side = 0; side < 2; side++)
        {
            var total = sides[side].Sum(i => heights[i]) + Math.Max(0, sides[side].Count - 1) * 24;
            var top = -total / 2;
            foreach (var child in sides[side]) { Place(child, 0, side == 0 ? 1 : -1, top, 1); top += heights[child] + 24; }
        }
        var minX = rectangles.Min(r => r.X) - 16;
        var minY = rectangles.Min(r => r.Y) - 16;
        DiagramRect Shift(DiagramRect r) => r with { X = r.X - minX, Y = r.Y - minY };
        var laidNodes = nodes.Select((node, index) => new LaidOutNode(node, Shift(rectangles[index]), levels[index])).ToArray();
        var edges = diagram.Edges.Select(edge =>
        {
            var from = laidNodes[indexes[edge.From]].Bounds;
            var to = laidNodes[indexes[edge.To]].Bounds;
            var right = to.Center.X > from.Center.X;
            var start = new DiagramPoint(right ? from.Right : from.X, from.Center.Y);
            var end = new DiagramPoint(right ? to.X : to.Right, to.Center.Y);
            var middle = (start.X + end.X) / 2;
            return new LaidOutEdge(edge, [start, new(middle, start.Y), new(middle, end.Y), end], [], [], null);
        }).ToArray();
        return new(laidNodes, edges, [], rectangles.Max(r => r.Right) - minX + 16, rectangles.Max(r => r.Bottom) - minY + 16);

        void Place(int index, int parent, int side, double top, int depth)
        {
            var p = rectangles[parent];
            rectangles[index] = new(side > 0 ? p.Right + 56 : p.X - 56 - sizes[index].W, top + (heights[index] - sizes[index].H) / 2, sizes[index].W, sizes[index].H);
            levels[index] = depth;
            var childTop = top;
            foreach (var child in children[index]) { Place(child, index, side, childTop, depth + 1); childTop += heights[child] + 24; }
        }
    }
}
