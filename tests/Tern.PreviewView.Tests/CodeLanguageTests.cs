using Avalonia.Headless.XUnit;
using Avalonia;
using Avalonia.Media;
using Avalonia.Controls.Documents;
using Avalonia.Input.Platform;

namespace Tern.PreviewView.Tests;

public sealed class CodeLanguageTests
{
    [Theory]
    [InlineData("csharp", "SELECT function Return", false)]
    [InlineData("javascript", "namespace sealed Class", false)]
    [InlineData("c", "var namespace function", false)]
    [InlineData("json", "public return # comment", false)]
    [InlineData("toml", "class return", false)]
    [InlineData("kql", "project extend summarize", true)]
    [InlineData("sql", "SELECT from WHERE", true)]
    public void Declared_language_uses_its_own_keyword_and_case_rules(string language, string source, bool keywords)
    {
        var tokens = CodeSyntaxHighlighter.Tokenize(source, language);
        Assert.Equal(source, string.Concat(tokens.Select(token => source.Substring(token.Start, token.Length))));
        if (keywords) Assert.Contains(tokens, token => token.Kind == CodeTokenKind.Keyword);
        else Assert.DoesNotContain(tokens, token => token.Kind == CodeTokenKind.Keyword);
    }

    public static IEnumerable<object[]> SupportedAliases()
    {
        foreach (var group in new[]
        {
            ("html,xhtml,xml,svg", "<node key=\"value\" />", "node", CodeTokenKind.Type),
            ("python,py,python3", "def sample(): return True", "def", CodeTokenKind.Keyword),
            ("sql,tsql,postgresql,mysql", "SELECT 'value';", "SELECT", CodeTokenKind.Keyword),
            ("bash,sh,shell,zsh", "if true; then echo 'value'; fi", "if", CodeTokenKind.Keyword),
            ("powershell,ps1,pwsh", "IF ($true) { \"value\" }", "IF", CodeTokenKind.Keyword),
            ("dockerfile", "FROM base", "FROM", CodeTokenKind.Keyword),
            ("csharp,cs,c#", "var x = 35;", "var", CodeTokenKind.Keyword),
            ("javascript,js", "const x = 35;", "const", CodeTokenKind.Keyword),
            ("typescript,ts", "interface Example { value: string; }", "interface", CodeTokenKind.Keyword),
            ("java", "public class Example {}", "public", CodeTokenKind.Keyword),
            ("c", "int x = 35;", "int", CodeTokenKind.Keyword),
            ("cpp,c++", "namespace example { int x = 35; }", "namespace", CodeTokenKind.Keyword),
            ("php", "function example() { return 35; }", "function", CodeTokenKind.Keyword),
            ("go,golang", "func example() { return }", "func", CodeTokenKind.Keyword),
            ("rust,rs", "fn example() { let x = 35; }", "fn", CodeTokenKind.Keyword),
            ("kotlin,kt,kts", "fun example() = \"value\"", "fun", CodeTokenKind.Keyword),
            ("swift", "func example() { return }", "func", CodeTokenKind.Keyword),
            ("css", ".example { color: red; }", "color", CodeTokenKind.Keyword),
            ("json,jsonc", "{\"value\": true}", "true", CodeTokenKind.Constant),
            ("yaml,yml", "value: true # note", "true", CodeTokenKind.Constant),
            ("toml", "value = true # note", "true", CodeTokenKind.Constant),
            ("ini", "value=\"example\"", "\"example\"", CodeTokenKind.String),
            ("kusto,kql", "T | project value", "project", CodeTokenKind.Keyword),
        })
            foreach (var alias in group.Item1.Split(',')) yield return [alias, group.Item2, group.Item3, group.Item4.ToString()];
    }

    [Theory]
    [MemberData(nameof(SupportedAliases))]
    public void Every_supported_alias_classifies_its_own_example_and_preserves_utf16_offsets(string alias, string source, string word, string expectedKind)
    {
        var kind = Enum.Parse<CodeTokenKind>(expectedKind);
        Assert.True(CodeSyntaxHighlighter.IsKnownLanguage(alias.ToUpperInvariant() + "\t title=example"));
        var tokens = CodeSyntaxHighlighter.Tokenize(source, alias);
        Assert.Equal(source, string.Concat(tokens.Select(token => source.Substring(token.Start, token.Length))));
        Assert.Contains(tokens, token => token.Kind == kind && source.Substring(token.Start, token.Length) == word);
        Assert.All(tokens, token => Assert.InRange(token.Start + token.Length, 1, source.Length));
    }

