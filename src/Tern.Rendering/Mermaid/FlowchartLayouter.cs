namespace Tern.Rendering.Mermaid;

/// <summary>
/// Deterministic layered (Sugiyama-style) flowchart layout: DFS cycle breaking, longest-path layering, dummy vertices for
/// long edges, barycenter crossing reduction and neighbour-driven coordinate packing.
/// </summary>
internal static class FlowchartLayouter
{
    private const double Margin = 16;
    private const double NodeGap = 32;
    private const double RankGap = 72;
    private const double LoopReserve = 28;
    private const double LoopSize = 22;
    private const double SubgraphPadding = 16;
    private const double SubgraphTitleHeight = 24;
    private const double ArrowLength = 10;
    private const double ArrowHalfWidth = 5;
    private const double DummySlot = 8;

    public static FlowchartLayout Layout(FlowchartDiagram diagram, Func<string, (double Width, double Height)> measure)
    {
        var nodes = diagram.Nodes.Select(node => node with
        {
            Label = MermaidDiagram.WrapLabel(node.Label, measure),
            Attributes = node.Attributes.Select(attribute => MermaidDiagram.WrapLabel(attribute, measure, 220)).ToArray(),
            Methods = node.Methods.Select(method => MermaidDiagram.WrapLabel(method, measure, 220)).ToArray(),
        }).ToArray();
        var n = nodes.Length;
        if (n == 0)
        {
            return new FlowchartLayout([], [], [], Margin * 2, Margin * 2);
        }

        var horizontal = diagram.Direction is FlowDirection.LeftRight or FlowDirection.RightLeft;
        var indexOf = new Dictionary<string, int>(n, StringComparer.Ordinal);
        for (var i = 0; i < n; i++)
        {
            indexOf[nodes[i].Id] = i;
        }

        var sizes = nodes.Select(node => NodeSize(node, measure)).ToArray();
        var rectangular = Enumerable.Range(0, n).Where(index => nodes[index].Shape is FlowNodeShape.Rectangle or FlowNodeShape.Rounded or FlowNodeShape.Subroutine or FlowNodeShape.Entity or FlowNodeShape.Class).ToArray();
        if (rectangular.Length > 0)
        {
            var commonWidth = rectangular.Max(index => sizes[index].W);
            foreach (var index in rectangular) sizes[index] = (commonWidth, sizes[index].H);
        }
        var edges = diagram.Edges;
        var displayEdges = edges.Select(edge => edge with { Label = edge.Label is null ? null : MermaidDiagram.WrapLabel(edge.Label, measure, 220) }).ToArray();
        var labelSizes = displayEdges.Select(e => e.Label is null ? (Width: 0d, Height: 0d) : LabelSize(e.Label, measure)).ToArray();
        var from = edges.Select(e => indexOf[e.From]).ToArray();
        var to = edges.Select(e => indexOf[e.To]).ToArray();

        var hasLoop = new bool[n];
        for (var e = 0; e < edges.Count; e++)
        {
            if (from[e] == to[e])
            {
                hasLoop[from[e]] = true;
            }
        }

        var layerOf = new List<int>(new int[n]);
        // A dummy vertex belongs to its original edge, not after every real node.
        // Source declaration order therefore remains the stable tie-breaker for
        // first/second branches when barycentres are otherwise identical.
        var declarationOrder = Enumerable.Range(0, n).Select(node =>
            Enumerable.Range(0, edges.Count).Where(edge => to[edge] == node && from[edge] != node).DefaultIfEmpty(edges.Count + node).Min()).ToList();
        var slot = new List<double>(n);
        var rankSize = new List<double>(n);
        for (var i = 0; i < n; i++)
        {
            var cross = horizontal ? sizes[i].H : sizes[i].W;
            slot.Add(cross + (hasLoop[i] ? LoopReserve * 2 : 0));
            rankSize.Add(horizontal ? sizes[i].W : sizes[i].H);
        }

        var reversed = BreakCycles(n, edges.Count, from, to);
        AssignLayers(n, edges.Count, from, to, reversed, layerOf);

        var chains = new List<int>?[edges.Count];
        var up = new List<List<int>>();
        var down = new List<List<int>>();
        for (var i = 0; i < n; i++)
        {
            up.Add([]);
            down.Add([]);
        }

        for (var e = 0; e < edges.Count; e++)
        {
            if (from[e] == to[e])
            {
                continue;
            }

            var (a, b) = reversed[e] ? (to[e], from[e]) : (from[e], to[e]);
            var chain = new List<int> { a };
            for (var l = layerOf[a] + 1; l < layerOf[b]; l++)
            {
                layerOf.Add(l);
                declarationOrder.Add(e);
                slot.Add(Math.Max(DummySlot, (horizontal ? labelSizes[e].Height : labelSizes[e].Width) + 16));
                rankSize.Add(0);
                up.Add([]);
                down.Add([]);
                chain.Add(layerOf.Count - 1);
            }

            chain.Add(b);
            for (var k = 0; k + 1 < chain.Count; k++)
            {
                down[chain[k]].Add(chain[k + 1]);
                up[chain[k + 1]].Add(chain[k]);
            }

            chains[e] = chain;
        }

        var layerCount = layerOf.Max() + 1;
        var layers = new List<List<int>>();
        for (var l = 0; l < layerCount; l++)
        {
            layers.Add([]);
        }

        for (var v = 0; v < layerOf.Count; v++)
        {
            layers[layerOf[v]].Add(v);
        }
        foreach (var layer in layers) layer.Sort((a, b) => declarationOrder[a].CompareTo(declarationOrder[b]));

        ReduceCrossings(layers, up, down);

        var position = new double[layerOf.Count];
        PlaceLayers(layers, slot, up, down, position);

        var thickness = layers.Select(layer => layer.Max(v => rankSize[v])).ToArray();
        var labelRank = labelSizes.Select(s => horizontal ? s.Width : s.Height).DefaultIfEmpty(0).Max();
        var cardinalitySpace = edges.Any(edge => edge.StartHead is FlowArrowHead.One or FlowArrowHead.ZeroOrOne or FlowArrowHead.Many or FlowArrowHead.ZeroOrMany) ? 24 : 0;
        var gap = RankGap + labelRank + cardinalitySpace;
        var rankCenter = new double[layerCount];
        var cursor = 0d;
        for (var l = 0; l < layerCount; l++)
        {
            cursor += thickness[l] / 2;
            rankCenter[l] = cursor;
            cursor += thickness[l] / 2 + gap;
        }

        var totalRank = cursor - gap;

        DiagramPoint Map(int vertex)
        {
            var rank = rankCenter[layerOf[vertex]];
            var cross = position[vertex];
            return diagram.Direction switch
            {
                FlowDirection.TopDown => new DiagramPoint(cross, rank),
                FlowDirection.BottomUp => new DiagramPoint(cross, totalRank - rank),
                FlowDirection.LeftRight => new DiagramPoint(rank, cross),
                _ => new DiagramPoint(totalRank - rank, cross),
            };
        }

        var rects = new DiagramRect[n];
        for (var i = 0; i < n; i++)
        {
            var c = Map(i);
            rects[i] = new DiagramRect(c.X - (sizes[i].W / 2), c.Y - (sizes[i].H / 2), sizes[i].W, sizes[i].H);
        }

        var sourcePorts = new double[edges.Count];
        var targetPorts = new double[edges.Count];
        var departureLanes = Enumerable.Repeat(0.5, edges.Count).ToArray();
        AssignPorts(from, to, rects, horizontal, sourcePorts, targetPorts, departureLanes);

        var routed = new List<(IReadOnlyList<DiagramPoint> Points, DiagramRect? Label)>(edges.Count);
        for (var e = 0; e < edges.Count; e++)
        {
            var points = from[e] == to[e]
                ? LoopPoints(nodes[from[e]].Shape, rects[from[e]], horizontal)
                : RoutePoints(chains[e]!, reversed[e], Map, nodes[from[e]].Shape, rects[from[e]], nodes[to[e]].Shape, rects[to[e]], horizontal,
                    sourcePorts[e], targetPorts[e], departureLanes[e], vertex => thickness[layerOf[vertex]]);
            routed.Add((points, null));
        }
        var frames = BuildSubgraphFrames(diagram, indexOf, rects);
        var labelObstacles = rects.Concat(frames.Select(frame => new DiagramRect(frame.Bounds.X, frame.Bounds.Y, frame.Bounds.Width, SubgraphTitleHeight))).ToArray();
        var placedLabels = new List<DiagramRect>();
        for (var e = 0; e < edges.Count; e++)
        {
            if (edges[e].Label is null) continue;
            var label = PlaceLabel(routed[e].Points, labelSizes[e], from[e] == to[e], labelObstacles, placedLabels,
                routed.Where((_, index) => index != e).Select(route => route.Points));
            routed[e] = (routed[e].Points, label);
            placedLabels.Add(label);
        }
        var leaders = routed.Select(route => route.Label is { } label
            ? LabelLeader(route.Points, label, labelObstacles, placedLabels) : (IReadOnlyList<DiagramPoint>)[]).ToArray();

        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;

        void Include(DiagramRect r)
        {
            minX = Math.Min(minX, r.X);
            minY = Math.Min(minY, r.Y);
            maxX = Math.Max(maxX, r.Right);
            maxY = Math.Max(maxY, r.Bottom);
        }

        foreach (var r in rects)
        {
            Include(r);
        }

        foreach (var f in frames)
        {
            Include(f.Bounds);
        }

        foreach (var (points, label) in routed)
        {
            foreach (var p in points)
            {
                Include(new DiagramRect(p.X, p.Y, 0, 0));
            }

            if (label is { } l)
            {
                Include(l);
            }
        }
        foreach (var point in leaders.SelectMany(leader => leader)) Include(new DiagramRect(point.X, point.Y, 0, 0));

        var dx = Margin - minX;
        var dy = Margin - minY;
        DiagramRect Shift(DiagramRect r) => new(r.X + dx, r.Y + dy, r.Width, r.Height);
        DiagramPoint ShiftPoint(DiagramPoint p) => new(p.X + dx, p.Y + dy);

        var laidNodes = new List<LaidOutNode>(n);
        for (var i = 0; i < n; i++)
        {
            laidNodes.Add(new LaidOutNode(nodes[i], Shift(rects[i]), layerOf[i]));
        }

        var laidEdges = new List<LaidOutEdge>(edges.Count);
        for (var e = 0; e < edges.Count; e++)
        {
            var points = routed[e].Points.Select(ShiftPoint).ToArray();
            var edge = displayEdges[e];
            DiagramPoint[] start = edge.StartHead == FlowArrowHead.None ? [] : HeadGeometry(edge.StartHead, points[0], points[1]);
            DiagramPoint[] end = edge.EndHead == FlowArrowHead.None ? [] : HeadGeometry(edge.EndHead, points[^1], points[^2]);
            laidEdges.Add(new LaidOutEdge(edge, points, start, end, routed[e].Label is { } lb ? Shift(lb) : null)
                { LabelLeader = leaders[e].Select(ShiftPoint).ToArray() });
        }

        var laidFrames = frames.Select(f => new LaidOutSubgraph(f.Subgraph, Shift(f.Bounds))).ToList();
        return new FlowchartLayout(laidNodes, laidEdges, laidFrames, (maxX - minX) + (Margin * 2), (maxY - minY) + (Margin * 2));
    }

