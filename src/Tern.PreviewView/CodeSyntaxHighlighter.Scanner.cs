namespace Tern.PreviewView;

/// <summary>Small value state only; source painting never retains a block's token list.</summary>
internal readonly record struct CodeLexicalState(string? End = null, CodeTokenKind Kind = CodeTokenKind.Plain,
    bool Escape = false, bool DoubledEnd = false, bool Multiline = false, string? NestedStart = null, int Depth = 0,
    CodeTokenKind ExpectedName = CodeTokenKind.Plain, bool InTag = false, char TagQuote = '\0', string? Embedded = null,
    char EscapeCharacter = '\\');

internal static partial class CodeSyntaxHighlighter
{
    internal static IReadOnlyList<CodeToken> TokenizeLine(string text, string? language, CodeLexicalState before, out CodeLexicalState after)
    {
        var name = NormalizeLanguage(language);
        if (!IsKnownLanguage(name)) { after = default; return text.Length == 0 ? [] : [new(0, text.Length, CodeTokenKind.Plain)]; }
        return name is "html" or "xml" or "svg" ? TokenizeMarkupLine(text, name, before, out after)
            : Scan(text, name, before, out after);
    }

    private static IReadOnlyList<CodeToken> Scan(string text, string? name, CodeLexicalState before, out CodeLexicalState after)
    {
        var tokens = new List<CodeToken>();
        var state = before;
        var index = 0;
        var expectName = state.ExpectedName;
        state = state with { ExpectedName = CodeTokenKind.Plain };
        var data = name is "json" or "jsonc" or "yaml" or "toml" or "ini";
        var keywords = Keywords(name);
        while (index < text.Length && tokens.Count < MaxTokens)
        {
            var start = index;
            if (state.End is not null)
            {
                ReadDelimited(text, ref index, ref state);
                Append(tokens, start, index - start, before.End is not null && start == 0 ? before.Kind : CodeTokenKind.String);
                // Continuation kind can be Comment, Type or String; it is captured before clearing.
                if (before.End is not null && start == 0) before = default;
                continue;
            }
            if (LineComment(text, index, name))
            {
                while (index < text.Length && text[index] is not ('\r' or '\n')) index++;
                Append(tokens, start, index - start, CodeTokenKind.Comment);
                continue;
            }
            var block = text.AsSpan(index).StartsWith("/*", StringComparison.Ordinal)
                && name is "csharp" or "javascript" or "typescript" or "java" or "c" or "cpp" or "php" or "go" or "rust" or "kotlin" or "swift" or "css" or "jsonc" or "sql" or "kql";
            var psBlock = name == "powershell" && text.AsSpan(index).StartsWith("<#", StringComparison.Ordinal);
            if (block || psBlock)
            {
                state = new(End: psBlock ? "#>" : "*/", Kind: CodeTokenKind.Comment, Multiline: true,
                    NestedStart: name is "rust" or "kotlin" or "swift" or "sql" ? "/*" : null, Depth: 1);
                index += 2;
                ReadDelimited(text, ref index, ref state);
                Append(tokens, start, index - start, CodeTokenKind.Comment);
                continue;
            }
            if (name == "rust" && text[index] == '\'' && char.IsLetter(Peek(text, index + 1)) && Peek(text, index + 2) != '\'')
            {
                index += 2;
                while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_')) index++;
                Append(tokens, start, index - start, CodeTokenKind.Type);
                continue;
            }
            if (TryString(text, index, name, out var prefixLength, out var literal))
            {
                index += prefixLength;
                state = literal;
                ReadDelimited(text, ref index, ref state);
                if (name != "python" || !AppendFormattedPythonString(tokens, text, start, index))
                    Append(tokens, start, index - start, literal.Kind);
                continue;
            }
            if (char.IsDigit(text[index]))
            {
                index++;
                while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] is '_' or '.')) index++;
                Append(tokens, start, index - start, CodeTokenKind.Constant);
                continue;
            }
            if (char.IsLetter(text[index]) || text[index] is '_' or '$')
            {
                index++;
                while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] is '_' or '$'
                    || name is "css" or "powershell" or "kql" && text[index] == '-')) index++;
                var word = text[start..index];
                var compared = IgnoresWordCase(name) ? word.ToLowerInvariant() : word;
                var lookahead = index;
                while (lookahead < text.Length && text[lookahead] is ' ' or '\t') lookahead++;
                CodeTokenKind kind;
                if (expectName != CodeTokenKind.Plain) { kind = expectName; expectName = CodeTokenKind.Plain; }
                else if (IsConstant(word, name)) kind = CodeTokenKind.Constant;
                else if ((keywords.Contains(compared) && (name != "dockerfile" || IsLineStart(text, start)))
                    || name == "css" && Peek(text, lookahead) == ':') kind = CodeTokenKind.Keyword;
                else if (!data && Peek(text, lookahead) == '(') kind = CodeTokenKind.Function;
                else if (!data && start > 0 && text[start - 1] == '.') kind = CodeTokenKind.Constant;
                else kind = CodeTokenKind.Plain;
                if (kind == CodeTokenKind.Keyword)
                {
                    if (compared is "def" or "function" or "func" or "fn" or "fun") expectName = CodeTokenKind.Function;
                    else if (compared is "class" or "struct" or "interface" or "enum" or "record") expectName = CodeTokenKind.Type;
                }
                Append(tokens, start, index - start, kind);
                continue;
            }
            index++;
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            Append(tokens, start, index - start, CodeTokenKind.Plain);
        }
        if (index < text.Length)
        {
            // Stop classification, preserve all text, and do not invent continuation beyond the budget.
            Append(tokens, index, text.Length - index, CodeTokenKind.Plain);
            state = default;
        }
        after = state with { ExpectedName = expectName };
        return tokens;
    }

    private static void ReadDelimited(string text, ref int index, ref CodeLexicalState state)
    {
        var end = state.End!;
        var depth = Math.Max(1, state.Depth);
        while (index < text.Length)
        {
            if (!state.Multiline && text[index] is '\r' or '\n') { state = default; return; }
            if (state.Escape && text[index] == state.EscapeCharacter) { index = Math.Min(text.Length, index + 2); continue; }
            if (state.NestedStart is { } nested && text.AsSpan(index).StartsWith(nested, StringComparison.Ordinal))
            { depth++; index += nested.Length; continue; }
            if (text.AsSpan(index).StartsWith(end, StringComparison.Ordinal))
            {
                index += end.Length;
                if (state.DoubledEnd && text.AsSpan(index).StartsWith(end, StringComparison.Ordinal)) { index += end.Length; continue; }
                if (--depth == 0) { state = default; return; }
                continue;
            }
            index++;
        }
        state = state.Multiline ? state with { Depth = depth } : default;
    }

    private static bool TryString(string text, int start, string? name, out int prefixLength, out CodeLexicalState state)
    {
        prefixLength = 0; state = default;
        var index = start;
        var verbatim = false;
        if (name == "csharp")
        {
            while (index < text.Length && index - start < 2 && text[index] is '$' or '@')
            { verbatim |= text[index] == '@'; index++; }
        }
        else if (name == "python" && IsPythonPrefix(text, start))
            while (index < text.Length && text[index] is not ('\'' or '"')) index++;
        else if (name == "kql" && Peek(text, index) == '@') { verbatim = true; index++; }
        else if (name == "rust" && Peek(text, index) == 'r')
        {
            index++;
            while (Peek(text, index) == '#') index++;
            if (Peek(text, index) == '"')
            {
                prefixLength = index - start + 1;
                state = new(End: "\"" + new string('#', index - start - 1), Kind: CodeTokenKind.String, Multiline: true);
                return true;
            }
            index = start;
        }
        var quote = Peek(text, index);
        if (quote is not ('\'' or '"' or '`')) return false;
        if (quote == '`' && name is not ("javascript" or "typescript" or "go" or "sql")) return false;
        if (quote == '\'' && name is "json" or "jsonc") return false;
        var quotes = 1;
        if (quote != '`' && name is "python" or "toml" or "csharp" or "java" or "kotlin" or "swift")
        {
            var count = 1;
            while (Peek(text, index + count) == quote) count++;
            if (count >= 3) quotes = name == "csharp" ? count : 3;
        }
        var multiline = quotes >= 3 || verbatim || quote == '`' || name is "sql" or "bash" or "powershell" or "php" or "yaml";
        var escape = !verbatim && name is not ("sql" or "powershell" or "ini") && !(name is "bash" or "toml" or "yaml" && quote == '\'')
            && !(quote == '`' && name == "go") && !(name == "csharp" && quotes >= 3);
        if (name == "powershell" && quote == '"') escape = true;
        var kind = name == "sql" && quote is '"' or '`' ? CodeTokenKind.Type : CodeTokenKind.String;
        prefixLength = index - start + quotes;
        state = new(End: new string(quote, quotes), Kind: kind, Escape: escape,
            DoubledEnd: quotes == 1 && (verbatim || name is "sql" or "powershell" || name == "yaml" && quote == '\''), Multiline: multiline, Depth: 1,
            EscapeCharacter: name == "powershell" ? '`' : '\\');
        return true;
    }

    private static bool IsLineStart(string text, int index)
    {
        while (--index >= 0 && text[index] is not ('\r' or '\n')) if (!char.IsWhiteSpace(text[index])) return false;
        return true;
    }

    private static bool LineComment(string text, int index, string? name)
    {
        var current = text[index]; var next = Peek(text, index + 1);
        if (current == '-' && next == '-' && name == "sql") return true;
        if (current == '/' && next == '/' && name is "csharp" or "javascript" or "typescript" or "java" or "c" or "cpp" or "php" or "go" or "rust" or "kotlin" or "swift" or "jsonc" or "kql") return true;
        if (name == "ini") return current is '#' or ';' && IsLineStart(text, index);
        if (current != '#') return false;
        return name is "python" or "powershell" or "php" or "toml"
            || name == "dockerfile" && IsLineStart(text, index)
            || name is "bash" or "yaml" && (index == 0 || char.IsWhiteSpace(text[index - 1]));
    }

    private static IReadOnlyList<CodeToken> TokenizeMarkupLine(string text, string name, CodeLexicalState before, out CodeLexicalState after)
    {
        var tokens = new List<CodeToken>();
        var state = before; var index = 0;
        while (index < text.Length && tokens.Count < MaxTokens)
        {
            var start = index;
            if (state.End == "-->")
            {
                var close = text.IndexOf("-->", index, StringComparison.Ordinal);
                index = close < 0 ? text.Length : close + 3;
                Append(tokens, start, index - start, CodeTokenKind.Comment);
                if (close >= 0) state = default;
                continue;
            }
            if (!state.InTag && state.Embedded is { } embedded)
            {
                var endTag = embedded == "css" ? "</style" : "</script";
                var close = text.IndexOf(endTag, index, StringComparison.OrdinalIgnoreCase);
                var end = close < 0 ? text.Length : close;
                var embeddedTokens = Scan(text[index..end], embedded, state with { Embedded = null }, out var embeddedState);
                foreach (var token in embeddedTokens)
                    Append(tokens, index + token.Start, token.Length, token.Kind);
                state = close < 0 ? embeddedState with { Embedded = embedded } : default;
                index = end;
                continue;
            }
            if (!state.InTag && text.AsSpan(index).StartsWith("<!--", StringComparison.Ordinal))
            { state = new(End: "-->", Kind: CodeTokenKind.Comment, Multiline: true); continue; }
            if (!state.InTag)
            {
                if (text[index] != '<')
                {
                    var next = text.IndexOf('<', index); index = next < 0 ? text.Length : next;
                    Append(tokens, start, index - start, CodeTokenKind.Plain); continue;
                }
                index++;
                var closing = Peek(text, index) == '/';
                if (closing || Peek(text, index) is '!' or '?') index++;
                Append(tokens, start, index - start, CodeTokenKind.Plain);
                start = index;
                while (index < text.Length && IsMarkupName(text[index])) index++;
                var tag = text[start..index];
                Append(tokens, start, index - start, CodeTokenKind.Type);
                state = new(InTag: true, Embedded: name == "html" && !closing ? tag.Equals("script", StringComparison.OrdinalIgnoreCase) ? "javascript" : tag.Equals("style", StringComparison.OrdinalIgnoreCase) ? "css" : null : null);
            }
            while (index < text.Length && state.InTag && tokens.Count < MaxTokens)
            {
                start = index;
                if (state.TagQuote != '\0')
                {
                    while (index < text.Length && text[index] != state.TagQuote) index++;
                    if (index < text.Length) { index++; state = state with { TagQuote = '\0' }; }
                    Append(tokens, start, index - start, CodeTokenKind.String); continue;
                }
                var value = text[index++];
                if (value is '\'' or '"') { state = state with { TagQuote = value }; Append(tokens, start, 1, CodeTokenKind.String); }
                else if (value == '>') { state = state with { InTag = false }; Append(tokens, start, 1, CodeTokenKind.Plain); }
                else if (IsMarkupName(value))
                { while (index < text.Length && IsMarkupName(text[index])) index++; Append(tokens, start, index - start, CodeTokenKind.Keyword); }
                else Append(tokens, start, 1, CodeTokenKind.Plain);
            }
        }
        if (index < text.Length) { Append(tokens, index, text.Length - index, CodeTokenKind.Plain); state = default; }
        after = state; return tokens;
    }
}
