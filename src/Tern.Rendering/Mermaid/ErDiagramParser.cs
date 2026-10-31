using System.Text.RegularExpressions;

namespace Tern.Rendering.Mermaid;

/// <summary>Native ER entities, attribute compartments and identifying/non-identifying relationships.</summary>
internal static partial class ErDiagramParser
{
    private const string Identifier = "(?:[\\p{L}_][\\p{L}\\p{N}_-]*|\"[^\"]+\")";

    public static FlowchartDiagram Parse(IReadOnlyList<MermaidDiagram.SourceLine> lines)
    {
        var nodes = new List<FlowNode>();
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var attributes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var edges = new List<FlowEdge>();
        string? current = null;
        var direction = FlowDirection.TopDown;
        var headerSeen = false;
        foreach (var (number, text) in lines)
        {
            if (text.Length == 0) continue;
            if (!headerSeen) { headerSeen = true; continue; }
            if (current is not null)
            {
                if (text == "}") { current = null; continue; }
                var attribute = Attribute().Match(text);
                if (!attribute.Success) throw new MermaidSyntaxException("Invalid entity attribute; expected type, name, optional PK/FK/UK and quoted comment.", number);
                var entries = attributes[current];
                if (entries.Count >= 100) throw new MermaidSyntaxException("An entity has more than 100 attributes.", number);
                var keys = attribute.Groups["keys"].Value;
                var comment = attribute.Groups["comment"].Value;
                entries.Add(MermaidDiagram.DecodeEntities(attribute.Groups["type"].Value + " " + attribute.Groups["name"].Value
                    + (keys.Length == 0 ? "" : " [" + keys + "]") + (comment.Length == 0 ? "" : " — " + comment)));
                continue;
            }

            if (text.StartsWith("direction ", StringComparison.Ordinal))
            {
                direction = text["direction ".Length..].Trim() switch
                {
                    "LR" => FlowDirection.LeftRight, "RL" => FlowDirection.RightLeft,
                    "TB" or "TD" => FlowDirection.TopDown, "BT" => FlowDirection.BottomUp,
                    _ => throw new MermaidSyntaxException("Unknown ER direction.", number),
                };
                continue;
            }

            var relationship = Relationship().Match(text);
            if (relationship.Success)
            {
                var from = Unquote(relationship.Groups["from"].Value);
                var to = Unquote(relationship.Groups["to"].Value);
                Declare(from, from, number);
                Declare(to, to, number);
                if (edges.Count >= MermaidLimits.MaxEdges) throw new MermaidSyntaxException("Too many entity relationships.", number);
                edges.Add(new FlowEdge(from, to, MermaidDiagram.DecodeEntities(Unquote(relationship.Groups["label"].Value.Trim())),
                    relationship.Groups["line"].Value == ".." ? FlowLineStyle.Dotted : FlowLineStyle.Solid,
                    Cardinality(relationship.Groups["left"].Value), Cardinality(relationship.Groups["right"].Value)));
                continue;
            }

            var declaration = Declaration().Match(text);
            if (!declaration.Success) throw new MermaidSyntaxException("Invalid entity declaration or relationship.", number);
            var id = Unquote(declaration.Groups["id"].Value);
            var label = declaration.Groups["alias"].Success ? Unquote(declaration.Groups["alias"].Value.Trim()) : id;
            Declare(id, MermaidDiagram.DecodeEntities(label), number);
            if (declaration.Groups["open"].Success) current = id;
        }

        if (current is not null) throw new MermaidSyntaxException("An entity attribute block is missing its closing brace.", lines[^1].Number);
        return new FlowchartDiagram(direction, nodes.Select(node => node with { Attributes = attributes[node.Id].ToArray() }).ToArray(), edges, [], GraphDiagramKind.EntityRelationship);

        void Declare(string id, string label, int line)
        {
            if (indexes.TryGetValue(id, out var index))
            {
                if (label != id) nodes[index] = nodes[index] with { Label = label };
                return;
            }

            if (nodes.Count >= MermaidLimits.MaxNodes) throw new MermaidSyntaxException("Too many entities.", line);
            indexes[id] = nodes.Count;
            nodes.Add(new FlowNode(id, label, FlowNodeShape.Entity));
            attributes[id] = [];
        }
    }

    private static string Unquote(string value) => value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    private static FlowArrowHead Cardinality(string value) => value.Contains('{', StringComparison.Ordinal) || value.Contains('}', StringComparison.Ordinal)
        ? value.Contains('o', StringComparison.Ordinal) ? FlowArrowHead.ZeroOrMany : FlowArrowHead.Many
        : value.Contains('o', StringComparison.Ordinal) ? FlowArrowHead.ZeroOrOne : FlowArrowHead.One;

    [GeneratedRegex("^(?<from>" + Identifier + ")\\s+(?<left>\\|\\||\\|o|o\\||}\\||}o)\\s*(?<line>--|\\.\\.)\\s*(?<right>\\|\\||o\\||\\|o|\\|\\{|o\\{)\\s+(?<to>" + Identifier + ")\\s*:\\s*(?<label>.+)$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex Relationship();

    [GeneratedRegex("^(?<id>" + Identifier + ")(?:\\s*\\[(?<alias>[^\\]]+)\\])?\\s*(?<open>\\{)?\\s*$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex Declaration();

    [GeneratedRegex("^(?<type>[^\\s]+)\\s+(?<name>[^\\s]+)(?:\\s+(?<keys>(?:PK|FK|UK)(?:\\s*,\\s*(?:PK|FK|UK))*))?(?:\\s+\"(?<comment>[^\"]*)\")?\\s*$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex Attribute();
}
