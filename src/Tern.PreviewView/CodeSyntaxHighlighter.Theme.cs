namespace Tern.PreviewView;

internal static partial class CodeSyntaxHighlighter
{
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
}