    private static void AssignPorts(int[] from, int[] to, DiagramRect[] rectangles, bool horizontal,
        double[] sourcePorts, double[] targetPorts, double[] departureLanes)
    {
        // Assign both ends together: incoming feedback and outgoing main edges
        // on the same face need distinct ports too. Opposite faces are independent.
        var endpoints = Enumerable.Range(0, from.Length).Where(index => from[index] != to[index])
            .SelectMany(index => new[]
            {
                (Edge: index, Source: true, Owner: from[index], Peer: to[index]),
                (Edge: index, Source: false, Owner: to[index], Peer: from[index]),
            });
        foreach (var group in endpoints.GroupBy(endpoint => (endpoint.Owner, Side: Math.Sign(horizontal
            ? rectangles[endpoint.Peer].Center.X - rectangles[endpoint.Owner].Center.X
            : rectangles[endpoint.Peer].Center.Y - rectangles[endpoint.Owner].Center.Y))))
        {
            var ordered = group.OrderBy(endpoint => horizontal ? rectangles[endpoint.Peer].Center.Y : rectangles[endpoint.Peer].Center.X)
                .ThenBy(endpoint => endpoint.Edge).ToArray();
            if (ordered.Length < 2) continue;
            var bounds = rectangles[group.Key.Owner];
            var crossSize = horizontal ? bounds.Height : bounds.Width;
            var span = Math.Min(48, Math.Max(0, crossSize - 24));
            var offsets = Enumerable.Range(0, ordered.Length)
                .Select(position => ((double)position / (ordered.Length - 1) - .5) * span).ToArray();
            if (ordered.Any(endpoint => endpoint.Source) && ordered.Any(endpoint => !endpoint.Source))
            {
                // Keep the first declared main relationship centered, and give
                // later feedback a separate port without disturbing that spine.
                var firstEdge = ordered.Min(item => item.Edge);
                var primary = Array.FindIndex(ordered, endpoint => endpoint.Edge == firstEdge);
                var centre = Array.FindIndex(offsets, offset => Math.Abs(offset) < .001);
                if (centre >= 0) (offsets[primary], offsets[centre]) = (offsets[centre], offsets[primary]);
                else offsets[primary] = 0;
            }
            for (var position = 0; position < ordered.Length; position++)
            {
                var endpoint = ordered[position];
                if (endpoint.Source) sourcePorts[endpoint.Edge] = offsets[position];
                else targetPorts[endpoint.Edge] = offsets[position];
                // Outer branches turn earlier, inner branches later. Their distinct ports and
                // tracks keep separate relationships from collapsing into one shared bus.
                if (endpoint.Source) departureLanes[endpoint.Edge] = 0.65 - (span > 0 ? Math.Abs(offsets[position] / span) : 0) * 0.7;
            }
        }
    }

