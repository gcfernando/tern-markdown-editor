namespace Tern.PreviewView;

internal static partial class CodeSyntaxHighlighter
{
    // These are lexical documentation profiles, not compiler or semantic models.
    internal static string? NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        var name = language.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0].Split('{')[0].ToLowerInvariant();
        return name switch
        {
            "cs" or "c#" => "csharp", "js" => "javascript", "ts" => "typescript",
            "py" or "python3" => "python", "tsql" or "postgresql" or "mysql" => "sql",
            "sh" or "shell" or "zsh" => "bash", "ps1" or "pwsh" => "powershell",
            "c++" => "cpp", "golang" => "go", "rs" => "rust", "kt" or "kts" => "kotlin",
            "yml" => "yaml", "kusto" => "kql", "xhtml" => "html", _ => name,
        };
    }

    private static readonly HashSet<string> JavaScriptKeywords = Words("as async await break case catch class const continue debugger default delete do else export extends finally for from function get if implements import in instanceof interface let new of package private protected public return set static super switch this throw try typeof var void while with yield null true false");
    private static readonly HashSet<string> TypeScriptKeywords = Words(string.Join(' ', JavaScriptKeywords) + " abstract any asserts bigint boolean constructor declare enum global implements infer interface is keyof module namespace never number object override private protected public readonly require satisfies string symbol type unique unknown");
    private static readonly HashSet<string> JavaKeywords = Words("abstract assert boolean break byte case catch char class const continue default do double else enum exports extends final finally float for if goto implements import instanceof int interface long module native new non-sealed open opens package permits private protected provides public record requires return sealed short static strictfp super switch synchronized this throw throws to transient transitive try uses var void volatile when while with yield true false null");
    private static readonly HashSet<string> CKeywords = Words("alignas alignof auto bool break case char const constexpr continue default do double else enum extern false float for goto if inline int long nullptr register restrict return short signed sizeof static static_assert struct switch thread_local true typedef typeof typeof_unqual union unsigned void volatile while _Alignas _Alignof _Atomic _BitInt _Bool _Complex _Decimal128 _Decimal32 _Decimal64 _Generic _Imaginary _Noreturn _Static_assert _Thread_local");
    private static readonly HashSet<string> CppKeywords = Words(string.Join(' ', CKeywords) + " and and_eq asm bitand bitor catch char8_t char16_t char32_t class co_await co_return co_yield compl concept const_cast consteval constinit decltype delete dynamic_cast explicit export friend mutable namespace new noexcept not not_eq operator or or_eq private protected public reinterpret_cast requires static_cast template this throw try typeid typename using virtual wchar_t xor xor_eq");
    private static readonly HashSet<string> PhpKeywords = Words("abstract and array as break callable case catch class clone const continue declare default die do echo else elseif empty enddeclare endfor endforeach endif endswitch endwhile enum eval exit extends final finally fn for foreach function global goto if implements include include_once instanceof insteadof interface isset list match namespace new or print private protected public readonly require require_once return static switch throw trait try unset use var while xor yield true false null");
    private static readonly HashSet<string> PowerShellKeywords = Words("begin break catch class clean configuration continue data define do dynamicparam else elseif end enum exit filter finally for foreach from function hidden if in inlinescript param process return switch throw trap try until using var while workflow");
    private static readonly HashSet<string> DockerKeywords = Words("add arg cmd copy entrypoint env expose from healthcheck label maintainer onbuild run shell stopsignal user volume workdir");
    private static readonly HashSet<string> KqlKeywords = Words("let set alias and as asc between by contains contains_cs count datatable desc distinct evaluate extend externaldata false find fork getschema has has_all has_any has_cs in in~ invoke join kind limit lookup make-series materialize mv-apply mv-expand not notcontains notin of on or order parse parse-where partition print project project-away project-keep project-rename project-reorder range reduce render restrict search serialize sort summarize take top top-nested true union where with");
    private static readonly HashSet<string> NoKeywords = [];

    private static HashSet<string> Keywords(string? name) => name switch
    {
        "python" => PythonKeywords, "sql" => SqlKeywords, "bash" => ShellKeywords,
        "powershell" => PowerShellKeywords, "dockerfile" => DockerKeywords,
        "csharp" => CStyleKeywords, "javascript" => JavaScriptKeywords, "typescript" => TypeScriptKeywords,
        "java" => JavaKeywords, "c" => CKeywords, "cpp" => CppKeywords, "php" => PhpKeywords,
        "go" => GoKeywords, "rust" => RustKeywords, "kotlin" => KotlinKeywords, "swift" => SwiftKeywords,
        "kql" => KqlKeywords, _ => NoKeywords,
    };

    private static bool IgnoresWordCase(string? name) => name is "sql" or "powershell" or "dockerfile" or "php";
    private static bool IsConstant(string word, string? name) => name switch
    {
        "python" => word is "True" or "False" or "None",
        "yaml" => word.ToLowerInvariant() is "true" or "false" or "null",
        "powershell" => word.Equals("$true", StringComparison.OrdinalIgnoreCase) || word.Equals("$false", StringComparison.OrdinalIgnoreCase) || word.Equals("$null", StringComparison.OrdinalIgnoreCase),
        "toml" => word is "true" or "false",
        "json" or "jsonc" or "kql" => word is "true" or "false" or "null",
        "sql" or "php" => word.ToLowerInvariant() is "true" or "false" or "null",
        "csharp" or "javascript" or "typescript" or "java" or "kotlin" => word is "true" or "false" or "null",
        "c" or "cpp" => word is "true" or "false" or "nullptr",
        "go" or "swift" => word is "true" or "false" or "nil",
        "rust" => word is "true" or "false",
        _ => false,
    };
}
