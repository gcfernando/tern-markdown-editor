namespace Tern.Rendering.Mermaid;

/// <summary>Direction in which a flowchart flows.</summary>
public enum FlowDirection
{
    /// <summary>Top to bottom (TD / TB).</summary>
    TopDown,

    /// <summary>Bottom to top (BT).</summary>
    BottomUp,

    /// <summary>Left to right (LR).</summary>
    LeftRight,

    /// <summary>Right to left (RL).</summary>
    RightLeft,
}

/// <summary>Outline of a flowchart node.</summary>
public enum FlowNodeShape
{
    /// <summary><c>[text]</c></summary>
    Rectangle,

    /// <summary><c>(text)</c></summary>
    Rounded,

    /// <summary><c>([text])</c></summary>
    Stadium,

    /// <summary><c>((text))</c></summary>
    Circle,

    /// <summary><c>{text}</c></summary>
    Diamond,

    /// <summary><c>{{text}}</c></summary>
    Hexagon,

    /// <summary><c>[[text]]</c></summary>
    Subroutine,

    /// <summary><c>[(text)]</c></summary>
    Cylinder,

    /// <summary>An entity with a separated attribute compartment.</summary>
    Entity,

    /// <summary>A UML class with separate attribute and operation compartments.</summary>
    Class,

    /// <summary>A filled initial-state marker.</summary>
    InitialState,

    /// <summary>A ring surrounding a filled final-state marker.</summary>
    FinalState,
}

/// <summary>Stroke style of an edge.</summary>
public enum FlowLineStyle
{
    /// <summary><c>--</c></summary>
    Solid,

    /// <summary><c>-.-</c></summary>
    Dotted,

    /// <summary><c>==</c></summary>
    Thick,

    /// <summary><c>~~~</c>: laid out but not drawn.</summary>
    Invisible,
}

/// <summary>Marker at one end of an edge.</summary>
public enum FlowArrowHead
{
    /// <summary>Plain line end.</summary>
    None,

    /// <summary>Arrowhead.</summary>
    Arrow,

    /// <summary>Open circle (<c>--o</c>).</summary>
    Circle,

    /// <summary>Cross (<c>--x</c>).</summary>
    Cross,

    /// <summary>ER cardinality: exactly one.</summary>
    One,

    /// <summary>ER cardinality: zero or one.</summary>
    ZeroOrOne,

    /// <summary>ER cardinality: one or more.</summary>
    Many,

    /// <summary>ER cardinality: zero or more.</summary>
    ZeroOrMany,

    /// <summary>An open navigability or dependency arrow.</summary>
    OpenArrow,

    /// <summary>A UML generalization triangle.</summary>
    HollowTriangle,

    /// <summary>A UML composition diamond.</summary>
    FilledDiamond,

    /// <summary>A UML aggregation diamond.</summary>
    HollowDiamond,
}

/// <summary>A flowchart node.</summary>
/// <param name="Id">The identifier used in edges.</param>
/// <param name="Label">Display text; may contain newlines.</param>
/// <param name="Shape">Outline.</param>
public sealed record FlowNode(string Id, string Label, FlowNodeShape Shape)
{
    /// <summary>Entity attributes, retaining type, name, keys and comment as display text.</summary>
    public IReadOnlyList<string> Attributes { get; init; } = [];

    /// <summary>Operations in a UML class, retaining visibility and signature text.</summary>
    public IReadOnlyList<string> Methods { get; init; } = [];
}

/// <summary>A flowchart edge.</summary>
public sealed record FlowEdge(string From, string To, string? Label, FlowLineStyle Style, FlowArrowHead StartHead, FlowArrowHead EndHead)
{
    /// <summary>Multiplicity at the source end of a class relationship.</summary>
    public string? StartLabel { get; init; }
    /// <summary>Multiplicity at the destination end of a class relationship.</summary>
    public string? EndLabel { get; init; }
}

/// <summary>Semantics represented by a natively laid out graph.</summary>
public enum GraphDiagramKind
{
    /// <summary>Ordinary Mermaid flowchart.</summary>
    Flowchart,
    /// <summary>Entity relationship graph.</summary>
    EntityRelationship,
    /// <summary>UML class graph.</summary>
    Class,
    /// <summary>State transition graph.</summary>
    State,
    /// <summary>Indented mind map.</summary>
    Mindmap,
}

/// <summary>A <c>subgraph</c> and the nodes it contains, including those of nested subgraphs.</summary>
public sealed record FlowSubgraph(string Id, string Title, IReadOnlyList<string> NodeIds);

