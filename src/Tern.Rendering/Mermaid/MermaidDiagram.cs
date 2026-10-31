using System.Text.RegularExpressions;

namespace Tern.Rendering.Mermaid;

/// <summary>Entry point for parsing Mermaid source and laying the result out natively, without any script or network use.</summary>
public static class MermaidDiagram
{
    internal static string DecodeEntities(string text) => Regex.Replace(text, @"#(?<entity>[A-Za-z0-9]+);", match =>
    {
        var entity = match.Groups["entity"].Value;
        var reference = entity.All(char.IsAsciiDigit) ? "&#" + entity + ";" : "&" + entity + ";";
        var decoded = System.Net.WebUtility.HtmlDecode(reference);
        return decoded == reference ? match.Value : decoded;
    }, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    /// <summary>Wraps labels for layout without changing the parsed source model.</summary>
    internal static string WrapLabel(string text, Func<string, (double Width, double Height)> measure, double maxWidth = 140)
    {
        var lines = new List<string>();
        foreach (var explicitLine in text.Split('\n'))
        {
            var line = string.Empty;
            foreach (var word in explicitLine.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (measure(candidate).Width <= maxWidth)
                {
                    line = candidate;
                    continue;
                }

                if (line.Length > 0) lines.Add(line);
                line = string.Empty;
                var elements = System.Globalization.StringInfo.GetTextElementEnumerator(word);
                while (elements.MoveNext())
                {
                    var element = elements.GetTextElement();
                    if (line.Length > 0 && measure(line + element).Width > maxWidth)
                    {
                        lines.Add(line);
                        line = string.Empty;
                    }

                    line += element;
                }
            }

            lines.Add(line);
        }

        return string.Join('\n', lines);
    }

    /// <summary>Diagram keywords that are valid Mermaid but not drawn by this library.</summary>
    private static readonly HashSet<string> KnownOtherTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "gantt", "pie", "classDiagram", "classDiagram-v2", "stateDiagram", "stateDiagram-v2", "journey",
        "gitGraph", "mindmap", "timeline", "quadrantChart", "requirementDiagram", "C4Context", "C4Container",
        "C4Component", "C4Dynamic", "C4Deployment", "sankey-beta", "xychart-beta", "block-beta", "packet-beta",
        "architecture-beta", "kanban", "zenuml",
    };

    /// <summary>Estimates text size from the character count; used when no real font measurer is supplied.</summary>
    /// <param name="text">The (possibly multi-line) text.</param>
    public static (double Width, double Height) EstimateTextSize(string text)
    {
        var lines = text.Split('\n');
        double width = 0;
        foreach (var line in lines)
        {
            double lineWidth = 0;
            foreach (var ch in line)
            {
                lineWidth += ch >= 0x2E80 || char.IsSurrogate(ch) ? 14 : 7.2;
            }

            width = Math.Max(width, lineWidth);
        }

        return (width, lines.Length * 18d);
    }

    /// <summary>Wraps a caller-supplied measurer so NaN, infinite or negative results cannot corrupt the geometry.</summary>
    internal static Func<string, (double Width, double Height)> Guard(Func<string, (double Width, double Height)>? measurer)
    {
        if (measurer is null)
        {
            return EstimateTextSize;
        }

        return text =>
        {
            var (w, h) = measurer(text);
            return (double.IsFinite(w) && w > 0 ? Math.Min(w, 100_000) : 0, double.IsFinite(h) && h > 0 ? Math.Min(h, 100_000) : 0);
        };
    }

    /// <summary>Parses Mermaid source. Never throws.</summary>
    /// <param name="source">The diagram source (the content of a mermaid code fence).</param>
    /// <param name="result">Always set. Holds the diagram, an unsupported notice or an error.</param>
    /// <returns>True when a flowchart or sequence diagram was parsed.</returns>
    public static bool TryParse(string? source, out MermaidParseResult result)
    {
        result = Parse(source);
        return result.Kind is MermaidResultKind.Flowchart or MermaidResultKind.Sequence or MermaidResultKind.Chart;
    }