    [Theory]
    [InlineData("json", "# no\n// no\n/* no */", false)]
    [InlineData("jsonc", "// yes\n/* yes */", true)]
    [InlineData("toml", "# yes\nvalue = true", true)]
    [InlineData("ini", "url=http://example.test/path#fragment", false)]
    [InlineData("yaml", "path: 'C:\\' # note", true)]
    [InlineData("powershell", "\"say `\"hello`\" # still a string\"", false)]
    public void Comment_and_escape_rules_follow_the_declared_profile(string language, string source, bool comments)
    {
        var tokens = CodeSyntaxHighlighter.Tokenize(source, language);
        Assert.Equal(source, string.Concat(tokens.Select(token => source.Substring(token.Start, token.Length))));
        Assert.Equal(comments, tokens.Any(token => token.Kind == CodeTokenKind.Comment));
        if (language == "yaml") Assert.Contains(tokens, token => token.Kind == CodeTokenKind.Comment && source.Substring(token.Start, token.Length) == "# note");
    }

    [Theory]
    [InlineData("csharp", "nil", false)]
    [InlineData("go", "null", false)]
    [InlineData("rust", "null", false)]
    [InlineData("swift", "null", false)]
    [InlineData("sql", "NULL", true)]
    [InlineData("toml", "true", true)]
    public void Literal_names_stay_within_the_declared_profile(string language, string source, bool literal)
    {
        Assert.Equal(literal, CodeSyntaxHighlighter.Tokenize(source, language).Any(token => token.Kind == CodeTokenKind.Constant));
    }

    [Theory]
    [InlineData("csharp", "var text = @\"first", "second\"; return text;")]
    [InlineData("python", "text = '''first", "second'''; return text")]
    [InlineData("javascript", "const text = `first", "second`; return text;")]
    [InlineData("sql", "SELECT 'first", "second'; SELECT 1;")]
    [InlineData("bash", "text='first", "second'; return")]
    [InlineData("powershell", "$text=\"first", "second\"; RETURN $text")]
    [InlineData("php", "$text=\"first", "second\"; return $text;")]
    [InlineData("yaml", "text: \"first", "second\" # comment")]
    public void Incremental_lines_continue_multiline_literals_and_resume_after_the_close(string language, string first, string second)
    {
        CodeSyntaxHighlighter.TokenizeLine(first, language, default, out var continuation);
        Assert.NotNull(continuation.End);
        var tokens = CodeSyntaxHighlighter.TokenizeLine(second, language, continuation, out var after);
        Assert.Equal(second, string.Concat(tokens.Select(token => second.Substring(token.Start, token.Length))));
        Assert.Equal(CodeTokenKind.String, tokens[0].Kind);
        Assert.Null(after.End);
        var whole = first + "\n" + second;
        var full = CodeSyntaxHighlighter.Tokenize(whole, language);
        Assert.Contains(full, token => token.Kind == CodeTokenKind.String && whole.Substring(token.Start, token.Length).Contains("first\nsecond", StringComparison.Ordinal));
    }

    [Fact]
    public void Markup_line_state_preserves_comments_attributes_and_embedded_script_without_execution()
    {
        var state = default(CodeLexicalState);
        var lines = new[] { "<!-- comment", "still comment -->", "<script", "type=\"text/javascript\">const text = `first", "second`; // comment", "</script>" };
        var kinds = new List<CodeTokenKind>();
        foreach (var line in lines)
        {
            var tokens = CodeSyntaxHighlighter.TokenizeLine(line, "html", state, out state);
            Assert.Equal(line, string.Concat(tokens.Select(token => line.Substring(token.Start, token.Length))));
            kinds.AddRange(tokens.Select(token => token.Kind));
        }
        Assert.Null(state.Embedded); Assert.Null(state.End); Assert.False(state.InTag);
        Assert.Contains(CodeTokenKind.Comment, kinds); Assert.Contains(CodeTokenKind.Keyword, kinds); Assert.Contains(CodeTokenKind.String, kinds);
    }

