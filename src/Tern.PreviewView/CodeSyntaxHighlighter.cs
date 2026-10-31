namespace Tern.PreviewView;

internal enum CodeTokenKind { Plain, Keyword, String, Function, Type, Constant, Comment }

internal readonly record struct CodeToken(int Start, int Length, CodeTokenKind Kind);

/// <summary>
/// A bounded, offline lexical highlighter for documentation examples. It never executes code, loads a grammar or
/// changes source. Unknown languages remain plain text; malformed strings/comments simply colour to their end.
/// This deliberately provides lexical colours rather than claiming a compiler's semantic classification.
/// </summary>
internal static partial class CodeSyntaxHighlighter
{
    private const int MaxTokens = 6000;
    private static readonly HashSet<string> PythonKeywords = Words("and as assert async await break case class continue def del elif else except finally for from global if import in is lambda match nonlocal not or pass raise return try while with yield");
    private static readonly HashSet<string> CStyleKeywords = Words("abstract as async await base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach from get if implicit in int interface internal is lock long namespace new null object operator out override params partial private protected public readonly record ref required return sbyte sealed select set short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using value var virtual void volatile when where while with yield export function import let extends implements package synchronized throws transient instanceof super final boolean");
    private static readonly HashSet<string> SqlKeywords = Words("add all alter and any as asc authorization backup begin between by cascade case cast check column commit constraint create cross database declare default delete desc distinct drop else end except exec execute exists foreign from full function grant group having if in index inner insert intersect into is join key left like limit merge not null offset on or order outer over partition primary procedure references return returns revoke right rollback row select sequence set table then top transaction trigger truncate union unique update use using values view when where while with");
    private static readonly HashSet<string> ShellKeywords = Words("if then else elif fi for while do done in case esac function return exit try catch finally param foreach switch begin process end from run cmd entrypoint copy add arg env expose volume workdir user label shell onbuild stopsignal healthcheck select let local export readonly source");
    private static readonly HashSet<string> Constants = Words("true false null none nil true false self cls");
    private static readonly HashSet<string> GoKeywords = Words("break default func interface select case defer go map struct chan else goto package switch const fallthrough if range type continue for import return var");
    private static readonly HashSet<string> RustKeywords = Words("as async await break const continue crate dyn else enum extern false fn for if impl in let loop match mod move mut pub ref return self static struct super trait true type unsafe use where while");
    private static readonly HashSet<string> KotlinKeywords = Words("as break class continue do else false for fun if in interface is null object package return super this throw true try typealias typeof val var when while by catch constructor delegate dynamic field file finally get import init param property receiver set setparam where actual abstract annotation companion const crossinline data enum expect external final infix inline inner internal lateinit noinline open operator out override private protected public reified sealed suspend tailrec vararg");
    private static readonly HashSet<string> SwiftKeywords = Words("associatedtype class deinit enum extension fileprivate func import init inout internal let open operator private precedencegroup protocol public rethrows static struct subscript typealias var break case catch continue default defer do else fallthrough for guard if in repeat return throw switch where while as any async await false is nil self some super throws true try weak unowned");