    private static MermaidParseResult Parse(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return MermaidParseResult.FromError("The diagram is empty.", 0);
        }

        if (source.Length > MermaidLimits.MaxSourceLength)
        {
            return MermaidParseResult.FromError($"The diagram source exceeds {MermaidLimits.MaxSourceLength / 1024} KB.", 0);
        }

        try
        {
            var lines = Preprocess(source);
            var header = lines.FirstOrDefault(l => l.Text.Length > 0);
            if (header.Text is null)
            {
                return MermaidParseResult.FromError("The diagram is empty.", 0);
            }

            var keywords = header.Text.Split([' ', '\t', ';'], 2, StringSplitOptions.RemoveEmptyEntries);
            if (keywords.Length == 0)
            {
                return MermaidParseResult.FromError("The diagram is empty.", header.Number);
            }

            var keyword = keywords[0];
            if (keyword.Equals("flowchart", StringComparison.OrdinalIgnoreCase)
                || keyword.Equals("flowchart-elk", StringComparison.OrdinalIgnoreCase)
                || keyword.Equals("graph", StringComparison.OrdinalIgnoreCase))
            {
                return MermaidParseResult.FromFlowchart(FlowchartParser.Parse(lines));
            }

            if (keyword.Equals("sequenceDiagram", StringComparison.OrdinalIgnoreCase))
            {
                return MermaidParseResult.FromSequence(SequenceParser.Parse(lines));
            }

            if (keyword.Equals("erDiagram", StringComparison.OrdinalIgnoreCase))
            {
                return MermaidParseResult.FromFlowchart(ErDiagramParser.Parse(lines));
            }

            if (keyword is "stateDiagram" or "stateDiagram-v2") return MermaidParseResult.FromFlowchart(StructuredGraphParsers.State(lines));
            if (keyword is "classDiagram" or "classDiagram-v2") return MermaidParseResult.FromFlowchart(StructuredGraphParsers.Class(lines));
            if (keyword == "mindmap") return MermaidParseResult.FromFlowchart(StructuredGraphParsers.Mindmap(source));
            if (keyword == "pie") return MermaidParseResult.FromChart(ChartParser.Pie(lines));
            if (keyword == "gantt") return MermaidParseResult.FromChart(ChartParser.Gantt(lines));

            if (KnownOtherTypes.Contains(keyword))
            {
                return MermaidParseResult.FromUnsupported(keyword);
            }

            return MermaidParseResult.FromError($"Unknown diagram type '{Truncate(keyword)}'.", header.Number);
        }
        catch (MermaidSyntaxException ex)
        {
            return MermaidParseResult.FromError(ex.Message, ex.Line);
        }
        catch (RegexMatchTimeoutException)
        {
            return MermaidParseResult.FromError("The diagram is too complex to parse.", 0);
        }
    }

    internal readonly record struct SourceLine(int Number, string Text);

    /// <summary>Splits into trimmed lines, dropping front matter, directives and comments but keeping original line numbers.</summary>
    private static List<SourceLine> Preprocess(string source)
    {
        var raw = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var lines = new List<SourceLine>(raw.Length);
        var inFrontMatter = false;
        for (var i = 0; i < raw.Length; i++)
        {
            var text = raw[i].Trim();
            if (text.Length > MermaidLimits.MaxLineLength)
            {
                throw new MermaidSyntaxException($"Line is longer than {MermaidLimits.MaxLineLength} characters.", i + 1);
            }

            if (text == "---" && (inFrontMatter || lines.All(l => l.Text.Length == 0)))
            {
                inFrontMatter = !inFrontMatter;
                lines.Add(new SourceLine(i + 1, string.Empty));
                continue;
            }

            if (inFrontMatter || text.StartsWith("%%", StringComparison.Ordinal))
            {
                text = string.Empty;
            }

            lines.Add(new SourceLine(i + 1, text));
        }

        if (inFrontMatter)
        {
            throw new MermaidSyntaxException("Front matter is not closed.", raw.Length);
        }

        return lines;
    }

    private static string Truncate(string value) => value.Length <= 40 ? value : value[..40] + "…";
}
