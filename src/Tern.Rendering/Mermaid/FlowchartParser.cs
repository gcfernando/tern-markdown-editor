using System.Text;
using System.Text.RegularExpressions;

namespace Tern.Rendering.Mermaid;

/// <summary>Parses the supported subset of Mermaid <c>flowchart</c> / <c>graph</c> syntax.</summary>
internal sealed partial class FlowchartParser
{
    private static readonly (string Open, string[] Close, FlowNodeShape Shape)[] ShapeTable =
    [
        ("((", ["))"], FlowNodeShape.Circle),
        ("([", ["])"], FlowNodeShape.Stadium),
        ("[[", ["]]"], FlowNodeShape.Subroutine),
        ("[(", [")]"], FlowNodeShape.Cylinder),
        ("{{", ["}}"], FlowNodeShape.Hexagon),
        ("[/", ["/]", "\\]"], FlowNodeShape.Rectangle),
        ("[\\", ["\\]", "/]"], FlowNodeShape.Rectangle),
        ("[", ["]"], FlowNodeShape.Rectangle),
        ("(", [")"], FlowNodeShape.Rounded),
        ("{", ["}"], FlowNodeShape.Diamond),
        (">", ["]"], FlowNodeShape.Rectangle),
    ];

    private static readonly string[] IgnoredKeywords =
        ["classDef", "class", "style", "linkStyle", "click", "accTitle", "accDescr", "direction"];

    private readonly List<FlowNode> nodes = [];
    private readonly Dictionary<string, int> nodeIndex = new(StringComparer.Ordinal);
    private readonly List<FlowEdge> edges = [];
    private readonly List<(string Id, string Title, List<string> Members)> subgraphs = [];
    private readonly Stack<int> openSubgraphs = new();
    private readonly HashSet<string> subgraphIds = new(StringComparer.Ordinal);
    private int lineNumber;

    public static FlowchartDiagram Parse(IReadOnlyList<MermaidDiagram.SourceLine> lines)
    {
        var parser = new FlowchartParser();
        return parser.Run(lines);
    }

    private FlowchartDiagram Run(IReadOnlyList<MermaidDiagram.SourceLine> lines)
    {
        var direction = FlowDirection.TopDown;
        var headerSeen = false;
        foreach (var line in lines)
        {
            if (line.Text.Length == 0)
            {
                continue;
            }

            lineNumber = line.Number;
            if (!headerSeen)
            {
                headerSeen = true;
                var statements = SplitStatements(line.Text);
                direction = ParseHeader(statements[0]);
                foreach (var statement in statements.Skip(1))
                {
                    ParseStatement(statement);
                }

                continue;
            }

            if (StartsWithKeyword(line.Text, IgnoredKeywords))
            {
                continue;
            }

            foreach (var statement in SplitStatements(line.Text))
            {
                ParseStatement(statement);
            }
        }

        if (openSubgraphs.Count > 0)
        {
            throw new MermaidSyntaxException("A subgraph is missing its 'end'.", lineNumber);
        }

        return new FlowchartDiagram(
            direction,
            nodes,
            edges,
            subgraphs.Select(s => new FlowSubgraph(s.Id, s.Title, s.Members)).ToList());
    }

    private FlowDirection ParseHeader(string statement)
    {
        var parts = statement.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            return FlowDirection.TopDown;
        }

