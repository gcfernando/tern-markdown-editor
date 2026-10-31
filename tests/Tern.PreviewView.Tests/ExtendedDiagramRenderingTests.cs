using Avalonia.Headless.XUnit;

namespace Tern.PreviewView.Tests;

public sealed class ExtendedDiagramRenderingTests
{
    [AvaloniaTheory]
    [InlineData("stateDiagram-v2\n[*] --> Ready\nReady --> [*]", 3)]
    [InlineData("classDiagram\nclass Account {\n+balance() decimal\n}\nAccount *-- Entry", 2)]
    [InlineData("mindmap\n root((Study))\n   Notes\n   Sources", 3)]
    [InlineData("pie showData\n\"Ready\" : 3\n\"Pending\" : 1", 2)]
    [InlineData("pie title Pets\n\"Dogs\" : 5", 1)]
    [InlineData("gantt\ndateFormat YYYY-MM-DD\nDraft :2027-03-01, 2d", 1)]
    public void New_diagrams_use_native_graphics_with_visible_bounds(string source, int elements)
    {
        var (window, view) = Harness.Show(Harness.Build("```mermaid\n" + source + "\n```"));
        try
        {
            var diagram = Assert.Single(Harness.All<DiagramControl>(view));
            Assert.Equal(elements, diagram.ElementCount);
            Assert.True(diagram.DiagramWidth > 20 && diagram.DiagramHeight > 20);
            Assert.True(diagram.Bounds.Width > 20 && diagram.Bounds.Height > 20);
            Assert.Empty(Harness.All<CodeBlockView>(view));
        }
        finally { window.Close(); }
    }
}