    [AvaloniaFact]
    public async Task Tab_stops_are_display_only_copy_is_exact_and_find_maps_repeated_text_after_tabs()
    {
        const string source = "```csharp\r\nvar\tx = 35; // 😀\r\nx\t= x;\r\n```";
        var (window, view) = Harness.Show(Harness.Build(source));
        try
        {
            var code = Assert.Single(Harness.All<CodeBlockView>(view));
            Assert.Contains('\t', code.Code);
            var displayed = Harness.PlainText(code.CodeText);
            Assert.DoesNotContain('\t', displayed);
            Assert.Contains("var x = 35;", displayed);
            await code.CopyAsync();
            Assert.Equal(code.Code, await window.Clipboard!.TryGetTextAsync());
            var offset = source.LastIndexOf("x;", StringComparison.Ordinal);
            view.HighlightSearchMatch(offset, 1, source); Harness.Pump(window);
            var ranges = ((RichTextBlock)code.CodeText).SearchRanges();
            Assert.True(ranges.Count == 1, $"Code={System.Text.Json.JsonSerializer.Serialize(code.Code)}; Display={System.Text.Json.JsonSerializer.Serialize(displayed)}; sourceOffset={offset}");
            var projected = Assert.Single(ranges);
            Assert.Equal(displayed.LastIndexOf("x;", StringComparison.Ordinal), projected.Start);
            Assert.Equal(1, projected.Length);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Declared_code_examples_are_readable_in_both_themes_with_scrollable_long_lines(bool dark)
    {
        var resources = Application.Current!.Resources.MergedDictionaries.ToArray();
        Application.Current.Resources.MergedDictionaries.Clear();
        Application.Current.Resources.MergedDictionaries.Add(dark ? PreviewTheme.Midnight() : PreviewTheme.Paper());
        const string code = "# Code language examples\n\n```csharp\nvar x = 35;\nvar text = @\"first\nsecond\";\n```\n\n```python\nclass Note:\n\tdef title(self):\n\t\treturn f\"{self.name}\"\n```\n\n```sql\nSELECT name FROM notes WHERE active = true;\n```\n\n```json\n{\"ready\": true, \"value\": 35}\n```\n\n```html\n<div class=\"card\">Example, never executed</div>\n```\n\n```unknown\nplain text stays plain\n```\n\n```javascript\nconst veryLongLine = \"abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyz\";\n```";
        var (window, view) = Harness.Show(Harness.Build(code), width: dark ? 620 : 980, height: dark ? 650 : 1000);
        try
        {
            CapturePreview(window, dark ? "preview-Midnight-top" : "preview-Paper-top");
            view.ScrollToSourceLine(31); Harness.Pump(window);
            var unknown = Assert.Single(Harness.All<CodeBlockView>(view), block => block.Language == "unknown");
            Assert.Equal("Plain text", unknown.LanguageLabel.Text);
            var wide = Assert.Single(Harness.All<CodeBlockView>(view), block => block.Language == "javascript");
            Assert.True(wide.CodeText.TextLayout.WidthIncludingTrailingWhitespace > view.Bounds.Width);
            Assert.Equal("Cascadia Mono", wide.CodeText.FontFamily.Name);
            CapturePreview(window, dark ? "preview-Midnight-lower" : "preview-Paper-lower");
        }
        finally
        {
            window.Close(); Application.Current.Resources.MergedDictionaries.Clear();
            foreach (var resource in resources) Application.Current.Resources.MergedDictionaries.Add(resource);
        }
    }

    private static void CapturePreview(Avalonia.Controls.Window window, string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Tern.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var folder = Tern.Tests.Shared.TestOutput.Folder("screenshots", "reported-code-formatting");
        Directory.CreateDirectory(folder);
        using var frame = Harness.Capture(window); Assert.NotNull(frame);
        using var output = File.Create(Path.Combine(folder, name + ".png"));
        frame.Save(output, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
