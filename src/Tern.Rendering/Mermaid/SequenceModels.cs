namespace Tern.Rendering.Mermaid;

/// <summary>End marker of a sequence diagram message.</summary>
public enum SequenceHead
{
    /// <summary>Filled arrowhead (<c>-&gt;&gt;</c>).</summary>
    Filled,

    /// <summary>A plain line without an arrowhead (<c>-&gt;</c>).</summary>
    None,

    /// <summary>A cross (<c>-x</c>).</summary>
    Cross,

    /// <summary>An open asynchronous arrowhead (<c>-)</c>).</summary>
    Open,
}

/// <summary>Where a note sits relative to its participants.</summary>
public enum SequenceNotePlacement
{
    /// <summary><c>left of</c></summary>
    LeftOf,

    /// <summary><c>right of</c></summary>
    RightOf,

    /// <summary><c>over</c> one or two participants</summary>
    Over,
}

/// <summary>A participant or actor.</summary>
/// <param name="Id">The identifier used in messages.</param>
/// <param name="Label">Display text.</param>
/// <param name="IsActor">True for <c>actor</c>.</param>
public sealed record SequenceParticipant(string Id, string Label, bool IsActor);

/// <summary>An item in the vertical timeline of a sequence diagram.</summary>
public abstract record SequenceEvent;

/// <summary>A message between participants (or to itself when both ids are equal).</summary>
public sealed record SequenceMessage(string From, string To, string Text, bool IsDashed, SequenceHead Head, int? Number) : SequenceEvent;

/// <summary>A note attached to one or two participants.</summary>
public sealed record SequenceNote(SequenceNotePlacement Placement, IReadOnlyList<string> Participants, string Text) : SequenceEvent;

/// <summary>Start of a <c>loop</c>, <c>alt</c>, <c>opt</c>, <c>par</c>, <c>critical</c>, <c>break</c> or <c>rect</c> block.</summary>
public sealed record SequenceFrameStart(string Kind, string Label) : SequenceEvent;

/// <summary>An <c>else</c>, <c>and</c> or <c>option</c> divider inside a frame.</summary>
public sealed record SequenceFrameDivider(string Label) : SequenceEvent;

/// <summary>End of a frame.</summary>
public sealed record SequenceFrameEnd : SequenceEvent;

/// <summary>A parsed sequence diagram. Call <see cref="Layout"/> to obtain geometry.</summary>
public sealed class SequenceDiagram
{
    internal SequenceDiagram(IReadOnlyList<SequenceParticipant> participants, IReadOnlyList<SequenceEvent> events, string? title)
    {
        Participants = participants;
        Events = events;
        Title = title;
    }

    /// <summary>Participants from left to right, in order of declaration or first use.</summary>
    public IReadOnlyList<SequenceParticipant> Participants { get; }

    /// <summary>Messages, notes and frame markers in timeline order.</summary>
    public IReadOnlyList<SequenceEvent> Events { get; }

    /// <summary>The <c>title</c> line, if any.</summary>
    public string? Title { get; }

    /// <summary>Computes a deterministic layout.</summary>
    /// <param name="measurer">Measures label text; defaults to <see cref="MermaidDiagram.EstimateTextSize"/>.</param>
    public SequenceLayout Layout(Func<string, (double Width, double Height)>? measurer = null) =>
        SequenceLayouter.Layout(this, MermaidDiagram.Guard(measurer));
}

/// <summary>A positioned participant with its lifeline.</summary>
public sealed record LaidOutParticipant(SequenceParticipant Participant, DiagramRect Header, DiagramRect Footer, double CenterX, double LifelineTop, double LifelineBottom);

/// <summary>A positioned message.</summary>
/// <param name="Message">The message.</param>
/// <param name="Points">Polyline of the arrow (two points, or four for a self message).</param>
/// <param name="HeadMarker">Marker vertices at the end of the polyline; empty when there is no head.</param>
/// <param name="LabelBounds">Label rectangle, or null when the message has no text.</param>
public sealed record LaidOutMessage(SequenceMessage Message, IReadOnlyList<DiagramPoint> Points, IReadOnlyList<DiagramPoint> HeadMarker, DiagramRect? LabelBounds);

/// <summary>A positioned note.</summary>
public sealed record LaidOutNote(SequenceNote Note, DiagramRect Bounds);

/// <summary>A divider line inside a frame.</summary>
public sealed record LaidOutDivider(double Y, string Label);

/// <summary>A positioned frame.</summary>
public sealed record LaidOutFrame(string Kind, string Label, DiagramRect Bounds, IReadOnlyList<LaidOutDivider> Dividers);

/// <summary>Geometry of a laid out sequence diagram.</summary>
public sealed class SequenceLayout
{
    internal SequenceLayout(
        IReadOnlyList<LaidOutParticipant> participants,
        IReadOnlyList<LaidOutMessage> messages,
        IReadOnlyList<LaidOutNote> notes,
        IReadOnlyList<LaidOutFrame> frames,
        double width,
        double height)
    {
        Participants = participants;
        Messages = messages;
        Notes = notes;
        Frames = frames;
        Width = width;
        Height = height;
    }

    /// <summary>Participant boxes and lifelines.</summary>
    public IReadOnlyList<LaidOutParticipant> Participants { get; }

    /// <summary>Messages in timeline order.</summary>
    public IReadOnlyList<LaidOutMessage> Messages { get; }

    /// <summary>Notes in timeline order.</summary>
    public IReadOnlyList<LaidOutNote> Notes { get; }

    /// <summary>Frames in order of their start.</summary>
    public IReadOnlyList<LaidOutFrame> Frames { get; }

    /// <summary>Total width including margins.</summary>
    public double Width { get; }

    /// <summary>Total height including margins.</summary>
    public double Height { get; }
}
