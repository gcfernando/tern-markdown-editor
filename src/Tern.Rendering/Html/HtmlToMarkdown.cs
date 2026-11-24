using System.Text;
using System.Text.RegularExpressions;
using Tern.Markdown;

namespace Tern.Rendering.Html;

public static partial class HtmlToMarkdown
{
    public const int MaxInputLength = 1_000_000;

    public static string Convert(string html, string newline = "\n")
    {
        var pieces = new HtmlSanitizer(new HtmlSanitizerOptions { RejectTruncation = true }).Parse(html);
        var root = new Node(null);
        var stack = new Stack<Node>();
        stack.Push(root);
        foreach (var piece in pieces)
        {
            if (piece.Kind == HtmlPieceKind.Close)
            {
                if (stack.Count > 1) stack.Pop();
                continue;
            }
            var node = new Node(piece);
            stack.Peek().Children.Add(node);
            if (piece.Kind == HtmlPieceKind.Open) stack.Push(node);
        }
        return Render(root).Trim().Replace("\n", newline, StringComparison.Ordinal);
    }

    private sealed class Node(HtmlPiece? piece)
    {
        public HtmlPiece? Piece { get; } = piece;
        public List<Node> Children { get; } = [];
    }

    private static string Render(Node node)
    {
        var piece = node.Piece;
        if (piece?.Kind == HtmlPieceKind.Text) return Escape(Whitespace().Replace(piece.Text, " "));
        var tag = piece?.Tag;
        if (tag == "pre")
        {
            var text = Plain(node).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            var fence = new string('`', Math.Max(3, LongestRun(text, '`') + 1));
            return "\n\n" + fence + "\n" + text.TrimEnd('\n') + "\n" + fence + "\n\n";
        }
        if (tag == "code")
        {
            var text = Whitespace().Replace(Plain(node), " ");
            if (text.Length == 0) return "";
            var fence = new string('`', LongestRun(text, '`') + 1);
            var pad = text.StartsWith('`') || text.EndsWith('`') || text.StartsWith(' ') && text.EndsWith(' ') ? " " : "";
            return fence + pad + text + pad + fence;
        }
        if (tag is "ul" or "ol") return RenderList(node, tag == "ol");
        if (tag == "table") return RenderTable(node);
        var content = string.Concat(node.Children.Select(Render));
        return tag switch
        {
            "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => "\n\n" + new string('#', tag[1] - '0') + " " + content.Trim() + "\n\n",
            "p" or "div" or "figure" or "figcaption" or "details" or "summary" => "\n\n" + content.Trim() + "\n\n",
            "strong" or "b" => Emphasis(content, "**"),
            "em" or "i" => Emphasis(content, "*"),
            "s" or "del" => Emphasis(content, "~~"),
            "a" when piece!.GetAttribute("href") is { } href => "[" + content.Trim() + "](<" + Destination(href) + ">)",
            // Pasting must not opt the document into fetching remote images.
            "img" => Escape(piece!.GetAttribute("alt") ?? ""),
            "br" => "  \n",
            "hr" => "\n\n---\n\n",
            "blockquote" => "\n\n" + string.Join("\n", content.Trim().Split('\n').Select(line => "> " + line)) + "\n\n",
            _ => content
        };
    }

    private static string RenderList(Node node, bool ordered)
    {
        var number = int.TryParse(node.Piece?.GetAttribute("start"), out var start) && start > 0 ? start : 1;
        var output = new StringBuilder("\n\n");
        foreach (var child in node.Children)
        {
            if (child.Piece?.Tag != "li") continue;
            var marker = ordered ? (number++).ToString(System.Globalization.CultureInfo.InvariantCulture) + ". " : "- ";
            var content = string.Concat(child.Children.Select(Render)).Trim();
            var lines = content.Split('\n');
            output.Append(marker).Append(lines[0]).Append('\n');
            foreach (var line in lines.Skip(1))
                output.Append(' ', marker.Length).Append(line).Append('\n');
        }
        return output.Append('\n').ToString();
    }

    private static string RenderTable(Node node)
    {
        var rows = new List<List<string>>();
        void Collect(Node current)
        {
            if (current != node && current.Piece?.Tag == "table")
                throw new InvalidDataException("Nested HTML tables cannot be converted safely. Use Paste Plain Text instead.");
            if (current.Piece?.Tag == "tr")
            {
                var cells = current.Children.Where(child => child.Piece?.Tag is "td" or "th").ToList();
                if (cells.Any(ContainsTable))
                    throw new InvalidDataException("Nested HTML tables cannot be converted safely. Use Paste Plain Text instead.");
                if (cells.Any(cell => cell.Piece!.GetAttribute("rowspan") is { } r && r != "1"
                    || cell.Piece!.GetAttribute("colspan") is { } c && c != "1"))
                    throw new InvalidDataException("Merged HTML table cells cannot be converted safely. Use Paste Plain Text instead.");
                rows.Add(cells.Select(cell => string.Concat(cell.Children.Select(Render)).Trim()
                    .Replace("  \n", "<br>", StringComparison.Ordinal)
                    .Replace("\n", "<br>", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal)).ToList());
                return;
            }
            foreach (var child in current.Children) Collect(child);
        }
        Collect(node);
        if (rows.Count == 0 || rows.All(row => row.Count == 0)) return "";
        var columns = rows.Max(row => row.Count);
        foreach (var row in rows) while (row.Count < columns) row.Add("");
        var table = new MarkdownTable();
        table.Header.AddRange(rows[0]);
        table.Alignments.AddRange(Enumerable.Repeat(ColumnAlignment.None, columns));
        table.Rows.AddRange(rows.Skip(1));
        var caption = string.Concat(node.Children.Where(child => child.Piece?.Tag == "caption").Select(Render)).Trim();
        return "\n\n" + (caption.Length > 0 ? caption + "\n\n" : "") + table.Render() + "\n\n";
    }

    private static bool ContainsTable(Node node) => node.Piece?.Tag == "table" || node.Children.Any(ContainsTable);

    private static string Plain(Node node) => node.Piece?.Kind == HtmlPieceKind.Text ? node.Piece.Text
        : node.Piece?.Tag == "br" ? "\n" : string.Concat(node.Children.Select(Plain));

    private static string Emphasis(string content, string marker) =>
        content.Trim().Length == 0 ? content : content[..(content.Length - content.TrimStart().Length)]
            + marker + content.Trim() + marker + content[(content.TrimEnd().Length)..];

    private static string Destination(string value) => value.Replace("<", "%3C", StringComparison.Ordinal)
        .Replace(">", "%3E", StringComparison.Ordinal).Replace("\r", "%0D", StringComparison.Ordinal).Replace("\n", "%0A", StringComparison.Ordinal);

    private static string Escape(string value)
    {
        var output = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '#' or '~' or '-' or '+' or '!' or '=' or '.') output.Append('\\');
            output.Append(c);
        }
        return output.ToString();
    }

    private static int LongestRun(string text, char character)
    {
        var longest = 0;
        var current = 0;
        foreach (var c in text) { current = c == character ? current + 1 : 0; longest = Math.Max(longest, current); }
        return longest;
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