    private static (double W, double H) NodeSize(FlowNode node, Func<string, (double Width, double Height)> measure)
    {
        if (node.Shape is FlowNodeShape.InitialState or FlowNodeShape.FinalState) return (22, 22);
        var (tw, th) = measure(node.Label);
        if (node.Shape == FlowNodeShape.Class)
        {
            var attributes = measure(string.Join('\n', node.Attributes));
            var methods = measure(string.Join('\n', node.Methods));
            return (Math.Max(80, Math.Max(tw, Math.Max(attributes.Width, methods.Width)) + 32),
                th + 20 + Math.Max(20, node.Attributes.Count == 0 ? 0 : attributes.Height + 16) + Math.Max(20, node.Methods.Count == 0 ? 0 : methods.Height + 16));
        }
        if (node.Attributes.Count > 0)
        {
            var body = measure(string.Join('\n', node.Attributes));
            tw = Math.Max(tw, body.Width);
            th += body.Height + 16;
        }
        double w = tw + 32;
        double h = th + 20;
        switch (node.Shape)
        {
            case FlowNodeShape.Diamond:
                w *= 1.6;
                h *= 1.6;
                break;
            case FlowNodeShape.Hexagon:
                w += 28;
                break;
            case FlowNodeShape.Stadium or FlowNodeShape.Subroutine:
                w += 16;
                break;
            case FlowNodeShape.Cylinder:
                h += 16;
                break;
            case FlowNodeShape.Circle:
                w = h = Math.Max(w, h) + 8;
                break;
        }

        return (Math.Max(w, 120), Math.Max(h, 48));
    }

