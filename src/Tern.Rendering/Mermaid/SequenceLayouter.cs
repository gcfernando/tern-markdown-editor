namespace Tern.Rendering.Mermaid;

/// <summary>Deterministic top-to-bottom layout of a sequence diagram.</summary>
internal static class SequenceLayouter
{
    private const double Margin = 16;
    private const double MinBoxWidth = 80;
    private const double ParticipantGap = 24;
    private const double TitleGap = 14;
    private const double SelfLoopWidth = 40;
    private const double SelfLoopHeight = 22;
    private const double FramePadding = 14;
    private const double FrameNestingInset = 8;

    private sealed class OpenFrame(string kind, string label, double top)
    {
        public string Kind { get; } = kind;

        public string Label { get; } = label;

        public double Top { get; } = top;

        public int MinIndex { get; set; } = int.MaxValue;

        public int MaxIndex { get; set; } = int.MinValue;

        public int Height { get; set; } = 1;

        public List<LaidOutDivider> Dividers { get; } = [];
    }

    public static SequenceLayout Layout(SequenceDiagram diagram, Func<string, (double Width, double Height)> measure)
    {
        var participants = diagram.Participants;
        var count = participants.Count;
        if (count == 0)
        {
            return new SequenceLayout([], [], [], [], Margin * 2, Margin * 2);
        }

        var indexOf = new Dictionary<string, int>(count, StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            indexOf[participants[i].Id] = i;
        }

        var boxWidth = new double[count];
        var boxHeight = 0d;
        for (var i = 0; i < count; i++)
        {
            var (w, h) = measure(participants[i].Label);
            boxWidth[i] = Math.Max(MinBoxWidth, w + 24);
            boxHeight = Math.Max(boxHeight, h + 16);
        }

        boxHeight = Math.Max(boxHeight, 36);

        var textSizes = new Dictionary<SequenceEvent, (double Width, double Height)>(ReferenceEqualityComparer.Instance);
        foreach (var ev in diagram.Events)
        {
            switch (ev)
            {
                case SequenceMessage { Text.Length: > 0 } message:
                    textSizes[ev] = measure(message.Text);
                    break;
                case SequenceNote note:
                    textSizes[ev] = measure(note.Text.Length == 0 ? " " : note.Text);
                    break;
            }
        }

        var centers = AssignCenters(diagram, count, indexOf, boxWidth, textSizes);

        var laidMessages = new List<LaidOutMessage>();
        var laidNotes = new List<LaidOutNote>();
        var laidFrames = new List<LaidOutFrame>();
        var stack = new Stack<OpenFrame>();
        var cursor = Margin + boxHeight + TitleGap;

        void Touch(int index)
        {
            foreach (var frame in stack)
            {
                frame.MinIndex = Math.Min(frame.MinIndex, index);
                frame.MaxIndex = Math.Max(frame.MaxIndex, index);
            }
        }

        foreach (var ev in diagram.Events)
        {
            switch (ev)
            {
                case SequenceMessage message:
                    cursor = PlaceMessage(message, indexOf, centers, textSizes, cursor, laidMessages, Touch);
                    break;
                case SequenceNote note:
                    cursor = PlaceNote(note, indexOf, centers, textSizes[note], cursor, laidNotes, Touch);
                    break;
                case SequenceFrameStart start:
                    stack.Push(new OpenFrame(start.Kind, start.Label, cursor));
                    cursor += 28;
                    break;
                case SequenceFrameDivider divider:
                    if (stack.Count > 0)
                    {
                        stack.Peek().Dividers.Add(new LaidOutDivider(cursor + 4, divider.Label));
                    }

                    cursor += 26;
                    break;
                case SequenceFrameEnd:
                    cursor = CloseFrame(stack, centers, cursor, laidFrames);
                    break;
            }
        }

        var footerTop = cursor + 8;
        var laidParticipants = new List<LaidOutParticipant>(count);
        for (var i = 0; i < count; i++)
        {
            var header = new DiagramRect(centers[i] - (boxWidth[i] / 2), Margin, boxWidth[i], boxHeight);
            var footer = new DiagramRect(header.X, footerTop, boxWidth[i], boxHeight);
            laidParticipants.Add(new LaidOutParticipant(participants[i], header, footer, centers[i], header.Bottom, footerTop));
        }

        return Normalize(laidParticipants, laidMessages, laidNotes, laidFrames, footerTop + boxHeight);
    }

