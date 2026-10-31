using System.Text.RegularExpressions;

namespace Tern.Rendering.Mermaid;

/// <summary>Bounded native state, class and indented mind-map grammars.</summary>
internal static partial class StructuredGraphParsers
{
    private const string Id = @"[\p{L}_][\p{L}\p{N}_.-]*";

    public static FlowchartDiagram State(IReadOnlyList<MermaidDiagram.SourceLine> lines)
    {
        var nodes = new List<FlowNode>();
        var edges = new List<FlowEdge>();
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var direction = FlowDirection.TopDown;
        foreach (var (number, text) in lines.SkipWhile(line => line.Text.Length == 0).Skip(1))
        {
            if (text.Length == 0) continue;
            if (text.StartsWith("direction ", StringComparison.Ordinal)) { direction = Direction(text[10..], number); continue; }
            var transition = StateTransition().Match(text.TrimEnd(';'));
            if (transition.Success)
            {
                var from = StateId(transition.Groups["from"].Value, false, number);
                var to = StateId(transition.Groups["to"].Value, true, number);
                if (edges.Count >= MermaidLimits.MaxEdges) throw new MermaidSyntaxException("Too many state transitions.", number);
                edges.Add(new(from, to, Text(transition.Groups["label"].Value), FlowLineStyle.Solid, FlowArrowHead.None, FlowArrowHead.Arrow));
                continue;
            }
            var declaration = StateDeclaration().Match(text);
            if (!declaration.Success) throw new MermaidSyntaxException("Unsupported state statement; expected a declaration or transition.", number);
            var id = declaration.Groups["id"].Value;
            var label = declaration.Groups["label"].Value;
            Add(id, label.Length == 0 ? id : Text(label), FlowNodeShape.Rounded, number, true);
        }
        if (nodes.Count == 0) throw new MermaidSyntaxException("The state diagram has no states.", 1);
        return new(direction, nodes, edges, [], GraphDiagramKind.State);

        string StateId(string id, bool final, int line)
        {
            if (id == "[*]")
            {
                var key = final ? "\u0001state-final" : "\u0001state-initial";
                Add(key, string.Empty, final ? FlowNodeShape.FinalState : FlowNodeShape.InitialState, line, false);
                return key;
            }
            Add(id, id, FlowNodeShape.Rounded, line, false);
            return id;
        }
        void Add(string id, string label, FlowNodeShape shape, int line, bool replace)
        {
            if (indexes.TryGetValue(id, out var index)) { if (replace) nodes[index] = new(id, label, shape); return; }
            CheckNodeCount(nodes.Count, line);
            indexes[id] = nodes.Count;
            nodes.Add(new(id, label, shape));
        }
    }

    public static FlowchartDiagram Class(IReadOnlyList<MermaidDiagram.SourceLine> lines)
    {
        var nodes = new List<FlowNode>();
        var edges = new List<FlowEdge>();
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var attributes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var methods = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var direction = FlowDirection.TopDown;
        string? current = null;
        foreach (var (number, text) in lines.SkipWhile(line => line.Text.Length == 0).Skip(1))
        {
            if (text.Length == 0) continue;
            if (current is not null)
            {
                if (text == "}") { current = null; continue; }
                if (text.Contains('{') || text.Contains('}')) throw new MermaidSyntaxException("Nested class blocks are not supported.", number);
                if (attributes[current].Count + methods[current].Count >= 100) throw new MermaidSyntaxException("A class has more than 100 members.", number);
                (text.Contains('(') ? methods[current] : attributes[current]).Add(Text(text));
                continue;
            }
            if (text.StartsWith("direction ", StringComparison.Ordinal)) { direction = Direction(text[10..], number); continue; }
            var relation = ClassRelationship().Match(text.TrimEnd(';'));
            if (relation.Success)
            {
                var from = relation.Groups["from"].Value;
                var to = relation.Groups["to"].Value;
                Add(from, from, number, false); Add(to, to, number, false);
                var arrow = relation.Groups["arrow"].Value;
                var start = arrow.StartsWith("<|", StringComparison.Ordinal) ? FlowArrowHead.HollowTriangle
                    : arrow.StartsWith('<') ? FlowArrowHead.OpenArrow : arrow.StartsWith('*') ? FlowArrowHead.FilledDiamond
                    : arrow.StartsWith('o') ? FlowArrowHead.HollowDiamond : FlowArrowHead.None;
                var end = arrow.EndsWith("|>", StringComparison.Ordinal) ? FlowArrowHead.HollowTriangle
                    : arrow.EndsWith('>') ? FlowArrowHead.OpenArrow : arrow.EndsWith('*') ? FlowArrowHead.FilledDiamond
                    : arrow.EndsWith('o') ? FlowArrowHead.HollowDiamond : FlowArrowHead.None;
                if (edges.Count >= MermaidLimits.MaxEdges) throw new MermaidSyntaxException("Too many class relationships.", number);
                edges.Add(new(from, to, Text(relation.Groups["label"].Value), arrow.Contains("..", StringComparison.Ordinal) ? FlowLineStyle.Dotted : FlowLineStyle.Solid, start, end)
                { StartLabel = NullText(relation.Groups["start"].Value), EndLabel = NullText(relation.Groups["end"].Value) });
                continue;
            }
            var declaration = ClassDeclaration().Match(text);
            if (!declaration.Success) throw new MermaidSyntaxException("Unsupported class statement; expected a class block or relationship.", number);
            var id = declaration.Groups["id"].Value;
            Add(id, declaration.Groups["label"].Success ? Text(declaration.Groups["label"].Value) : id, number, true);
            if (declaration.Groups["open"].Success) current = id;
        }
        if (current is not null) throw new MermaidSyntaxException("A class block is missing its closing brace.", lines[^1].Number);
        if (nodes.Count == 0) throw new MermaidSyntaxException("The class diagram has no classes.", 1);
        return new(direction, nodes.Select(node => node with { Attributes = attributes[node.Id].ToArray(), Methods = methods[node.Id].ToArray() }).ToArray(), edges, [], GraphDiagramKind.Class);

        void Add(string id, string label, int line, bool replace)
        {
            if (indexes.TryGetValue(id, out var index)) { if (replace) nodes[index] = new(id, label, FlowNodeShape.Class); return; }
            CheckNodeCount(nodes.Count, line); indexes[id] = nodes.Count;
            nodes.Add(new(id, label, FlowNodeShape.Class)); attributes[id] = []; methods[id] = [];
        }
    }

