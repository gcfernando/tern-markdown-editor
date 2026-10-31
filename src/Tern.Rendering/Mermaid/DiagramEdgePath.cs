namespace Tern.Rendering.Mermaid;

/// <summary>A straight segment or a quadratic corner, shared by native and export drawing.</summary>
public readonly record struct DiagramPathSegment(DiagramPoint Start, DiagramPoint End, DiagramPoint? Control = null);

/// <summary>Rounds routed corners without changing endpoints or arrow tangents.</summary>
public static class DiagramEdgePath
{
    /// <summary>Builds tangent-continuous quadratic corners with a bounded 16-unit radius.</summary>
    public static IReadOnlyList<DiagramPathSegment> Build(IReadOnlyList<DiagramPoint> points)
    {
        var clean = new List<DiagramPoint>();
        foreach (var point in points)
        {
            if (clean.Count > 0 && Length(clean[^1], point) < 0.001) continue;
            while (clean.Count >= 2 && Collinear(clean[^2], clean[^1], point)) clean.RemoveAt(clean.Count - 1);
            clean.Add(point);
        }
        var result = new List<DiagramPathSegment>();
        if (clean.Count < 2) return result;
        var current = clean[0];
        for (var i = 1; i < clean.Count - 1; i++)
        {
            var a = clean[i - 1]; var b = clean[i]; var c = clean[i + 1];
            var incoming = Length(a, b); var outgoing = Length(b, c);
            var radius = Math.Min(16, Math.Min(incoming, outgoing) / 2);
            if (radius < 0.001 || Math.Abs((b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X)) < 0.001)
            { result.Add(new(current, b)); current = b; continue; }
            var entry = new DiagramPoint(b.X - (b.X - a.X) / incoming * radius, b.Y - (b.Y - a.Y) / incoming * radius);
            var exit = new DiagramPoint(b.X + (c.X - b.X) / outgoing * radius, b.Y + (c.Y - b.Y) / outgoing * radius);
            if (Length(current, entry) > 0.001) result.Add(new(current, entry));
            result.Add(new(entry, exit, b));
            current = exit;
        }
        if (Length(current, clean[^1]) > 0.001) result.Add(new(current, clean[^1]));
        return result;
    }

    private static double Length(DiagramPoint a, DiagramPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private static bool Collinear(DiagramPoint a, DiagramPoint b, DiagramPoint c) =>
        Math.Abs((b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X)) < 0.001
        && (b.X - a.X) * (c.X - b.X) + (b.Y - a.Y) * (c.Y - b.Y) >= 0;
}