    private static double[] AssignCenters(
        SequenceDiagram diagram,
        int count,
        Dictionary<string, int> indexOf,
        double[] boxWidth,
        Dictionary<SequenceEvent, (double Width, double Height)> textSizes)
    {
        var gaps = new double[Math.Max(count - 1, 0)];
        for (var i = 0; i < gaps.Length; i++)
        {
            gaps[i] = (boxWidth[i] / 2) + (boxWidth[i + 1] / 2) + ParticipantGap;
        }

        var constraints = new List<(int From, int To, double Required)>();
        foreach (var ev in diagram.Events)
        {
            switch (ev)
            {
                case SequenceMessage message:
                {
                    var a = indexOf[message.From];
                    var b = indexOf[message.To];
                    var textWidth = textSizes.TryGetValue(ev, out var size) ? size.Width : 0;
                    if (a == b)
                    {
                        if (a + 1 < count)
                        {
                            constraints.Add((a, a + 1, SelfLoopWidth + textWidth + 24));
                        }
                    }
                    else
                    {
                        constraints.Add((Math.Min(a, b), Math.Max(a, b), textWidth + 32));
                    }

                    break;
                }

                case SequenceNote note:
                {
                    var noteWidth = NoteWidth(textSizes[ev].Width);
                    var first = indexOf[note.Participants[0]];
                    if (note.Placement == SequenceNotePlacement.RightOf && first + 1 < count)
                    {
                        constraints.Add((first, first + 1, noteWidth + 30));
                    }
                    else if (note.Placement == SequenceNotePlacement.LeftOf && first > 0)
                    {
                        constraints.Add((first - 1, first, noteWidth + 30));
                    }
                    else if (note.Placement == SequenceNotePlacement.Over && note.Participants.Count == 2)
                    {
                        var second = indexOf[note.Participants[1]];
                        if (first != second)
                        {
                            constraints.Add((Math.Min(first, second), Math.Max(first, second), noteWidth - 20));
                        }
                    }

                    break;
                }
            }
        }

        foreach (var (from, to, required) in constraints.OrderBy(c => c.To - c.From))
        {
            var current = 0d;
            for (var i = from; i < to; i++)
            {
                current += gaps[i];
            }

            if (current < required)
            {
                gaps[to - 1] += required - current;
            }
        }

        var centers = new double[count];
        centers[0] = boxWidth[0] / 2;
        for (var i = 1; i < count; i++)
        {
            centers[i] = centers[i - 1] + gaps[i - 1];
        }

        return centers;
    }

    private static double NoteWidth(double textWidth) => Math.Max(60, textWidth + 20);

    private static double PlaceMessage(
        SequenceMessage message,
        Dictionary<string, int> indexOf,
        double[] centers,
        Dictionary<SequenceEvent, (double Width, double Height)> textSizes,
        double cursor,
        List<LaidOutMessage> output,
        Action<int> touch)
    {
        var a = indexOf[message.From];
        var b = indexOf[message.To];
        touch(a);
        touch(b);
        var hasText = textSizes.TryGetValue(message, out var size);
        var labelHeight = hasText ? size.Height : 0;
        var y = cursor + (hasText ? labelHeight + 6 : 10);
        var startX = centers[a];
        var endX = centers[b];

        List<DiagramPoint> points;
        DiagramRect? label = null;
        double next;
        if (a == b)
        {
            points =
            [
                new DiagramPoint(startX, y),
                new DiagramPoint(startX + SelfLoopWidth, y),
                new DiagramPoint(startX + SelfLoopWidth, y + SelfLoopHeight),
                new DiagramPoint(startX, y + SelfLoopHeight),
            ];
            if (hasText)
            {
                label = new DiagramRect(startX + SelfLoopWidth + 6, y + (SelfLoopHeight / 2) - (size.Height / 2), size.Width, size.Height);
            }

            next = y + SelfLoopHeight + 12;
        }
        else
        {
            points = [new DiagramPoint(startX, y), new DiagramPoint(endX, y)];
            if (hasText)
            {
                label = new DiagramRect(((startX + endX) / 2) - (size.Width / 2), y - 4 - size.Height, size.Width, size.Height);
            }

            next = y + 14;
        }

        DiagramPoint[] head = message.Head == SequenceHead.None
            ? []
            : FlowchartLayouter.HeadGeometry(message.Head == SequenceHead.Cross ? FlowArrowHead.Cross : FlowArrowHead.Arrow, points[^1], points[^2]);
        output.Add(new LaidOutMessage(message, points, head, label));
        return next;
    }

