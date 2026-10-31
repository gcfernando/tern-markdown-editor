using System.Text.RegularExpressions;

namespace Tern.Rendering.Mermaid;

/// <summary>Parses the supported subset of Mermaid <c>sequenceDiagram</c> syntax.</summary>
internal static partial class SequenceParser
{
    private static readonly string[] FrameKeywords = ["loop", "alt", "opt", "par", "critical", "break", "rect"];
    private static readonly string[] DividerKeywords = ["else", "and", "option"];

    public static SequenceDiagram Parse(IReadOnlyList<MermaidDiagram.SourceLine> lines)
    {
        var participants = new List<SequenceParticipant>();
        var events = new List<SequenceEvent>();
        var frameDepth = 0;
        var autoNumber = false;
        var counter = 0;
        var messageCount = 0;
        string? title = null;
        var headerSeen = false;
        var lastLine = 0;

        foreach (var (number, text) in lines)
        {
            if (text.Length == 0)
            {
                continue;
            }

            lastLine = number;
            if (!headerSeen)
            {
                headerSeen = true;
                continue;
            }

            var statement = text.TrimEnd(';').Trim();
            var keyword = FirstWord(statement);

            if (keyword is "participant" or "actor")
            {
                var match = ParticipantDeclaration().Match(statement);
                if (!match.Success)
                {
                    throw new MermaidSyntaxException($"Invalid {keyword} declaration.", number);
                }

                var id = match.Groups["id"].Value;
                var label = match.Groups["label"].Success ? CleanText(match.Groups["label"].Value) : id;
                Declare(participants, id, label, keyword == "actor", number, overwrite: true);
            }
            else if (keyword == "autonumber")
            {
                autoNumber = true;
            }
            else if (keyword == "title")
            {
                title = CleanText(statement["title".Length..].TrimStart(':', ' '));
            }
            else if (keyword is "activate" or "deactivate" or "link" or "links" or "properties" or "details")
            {
                continue;
            }
            else if (FrameKeywords.Contains(keyword, StringComparer.Ordinal))
            {
                frameDepth++;
                events.Add(new SequenceFrameStart(keyword, CleanText(statement[keyword.Length..])));
            }
            else if (DividerKeywords.Contains(keyword, StringComparer.Ordinal))
            {
                if (frameDepth == 0)
                {
                    throw new MermaidSyntaxException($"'{keyword}' outside of a block.", number);
                }

                events.Add(new SequenceFrameDivider(CleanText(statement[keyword.Length..])));
            }
            else if (keyword == "end")
            {
                if (frameDepth == 0)
                {
                    throw new MermaidSyntaxException("'end' without a matching block.", number);
                }

                frameDepth--;
                events.Add(new SequenceFrameEnd());
            }
            else if (keyword.Equals("note", StringComparison.OrdinalIgnoreCase))
            {
                var match = NoteStatement().Match(statement);
                if (!match.Success)
                {
                    throw new MermaidSyntaxException("Invalid note.", number);
                }

                var placement = match.Groups["place"].Value.ToLowerInvariant() switch
                {
                    "left of" => SequenceNotePlacement.LeftOf,
                    "right of" => SequenceNotePlacement.RightOf,
                    _ => SequenceNotePlacement.Over,
                };
                var ids = match.Groups["who"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (ids.Length == 0 || (placement != SequenceNotePlacement.Over && ids.Length != 1) || ids.Length > 2)
                {
                    throw new MermaidSyntaxException("A note needs one participant, or two for 'over'.", number);
                }

                foreach (var id in ids)
                {
                    Declare(participants, id, id, false, number, overwrite: false);
                }

                CountEvent(ref messageCount, number);
                events.Add(new SequenceNote(placement, ids, CleanText(match.Groups["text"].Value)));
            }
            else
            {
                var match = MessageStatement().Match(statement);
                if (!match.Success)
                {
                    throw new MermaidSyntaxException($"Unrecognised statement '{Truncate(statement)}'.", number);
                }

                var from = match.Groups["from"].Value;
                var to = match.Groups["to"].Value;
                Declare(participants, from, from, false, number, overwrite: false);
                Declare(participants, to, to, false, number, overwrite: false);
                CountEvent(ref messageCount, number);
                var arrow = match.Groups["arrow"].Value;
                var head = arrow[^1] switch
                {
                    '>' when arrow.EndsWith(">>", StringComparison.Ordinal) => SequenceHead.Filled,
                    '>' => SequenceHead.None,
                    'x' => SequenceHead.Cross,
                    _ => SequenceHead.Open,
                };
                events.Add(new SequenceMessage(from, to, CleanText(match.Groups["text"].Value), arrow.StartsWith("--", StringComparison.Ordinal), head, autoNumber ? ++counter : null));
            }
        }

        if (frameDepth > 0)
        {
            throw new MermaidSyntaxException("A block is missing its 'end'.", lastLine);
        }

        return new SequenceDiagram(participants, events, title);
    }

    private static void CountEvent(ref int count, int line)
    {
        if (++count > MermaidLimits.MaxEdges)
        {
            throw new MermaidSyntaxException($"The diagram has more than {MermaidLimits.MaxEdges} messages and notes.", line);
        }
    }

    private static void Declare(List<SequenceParticipant> participants, string id, string label, bool isActor, int line, bool overwrite)
    {
        var index = participants.FindIndex(p => p.Id == id);
        if (index >= 0)
        {
            if (overwrite)
            {
                participants[index] = new SequenceParticipant(id, label, isActor);
            }

            return;
        }

        if (participants.Count >= MermaidLimits.MaxNodes)
        {
            throw new MermaidSyntaxException($"The diagram has more than {MermaidLimits.MaxNodes} participants.", line);
        }

        participants.Add(new SequenceParticipant(id, label, isActor));
    }

    private static string FirstWord(string statement)
    {
        var end = 0;
        while (end < statement.Length && !char.IsWhiteSpace(statement[end]))
        {
            end++;
        }

        return statement[..end];
    }

    private static string CleanText(string raw) =>
        MermaidDiagram.DecodeEntities(LineBreakTag().Replace(raw.Trim(), "\n")).Trim();

    private static string Truncate(string value) => value.Length <= 40 ? value : value[..40] + "…";

    [GeneratedRegex(@"^(?:participant|actor)\s+(?<id>[\p{L}\p{N}_.]+)(?:\s+as\s+(?<label>.+))?$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex ParticipantDeclaration();

    [GeneratedRegex(@"^note\s+(?<place>left of|right of|over)\s+(?<who>[^:]+):\s*(?<text>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex NoteStatement();

    [GeneratedRegex(@"^(?<from>[\p{L}\p{N}_.]+)\s*(?<arrow>-->>|->>|-->|->|--x|-x|--\)|-\))\s*[+-]?\s*(?<to>[\p{L}\p{N}_.]+)\s*(?::\s*(?<text>.*))?$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex MessageStatement();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex LineBreakTag();
}
