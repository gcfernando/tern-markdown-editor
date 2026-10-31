using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Tern.Rendering.Preview;

namespace Tern.PreviewView.Tests;

public class CodePresentationTests
{
    private const string Python = "class Dog:\n    def __init__(self, name):\n        self.name = name\n\n    def speak(self):\n        return f\"{self.name} says woof\"\n";

    [Fact]
    public void Python_reference_example_has_distinct_lexical_colours_without_changing_text()
    {
        var tokens = CodeSyntaxHighlighter.Tokenize(Python, "python");
        Assert.Equal(Python, string.Concat(tokens.Select(t => Python.Substring(t.Start, t.Length))));
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Keyword && Python.Substring(t.Start, t.Length) == "class");
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Type && Python.Substring(t.Start, t.Length) == "Dog");
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Function && Python.Substring(t.Start, t.Length) == "__init__");
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.String && Python.Substring(t.Start, t.Length).Contains("says woof", StringComparison.Ordinal));
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Constant && Python.Substring(t.Start, t.Length) == "name");
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Plain && Python.Substring(t.Start, t.Length).Contains("{self.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("csharp", "public string Speak() => \"Hello\"; // comment")]
    [InlineData("sql", "SELECT name, COUNT(*) FROM users WHERE id = 42 AND name = 'Ada'; -- note")]
    [InlineData("powershell", "if ($ready) { Write-Host \"Hello\" } # note")]
    [InlineData("python", "text = '''a\nmultiline string'''\n# comment\nreturn text")]
    [InlineData("python", "name = \"unfinished\nreturn 3")]
    [InlineData("python", "return f\"{{literal}} {self.name} {value!r:>10} end\"")]
    [InlineData("javascript", "/* multiline\ncomment */ const greet = () => `Hello`; ")]
    public void Corpus_languages_preserve_every_character_and_colour_strings_and_keywords(string language, string source)
    {
        var tokens = CodeSyntaxHighlighter.Tokenize(source, language);
        Assert.Equal(source, string.Concat(tokens.Select(t => source.Substring(t.Start, t.Length))));
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.String);
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Keyword);
        Assert.All(tokens, t => Assert.True(t.Length > 0));
    }

    [Fact]
    public void Unknown_language_is_plain_and_a_large_example_has_bounded_runs()
    {
        const string unknown = "<script>alert('not run')</script> 😀\r\n";
        Assert.Equal(CodeTokenKind.Plain, Assert.Single(CodeSyntaxHighlighter.Tokenize(unknown, "unknown")).Kind);
        var source = string.Concat(Enumerable.Repeat("return \"text\" + 123;\n", 30_000));
        var tokens = CodeSyntaxHighlighter.Tokenize(source, "python");
        Assert.InRange(tokens.Count, 1, 6001);
        Assert.Equal(source, string.Concat(tokens.Select(t => source.Substring(t.Start, t.Length))));
    }

    [Theory]
    [InlineData("go", "package main\nfunc greet() string { return \"Hello\" } // note", "func", "greet")]
    [InlineData("rust", "fn greet<'a>(name: &'a str) -> &'a str { let message = \"Hello\"; name }", "fn", "greet")]
    [InlineData("kotlin", "data class User(val name: String)\nfun greet() = \"Hello\"", "fun", "greet")]
    [InlineData("swift", "struct User { let name: String }\nfunc greet() -> String { return \"Hello\" }", "func", "greet")]
    public void Additional_languages_colour_declarations_without_losing_source(string language, string source, string keyword, string function)
    {
        var tokens = CodeSyntaxHighlighter.Tokenize(source, language);
        Assert.Equal(source, string.Concat(tokens.Select(t => source.Substring(t.Start, t.Length))));
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Keyword && source.Substring(t.Start, t.Length) == keyword);
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Function && source.Substring(t.Start, t.Length) == function);
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.String && source.Substring(t.Start, t.Length) == "\"Hello\"");
    }

    [Theory]
    [InlineData("html", "<!-- sample -->\n<div class=\"card\">Hello 😀</div><style>.card { max-width: 32rem; }</style><script>const message = 'Hello';</script>")]
    [InlineData("xml", "<?xml version=\"1.0\"?><root name=\"sample\"><child /></root>")]
    public void Markup_source_colours_tags_attributes_and_literals_without_rendering_them(string language, string source)
    {
        var tokens = CodeSyntaxHighlighter.Tokenize(source, language);
        Assert.Equal(source, string.Concat(tokens.Select(t => source.Substring(t.Start, t.Length))));
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Type);
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Keyword);
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.String);
        if (language == "html")
        {
            Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Comment);
            Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Keyword && source.Substring(t.Start, t.Length) == "max-width");
            Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Keyword && source.Substring(t.Start, t.Length) == "const");
        }
    }

    [Fact]
    public void Large_and_unfinished_markup_preserves_tail_with_bounded_runs()
    {
        var source = string.Concat(Enumerable.Repeat("<span name=\"sample\">😀</span>", 20_000)) + "<script>const unfinished = 'tail";
        var tokens = CodeSyntaxHighlighter.Tokenize(source, "html");
        Assert.InRange(tokens.Count, 1, 6002);
        Assert.Equal(source, string.Concat(tokens.Select(t => source.Substring(t.Start, t.Length))));
    }

    [AvaloniaFact]
    public void Code_view_is_selectable_monospace_and_updates_its_token_colours_with_the_theme()
    {
        var application = Application.Current!;
        var original = application.Resources.MergedDictionaries.ToArray();
        application.Resources.MergedDictionaries.Clear();
        application.Resources.MergedDictionaries.Add(PreviewTheme.Midnight());
        var document = Harness.Build("```python\n" + Python + "```\n");
        var parsedCode = Assert.Single(document.Blocks.OfType<CodeBlockNode>()).Code;
        var (window, view) = Harness.Show(document);
        try
        {
            var code = Assert.Single(Harness.All<CodeBlockView>(view));
            var runs = code.CodeText.Inlines!.OfType<Run>().ToList();
            Assert.Equal(Python.TrimEnd('\n'), parsedCode);
            Assert.Equal(parsedCode, string.Concat(runs.Select(r => r.Text)));
            Assert.Equal(parsedCode, code.Code);
            Assert.Equal(parsedCode, Avalonia.Automation.AutomationProperties.GetName(code.CodeText));
            Assert.True(code.CodeText.LineHeight > code.CodeText.FontSize);
            Assert.Equal("Cascadia Mono", code.CodeText.FontFamily.Name);
            var keyword = Assert.Single(runs, r => r.Text == "class");
            var dark = Assert.IsAssignableFrom<ISolidColorBrush>(keyword.Foreground).Color;
            Assert.Equal(Color.Parse("#FF7B72"), dark);
            application.Resources.MergedDictionaries.Clear();
            application.Resources.MergedDictionaries.Add(PreviewTheme.Paper());
            Harness.Pump(window);
            Assert.Equal(Color.Parse("#CF222E"), Assert.IsAssignableFrom<ISolidColorBrush>(keyword.Foreground).Color);
        }
        finally
        {
            window.Close();
            application.Resources.MergedDictionaries.Clear();
            foreach (var resources in original) application.Resources.MergedDictionaries.Add(resources);
        }
    }
}