    private static double PlaceNote(
        SequenceNote note,
        Dictionary<string, int> indexOf,
        double[] centers,
        (double Width, double Height) size,
        double cursor,
        List<LaidOutNote> output,
        Action<int> touch)
    {
        var first = indexOf[note.Participants[0]];
        touch(first);
        var width = NoteWidth(size.Width);
        var height = size.Height + 12;
        double x;
        switch (note.Placement)
        {
            case SequenceNotePlacement.LeftOf:
                x = centers[first] - 10 - width;
                break;
            case SequenceNotePlacement.RightOf:
                x = centers[first] + 10;
                break;
            default:
            {
                var second = note.Participants.Count == 2 ? indexOf[note.Participants[1]] : first;
                touch(second);
                var left = Math.Min(centers[first], centers[second]);
                var right = Math.Max(centers[first], centers[second]);
                width = Math.Max(width, right - left + 20);
                x = ((left + right) / 2) - (width / 2);
                break;
            }
        }

        output.Add(new LaidOutNote(note, new DiagramRect(x, cursor + 4, width, height)));
        return cursor + 4 + height + 10;
    }

    private static double CloseFrame(Stack<OpenFrame> stack, double[] centers, double cursor, List<LaidOutFrame> output)
    {
        if (stack.Count == 0)
        {
            return cursor;
        }

        var frame = stack.Pop();
        var minIndex = frame.MinIndex == int.MaxValue ? 0 : frame.MinIndex;
        var maxIndex = frame.MaxIndex == int.MinValue ? centers.Length - 1 : frame.MaxIndex;
        if (stack.Count > 0)
        {
            var parent = stack.Peek();
            parent.Height = Math.Max(parent.Height, frame.Height + 1);
        }

        var padding = FramePadding + (FrameNestingInset * (frame.Height - 1));
        var left = centers[minIndex] - padding - 30;
        var right = centers[maxIndex] + padding + 30;
        var bottom = cursor + 6;
        output.Add(new LaidOutFrame(frame.Kind, frame.Label, new DiagramRect(left, frame.Top, right - left, bottom - frame.Top), frame.Dividers));
        return cursor + 18;
    }

    private static SequenceLayout Normalize(
        List<LaidOutParticipant> participants,
        List<LaidOutMessage> messages,
        List<LaidOutNote> notes,
        List<LaidOutFrame> frames,
        double contentBottom)
    {
        var minX = double.MaxValue;
        var maxX = double.MinValue;

        void Include(DiagramRect r)
        {
            minX = Math.Min(minX, r.X);
            maxX = Math.Max(maxX, r.Right);
        }

        foreach (var p in participants)
        {
            Include(p.Header);
        }

        foreach (var m in messages)
        {
            foreach (var point in m.Points)
            {
                Include(new DiagramRect(point.X, 0, 0, 0));
            }

            if (m.LabelBounds is { } label)
            {
                Include(label);
            }
        }

        foreach (var n in notes)
        {
            Include(n.Bounds);
        }

        foreach (var f in frames)
        {
            Include(f.Bounds);
        }

        var dx = Margin - minX;
        DiagramRect Shift(DiagramRect r) => new(r.X + dx, r.Y, r.Width, r.Height);
        DiagramPoint ShiftPoint(DiagramPoint p) => new(p.X + dx, p.Y);

        var shiftedParticipants = participants
            .Select(p => new LaidOutParticipant(p.Participant, Shift(p.Header), Shift(p.Footer), p.CenterX + dx, p.LifelineTop, p.LifelineBottom))
            .ToList();
        var shiftedMessages = messages
            .Select(m => new LaidOutMessage(m.Message, m.Points.Select(ShiftPoint).ToList(), m.HeadMarker.Select(ShiftPoint).ToList(), m.LabelBounds is { } l ? Shift(l) : null))
            .ToList();
        var shiftedNotes = notes.Select(n => new LaidOutNote(n.Note, Shift(n.Bounds))).ToList();
        var shiftedFrames = frames.Select(f => new LaidOutFrame(f.Kind, f.Label, Shift(f.Bounds), f.Dividers)).ToList();
        return new SequenceLayout(shiftedParticipants, shiftedMessages, shiftedNotes, shiftedFrames, (maxX - minX) + (Margin * 2), contentBottom + Margin);
    }
}