        return parts[1].ToUpperInvariant() switch
        {
            "TD" or "TB" => FlowDirection.TopDown,
            "BT" => FlowDirection.BottomUp,
            "LR" => FlowDirection.LeftRight,
            "RL" => FlowDirection.RightLeft,
            _ => throw new MermaidSyntaxException($"Unknown flow direction '{parts[1]}'.", lineNumber),
        };
    }

    private static bool StartsWithKeyword(string text, string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (text.StartsWith(keyword, StringComparison.Ordinal)
                && (text.Length == keyword.Length || char.IsWhiteSpace(text[keyword.Length])))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Splits on semicolons that are not inside quotes or brackets.</summary>
    private static List<string> SplitStatements(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        var inQuote = false;
        var inPipeLabel = false;
        foreach (var ch in line)
        {
            if (ch == '"')
            {
                inQuote = !inQuote;
            }
            else if (!inQuote && depth == 0 && ch == '|')
            {
                inPipeLabel = !inPipeLabel;
            }
            else if (!inQuote && !inPipeLabel && ch is '[' or '(' or '{')
            {
                depth++;
            }
            else if (!inQuote && !inPipeLabel && ch is ']' or ')' or '}')
            {
                depth = Math.Max(0, depth - 1);
            }

            if (ch == ';' && !inQuote && !inPipeLabel && depth == 0 && !EndsWithEntityReference(current))
            {
                Flush();
            }
            else
            {
                current.Append(ch);
            }
        }

        Flush();
        if (result.Count == 0)
        {
            result.Add(string.Empty);
        }

        return result;

        void Flush()
        {
            var text = current.ToString().Trim();
            if (text.Length > 0)
            {
                result.Add(text);
            }

            current.Clear();
        }

        static bool EndsWithEntityReference(StringBuilder builder)
        {
            var index = builder.Length - 1;
            var length = 0;
            while (index >= 0 && length < 32 && char.IsAsciiLetterOrDigit(builder[index]))
            {
                index--;
                length++;
            }

            return length > 0 && index >= 0 && builder[index] == '#';
        }
    }

    private void ParseStatement(string statement)
    {
        if (statement.Length == 0 || StartsWithKeyword(statement, IgnoredKeywords))
        {
            return;
        }

        if (StartsWithKeyword(statement, ["subgraph"]))
        {
            OpenSubgraph(statement["subgraph".Length..].Trim());
            return;
        }

        if (statement == "end")
        {
            if (openSubgraphs.Count == 0)
            {
                throw new MermaidSyntaxException("'end' without a matching 'subgraph'.", lineNumber);
            }

            openSubgraphs.Pop();
            return;
        }

        ParseChain(statement);
    }

    private void OpenSubgraph(string rest)
    {
        if (rest.Length == 0)
        {
            throw new MermaidSyntaxException("A subgraph needs a title.", lineNumber);
        }

        string id;
        string title;
        var match = SubgraphWithTitle().Match(rest);
        if (match.Success)
        {
            id = match.Groups["id"].Value;
            title = CleanLabel(match.Groups["title"].Value, id);
        }
        else
        {
            id = rest;
            title = CleanLabel(rest, rest);
        }

        subgraphIds.Add(id);
        subgraphs.Add((id, title, []));
        openSubgraphs.Push(subgraphs.Count - 1);
    }

    private void ParseChain(string s)
    {
        var pos = 0;
        var left = ParseNodeGroup(s, ref pos);
        while (true)
        {
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length)
            {
                return;
            }

            var op = ParseEdgeOperator(s, ref pos) ?? throw new MermaidSyntaxException($"Unexpected text '{Excerpt(s, pos)}'.", lineNumber);
            var right = ParseNodeGroup(s, ref pos);
            foreach (var from in left)
            {
                foreach (var to in right)
                {
                    if (edges.Count >= MermaidLimits.MaxEdges)
                    {
                        throw new MermaidSyntaxException($"The diagram has more than {MermaidLimits.MaxEdges} edges.", lineNumber);
                    }

                    edges.Add(new FlowEdge(from, to, op.Label, op.Style, op.StartHead, op.EndHead));
                }
            }

            left = right;
        }
    }

    private List<string> ParseNodeGroup(string s, ref int pos)
    {
        var ids = new List<string>();
        while (true)
        {
            ids.Add(ParseNode(s, ref pos));
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == '&')
            {
                pos++;
                continue;
            }

            return ids;
        }
    }

    private string ParseNode(string s, ref int pos)
    {
        SkipWhitespace(s, ref pos);
        var start = pos;
        while (pos < s.Length && IsIdChar(s[pos]))
        {
            pos++;
        }

        if (pos == start)
        {
            throw new MermaidSyntaxException(pos < s.Length ? $"Unexpected text '{Excerpt(s, pos)}'." : "Expected a node.", lineNumber);
        }

        var id = s[start..pos];
        if (subgraphIds.Contains(id))
        {
            throw new MermaidSyntaxException($"Edges to the subgraph '{id}' are not supported.", lineNumber);
        }

        FlowNode? declared = null;
        foreach (var (open, close, shape) in ShapeTable)
        {
            if (string.CompareOrdinal(s, pos, open, 0, open.Length) != 0)
            {
                continue;
            }

            var label = ReadShapeLabel(s, ref pos, open, close);
            declared = new FlowNode(id, CleanLabel(label, id), shape);
            break;
        }

        if (pos + 3 <= s.Length && string.CompareOrdinal(s, pos, ":::", 0, 3) == 0)
        {
            pos += 3;
            while (pos < s.Length && IsIdChar(s[pos]))
            {
                pos++;
            }
        }

        Register(id, declared);
        return id;
    }

    private void Register(string id, FlowNode? declared)
    {
        if (nodeIndex.TryGetValue(id, out var index))
        {
            if (declared is not null)
            {
                nodes[index] = declared;
            }
        }
        else
        {
            if (nodes.Count >= MermaidLimits.MaxNodes)
            {
                throw new MermaidSyntaxException($"The diagram has more than {MermaidLimits.MaxNodes} nodes.", lineNumber);
            }

            nodeIndex[id] = nodes.Count;
            nodes.Add(declared ?? new FlowNode(id, id, FlowNodeShape.Rectangle));
        }

        foreach (var open in openSubgraphs)
        {
            var members = subgraphs[open].Members;
            if (!members.Contains(id))
            {
                members.Add(id);
            }
        }
    }

    private string ReadShapeLabel(string s, ref int pos, string open, string[] closers)
    {
        var start = pos + open.Length;
        var probe = start;
        SkipWhitespace(s, ref probe);
        if (probe < s.Length && s[probe] == '"')
        {
            var quoteEnd = s.IndexOf('"', probe + 1);
            if (quoteEnd < 0)
            {
                throw new MermaidSyntaxException("Unclosed quote in node label.", lineNumber);
            }

            var after = quoteEnd + 1;
            SkipWhitespace(s, ref after);
            foreach (var closer in closers)
            {
                if (string.CompareOrdinal(s, after, closer, 0, closer.Length) == 0)
                {
                    pos = after + closer.Length;
                    return s[(probe + 1)..quoteEnd];
                }
            }

            throw new MermaidSyntaxException("Node label is not closed.", lineNumber);
        }

        var bestIndex = -1;
        var bestLength = 0;
        foreach (var closer in closers)
        {
            var found = s.IndexOf(closer, start, StringComparison.Ordinal);
            if (found >= 0 && (bestIndex < 0 || found < bestIndex))
            {
                bestIndex = found;
                bestLength = closer.Length;
            }
        }

        if (bestIndex < 0)
        {
            throw new MermaidSyntaxException($"Node shape '{open}' is not closed.", lineNumber);
        }

        pos = bestIndex + bestLength;
        return s[start..bestIndex];
    }

    private readonly record struct EdgeOperator(string? Label, FlowLineStyle Style, FlowArrowHead StartHead, FlowArrowHead EndHead);

    private EdgeOperator? ParseEdgeOperator(string s, ref int pos)
    {
        EdgeOperator? result = null;
        var end = pos;

        var match = LabelledSolid().Match(s, pos);
        if (match.Success)
        {
            result = new EdgeOperator(CleanLabelOrNull(match.Groups["l"].Value), FlowLineStyle.Solid, FlowArrowHead.None, Head(match.Groups["e"].Value));
        }
        else if ((match = LabelledDotted().Match(s, pos)).Success)
        {
            result = new EdgeOperator(CleanLabelOrNull(match.Groups["l"].Value), FlowLineStyle.Dotted, FlowArrowHead.None, Head(match.Groups["e"].Value));
        }
        else if ((match = LabelledThick().Match(s, pos)).Success)
        {
            result = new EdgeOperator(CleanLabelOrNull(match.Groups["l"].Value), FlowLineStyle.Thick, FlowArrowHead.None, Head(match.Groups["e"].Value));
        }
        else if ((match = PlainDotted().Match(s, pos)).Success)
        {
            result = new EdgeOperator(null, FlowLineStyle.Dotted, Head(match.Groups["s"].Value), Head(match.Groups["e"].Value));
        }
        else if ((match = PlainThick().Match(s, pos)).Success)
        {
            result = new EdgeOperator(null, FlowLineStyle.Thick, Head(match.Groups["s"].Value), Head(match.Groups["e"].Value));
        }
        else if ((match = PlainSolid().Match(s, pos)).Success)
        {
            var hasHeads = match.Groups["s"].Length > 0 || match.Groups["e"].Length > 0;
            if (match.Groups["d"].Length >= 3 || hasHeads)
            {
                result = new EdgeOperator(null, FlowLineStyle.Solid, Head(match.Groups["s"].Value), Head(match.Groups["e"].Value));
            }
        }
        else if ((match = Invisible().Match(s, pos)).Success)
        {
            result = new EdgeOperator(null, FlowLineStyle.Invisible, FlowArrowHead.None, FlowArrowHead.None);
        }

        if (result is null)
        {
            return null;
        }

        end = match.Index + match.Length;
        var labelMatch = PipeLabel().Match(s, end);
        if (labelMatch.Success)
        {
            end = labelMatch.Index + labelMatch.Length;
            result = result.Value with { Label = CleanLabelOrNull(labelMatch.Groups["t"].Value) ?? result.Value.Label };
        }

        pos = end;
        return result;
    }

    private static FlowArrowHead Head(string marker) => marker switch
    {
        ">" or "<" => FlowArrowHead.Arrow,
        "o" => FlowArrowHead.Circle,
        "x" => FlowArrowHead.Cross,
        _ => FlowArrowHead.None,
    };

    private static bool IsIdChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static void SkipWhitespace(string s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos]))
        {
            pos++;
        }
    }

    private static string Excerpt(string s, int pos) => s.Length - pos <= 20 ? s[pos..] : string.Concat(s.AsSpan(pos, 20), "…");

    private static string? CleanLabelOrNull(string raw)
    {
        var label = CleanLabel(raw, string.Empty);
        return label.Length == 0 ? null : label;
    }

    private static string CleanLabel(string raw, string fallback)
    {
        var label = raw.Trim();
        if (label.Length >= 2 && label[0] == '"' && label[^1] == '"')
        {
            label = label[1..^1];
        }

        label = LineBreakTag().Replace(label, "\n");
        label = MermaidDiagram.DecodeEntities(HtmlTag().Replace(label, string.Empty)).Trim();
        return label.Length == 0 ? fallback : label;
    }

    [GeneratedRegex(@"</?[A-Za-z][^>]*>", RegexOptions.CultureInvariant, 200)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"^(?<id>[^\s\[]+)\s*\[(?<title>.*)\]$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex SubgraphWithTitle();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex LineBreakTag();

    [GeneratedRegex(@"\G\s*--(?!(?:[-<>]|[ox](?=\s|\||$)))\s*(?<l>[^|\r\n]+?)\s*-{2,}(?<e>>|[ox](?=\s|\||$))?", RegexOptions.CultureInvariant, 200)]
    private static partial Regex LabelledSolid();

    [GeneratedRegex(@"\G\s*-\.(?!-)\s*(?<l>[^|\r\n]+?)\s*\.+-(?<e>>|[ox](?=\s|\||$))?", RegexOptions.CultureInvariant, 200)]
    private static partial Regex LabelledDotted();

    [GeneratedRegex(@"\G\s*==(?![=>])\s*(?<l>[^|\r\n]+?)\s*={2,}(?<e>>|[ox](?=\s|\||$))?", RegexOptions.CultureInvariant, 200)]
    private static partial Regex LabelledThick();

    [GeneratedRegex(@"\G\s*(?<s><|[ox](?=-))?-\.+-(?<e>>|[ox](?=\s|\||$))?", RegexOptions.CultureInvariant, 200)]
    private static partial Regex PlainDotted();

    [GeneratedRegex(@"\G\s*(?<s><|[ox](?==))?(?<d>={2,})(?<e>>|[ox](?=\s|\||$))?", RegexOptions.CultureInvariant, 200)]
    private static partial Regex PlainThick();

    [GeneratedRegex(@"\G\s*(?<s><|[ox](?=-))?(?<d>-{2,})(?<e>>|[ox](?=\s|\||$))?", RegexOptions.CultureInvariant, 200)]
    private static partial Regex PlainSolid();

    [GeneratedRegex(@"\G\s*~~~+", RegexOptions.CultureInvariant, 200)]
    private static partial Regex Invisible();

    [GeneratedRegex(@"\G\s*\|(?<t>""[^""]*""|[^|]*)\|", RegexOptions.CultureInvariant, 200)]
    private static partial Regex PipeLabel();
}
