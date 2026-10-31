using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Markdig;
using Markdig.Syntax;
using Markdig.Extensions.Yaml;
using Markdig.Extensions.Tables;
using Markdig.Syntax.Inlines;
using Tern.Rendering.Mermaid;
using Tern.Rendering.Mathematics;
using Tern.Rendering.Resources;
using Tern.Markdown;
using Tern.Rendering.Preview;

namespace Tern.PreviewView.Tests;

public sealed class CorpusValidationTests
{
    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true };
    [AvaloniaFact]
    public void Every_inventoried_document_preserves_semantic_blocks_and_realizes_native_preview()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Tern.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var output = Path.Combine(root.FullName, "artifacts", "rendering-validation", "tern"); Directory.CreateDirectory(output);
        var inventoryPath = Path.Combine(root.FullName, "artifacts", "rendering-validation", "inventory.json");
        var files = new List<(string Id, string Path, string Workspace)>();
        if (File.Exists(inventoryPath))
        {
            using var inventory = JsonDocument.Parse(File.ReadAllText(inventoryPath));
            var roots = inventory.RootElement.GetProperty("sources").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetString()!, x => x.GetProperty("root").GetString()!);
            foreach (var file in inventory.RootElement.GetProperty("files").EnumerateArray())
                files.Add((file.GetProperty("id").GetString()!, file.GetProperty("localPath").GetString()!, roots[file.GetProperty("source").GetString()!]));
        }
        else
            foreach (var folder in new[] { "MD Files Samples 01", "MD Files Samples 02" })
                foreach (var path in Directory.EnumerateFiles(Path.Combine(root.FullName, folder), "*", SearchOption.AllDirectories).Where(p => Path.GetExtension(p).Equals(".md", StringComparison.OrdinalIgnoreCase)))
                    files.Add((folder + ":" + Path.GetRelativePath(Path.Combine(root.FullName, folder), path), path, Path.Combine(root.FullName, folder)));
        Assert.NotEmpty(files);
        var reports = new List<object>(); var failures = new List<string>();
        foreach (var file in files.OrderBy(f => f.Id, StringComparer.Ordinal))
        {
            var errors = new List<string>(); var observations = new List<object>();
            var bytes = File.ReadAllBytes(file.Path); var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var text = File.ReadAllText(file.Path);
            var parsed = Markdig.Markdown.Parse(text, MarkdownPipelines.For(MarkdownProfile.Extended));
            var document = Harness.Build(text, new PreviewOptions { DocumentPath = file.Path, WorkspaceRoot = file.Workspace });
            var nodes = Walk(document.Blocks).ToArray();
            if (document.Truncated) errors.Add("Preview truncated: " + document.Truncation);
            if (document.SourceLength != text.Length) errors.Add("Incorrect source length");
            var astCode = parsed.Descendants<CodeBlock>().Where(n => n is not YamlFrontMatterBlock).ToArray();
            var code = nodes.OfType<CodeBlockNode>().Select(n => n.Code).Concat(nodes.OfType<DiagramNode>().Select(n => n.DiagramSource)).Concat(nodes.OfType<MathBlockNode>().Select(n => n.Latex)).Select(NormalizeCode).ToList();
            foreach (var expected in astCode)
            {
                var value = NormalizeCode(expected.Lines.ToString()); var index = code.IndexOf(value);
                if (index < 0) errors.Add("Code payload omitted/changed at line " + (expected.Line + 1)); else code.RemoveAt(index);
            }
            foreach (var block in nodes.OfType<CodeBlockNode>())
                if (string.Concat(CodeSyntaxHighlighter.Tokenize(block.Code, block.Language).Select(t => block.Code.Substring(t.Start, t.Length))) != block.Code)
                    errors.Add("Syntax highlighting lost code at line " + block.Source.StartLine);
            if (nodes.OfType<HeadingNode>().Count() < parsed.Descendants<HeadingBlock>().Count()) errors.Add("Markdown heading omitted");
            if (nodes.OfType<TableNode>().Count() < parsed.Descendants<Table>().Count()) errors.Add("Markdown table omitted");
            if (nodes.OfType<ListNode>().Count() < parsed.Descendants<ListBlock>().Count()) errors.Add("Markdown list omitted");
            var retainedText = string.Concat(nodes.Select(n => n switch { TextNode t => t.Text, ImageNode i => i.Alt, CodeSpanNode c => c.Code, _ => "" }));
            // Task-marker removal intentionally trims the following separator; ordinary inline edge
            // whitespace is presentation, while code whitespace is checked separately and exactly.
            var missingLiterals = parsed.Descendants<LiteralInline>().Select(n => n.Content.ToString().Trim()).Where(t => t.Length >= 2 && !retainedText.Contains(t, StringComparison.Ordinal)).Distinct().ToArray();
            if (missingLiterals.Length > 0) errors.Add("Literal inline text omitted: " + string.Join(" | ", missingLiterals.Take(8)));
            foreach (var heading in nodes.OfType<HeadingNode>())
                if (document.FindAnchorSourceLine(heading.Anchor) is null) errors.Add("Unnavigable heading anchor " + heading.Anchor);
            var (window, view) = Harness.Show(document, 1000, 700);
            try
            {
                foreach (var line in new[] { 1, Math.Max(1, document.LineCount / 2), Math.Max(1, document.LineCount) })
                {
                    view.ScrollToSourceLine(line); Harness.Pump(window);
                    if (!double.IsFinite(view.Scroller.Offset.Y) || !double.IsFinite(view.Scroller.Extent.Height)) errors.Add("Nonfinite scroll geometry");
                    if (document.Blocks.Count > 0 && view.Panel.RealizedCount == 0) errors.Add("No realized native block at line " + line);
                    observations.Add(new { sourceLine = line, topSourceLine = view.TopSourceLine, offset = view.Scroller.Offset.Y, extent = view.Scroller.Extent.Height, realizedBlocks = view.Panel.RealizedCount });
                }
            }
            catch (Exception ex) { errors.Add("Native realization: " + ex.GetType().Name + ": " + ex.Message); }
            finally { window.Close(); }
            if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.Path))) != hash) errors.Add("Original source changed");
            var resources = nodes.OfType<LinkNode>().Select(n => new { kind = "link", line = n.Source.StartLine, destination = n.Destination, decision = n.Resolution.GetType().Name, reason = n.Resolution is Blocked b ? b.Reason.ToString() : null })
                .Concat(nodes.OfType<ImageNode>().Select(n => new { kind = "image", line = n.Source.StartLine, destination = n.Destination, decision = n.Resolution.GetType().Name, reason = n.Resolution is Blocked b ? b.Reason.ToString() : null })).ToArray();
            var diagrams = nodes.OfType<DiagramNode>().Select(n => { var accepted = MermaidDiagram.TryParse(n.DiagramSource, out var result); return new { line = n.Source.StartLine, accepted, kind = result.Kind.ToString() }; }).ToArray();
            var mathematics = nodes.OfType<MathBlockNode>().Select(n => (n.Source, n.Latex)).Concat(nodes.OfType<MathSpanNode>().Select(n => (n.Source, n.Latex)))
                .Select(n => new { line = n.Source.StartLine, source = n.Latex, error = MathText.Render(n.Latex).ErrorMessage }).ToArray();
            var headings = nodes.OfType<HeadingNode>().Select(n => new { line = n.Source.StartLine, level = n.Level, text = n.PlainText, anchor = n.Anchor }).ToArray();
            var codeBlocks = nodes.OfType<CodeBlockNode>().Select(n => new { line = n.Source.StartLine, language = n.Language, text = n.Code, fenced = n.IsFenced }).ToArray();
            var tables = nodes.OfType<TableNode>().Select(n => new { line = n.Source.StartLine, alignments = n.Alignments.Select(a => a.ToString()).ToArray(), header = n.Header?.Cells.Select(c => Text(c.Inlines)).ToArray(), rows = n.Body.Select(r => r.Cells.Select(c => Text(c.Inlines)).ToArray()).ToArray() }).ToArray();
            var lists = nodes.OfType<ListNode>().Select(n => new { line = n.Source.StartLine, ordered = n.Ordered, start = n.StartNumber, tight = n.Tight, items = n.Items.Select(i => new { task = i.Task.ToString(), text = Text(i.Blocks) }).ToArray() }).ToArray();
            var links = nodes.OfType<LinkNode>().Select(n => new { line = n.Source.StartLine, text = Text(n.Children), destination = n.Destination }).ToArray();
            var images = nodes.OfType<ImageNode>().Select(n => new { line = n.Source.StartLine, alt = n.Alt, destination = n.Destination }).ToArray();
            reports.Add(new { id = file.Id, localPath = file.Path, sha256 = hash, byteLength = bytes.Length, profile = "Extended", viewport = "1000x700", status = errors.Count == 0 ? "Pass" : "Fail", checks = new[] { "AST code payload preservation", "syntax token coverage", "literal inline retention", "table/list/heading counts", "heading anchor resolution", "source length/budget", "native top/middle/end realization", "finite geometry", "source SHA integrity" }, errors, nodeCounts = nodes.GroupBy(n => n.GetType().Name).ToDictionary(g => g.Key, g => g.Count()), visibleText = retainedText, headings, codeBlocks, tables, lists, links, images, resources, diagrams, mathematics, observations });
            failures.AddRange(errors.Select(e => file.Id + ": " + e));
            File.WriteAllText(Path.Combine(output, "corpus-results.json"), JsonSerializer.Serialize(new { inventoryPath, executed = reports.Count, total = files.Count, complete = reports.Count == files.Count, results = reports }, ReportOptions));
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static string NormalizeCode(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\r', '\n');
    private static string Text(object nodes) => string.Concat(Walk(nodes).Select(n => n switch { TextNode t => t.Text, CodeSpanNode c => c.Code, ImageNode i => i.Alt, LineBreakNode => "\n", _ => "" }));
    private static IEnumerable<PreviewNode> Walk(object? value)
    {
        if (value is null or string) yield break;
        if (value is PreviewNode node)
        {
            yield return node;
            foreach (var property in node.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.GetIndexParameters().Length == 0))
                foreach (var child in Walk(property.GetValue(node))) yield return child;
        }
        else if (value is IEnumerable items)
            foreach (var item in items) foreach (var child in Walk(item)) yield return child;
    }
}