    private static (double Width, double Height) LabelSize(string label, Func<string, (double Width, double Height)> measure)
    {
        var (w, h) = measure(label);
        return (w + 10, h + 6);
    }

    private static bool[] BreakCycles(int n, int edgeCount, int[] from, int[] to)
    {
        var outgoing = new List<int>[n];
        for (var i = 0; i < n; i++)
        {
            outgoing[i] = [];
        }

        for (var e = 0; e < edgeCount; e++)
        {
            if (from[e] != to[e])
            {
                outgoing[from[e]].Add(e);
            }
        }

        var reversed = new bool[edgeCount];
        var state = new byte[n];

        // Iterative DFS so pathological chains of 500 nodes can never exhaust the stack.
        for (var root = 0; root < n; root++)
        {
            if (state[root] != 0)
            {
                continue;
            }

            var stack = new Stack<(int Node, int Next)>();
            stack.Push((root, 0));
            state[root] = 1;
            while (stack.Count > 0)
            {
                var (node, next) = stack.Pop();
                if (next >= outgoing[node].Count)
                {
                    state[node] = 2;
                    continue;
                }

                stack.Push((node, next + 1));
                var e = outgoing[node][next];
                var target = to[e];
                if (state[target] == 1)
                {
                    reversed[e] = true;
                }
                else if (state[target] == 0)
                {
                    state[target] = 1;
                    stack.Push((target, 0));
                }
            }
        }

        return reversed;
    }

    private static void AssignLayers(int n, int edgeCount, int[] from, int[] to, bool[] reversed, List<int> layerOf)
    {
        var successors = new List<int>[n];
        var inDegree = new int[n];
        for (var i = 0; i < n; i++)
        {
            successors[i] = [];
        }

        for (var e = 0; e < edgeCount; e++)
        {
            if (from[e] == to[e])
            {
                continue;
            }

            var (a, b) = reversed[e] ? (to[e], from[e]) : (from[e], to[e]);
            successors[a].Add(b);
            inDegree[b]++;
        }

        var queue = new Queue<int>(Enumerable.Range(0, n).Where(i => inDegree[i] == 0));
        while (queue.Count > 0)
        {
            var u = queue.Dequeue();
            foreach (var v in successors[u])
            {
                layerOf[v] = Math.Max(layerOf[v], layerOf[u] + 1);
                if (--inDegree[v] == 0)
                {
                    queue.Enqueue(v);
                }
            }
        }
    }

    private static void ReduceCrossings(List<List<int>> layers, List<List<int>> up, List<List<int>> down)
    {
        var order = new double[up.Count];
        void Reindex(List<int> layer)
        {
            for (var i = 0; i < layer.Count; i++)
            {
                order[layer[i]] = i;
            }
        }

        foreach (var layer in layers)
        {
            Reindex(layer);
        }

        void Sort(List<int> layer, List<List<int>> neighbours)
        {
            var keyed = layer
                .Select(v => (Vertex: v, Key: neighbours[v].Count == 0 ? order[v] : neighbours[v].Average(u => order[u]), Old: order[v]))
                .OrderBy(x => x.Key)
                .ThenBy(x => x.Old)
                .Select(x => x.Vertex)
                .ToList();
            layer.Clear();
            layer.AddRange(keyed);
            Reindex(layer);
        }

        for (var iteration = 0; iteration < 6; iteration++)
        {
            for (var l = 1; l < layers.Count; l++)
            {
                Sort(layers[l], up);
            }

            for (var l = layers.Count - 2; l >= 0; l--)
            {
                Sort(layers[l], down);
            }
        }
    }

    private static void PlaceLayers(List<List<int>> layers, List<double> slot, List<List<int>> up, List<List<int>> down, double[] position)
    {
        foreach (var layer in layers)
        {
            var desired = new double[layer.Count];
            Pack(layer, slot, desired, position);
        }

        for (var pass = 0; pass < 4; pass++)
        {
            for (var l = 1; l < layers.Count; l++)
            {
                Relax(layers[l], up, slot, position);
            }

            for (var l = layers.Count - 2; l >= 0; l--)
            {
                Relax(layers[l], down, slot, position);
            }
        }
    }