/// <summary>A parsed flowchart. Call <see cref="Layout"/> to obtain geometry.</summary>
public sealed class FlowchartDiagram
{
    internal FlowchartDiagram(FlowDirection direction, IReadOnlyList<FlowNode> nodes, IReadOnlyList<FlowEdge> edges, IReadOnlyList<FlowSubgraph> subgraphs, GraphDiagramKind kind = GraphDiagramKind.Flowchart)
    {
        Direction = direction;
        Nodes = nodes;
        Edges = edges;
        Subgraphs = subgraphs;
        Kind = kind;
    }

    /// <summary>The flow direction.</summary>
    public FlowDirection Direction { get; }

    /// <summary>The graph's semantic type.</summary>
    public GraphDiagramKind Kind { get; }

    /// <summary>Plain accessible description shared by native and export renderers.</summary>
    public string Description => Kind switch
    {
        GraphDiagramKind.EntityRelationship => "Entity relationship diagram",
        GraphDiagramKind.Class => "Class diagram",
        GraphDiagramKind.State => "State diagram",
        GraphDiagramKind.Mindmap => "Mind map",
        _ => "Flowchart diagram",
    };

    /// <summary>Nodes in order of first appearance.</summary>
    public IReadOnlyList<FlowNode> Nodes { get; }

    /// <summary>Edges in source order.</summary>
    public IReadOnlyList<FlowEdge> Edges { get; }

    /// <summary>Subgraphs in order of declaration.</summary>
    public IReadOnlyList<FlowSubgraph> Subgraphs { get; }

    /// <summary>Computes a deterministic layered layout.</summary>
    /// <param name="measurer">Measures label text; defaults to <see cref="MermaidDiagram.EstimateTextSize"/>.</param>
    public FlowchartLayout Layout(Func<string, (double Width, double Height)>? measurer = null) =>
        Kind == GraphDiagramKind.Mindmap ? MindmapLayouter.Layout(this, MermaidDiagram.Guard(measurer))
        : ClassLabelLayouter.AddEndLabels(FlowchartLayouter.Layout(this, MermaidDiagram.Guard(measurer)), MermaidDiagram.Guard(measurer));
}

/// <summary>A positioned node.</summary>
/// <param name="Node">The node.</param>
/// <param name="Bounds">Bounding rectangle.</param>
/// <param name="Layer">Zero-based rank along the flow direction.</param>
public sealed record LaidOutNode(FlowNode Node, DiagramRect Bounds, int Layer);

/// <summary>A routed edge.</summary>
/// <param name="Edge">The edge.</param>
/// <param name="Points">Polyline from the source boundary to the target boundary.</param>
/// <param name="StartArrow">Marker vertices at the start (a triangle for arrows, a bounding square for circle and cross markers); empty when there is none.</param>
/// <param name="EndArrow">Marker vertices at the end, same conventions as <paramref name="StartArrow"/>.</param>
/// <param name="LabelBounds">Where the label goes, or null when the edge has none.</param>
public sealed record LaidOutEdge(FlowEdge Edge, IReadOnlyList<DiagramPoint> Points, IReadOnlyList<DiagramPoint> StartArrow, IReadOnlyList<DiagramPoint> EndArrow, DiagramRect? LabelBounds)
{
    /// <summary>A thin annotation leader when a label needs to sit beside its connection.</summary>
    public IReadOnlyList<DiagramPoint> LabelLeader { get; init; } = [];
    /// <summary>Position of the source multiplicity.</summary>
    public DiagramRect? StartLabelBounds { get; init; }
    /// <summary>Position of the destination multiplicity.</summary>
    public DiagramRect? EndLabelBounds { get; init; }
}

/// <summary>A positioned subgraph frame.</summary>
public sealed record LaidOutSubgraph(FlowSubgraph Subgraph, DiagramRect Bounds);

/// <summary>Geometry of a laid out flowchart.</summary>
public sealed class FlowchartLayout
{
    internal FlowchartLayout(IReadOnlyList<LaidOutNode> nodes, IReadOnlyList<LaidOutEdge> edges, IReadOnlyList<LaidOutSubgraph> subgraphs, double width, double height)
    {
        Nodes = nodes;
        Edges = edges;
        Subgraphs = subgraphs;
        Width = width;
        Height = height;
    }

    /// <summary>Positioned nodes, in the same order as the diagram.</summary>
    public IReadOnlyList<LaidOutNode> Nodes { get; }

    /// <summary>Routed edges, in the same order as the diagram.</summary>
    public IReadOnlyList<LaidOutEdge> Edges { get; }

    /// <summary>Subgraph frames.</summary>
    public IReadOnlyList<LaidOutSubgraph> Subgraphs { get; }

    /// <summary>Total width including margins.</summary>
    public double Width { get; }

    /// <summary>Total height including margins.</summary>
    public double Height { get; }
}
