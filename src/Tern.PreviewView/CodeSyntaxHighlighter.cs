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
    internal static bool IsKnownLanguage(string? language) => NormalizeLanguage(language) is
        "html" or "xml" or "svg" or "xhtml" or "python" or "py" or "python3" or "sql" or "tsql" or "postgresql" or "mysql"
        or "bash" or "sh" or "shell" or "zsh" or "powershell" or "ps1" or "pwsh" or "dockerfile" or "csharp" or "cs" or "c#"
        or "javascript" or "js" or "typescript" or "ts" or "java" or "c" or "cpp" or "c++" or "php" or "go" or "golang" or "rust" or "rs"
        or "kotlin" or "kt" or "kts" or "swift" or "css" or "json" or "jsonc" or "yaml" or "yml" or "toml" or "ini" or "kusto" or "kql";
    private const int MaxTokens = 6000;
    private static readonly HashSet<string> PythonKeywords = Words("and as assert async await break case class continue def del elif else except finally for from global if import in is lambda match nonlocal not or pass raise return try while with yield");
    private static readonly HashSet<string> CStyleKeywords = Words("abstract add alias and args as ascending async await base bool break by byte case catch char checked class const continue decimal default delegate descending do double dynamic else enum equals event explicit extern false field file finally fixed float for foreach from get global goto group if implicit in init int interface internal into is join let lock long managed nameof namespace new not notnull null object on operator or orderby out override params partial private protected public readonly record ref remove required return sbyte scoped sealed select set short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unmanaged unsafe ushort using value var virtual void volatile when where while with yield");
    private static readonly HashSet<string> SqlKeywords = Words("add all alter and any as asc authorization backup begin between by cascade case cast check column commit constraint create cross database declare default delete desc distinct drop else end except exec execute exists foreign from full function grant group having if in index inner insert intersect into is join key left like limit merge not null offset on or order outer over partition primary procedure references return returns revoke right rollback row select sequence set table then top transaction trigger truncate union unique update use using values view when where while with");
    private static readonly HashSet<string> ShellKeywords = Words("if then else elif fi for while until do done in case esac function return exit select time coproc let local export readonly source declare typeset unset shift trap break continue eval exec");
    private static readonly HashSet<string> GoKeywords = Words("break default func interface select case defer go map struct chan else goto package switch const fallthrough if range type continue for import return var");
    private static readonly HashSet<string> RustKeywords = Words("as async await break const continue crate dyn else enum extern false fn for if impl in let loop match mod move mut pub ref return self static struct super trait true type unsafe use where while");
    private static readonly HashSet<string> KotlinKeywords = Words("as break class continue do else false for fun if in interface is null object package return super this throw true try typealias typeof val var when while by catch constructor delegate dynamic field file finally get import init param property receiver set setparam where actual abstract annotation companion const crossinline data enum expect external final infix inline inner internal lateinit noinline open operator out override private protected public reified sealed suspend tailrec vararg");
    private static readonly HashSet<string> SwiftKeywords = Words("associatedtype class deinit enum extension fileprivate func import init inout internal let open operator private precedencegroup protocol public rethrows static struct subscript typealias var break case catch continue default defer do else fallthrough for guard if in repeat return throw switch where while as any async await false is nil self some super throws true try weak unowned");

    public static IReadOnlyList<CodeToken> Tokenize(string text, string? language)
    {
        var name = NormalizeLanguage(language);
        if (!IsKnownLanguage(name)) return text.Length == 0 ? [] : [new(0, text.Length, CodeTokenKind.Plain)];
        var tokens = name is "html" or "xml" or "svg" ? TokenizeMarkup(text, name == "html") : Scan(text, name, default, out _);
        if (tokens.Count <= MaxTokens + 1) return tokens;
        var bounded = tokens.Take(MaxTokens).ToList();
        var tail = bounded[^1].Start;
        bounded[^1] = new(tail, text.Length - tail, CodeTokenKind.Plain);
        return bounded;
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

    private static bool IsPythonPrefix(string text, int start)
    {
        var index = start;
        while (index < text.Length && index - start < 2 && text[index] is 'r' or 'R' or 'f' or 'F' or 'b' or 'B' or 'u' or 'U') index++;
        return index > start && Peek(text, index) is '\'' or '"';
    }

    private static char Peek(string text, int index) => index < text.Length ? text[index] : '\0';

    private static HashSet<string> Words(string words) => new(words.Split(' '), StringComparer.Ordinal);
}