    private static void Relax(List<int> layer, List<List<int>> neighbours, List<double> slot, double[] position)
    {
        var desired = layer.Select(v => neighbours[v].Count == 0 ? position[v] : neighbours[v].Average(u => position[u])).ToArray();
        Pack(layer, slot, desired, position);
    }

    // Keeps the order and the minimum gap, pulling each vertex towards its desired centre while preserving the layer's mean offset.
    private static void Pack(List<int> layer, List<double> slot, double[] desired, double[] position)
    {
        var packed = new double[layer.Count];
        for (var i = 0; i < layer.Count; i++)
        {
            packed[i] = i == 0
                ? desired[0]
                : Math.Max(desired[i], packed[i - 1] + (slot[layer[i - 1]] / 2) + NodeGap + (slot[layer[i]] / 2));
        }

        var shift = 0d;
        for (var i = 0; i < layer.Count; i++)
        {
            shift += desired[i] - packed[i];
        }

        shift /= layer.Count;
        for (var i = 0; i < layer.Count; i++)
        {
            position[layer[i]] = packed[i] + shift;
        }
    }

    private static List<DiagramPoint> RoutePoints(
        List<int> chain,
        bool reversed,
        Func<int, DiagramPoint> map,
        FlowNodeShape fromShape,
        DiagramRect fromRect,
        FlowNodeShape toShape,
        DiagramRect toRect,
        bool horizontal,
        double sourceOffset,
        double targetOffset,
        double departureLane,
        Func<int, double> rankThickness)
    {
        var vertices = chain.ToArray();
        if (reversed)
        {
            Array.Reverse(vertices);
        }
        var centres = vertices.Select(map).ToList();

        var first = centres[0];
        var last = centres[^1];
        var direction = horizontal ? Math.Sign(last.X - first.X) : Math.Sign(last.Y - first.Y);
        if (direction == 0) direction = 1;
        var sourcePort = horizontal ? new DiagramPoint(first.X + direction * fromRect.Width, first.Y + 2 * sourceOffset) : new DiagramPoint(first.X + 2 * sourceOffset, first.Y + direction * fromRect.Height);
        var targetPort = horizontal ? new DiagramPoint(last.X - direction * toRect.Width, last.Y + 2 * targetOffset) : new DiagramPoint(last.X + 2 * targetOffset, last.Y - direction * toRect.Height);
        centres[0] = Clip(fromShape, fromRect, sourcePort);
        centres[^1] = Clip(toShape, toRect, targetPort);
        var points = new List<DiagramPoint> { centres[0] };
        for (var index = 1; index < centres.Count; index++)
        {
            var a = centres[index - 1];
            var b = centres[index];
            var lane = index == 1 ? departureLane : 0.5;
            var previousCentre = map(vertices[index - 1]);
            var nextCentre = map(vertices[index]);
            var previousRank = horizontal ? previousCentre.X : previousCentre.Y;
            var nextRank = horizontal ? nextCentre.X : nextCentre.Y;
            var corridorStart = previousRank + direction * rankThickness(vertices[index - 1]) / 2;
            var corridorEnd = nextRank - direction * rankThickness(vertices[index]) / 2;
            // Turn only in the free rank corridor. Using centre-to-centre
            // midpoints can cut through a tall node in the neighbouring rank.
            var middle = corridorStart + (corridorEnd - corridorStart) * lane;
            if (horizontal)
            {
                Add(new DiagramPoint(middle, a.Y));
                Add(new DiagramPoint(middle, b.Y));
            }
            else
            {
                Add(new DiagramPoint(a.X, middle));
                Add(new DiagramPoint(b.X, middle));
            }

            Add(b);
        }

        return points;

        void Add(DiagramPoint point)
        {
            if (Distance(points[^1], point) > 0.001) points.Add(point);
        }
    }

    private static List<DiagramPoint> LoopPoints(FlowNodeShape shape, DiagramRect rect, bool horizontal)
    {
        var c = rect.Center;
        if (!horizontal)
        {
            var a = Clip(shape, rect, new DiagramPoint(c.X + rect.Width, c.Y - (rect.Height / 4)));
            var b = Clip(shape, rect, new DiagramPoint(c.X + rect.Width, c.Y + (rect.Height / 4)));
            var outer = Math.Max(a.X, b.X) + LoopSize;
            return [a, new DiagramPoint(outer, a.Y), new DiagramPoint(outer, b.Y), b];
        }

        var left = Clip(shape, rect, new DiagramPoint(c.X - (rect.Width / 4), c.Y + rect.Height));
        var right = Clip(shape, rect, new DiagramPoint(c.X + (rect.Width / 4), c.Y + rect.Height));
        var below = Math.Max(left.Y, right.Y) + LoopSize;
        return [left, new DiagramPoint(left.X, below), new DiagramPoint(right.X, below), right];
    }