    public static FlowchartDiagram Mindmap(string source)
    {
        var nodes = new List<FlowNode>();
        var edges = new List<FlowEdge>();
        var stack = new List<(int Indent, string Id)>();
        var header = false;
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var raw = lines[index];
            var text = raw.Trim();
            if (text.Length == 0 || text.StartsWith("%%", StringComparison.Ordinal)) continue;
            if (!header) { header = true; continue; }
            var indent = raw.TakeWhile(char.IsWhiteSpace).Sum(ch => ch == '\t' ? 4 : 1);
            while (stack.Count > 0 && indent <= stack[^1].Indent) stack.RemoveAt(stack.Count - 1);
            if (nodes.Count > 0 && stack.Count == 0) throw new MermaidSyntaxException("A mind map needs exactly one indented root tree.", index + 1);
            if (stack.Count >= 64) throw new MermaidSyntaxException("Mind-map nesting exceeds 64 levels.", index + 1);
            CheckNodeCount(nodes.Count, index + 1);
            var label = text;
            var shape = FlowNodeShape.Rounded;
            var first = text.IndexOfAny(['(', '[', '{']);
            if (first >= 0)
            {
                var token = text[first..];
                var pairs = new[] { ("((", "))", FlowNodeShape.Circle), ("[[", "]]", FlowNodeShape.Subroutine), ("{{", "}}", FlowNodeShape.Hexagon), ("[", "]", FlowNodeShape.Rectangle), ("(", ")", FlowNodeShape.Rounded), ("{", "}", FlowNodeShape.Diamond) };
                var pair = pairs.FirstOrDefault(pair => token.StartsWith(pair.Item1, StringComparison.Ordinal) && token.EndsWith(pair.Item2, StringComparison.Ordinal));
                if (pair.Item1 is null) throw new MermaidSyntaxException("Unbalanced mind-map node shape.", index + 1);
                label = token[pair.Item1.Length..^pair.Item2.Length]; shape = pair.Item3;
            }
            if (label.Length == 0 || text.Contains("::", StringComparison.Ordinal)) throw new MermaidSyntaxException("Empty nodes and mind-map directives are not supported.", index + 1);
            var id = "\u0001mind-" + nodes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            nodes.Add(new(id, Text(label.Trim('"')), shape));
            if (stack.Count > 0) edges.Add(new(stack[^1].Id, id, null, FlowLineStyle.Solid, FlowArrowHead.None, FlowArrowHead.None));
            stack.Add((indent, id));
        }
        if (nodes.Count == 0) throw new MermaidSyntaxException("The mind map is empty.", 1);
        return new(FlowDirection.LeftRight, nodes, edges, [], GraphDiagramKind.Mindmap);
    }

    private static FlowDirection Direction(string value, int line) => value.Trim() switch
    { "TD" or "TB" => FlowDirection.TopDown, "BT" => FlowDirection.BottomUp, "LR" => FlowDirection.LeftRight, "RL" => FlowDirection.RightLeft, _ => throw new MermaidSyntaxException("Unknown graph direction.", line) };
    private static string Text(string value) => MermaidDiagram.DecodeEntities(LineBreak().Replace(value.Trim(), "\n"));
    private static string? NullText(string value) => value.Length == 0 ? null : Text(value);
    private static void CheckNodeCount(int count, int line) { if (count >= MermaidLimits.MaxNodes) throw new MermaidSyntaxException("The graph has more than 500 nodes.", line); }

    [GeneratedRegex("^(?<from>\\[\\*\\]|" + Id + ")\\s*-->\\s*(?<to>\\[\\*\\]|" + Id + ")(?:\\s*:\\s*(?<label>.*))?$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex StateTransition();
    [GeneratedRegex("^(?:state\\s+\"(?<label>[^\"]+)\"\\s+as\\s+)?(?<id>" + Id + ")(?:\\s*:\\s*(?<label>.+))?$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex StateDeclaration();
    [GeneratedRegex("^class\\s+(?<id>" + Id + ")(?:\\s*\\[\"(?<label>[^\"]+)\"\\])?\\s*(?<open>\\{)?\\s*$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex ClassDeclaration();
    [GeneratedRegex("^(?<from>" + Id + ")(?:\\s+\"(?<start>[^\"]{1,32})\")?\\s*(?<arrow><\\|--|<\\|\\.\\.|--\\|>|\\.\\.\\|>|\\*--|--\\*|o--|--o|-->|<--|\\.\\.>|<\\.\\.|--|\\.\\.)\\s*(?:\"(?<end>[^\"]{1,32})\"\\s+)?(?<to>" + Id + ")(?:\\s*:\\s*(?<label>.*))?$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex ClassRelationship();
    [GeneratedRegex("<br\\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex LineBreak();
}