    public static IReadOnlyList<CodeToken> Tokenize(string text, string? language)
    {
        var name = language?.Trim().ToLowerInvariant().Split(' ', '{')[0];
        if (name is "html" or "xml" or "svg" or "xhtml") return TokenizeMarkup(text, name == "html" || name == "xhtml");
        var python = name is "python" or "py" or "python3";
        var sql = name is "sql" or "tsql" or "postgresql" or "mysql";
        var shell = name is "bash" or "sh" or "shell" or "zsh" or "powershell" or "ps1" or "pwsh" or "dockerfile";
        var rust = name is "rust" or "rs";
        var css = name is "css";
        var cStyle = name is "csharp" or "cs" or "c#" or "javascript" or "js" or "typescript" or "ts" or "java" or "c" or "cpp" or "c++" or "php" or "go" or "golang" or "rust" or "rs" or "kotlin" or "kt" or "kts" or "swift" or "css";
        var data = name is "json" or "jsonc" or "yaml" or "yml" or "toml" or "ini" or "kusto" or "kql";
        if (!python && !sql && !shell && !cStyle && !data) return [new(0, text.Length, CodeTokenKind.Plain)];

        var tokens = new List<CodeToken>();
        var keywords = name switch
        {
            "go" or "golang" => GoKeywords,
            "rust" or "rs" => RustKeywords,
            "kotlin" or "kt" or "kts" => KotlinKeywords,
            "swift" => SwiftKeywords,
            _ => python ? PythonKeywords : sql ? SqlKeywords : shell ? ShellKeywords : CStyleKeywords,
        };
        var index = 0;
        var expectName = CodeTokenKind.Plain;
        while (index < text.Length && tokens.Count < MaxTokens)
        {
            var start = index;
            var current = text[index];
            CodeTokenKind kind;
            if ((current == '#' && (python || shell || data)) || (current == '/' && Peek(text, index + 1) == '/' && (cStyle || data)) || (current == '-' && Peek(text, index + 1) == '-' && sql))
            {
                while (index < text.Length && text[index] is not ('\n' or '\r')) index++;
                kind = CodeTokenKind.Comment;
            }
            else if (current == '/' && Peek(text, index + 1) == '*' && (cStyle || sql || data))
            {
                index += 2;
                while (index < text.Length && !(text[index] == '*' && Peek(text, index + 1) == '/')) index++;
                index = Math.Min(text.Length, index + 2);
                kind = CodeTokenKind.Comment;
            }
            else if (current == '\'' && rust && char.IsLetter(Peek(text, index + 1)) && Peek(text, index + 2) != '\'')
            {
                // Rust lifetime names are identifiers, not unterminated character literals.
                index += 2;
                while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_')) index++;
                kind = CodeTokenKind.Type;
            }
            else if (current is '\'' or '"' or '`' || (python && IsPythonPrefix(text, index)))
            {
                if (python && IsPythonPrefix(text, index))
                    while (index < text.Length && text[index] is not ('\'' or '"')) index++;
                var quote = text[index++];
                var triple = python && Peek(text, index) == quote && Peek(text, index + 1) == quote;
                if (triple) index += 2;
                while (index < text.Length)
                {
                    if (text[index] == '\\') { index = Math.Min(text.Length, index + 2); continue; }
                    if (text[index] == quote && (!triple || Peek(text, index + 1) == quote && Peek(text, index + 2) == quote))
                    { index += triple ? 3 : 1; break; }
                    if (!triple && quote != '`' && text[index] is '\n' or '\r') break;
                    index++;
                }
                kind = CodeTokenKind.String;
            }
            else if (char.IsDigit(current))
            {
                index++;
                while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] is '_' or '.')) index++;
                kind = CodeTokenKind.Constant;
            }
            else if (char.IsLetter(current) || current is '_' or '$')
            {
                index++;
                while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] is '_' or '$' || css && text[index] == '-')) index++;
                var word = text[start..index];
                var folded = word.ToLowerInvariant();
                var lookahead = index;
                while (lookahead < text.Length && text[lookahead] is ' ' or '\t') lookahead++;
                if (expectName != CodeTokenKind.Plain) { kind = expectName; expectName = CodeTokenKind.Plain; }
                else if (keywords.Contains(folded) || css && Peek(text, lookahead) == ':') kind = CodeTokenKind.Keyword;
                else if (Constants.Contains(folded) && folded is not ("self" or "cls")) kind = CodeTokenKind.Constant;
                else if (Peek(text, lookahead) == '(') kind = CodeTokenKind.Function;
                else if (start > 0 && text[start - 1] == '.') kind = CodeTokenKind.Constant;
                else kind = CodeTokenKind.Plain;
                if (folded is "def" or "function" or "func" or "fn" or "fun") expectName = CodeTokenKind.Function;
                else if (folded is "class" or "struct" or "interface" or "enum" or "record") expectName = CodeTokenKind.Type;
            }
            else
            {
                index++;
                while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
                kind = CodeTokenKind.Plain;
            }

            if (kind != CodeTokenKind.String || !python || !AppendFormattedPythonString(tokens, text, start, index))
                Append(tokens, start, index - start, kind);
        }

        if (index < text.Length) tokens.Add(new CodeToken(index, text.Length - index, CodeTokenKind.Plain));
        if (tokens.Count > MaxTokens)
        {
            var remainder = tokens[MaxTokens - 1].Start;
            tokens.RemoveRange(MaxTokens - 1, tokens.Count - MaxTokens + 1);
            tokens.Add(new CodeToken(remainder, text.Length - remainder, CodeTokenKind.Plain));
        }
        return tokens;
    }

    private static bool AppendFormattedPythonString(List<CodeToken> tokens, string text, int start, int end)
    {
        var quote = start;
        while (quote < end && text[quote] is not ('\'' or '"')) quote++;
        if (quote == end || text.AsSpan(start, quote - start).IndexOfAny('f', 'F') < 0) return false;

        var segment = start;
        var cursor = quote + 1;
        while (cursor < end && tokens.Count < MaxTokens)
        {
            if (text[cursor] == '\\') { cursor = Math.Min(end, cursor + 2); continue; }
            if (text[cursor] != '{') { cursor++; continue; }
            if (cursor + 1 < end && text[cursor + 1] == '{') { cursor += 2; continue; }
            var close = text.IndexOf('}', cursor + 1, end - cursor - 1);
            if (close < 0) break;
            Append(tokens, segment, cursor - segment, CodeTokenKind.String);
            Append(tokens, cursor, 1, CodeTokenKind.Plain);
            cursor++;
            // Simple f-string expressions are lexical only: identifiers, dotted members, calls and numbers.
            // Complex format specifications stay readable without recursive grammar parsing or execution.
            while (cursor < close && tokens.Count < MaxTokens)
            {
                var wordStart = cursor;
                var kind = CodeTokenKind.Plain;
                if (char.IsLetter(text[cursor]) || text[cursor] == '_')
                {
                    cursor++;
                    while (cursor < close && (char.IsLetterOrDigit(text[cursor]) || text[cursor] == '_')) cursor++;
                    var word = text[wordStart..cursor];
                    var next = cursor;
                    while (next < close && char.IsWhiteSpace(text[next])) next++;
                    kind = PythonKeywords.Contains(word) ? CodeTokenKind.Keyword
                        : next < close && text[next] == '(' ? CodeTokenKind.Function
                        : wordStart > 0 && text[wordStart - 1] == '.' ? CodeTokenKind.Constant
                        : CodeTokenKind.Plain;
                }
                else if (char.IsDigit(text[cursor]))
                {
                    cursor++;
                    while (cursor < close && (char.IsDigit(text[cursor]) || text[cursor] == '.')) cursor++;
                    kind = CodeTokenKind.Constant;
                }
                else cursor++;
                Append(tokens, wordStart, cursor - wordStart, kind);
            }
            if (cursor < close) Append(tokens, cursor, close - cursor, CodeTokenKind.Plain);
            Append(tokens, close, 1, CodeTokenKind.Plain);
            cursor = segment = close + 1;
        }
        Append(tokens, segment, end - segment, CodeTokenKind.String);
        return true;
    }

    private static void Append(List<CodeToken> tokens, int start, int length, CodeTokenKind kind)
    {
        if (length == 0) return;
        if (tokens.Count > 0 && tokens[^1].Kind == kind)
        {
            var previous = tokens[^1];
            tokens[^1] = previous with { Length = start + length - previous.Start };
        }
        else tokens.Add(new CodeToken(start, length, kind));
    }

    public static string ThemeKey(CodeTokenKind kind) => kind switch
    {
        CodeTokenKind.Keyword => PreviewKeys.CodeKeyword,
        CodeTokenKind.String => PreviewKeys.CodeString,
        CodeTokenKind.Function => PreviewKeys.CodeFunction,
        CodeTokenKind.Type => PreviewKeys.CodeType,
        CodeTokenKind.Constant => PreviewKeys.CodeConstant,
        CodeTokenKind.Comment => PreviewKeys.CodeComment,
        _ => PreviewKeys.CodeForeground,
    };

    private static bool IsPythonPrefix(string text, int start)
    {
        var index = start;
        while (index < text.Length && index - start < 2 && text[index] is 'r' or 'R' or 'f' or 'F' or 'b' or 'B' or 'u' or 'U') index++;
        return index > start && Peek(text, index) is '\'' or '"';
    }

    private static char Peek(string text, int index) => index < text.Length ? text[index] : '\0';

    private static HashSet<string> Words(string words) => new(words.Split(' '), StringComparer.Ordinal);
}
