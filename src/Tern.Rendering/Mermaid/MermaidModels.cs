namespace Tern.Rendering.Mermaid;

/// <summary>A point in diagram space; the origin is the top-left corner and Y grows downwards.</summary>
public readonly record struct DiagramPoint(double X, double Y);

/// <summary>An axis-aligned rectangle in diagram space.</summary>
public readonly record struct DiagramRect(double X, double Y, double Width, double Height)
{
    /// <summary>X of the right edge.</summary>
    public double Right => X + Width;

    /// <summary>Y of the bottom edge.</summary>
    public double Bottom => Y + Height;

    /// <summary>The centre point.</summary>
    public DiagramPoint Center => new(X + (Width / 2), Y + (Height / 2));

    /// <summary>True when the interiors of both rectangles overlap (touching edges do not count).</summary>
    public bool Overlaps(DiagramRect other) =>
        X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    /// <summary>True when the point lies inside or on the border, allowing <paramref name="tolerance"/> of slack.</summary>
    public bool Contains(DiagramPoint point, double tolerance = 0) =>
        point.X >= X - tolerance && point.X <= Right + tolerance && point.Y >= Y - tolerance && point.Y <= Bottom + tolerance;
}

/// <summary>Limits applied while parsing a Mermaid diagram.</summary>
public static class MermaidLimits
{
    /// <summary>Maximum source size in UTF-16 characters (100 KB).</summary>
    public const int MaxSourceLength = 100 * 1024;

    /// <summary>Maximum number of flowchart nodes or sequence participants.</summary>
    public const int MaxNodes = 500;

    /// <summary>Maximum number of flowchart edges or sequence messages.</summary>
    public const int MaxEdges = 1000;

    /// <summary>Maximum length of one source line.</summary>
    public const int MaxLineLength = 4000;
}

/// <summary>What <see cref="MermaidDiagram.TryParse"/> found.</summary>
public enum MermaidResultKind
{
    /// <summary>A flowchart or graph.</summary>
    Flowchart,

    /// <summary>A sequence diagram.</summary>
    Sequence,

    /// <summary>A bounded native pie or Gantt chart.</summary>
    Chart,

    /// <summary>A valid Mermaid diagram of a type this library cannot draw; show the source with a notice.</summary>
    Unsupported,

    /// <summary>The source is empty, over budget or malformed.</summary>
    Error,
}

/// <summary>The outcome of parsing Mermaid source. Exactly one payload matching <see cref="Kind"/> is set.</summary>
public sealed class MermaidParseResult
{
    private MermaidParseResult(MermaidResultKind kind) => Kind = kind;

    /// <summary>The kind of result.</summary>
    public MermaidResultKind Kind { get; }

    /// <summary>The parsed flowchart when <see cref="Kind"/> is <see cref="MermaidResultKind.Flowchart"/>.</summary>
    public FlowchartDiagram? Flowchart { get; private init; }

    /// <summary>The parsed sequence diagram when <see cref="Kind"/> is <see cref="MermaidResultKind.Sequence"/>.</summary>
    public SequenceDiagram? Sequence { get; private init; }

    /// <summary>The parsed chart when Kind is Chart.</summary>
    public ChartDiagram? Chart { get; private init; }

    /// <summary>The diagram type keyword (e.g. <c>gantt</c>) when <see cref="Kind"/> is <see cref="MermaidResultKind.Unsupported"/>.</summary>
    public string? UnsupportedType { get; private init; }

    /// <summary>A description of the problem when <see cref="Kind"/> is <see cref="MermaidResultKind.Error"/>.</summary>
    public string? ErrorMessage { get; private init; }

    /// <summary>The 1-based source line of the error, or 0 when it is not tied to a line.</summary>
    public int ErrorLine { get; private init; }

    internal static MermaidParseResult FromFlowchart(FlowchartDiagram diagram) =>
        new(MermaidResultKind.Flowchart) { Flowchart = diagram };

    internal static MermaidParseResult FromSequence(SequenceDiagram diagram) =>
        new(MermaidResultKind.Sequence) { Sequence = diagram };

    internal static MermaidParseResult FromChart(ChartDiagram diagram) => new(MermaidResultKind.Chart) { Chart = diagram };

    internal static MermaidParseResult FromUnsupported(string type) =>
        new(MermaidResultKind.Unsupported) { UnsupportedType = type };

    internal static MermaidParseResult FromError(string message, int line) =>
        new(MermaidResultKind.Error) { ErrorMessage = message, ErrorLine = line };
}

internal sealed class MermaidSyntaxException(string message, int line) : Exception(message)
{
    public int Line { get; } = line;
}
