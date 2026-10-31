namespace Tern.PreviewView;

internal static partial class CodeSyntaxHighlighter
{
    private static IReadOnlyList<CodeToken> TokenizeMarkup(string text, bool html)
    {
        var tokens = new List<CodeToken>();
        var index = 0;
        while (index < text.Length && tokens.Count < MaxTokens)
        {
            var start = index;
            if (text.AsSpan(index).StartsWith("<!--", StringComparison.Ordinal))
            {
                var end = text.IndexOf("-->", index + 4, StringComparison.Ordinal);
                index = end < 0 ? text.Length : end + 3;
                Append(tokens, start, index - start, CodeTokenKind.Comment);
                continue;
            }
            if (text[index] != '<')
            {
                var next = text.IndexOf('<', index);
                index = next < 0 ? text.Length : next;
                Append(tokens, start, index - start, CodeTokenKind.Plain);
                continue;
            }

            index++;
            var closing = Peek(text, index) == '/';
            if (closing || Peek(text, index) is '!' or '?') index++;
            Append(tokens, start, index - start, CodeTokenKind.Plain);
            var tagStart = index;
            while (index < text.Length && IsMarkupName(text[index])) index++;
            var tag = text[tagStart..index];
            Append(tokens, tagStart, index - tagStart, CodeTokenKind.Type);
            var complete = false;
            while (index < text.Length && tokens.Count < MaxTokens)
            {
                start = index;
                var current = text[index++];
                var kind = CodeTokenKind.Plain;
                if (current == '>') complete = true;
                else if (current is '\'' or '"')
                {
                    while (index < text.Length && text[index] != current) index++;
                    if (index < text.Length) index++;
                    kind = CodeTokenKind.String;
                }
                else if (IsMarkupName(current))
                {
                    while (index < text.Length && IsMarkupName(text[index])) index++;
                    kind = CodeTokenKind.Keyword;
                }
                Append(tokens, start, index - start, kind);
                if (complete) break;
            }
            if (!html || closing || !complete || !(tag.Equals("script", StringComparison.OrdinalIgnoreCase) || tag.Equals("style", StringComparison.OrdinalIgnoreCase))) continue;
            var close = text.IndexOf("</" + tag, index, StringComparison.OrdinalIgnoreCase);
            var contentEnd = close < 0 ? text.Length : close;
            var embedded = text[index..contentEnd];
            foreach (var token in Tokenize(embedded, tag.Equals("style", StringComparison.OrdinalIgnoreCase) ? "css" : "javascript"))
            {
                if (tokens.Count >= MaxTokens) break;
                Append(tokens, index + token.Start, token.Length, token.Kind);
            }
            // Preserve an uncoloured tail when the global run budget is exhausted.
            var coveredEnd = tokens.Count == 0 ? index : tokens[^1].Start + tokens[^1].Length;
            if (coveredEnd < contentEnd) Append(tokens, coveredEnd, contentEnd - coveredEnd, CodeTokenKind.Plain);
            index = contentEnd;
        }
        if (index < text.Length) Append(tokens, index, text.Length - index, CodeTokenKind.Plain);
        return tokens;
    }

    private static bool IsMarkupName(char value) => char.IsLetterOrDigit(value) || value is '_' or '-' or ':' or '.';
}