    private static DiagramRect LabelRect(IReadOnlyList<DiagramPoint> points, (double Width, double Height) size, bool isLoop)
    {
        DiagramPoint anchor;
        if (isLoop)
        {
            anchor = new DiagramPoint((points[1].X + points[2].X) / 2, (points[1].Y + points[2].Y) / 2);
            return new DiagramRect(anchor.X + 4, anchor.Y - (size.Height / 2), size.Width, size.Height);
        }

        var total = 0d;
        for (var i = 1; i < points.Count; i++)
        {
            total += Distance(points[i - 1], points[i]);
        }

        var remaining = total / 2;
        anchor = points[0];
        for (var i = 1; i < points.Count; i++)
        {
            var length = Distance(points[i - 1], points[i]);
            if (remaining <= length || i == points.Count - 1)
            {
                var t = length == 0 ? 0 : remaining / length;
                anchor = new DiagramPoint(
                    points[i - 1].X + ((points[i].X - points[i - 1].X) * t),
                    points[i - 1].Y + ((points[i].Y - points[i - 1].Y) * t));
                break;
            }

            remaining -= length;
        }

        return new DiagramRect(anchor.X - (size.Width / 2), anchor.Y - (size.Height / 2), size.Width, size.Height);
    }

    private static DiagramRect PlaceLabel(IReadOnlyList<DiagramPoint> points, (double Width, double Height) size, bool isLoop,
        IReadOnlyList<DiagramRect> nodes, IReadOnlyList<DiagramRect> labels, IEnumerable<IReadOnlyList<DiagramPoint>> otherRoutes)
    {
        var preferred = LabelRect(points, size, isLoop);
        var candidates = new List<DiagramRect> { preferred };
        // Labels belong to straight portions of their edge. Search a bounded set
        // of the longest segments, rather than blindly masking whatever happens
        // to sit at half the total path length.
        var segments = Enumerable.Range(1, points.Count - 1)
            .Select(index => (A: points[index - 1], B: points[index]))
            .OrderByDescending(segment => Distance(segment.A, segment.B)).Take(8);
        foreach (var (a, b) in segments)
        {
            var vertical = Math.Abs(a.X - b.X) < 0.001;
            foreach (var fraction in new[] { 0.5, 0.25, 0.75, 0.1, 0.9 })
            {
                var centre = new DiagramPoint(a.X + (b.X - a.X) * fraction, a.Y + (b.Y - a.Y) * fraction);
                candidates.Add(new DiagramRect(centre.X - size.Width / 2, centre.Y - size.Height / 2, size.Width, size.Height));
                // An adjacent edge can share a corridor. A small perpendicular
                // offset keeps its arrow visible while retaining label proximity.
                var offset = vertical ? size.Width / 2 + 8 : size.Height / 2 + 8;
                candidates.Add(new DiagramRect(centre.X - size.Width / 2 + (vertical ? offset : 0), centre.Y - size.Height / 2 + (vertical ? 0 : offset), size.Width, size.Height));
                candidates.Add(new DiagramRect(centre.X - size.Width / 2 - (vertical ? offset : 0), centre.Y - size.Height / 2 - (vertical ? 0 : offset), size.Width, size.Height));
                for (var lane = 1; lane <= 6; lane++)
                {
                    var clearance = offset + lane * 20;
                    candidates.Add(new DiagramRect(centre.X - size.Width / 2 + (vertical ? clearance : 0), centre.Y - size.Height / 2 + (vertical ? 0 : clearance), size.Width, size.Height));
                    candidates.Add(new DiagramRect(centre.X - size.Width / 2 - (vertical ? clearance : 0), centre.Y - size.Height / 2 - (vertical ? 0 : clearance), size.Width, size.Height));
                }
            }
        }
        var foreignRoutes = otherRoutes.ToArray();
        var foreignSegments = foreignRoutes.SelectMany(route => Enumerable.Range(1, route.Count - 1)
            .Select(index => (A: route[index - 1], B: route[index]))).ToArray();
        // A rounded corner lies inside its control hull, not on the original
        // orthogonal segments. Reserve that hull so curves cannot clip labels.
        var foreignCorners = foreignRoutes.SelectMany(DiagramEdgePath.Build).Where(segment => segment.Control is not null)
            .Select(segment => Union([new(segment.Start.X, segment.Start.Y, 0, 0), new(segment.End.X, segment.End.Y, 0, 0),
                new(segment.Control!.Value.X, segment.Control.Value.Y, 0, 0)])).ToArray();
        var best = preferred;
        var bestScore = (Nodes: int.MaxValue, Labels: int.MaxValue, Lines: int.MaxValue, Distance: double.PositiveInfinity);
        foreach (var candidate in candidates.OrderBy(candidate =>
            Math.Pow(candidate.Center.X - preferred.Center.X, 2) + Math.Pow(candidate.Center.Y - preferred.Center.Y, 2)))
        {
            var padded = new DiagramRect(candidate.X - 4, candidate.Y - 4, candidate.Width + 8, candidate.Height + 8);
            var nodeCollisions = nodes.Count(padded.Overlaps);
            var labelCollisions = labels.Count(padded.Overlaps);
            var lineCollisions = foreignSegments.Count(segment => SegmentIntersects(padded, segment.A, segment.B)) + foreignCorners.Count(padded.Overlaps);
            if (nodeCollisions == 0 && labelCollisions == 0 && lineCollisions == 0) return candidate;
            var dx = candidate.Center.X - preferred.Center.X;
            var dy = candidate.Center.Y - preferred.Center.Y;
            var score = (Nodes: nodeCollisions, Labels: labelCollisions, Lines: lineCollisions, Distance: dx * dx + dy * dy);
            if (score.CompareTo(bestScore) >= 0) continue;
            best = candidate;
            bestScore = score;
        }
        // Dense feedback/parallel edges can exhaust the local corridors. Keep
        // searching outward rather than knowingly publishing an overlapping label.
        if (bestScore.Nodes + bestScore.Labels + bestScore.Lines > 0)
        {
            for (var ring = 1; ring <= 64; ring++)
            {
                foreach (var direction in new[] { (X: 0, Y: -1), (X: 0, Y: 1), (X: -1, Y: 0), (X: 1, Y: 0) })
                {
                    var candidate = preferred with { X = preferred.X + direction.X * ring * (size.Width + 16), Y = preferred.Y + direction.Y * ring * (size.Height + 16) };
                    var padded = new DiagramRect(candidate.X - 4, candidate.Y - 4, candidate.Width + 8, candidate.Height + 8);
                    if (!nodes.Any(padded.Overlaps) && !labels.Any(padded.Overlaps)
                        && !foreignSegments.Any(segment => SegmentIntersects(padded, segment.A, segment.B)) && !foreignCorners.Any(padded.Overlaps)) return candidate;
                }
            }
            // The final escape lane lies above every foreign obstacle, so even
            // a diagram at the parser's density limit retains readable labels.
            var top = nodes.Concat(labels).Select(rect => rect.Y)
                .Concat(foreignSegments.Select(segment => Math.Min(segment.A.Y, segment.B.Y)))
                .Append(preferred.Y).Min();
            return preferred with { Y = top - size.Height - 16 };
        }
        return best;
    }

