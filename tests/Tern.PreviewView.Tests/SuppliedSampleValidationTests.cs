using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Tern.Rendering.Mathematics;
using Tern.Rendering.Mermaid;
using Tern.Rendering.Preview;
using Tern.Rendering.Resources;

namespace Tern.PreviewView.Tests;

/// <summary>Read-only inspection of the user's supplied samples; reports capabilities rather than assuming parity.</summary>
public sealed class SuppliedSampleValidationTests
{
    private static readonly JsonSerializerOptions ReportJson = new() { WriteIndented = true };
    private const string Smoke = "# Explicit smoke fixture\n\nReadable **text**, $x^2$ and `code`.\n\n```python\nprint('smoke')\n```\n\n## Diagram\n\n```mermaid\nflowchart LR\n A[Start] --> B[End]\n```\n";

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Supplied_documents_are_untruncated_captured_and_left_unchanged(bool paper)
    {
        var root = Environment.GetEnvironmentVariable("TERN_SAMPLE_VALIDATION_ROOT");
        var pcb = Environment.GetEnvironmentVariable("TERN_SAMPLE_VALIDATION_PCB");
        var configured = root is not null || pcb is not null;
        var files = new List<string?>();
        if (configured)
        {
            Assert.False(string.IsNullOrWhiteSpace(root), "Configure both sample root and standalone PCB document.");
            Assert.False(string.IsNullOrWhiteSpace(pcb), "Configure both sample root and standalone PCB document.");
            var samples = Directory.GetFiles(root!, "*.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(20, samples.Length);
            Assert.True(File.Exists(pcb), "Standalone PCB document is missing.");
            files.AddRange(samples);
            files.Add(Path.GetFullPath(pcb!));
            Assert.Equal(21, files.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        else files.Add(null); // An actual smoke run, never a claimed 21-file validation or a silent skip.

        var output = Path.GetFullPath(Environment.GetEnvironmentVariable("TERN_SAMPLE_VALIDATION_OUTPUT")
            ?? Path.Combine(Path.GetTempPath(), "tern-supplied-sample-validation", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(output);
        var theme = paper ? "paper" : "midnight";
        var reports = new List<object>();
        WriteReport(output, theme, configured, files.Count, reports, false);
        var resources = Application.Current!.Resources.MergedDictionaries.ToArray();
        Application.Current.Resources.MergedDictionaries.Clear();
        Application.Current.Resources.MergedDictionaries.Add(paper ? PreviewTheme.Paper() : PreviewTheme.Midnight());
        try
        {
            foreach (var path in files)
            {
                var bytes = path is null ? Encoding.UTF8.GetBytes(Smoke) : await File.ReadAllBytesAsync(path);
                var before = Convert.ToHexString(SHA256.HashData(bytes));
                using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var source = await reader.ReadToEndAsync();
                var document = Harness.Build(source, new PreviewOptions
                {
                    DocumentPath = path,
                    WorkspaceRoot = path is null ? null : Path.GetDirectoryName(path),
                });
                Assert.False(document.Truncated, $"Truncated supplied document: {path}");
                Assert.Equal(source.Length, document.SourceLength);
                Assert.NotEmpty(document.Blocks);
                var blocks = Flatten(document.Blocks).ToArray();
                var inlines = blocks.SelectMany(InlineNodes).SelectMany(FlattenInline).ToArray();
                var diagrams = blocks.OfType<DiagramNode>().Select(node =>
                {
                    var rendered = MermaidDiagram.TryParse(node.DiagramSource, out var parsed);
                    return new
                    {
                        line = node.Source.StartLine,
                        keyword = node.DiagramSource.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0),
                        parsedKind = parsed.Kind.ToString(),
                        nativeDiagramSupported = rendered,
                        unsupportedType = parsed.UnsupportedType,
                        error = parsed.ErrorMessage,
                        errorLine = parsed.ErrorLine,
                        presentation = rendered ? "native diagram" : "source fallback",
                    };
                }).ToArray();
                var formulas = blocks.OfType<MathBlockNode>().Select(node => (node.Source.StartLine, node.Latex))
                    .Concat(inlines.OfType<MathSpanNode>().Select(node => (node.Source.StartLine, node.Latex)))
                    .Select(formula =>
                    {
                        var parsed = MathText.Render(formula.Latex);
                        return new { line = formula.StartLine, nativeMathSupported = !parsed.IsError, error = parsed.ErrorMessage };
                    }).ToArray();
                var svgLines = Regex.Matches(source, @"(?im)^\s*<svg\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
                    .Select(match => 1 + source.AsSpan(0, match.Index).Count('\n')).ToArray();
                var sections = new SortedSet<int> { 1, Math.Max(1, document.LineCount) };
                foreach (var heading in document.Outline) sections.Add(heading.Line);
                foreach (var block in blocks.Where(block => block is DiagramNode or MathBlockNode or CodeBlockNode or TableNode)) sections.Add(block.Source.StartLine);
                foreach (var image in inlines.OfType<ImageNode>()) sections.Add(image.Source.StartLine);
                foreach (var line in svgLines) sections.Add(line);
                var captures = new List<object>();
                var (window, view) = Harness.Show(document, 1200, 1000);
                var identity = path is null ? "explicit-smoke" : Path.GetFileNameWithoutExtension(path);
                identity = Regex.Replace(identity, @"[^A-Za-z0-9_.-]", "_", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                try
                {
                    view.FontSize = 16;
                    view.FontFamily = new FontFamily("Segoe UI, Helvetica, Arial, sans-serif");
                    view.ReduceMotion = true;
                    var index = 0;
                    foreach (var line in sections)
                    {
                        view.ScrollToSourceLine(line);
                        Harness.Pump(window, 8);
                        await ImagesReady(window, view);
                        var imageStates = Harness.All<ImageView>(view).Select(image => new
                        {
                            image.Alt,
                            state = image.State.ToString(),
                            width = image.Bounds.Width,
                            height = image.Bounds.Height,
                        }).ToArray();
                        var notices = Harness.All<CodeBlockView>(view)
                            .SelectMany(code => code.GetVisualDescendants().OfType<TextBlock>())
                            .Select(text => text.Text)
                            .Where(text => text is not null && (text.Contains("showing source", StringComparison.OrdinalIgnoreCase)
                                || text.Contains("could not be drawn", StringComparison.OrdinalIgnoreCase)))
                            .Distinct(StringComparer.Ordinal).ToArray();
                        var filename = $"{identity}-{theme}-{index++:D3}-line-{line}.png";
                        using var frame = Harness.Capture(window);
                        Assert.NotNull(frame);
                        Assert.True(Harness.NonBackgroundPixels(frame, new PixelRect(0, 0, frame.PixelSize.Width, frame.PixelSize.Height)) > 100);
                        using (var file = File.Create(Path.Combine(output, filename))) frame.Save(file, new PngBitmapEncoderOptions());
                        captures.Add(new
                        {
                            requestedLine = line,
                            actualTopLine = view.TopSourceLine,
                            screenshot = filename,
                            realizedNativeDiagrams = Harness.All<DiagramControl>(view).Count,
                            realizedImages = imageStates,
                            realizedFallbackNotices = notices,
                        });
                    }
                }
                finally
                {
                    window.Close();
                    await view.ImageCleanupCompletion.WaitAsync(TimeSpan.FromSeconds(5));
                    if (path is not null) Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
                }
                var after = path is null ? before : Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
                Assert.Equal(before, after);
                reports.Add(new
                {
                    path = path ?? "explicit smoke fixture (not user corpus)",
                    sha256Before = before,
                    sha256After = after,
                    bytes = bytes.Length,
                    sourceCharacters = source.Length,
                    lineCount = document.LineCount,
                    truncated = document.Truncated,
                    headings = document.Outline.Select(heading => new { heading.Text, heading.Level, heading.Line }).ToArray(),
                    tables = blocks.OfType<TableNode>().Count(),
                    codeBlocks = blocks.OfType<CodeBlockNode>().Select(code => new { code.Language, line = code.Source.StartLine }).ToArray(),
                    math = formulas,
                    diagrams,
                    rawSvgOpeningLines = svgLines,
                    modelImages = inlines.OfType<ImageNode>().Select(image => new
                    {
                        line = image.Source.StartLine,
                        image.Alt,
                        resolution = image.Resolution.GetType().Name,
                        svg = image.Resolution is InlineImage { MediaType: "image/svg+xml" }
                            || image.Resolution is LocalFile local && Path.GetExtension(local.CanonicalPath).Equals(".svg", StringComparison.OrdinalIgnoreCase),
                        blockedReason = image.Resolution is Blocked blocked ? blocked.Reason.ToString() : null,
                    }).ToArray(),
                    captures,
                });
                WriteReport(output, theme, configured, files.Count, reports, false);
            }
            WriteReport(output, theme, configured, files.Count, reports, true);
        }
        finally
        {
            Application.Current.Resources.MergedDictionaries.Clear();
            foreach (var resource in resources) Application.Current.Resources.MergedDictionaries.Add(resource);
        }
    }

    private static async Task ImagesReady(Window window, PreviewView view)
    {
        for (var attempt = 0; attempt < 300 && Harness.All<ImageView>(view).Any(image => image.State == ImageState.Loading); attempt++)
        {
            Harness.Pump(window);
            await Task.Delay(10);
        }
        await Task.WhenAll(Harness.All<ImageView>(view).Select(image => image.LoadingCompletion)).WaitAsync(TimeSpan.FromSeconds(10));
        Harness.Pump(window, 8);
        Assert.DoesNotContain(Harness.All<ImageView>(view), image => image.State == ImageState.Loading);
    }

    private static IEnumerable<BlockNode> Flatten(IEnumerable<BlockNode> blocks)
    {
        foreach (var block in blocks)
        {
            yield return block;
            var children = block switch
            {
                BlockQuoteNode quote => quote.Blocks,
                AlertNode alert => alert.Blocks,
                DetailsNode details => details.Blocks,
                ListNode list => list.Items.SelectMany(item => item.Blocks),
                FootnoteSectionNode footnotes => footnotes.Items.SelectMany(item => item.Blocks),
                DefinitionListNode definitions => definitions.Items.SelectMany(item => item.Definitions),
                _ => [],
            };
            foreach (var child in Flatten(children)) yield return child;
        }
    }

    private static IEnumerable<InlineNode> InlineNodes(BlockNode block) => block switch
    {
        HeadingNode heading => heading.Inlines,
        ParagraphNode paragraph => paragraph.Inlines,
        TableNode table => (table.Header is null ? table.Body : table.Body.Prepend(table.Header)).SelectMany(row => row.Cells).SelectMany(cell => cell.Inlines),
        DetailsNode details => details.Summary,
        DefinitionListNode definitions => definitions.Items.SelectMany(item => item.Terms).SelectMany(term => term),
        _ => [],
    };

    private static IEnumerable<InlineNode> FlattenInline(InlineNode node)
    {
        yield return node;
        var children = node switch
        {
            EmphasisNode emphasis => emphasis.Children,
            StrongNode strong => strong.Children,
            StrikethroughNode strike => strike.Children,
            StyledNode styled => styled.Children,
            LinkNode link => link.Children,
            _ => [],
        };
        foreach (var child in children.SelectMany(FlattenInline)) yield return child;
    }

    private static void WriteReport(string output, string theme, bool configured, int expectedFiles, List<object> reports, bool completed) =>
        File.WriteAllText(Path.Combine(output, $"results-{theme}.json"), JsonSerializer.Serialize(new
        {
            completed,
            theme,
            scope = configured ? "21 exact supplied originals" : "one explicit smoke fixture; not supplied-file validation",
            expectedFiles,
            processedFiles = reports.Count,
            qualification = "Native headless captures and capability report; unsupported diagrams/math and image fallback states remain explicit. Not online or native interaction/pixel-parity acceptance.",
            files = reports,
        }, ReportJson));
}
