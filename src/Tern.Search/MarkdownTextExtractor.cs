using System.Text;
using System.Text.RegularExpressions;
using Tern.Markdown;
using Tern.Workspaces;

namespace Tern.Search;

/// <summary>What the index stores for a document.</summary>
/// <param name="Body">Plain text with exactly one output line per source line, so a line number in the body is a line number in the file.</param>
public sealed record ExtractedDocument(string Title, IReadOnlyList<string> Headings, IReadOnlyList<string> Tags, string Body, int TokenCount);

/// <summary>
/// Turns Markdown source into searchable plain text: formatting markers, link destinations and fence delimiters are
/// dropped, while link text, code text and headings are kept. Works line by line so source line numbers survive.
/// </summary>
public static partial class MarkdownTextExtractor
{
    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})", RegexOptions.None, 1000)]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"!?\[([^\]\n]*)\]\((?:<[^>\n]*>|[^)\n]*)\)", RegexOptions.None, 1000)]
    private static partial Regex InlineLinkRegex();

    [GeneratedRegex(@"!?\[([^\]\n]*)\]\[[^\]\n]*\]", RegexOptions.None, 1000)]
    private static partial Regex ReferenceLinkRegex();

    [GeneratedRegex(@"^ {0,3}\[[^\]\n]+\]:\s*\S.*$", RegexOptions.None, 1000)]
    private static partial Regex ReferenceDefinitionRegex();

    [GeneratedRegex(@"\[\[([^\]\n|]*)(?:\|([^\]\n]*))?\]\]", RegexOptions.None, 1000)]
    private static partial Regex WikiLinkRegex();

    [GeneratedRegex(@"<(?:https?|ftp|mailto):[^>\s]*>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex AutolinkRegex();

    [GeneratedRegex(@"https?://[^\s<>)\]]+", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex BareUrlRegex();

    [GeneratedRegex(@"</?[A-Za-z][^>\n]*>|<!--.*?-->", RegexOptions.None, 1000)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"`+([^`\n]*?)`+", RegexOptions.None, 1000)]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"^ {0,3}#{1,6}[ \t]+(.*?)(?:[ \t]+#+)?[ \t]*$", RegexOptions.None, 1000)]
    private static partial Regex AtxHeadingRegex();

    [GeneratedRegex(@"^\s*(?:>\s?)*(?:[-+*]|\d{1,9}[.)])\s+(?:\[[ xX]\]\s+)?", RegexOptions.None, 1000)]
    private static partial Regex ListMarkerRegex();

    [GeneratedRegex(@"^\s*(?:>\s?)+", RegexOptions.None, 1000)]
    private static partial Regex QuoteRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_&/#\\])#([\p{L}\p{N}_/-]*\p{L}[\p{L}\p{N}_/-]*)", RegexOptions.None, 1000)]
    private static partial Regex InlineTagRegex();

    [GeneratedRegex(@"^\s*(?:-{3,}|\*{3,}|_{3,}|={2,}|\|?\s*:?-{2,}:?\s*(?:\|\s*:?-{2,}:?\s*)*\|?)\s*$", RegexOptions.None, 1000)]
    private static partial Regex RuleOrTableDelimiterRegex();

    [GeneratedRegex(@"[*_~]{1,3}(?=\S)|(?<=\S)[*_~]{1,3}|\\(?=[\\`*_{}\[\]()#+\-.!|<>~])", RegexOptions.None, 1000)]
    private static partial Regex EmphasisRegex();

    public static ExtractedDocument Extract(string path, string text, MarkdownProfile profile = MarkdownProfile.Extended)
    {
        var analysis = MarkdownAnalyzer.Analyze(text, profile);
        var front = analysis.FrontMatter is { } fm ? FrontMatterReader.Read(fm.Yaml) : FrontMatterFields.Empty;

        var lines = SplitLines(text);
        var frontEndLine = analysis.FrontMatter is { } range ? analysis.Lines.LineOf(Math.Max(range.Range.Start, range.Range.End - 1)) : 0;
        var codeLines = new bool[lines.Count];
        MarkLinesInFences(lines, frontEndLine, codeLines);

        var body = new StringBuilder(text.Length);
        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in front.Tags)
        {
            AddTag(tags, tag);
        }

        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                body.Append('\n');
            }

            if (i < frontEndLine)
            {
                continue;
            }

            var raw = lines[i];
            if (codeLines[i])
            {
                if (!FenceRegex().IsMatch(raw))
                {
                    body.Append(raw);
                }

                continue;
            }

            body.Append(CleanProse(raw, tags));
        }

        var headings = analysis.Headings.Select(h => h.Text).ToList();
        var title = !string.IsNullOrWhiteSpace(front.Title)
            ? front.Title!.Trim()
            : headings.FirstOrDefault(h => h.Length > 0) ?? Path.GetFileNameWithoutExtension(path);
        var bodyText = body.ToString();
        var count = Tokenizer.Tokenize(bodyText).Count;
        return new ExtractedDocument(title, headings, [.. tags.Order(StringComparer.Ordinal)], bodyText, count);
    }

    private static string CleanProse(string line, HashSet<string> tags)
    {
        if (line.Length == 0 || RuleOrTableDelimiterRegex().IsMatch(line))
        {
            return string.Empty;
        }

        try
        {
            if (ReferenceDefinitionRegex().IsMatch(line))
            {
                return string.Empty;
            }

            var heading = AtxHeadingRegex().Match(line);
            var s = heading.Success ? heading.Groups[1].Value : line;
            if (!heading.Success)
            {
                s = ListMarkerRegex().Replace(s, string.Empty, 1);
                s = QuoteRegex().Replace(s, string.Empty, 1);
            }

            s = InlineLinkRegex().Replace(s, "$1");
            s = ReferenceLinkRegex().Replace(s, "$1");
            s = WikiLinkRegex().Replace(s, m => m.Groups[2].Success && m.Groups[2].Length > 0 ? m.Groups[2].Value : m.Groups[1].Value);
            s = AutolinkRegex().Replace(s, string.Empty);
            s = BareUrlRegex().Replace(s, string.Empty);
            s = HtmlTagRegex().Replace(s, string.Empty);

            // Tags are taken before code is unwrapped so "#include" inside backticks is not a tag.
            var withoutCode = InlineCodeRegex().Replace(s, " ");
            foreach (Match match in InlineTagRegex().Matches(withoutCode))
            {
                AddTag(tags, match.Groups[1].Value);
            }

            s = InlineCodeRegex().Replace(s, "$1");
            return EmphasisRegex().Replace(s, string.Empty).TrimEnd();
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathological line: index it verbatim rather than lose it.
            return line;
        }
    }

    private static void AddTag(HashSet<string> tags, string raw)
    {
        var tag = raw.Trim().TrimStart('#').Trim().ToLowerInvariant();
        if (tag.Length > 0 && tag.Length <= 100)
        {
            tags.Add(tag);
        }
    }

    private static void MarkLinesInFences(List<string> lines, int startLine, bool[] codeLines)
    {
        char fenceChar = '\0';
        var fenceLength = 0;
        for (var i = startLine; i < lines.Count; i++)
        {
            var match = FenceRegex().Match(lines[i]);
            if (fenceChar == '\0')
            {
                if (!match.Success)
                {
                    continue;
                }

                fenceChar = match.Groups[1].Value[0];
                fenceLength = match.Groups[1].Length;
                codeLines[i] = true;
                continue;
            }

            codeLines[i] = true;
            if (match.Success && match.Groups[1].Value[0] == fenceChar && match.Groups[1].Length >= fenceLength
                && lines[i].AsSpan().Trim().Length == match.Groups[1].Length)
            {
                fenceChar = '\0';
            }
        }
    }

    internal static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines.Add(text[start..(i > start && text[i - 1] == '\r' ? i - 1 : i)]);
                start = i + 1;
            }
            else if (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n'))
            {
                lines.Add(text[start..i]);
                start = i + 1;
            }
        }

        lines.Add(text[start..]);
        return lines;
    }
}