    private static bool SegmentIntersects(DiagramRect rectangle, DiagramPoint a, DiagramPoint b) =>
        Math.Abs(a.X - b.X) < 0.001
            ? a.X > rectangle.X && a.X < rectangle.Right && Math.Max(a.Y, b.Y) > rectangle.Y && Math.Min(a.Y, b.Y) < rectangle.Bottom
            : a.Y > rectangle.Y && a.Y < rectangle.Bottom && Math.Max(a.X, b.X) > rectangle.X && Math.Min(a.X, b.X) < rectangle.Right;

    private static IReadOnlyList<DiagramPoint> LabelLeader(IReadOnlyList<DiagramPoint> route, DiagramRect label,
        IReadOnlyList<DiagramRect> nodes, IReadOnlyList<DiagramRect> labels)
    {
        if (Enumerable.Range(1, route.Count - 1).Any(i => SegmentIntersects(label, route[i - 1], route[i]))) return [];
        var obstacles = nodes.Concat(labels.Where(other => other != label))
            .Select(r => new DiagramRect(r.X - 4, r.Y - 4, r.Width + 8, r.Height + 8)).ToArray();
        var lanesX = nodes.SelectMany(r => new[] { r.X - 10, r.Right + 10 }).Append(label.Center.X).ToArray();
        var lanesY = nodes.SelectMany(r => new[] { r.Y - 10, r.Bottom + 10 }).Append(label.Center.Y).ToArray();
        IReadOnlyList<DiagramPoint> best = [];
        var bestLength = double.PositiveInfinity;
        for (var i = 1; i < route.Count; i++)
        {
            var a = route[i - 1]; var b = route[i]; var length = Distance(a, b);
            if (length < 34) continue;
            var vertical = Math.Abs(a.X - b.X) < 0.001;
            var fraction = vertical ? (label.Center.Y - a.Y) / (b.Y - a.Y) : (label.Center.X - a.X) / (b.X - a.X);
            fraction = Math.Clamp(fraction, 16 / length, 1 - 16 / length);
            var anchor = new DiagramPoint(a.X + (b.X - a.X) * fraction, a.Y + (b.Y - a.Y) * fraction);
            var candidates = lanesX.Select(x => new[] { anchor, new DiagramPoint(x, anchor.Y), new DiagramPoint(x, label.Center.Y), label.Center })
                .Concat(lanesY.Select(y => new[] { anchor, new DiagramPoint(anchor.X, y), new DiagramPoint(label.Center.X, y), label.Center }));
            foreach (var candidate in candidates)
            {
                var points = candidate.Where((point, index) => index == 0 || Distance(point, candidate[index - 1]) > 0.001).ToArray();
                if (points.Length < 2) continue;
                points[^1] = Clip(FlowNodeShape.Rectangle, label, points[^2]);
                var total = 0d; var blocked = false;
                for (var segment = 1; segment < points.Length; segment++)
                {
                    total += Distance(points[segment - 1], points[segment]);
                    if (obstacles.Any(r => SegmentIntersects(r, points[segment - 1], points[segment]))) { blocked = true; break; }
                }
                if (blocked || total >= bestLength) continue;
                best = points; bestLength = total;
            }
        }
        return best;
    }

