namespace Tern.Rendering.Mermaid;

/// <summary>Places relationship multiplicities near their respective endpoints, outside node interiors.</summary>
internal static class ClassLabelLayouter
{
    public static FlowchartLayout AddEndLabels(FlowchartLayout layout, Func<string, (double Width, double Height)> measure)
    {
        if (!layout.Edges.Any(edge => edge.Edge.StartLabel is not null || edge.Edge.EndLabel is not null)) return layout;
        // Both sinks paint label backgrounds two pixels beyond the text box. Reserve
        // that paint plus breathing room, rather than merely avoiding text interiors.
        var occupied = layout.Nodes.Select(node => Expand(node.Bounds, 4))
            .Concat(layout.Edges.Where(edge => edge.LabelBounds is not null).Select(edge => Expand(edge.LabelBounds!.Value, 6))).ToList();
        var edges = new List<LaidOutEdge>();
        foreach (var edge in layout.Edges)
        {
            var start = Place(edge.Edge.StartLabel, edge.Points, false);
            var end = Place(edge.Edge.EndLabel, edge.Points, true);
            edges.Add(edge with { StartLabelBounds = start, EndLabelBounds = end });
        }
        var bounds = occupied.Aggregate(new DiagramRect(0, 0, layout.Width, layout.Height), Union);
        var dx = Math.Max(0, 16 - bounds.X);
        var dy = Math.Max(0, 16 - bounds.Y);
        DiagramRect Shift(DiagramRect r) => r with { X = r.X + dx, Y = r.Y + dy };
        DiagramPoint Point(DiagramPoint p) => new(p.X + dx, p.Y + dy);
        return new FlowchartLayout(layout.Nodes.Select(node => node with { Bounds = Shift(node.Bounds) }).ToArray(),
            edges.Select(edge => edge with { Points = edge.Points.Select(Point).ToArray(), StartArrow = edge.StartArrow.Select(Point).ToArray(), EndArrow = edge.EndArrow.Select(Point).ToArray(),
                LabelBounds = edge.LabelBounds is { } label ? Shift(label) : null,
                StartLabelBounds = edge.StartLabelBounds is { } sl ? Shift(sl) : null, EndLabelBounds = edge.EndLabelBounds is { } el ? Shift(el) : null }).ToArray(),
            layout.Subgraphs.Select(frame => frame with { Bounds = Shift(frame.Bounds) }).ToArray(), bounds.Right + dx + 16, bounds.Bottom + dy + 16);

        DiagramRect? Place(string? label, IReadOnlyList<DiagramPoint> points, bool end)
        {
            if (string.IsNullOrEmpty(label) || points.Count < 2) return null;
            var tip = end ? points[^1] : points[0];
            var next = end ? points[^2] : points[1];
            var vx = next.X - tip.X;
            var vy = next.Y - tip.Y;
            var length = Math.Max(1, Math.Sqrt(vx * vx + vy * vy));
            vx /= length; vy /= length;
            var size = measure(label);
            var candidates = new List<DiagramRect>();
            foreach (var along in new[] { 22d, 36, 52, 72 })
            foreach (var across in new[] { 14d, -14, 30, -30, 48, -48, 72, -72, 104, -104, 152, -152 })
                candidates.Add(new(tip.X + vx * along - vy * across - (size.Width + 8) / 2,
                    tip.Y + vy * along + vx * across - (size.Height + 4) / 2, size.Width + 8, size.Height + 4));
            var clear = candidates.Where(candidate => !occupied.Any(Expand(candidate, 6).Overlaps))
                .OrderBy(candidate => Math.Pow(candidate.Center.X - tip.X, 2) + Math.Pow(candidate.Center.Y - tip.Y, 2)).ToArray();
            // Dense graphs may exhaust the bounded local search. An external lane is
            // always clear and is included in the final diagram's measured bounds.
            var chosen = clear.Length > 0 ? clear[0]
                : new DiagramRect(occupied.Max(box => box.Right) + 8, tip.Y - (size.Height + 4) / 2, size.Width + 8, size.Height + 4);
            occupied.Add(Expand(chosen, 6));
            return chosen;
        }
    }

    private static DiagramRect Expand(DiagramRect bounds, double padding) =>
        new(bounds.X - padding, bounds.Y - padding, bounds.Width + padding * 2, bounds.Height + padding * 2);

    private static DiagramRect Union(DiagramRect a, DiagramRect b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.Right, b.Right) - Math.Min(a.X, b.X), Math.Max(a.Bottom, b.Bottom) - Math.Min(a.Y, b.Y));
}
