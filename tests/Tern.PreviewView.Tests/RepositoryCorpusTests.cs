using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Tern.Rendering.Mermaid;
using Tern.Rendering.Preview;

namespace Tern.PreviewView.Tests;

public sealed class RepositoryCorpusTests
{
    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true };
    [Fact]
    public void Reference_corpus_preserves_code_and_supports_every_diagram_without_truncation()
    {
        var root = Environment.GetEnvironmentVariable("TERN_PARITY_CORPUS");
        // The ordinary suite uses a self-contained fixture; the recorded corpus run
        // sets the root to the sixteen pinned public source files, including documents
        // about agent instructions. Their contents are rendered as data, never executed.
        var files = root is null ? Array.Empty<string>() : Directory.GetFiles(root, "*.md", SearchOption.AllDirectories);
        if (root is not null) Assert.Equal(16, files.Length);
        var extraRoot = Environment.GetEnvironmentVariable("TERN_PARITY_EXTRA_CORPUS");
        if (extraRoot is not null)
        {
            var extraFiles = Directory.GetFiles(extraRoot, "*.md", SearchOption.AllDirectories);
            Assert.Equal(7, extraFiles.Length);
            files = [.. files, .. extraFiles];
        }
        var reports = new List<object>();
        var unsupported = new List<string>();
        var diagrams = 0;
        foreach (var path in files.Order(StringComparer.Ordinal))
        {
            var before = File.ReadAllBytes(path);
            var document = PreviewBuilder.Build(File.ReadAllText(path), new PreviewOptions { DocumentPath = path });
            Assert.False(document.Truncated, Path.GetFileName(path) + ": " + document.Truncation);
            var nodes = document.Blocks.SelectMany(Descendants).ToArray();
            foreach (var diagram in nodes.OfType<DiagramNode>())
            {
                if (!MermaidDiagram.TryParse(diagram.DiagramSource, out _)) unsupported.Add(Path.GetFileName(path) + ": " + diagram.DiagramSource);
                diagrams++;
            }
            foreach (var code in nodes.OfType<CodeBlockNode>())
            {
                var tokens = CodeSyntaxHighlighter.Tokenize(code.Code, code.Language);
                Assert.Equal(code.Code, string.Concat(tokens.Select(token => code.Code.Substring(token.Start, token.Length))));
            }
            Assert.Equal(before, File.ReadAllBytes(path));
            reports.Add(new { file = path, sha256 = Convert.ToHexString(SHA256.HashData(before)), bytes = before.Length,
                blocks = document.Blocks.Count, headings = nodes.OfType<HeadingNode>().Count(), codeBlocks = nodes.OfType<CodeBlockNode>().Count(),
                diagrams = nodes.OfType<DiagramNode>().Count(), truncated = document.Truncated });
        }
        if (root is not null)
        {
            Assert.Equal(27, diagrams);
            var output = Environment.GetEnvironmentVariable("TERN_PARITY_REPORT");
            if (output is not null) File.WriteAllText(output, JsonSerializer.Serialize(reports, ReportOptions));
            Assert.Empty(unsupported);
        }
        else
        {
            const string code = "class Dog:\n    pass";
            var document = PreviewBuilder.Build("```python\n" + code + "\n```", new PreviewOptions());
            var block = Assert.IsType<CodeBlockNode>(Assert.Single(document.Blocks));
            Assert.Equal(code, block.Code);
            Assert.Equal(code, string.Concat(CodeSyntaxHighlighter.Tokenize(code, "python").Select(token => code.Substring(token.Start, token.Length))));
        }
    }

    private static IEnumerable<PreviewNode> Descendants(PreviewNode node)
    {
        yield return node;
        foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0) continue;
            if (property.GetValue(node) is not IEnumerable items || items is string) continue;
            foreach (var child in items.OfType<PreviewNode>())
                foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