    private static List<LaidOutSubgraph> BuildSubgraphFrames(FlowchartDiagram diagram, Dictionary<string, int> indexOf, DiagramRect[] rects)
    {
        var subgraphs = diagram.Subgraphs;
        var frames = new DiagramRect?[subgraphs.Count];
        for (var i = subgraphs.Count - 1; i >= 0; i--)
        {
            var members = subgraphs[i].NodeIds;
            if (members.Count == 0)
            {
                continue;
            }

            var union = Union(members.Select(id => rects[indexOf[id]]));
            for (var j = i + 1; j < subgraphs.Count; j++)
            {
                if (frames[j] is { } inner && subgraphs[j].NodeIds.All(members.Contains))
                {
                    union = Union([union, inner]);
                }
            }

            frames[i] = new DiagramRect(
                union.X - SubgraphPadding,
                union.Y - SubgraphPadding - SubgraphTitleHeight,
                union.Width + (SubgraphPadding * 2),
                union.Height + (SubgraphPadding * 2) + SubgraphTitleHeight);
        }

        var result = new List<LaidOutSubgraph>();
        for (var i = 0; i < subgraphs.Count; i++)
        {
            if (frames[i] is { } frame)
            {
                result.Add(new LaidOutSubgraph(subgraphs[i], frame));
            }
        }

        return result;
    }

    private static DiagramRect Union(IEnumerable<DiagramRect> rects)
    {
        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;
        foreach (var r in rects)
        {
            minX = Math.Min(minX, r.X);
            minY = Math.Min(minY, r.Y);
            maxX = Math.Max(maxX, r.Right);
            maxY = Math.Max(maxY, r.Bottom);
        }

        return new DiagramRect(minX, minY, maxX - minX, maxY - minY);
    }

    private static double Distance(DiagramPoint a, DiagramPoint b) => Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));

    /// <summary>Finds where the ray from the node centre towards <paramref name="toward"/> leaves the node outline.</summary>
    internal static DiagramPoint Clip(FlowNodeShape shape, DiagramRect rect, DiagramPoint toward)
    {
        var centre = rect.Center;
        var dx = toward.X - centre.X;
        var dy = toward.Y - centre.Y;
        if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9)
        {
            return centre;
        }

        var hw = rect.Width / 2;
        var hh = rect.Height / 2;
        var t = shape switch
        {
            FlowNodeShape.Diamond => 1 / ((Math.Abs(dx) / hw) + (Math.Abs(dy) / hh)),
            FlowNodeShape.Circle or FlowNodeShape.InitialState or FlowNodeShape.FinalState => 1 / Math.Sqrt(Math.Pow(dx / hw, 2) + Math.Pow(dy / hh, 2)),
            _ => Math.Min(
                Math.Abs(dx) < 1e-9 ? double.MaxValue : hw / Math.Abs(dx),
                Math.Abs(dy) < 1e-9 ? double.MaxValue : hh / Math.Abs(dy)),
        };
        return new DiagramPoint(centre.X + (dx * t), centre.Y + (dy * t));
    }

    internal static DiagramPoint[] HeadGeometry(FlowArrowHead head, DiagramPoint tip, DiagramPoint previous)
    {
        if (head is FlowArrowHead.One or FlowArrowHead.ZeroOrOne or FlowArrowHead.Many or FlowArrowHead.ZeroOrMany)
        {
            return [tip, previous];
        }

        var length = Distance(tip, previous);
        var ux = length < 1e-9 ? 0 : (tip.X - previous.X) / length;
        var uy = length < 1e-9 ? 1 : (tip.Y - previous.Y) / length;
        var px = -uy;
        var py = ux;
        if (head is FlowArrowHead.Arrow or FlowArrowHead.OpenArrow or FlowArrowHead.HollowTriangle)
        {
            var bx = tip.X - (ux * ArrowLength);
            var by = tip.Y - (uy * ArrowLength);
            return
            [
                tip,
                new DiagramPoint(bx + (px * ArrowHalfWidth), by + (py * ArrowHalfWidth)),
                new DiagramPoint(bx - (px * ArrowHalfWidth), by - (py * ArrowHalfWidth)),
            ];
        }

        if (head is FlowArrowHead.FilledDiamond or FlowArrowHead.HollowDiamond)
        {
            return [tip, new(tip.X - ux * 7 + px * 5, tip.Y - uy * 7 + py * 5),
                new(tip.X - ux * 14, tip.Y - uy * 14), new(tip.X - ux * 7 - px * 5, tip.Y - uy * 7 - py * 5)];
        }

        const double half = 4;
        var cx = tip.X - (ux * half);
        var cy = tip.Y - (uy * half);
        return
        [
            new DiagramPoint(cx - half, cy - half),
            new DiagramPoint(cx + half, cy - half),
            new DiagramPoint(cx + half, cy + half),
            new DiagramPoint(cx - half, cy + half),
        ];
    }
}
