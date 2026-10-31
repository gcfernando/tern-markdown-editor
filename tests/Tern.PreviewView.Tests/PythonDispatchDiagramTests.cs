using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;

namespace Tern.PreviewView.Tests;

public sealed class PythonDispatchDiagramTests
{
    private const string Markdown = """
        ```mermaid
        graph TD
            START["You write: a + b"] --> L["Python calls<br/>type#40;a#41;.__add__#40;a, b#41;"]
            L -->|returns a value| DONE["Result"]
            L -->|returns NotImplemented| R["Python calls<br/>type#40;b#41;.__radd__#40;b, a#41;"]
            R -->|returns a value| DONE
            R -->|returns NotImplemented| ERR["TypeError:<br/>unsupported operand type#40;s#41;"]
        ```
        """;

    [AvaloniaTheory]
    [InlineData(700, false)]
    [InlineData(1000, false)]
    [InlineData(700, true)]
    [InlineData(1000, true)]
    public void Exact_reported_operator_dispatch_diagram_renders_all_nodes_at_readable_size(int width, bool paper)
    {
        if (Environment.GetEnvironmentVariable("TERN_PARITY_CORPUS") is { } corpus)
            Assert.Contains(Markdown, File.ReadAllText(Path.Combine(corpus, "python_dunder.md")).Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        var resources = Application.Current!.Resources.MergedDictionaries.ToArray();
        Application.Current.Resources.MergedDictionaries.Clear();
        Application.Current.Resources.MergedDictionaries.Add(paper ? PreviewTheme.Paper() : PreviewTheme.Midnight());
        try
        {
            var (window, view) = Harness.Show(Harness.Build(Markdown), width, 1200);
            try
            {
                var diagram = Assert.Single(Harness.All<DiagramControl>(view));
                Assert.DoesNotContain("Times", diagram.TypefaceFamilyName, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(5, diagram.ElementCount);
                Assert.Empty(Harness.All<CodeBlockView>(view));
                Assert.InRange(diagram.Bounds.Height, 300, 1150);
                using var frame = Harness.Capture(window);
                Assert.NotNull(frame);
                Assert.True(Harness.NonBackgroundPixels(frame, Harness.RegionOf(diagram, window)) > 500);
                if (Environment.GetEnvironmentVariable("TERN_PARITY_FLOW_SCREENSHOTS") is { } output)
                {
                    Directory.CreateDirectory(output);
                    File.WriteAllText(Path.Combine(output, "resolved-font.txt"), diagram.TypefaceFamilyName);
                    using var file = File.Create(Path.Combine(output, $"python-dispatch-{(paper ? "paper" : "midnight")}-{width}.png"));
                    frame.Save(file, new PngBitmapEncoderOptions());
                }
            }
            finally { window.Close(); }
        }
        finally
        {
            Application.Current.Resources.MergedDictionaries.Clear();
            foreach (var resource in resources) Application.Current.Resources.MergedDictionaries.Add(resource);
        }
    }
}
