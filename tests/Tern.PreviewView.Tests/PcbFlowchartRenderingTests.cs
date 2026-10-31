using System.Text.RegularExpressions;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Tern.Rendering.Mermaid;

namespace Tern.PreviewView.Tests;

public sealed class PcbFlowchartRenderingTests
{
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Supplied_diagrams_render_natively_with_real_font_measurements(bool paper)
    {
        var resources = Application.Current!.Resources.MergedDictionaries.ToArray();
        Application.Current.Resources.MergedDictionaries.Clear();
        Application.Current.Resources.MergedDictionaries.Add(paper ? PreviewTheme.Paper() : PreviewTheme.Midnight());
        try
        {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MarkdownSamples", "complex_pcb_circuit_network_design.md"));
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/diagram-upgrade/screenshots"));
        Directory.CreateDirectory(output);
        var index = 0;
        foreach (Match match in Regex.Matches(text, @"```mermaid\s*\r?\n(.*?)```", RegexOptions.Singleline))
        {
            Assert.True(MermaidDiagram.TryParse(match.Groups[1].Value, out var parsed), parsed.ErrorMessage);
            var diagram = new DiagramControl(parsed);
            diagram.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var window = new Window { Width = Math.Ceiling(diagram.DesiredSize.Width) + 8, Height = Math.Ceiling(diagram.DesiredSize.Height) + 8,
                Content = diagram, Background = PreviewTheme.Resolve(diagram, PreviewKeys.Background) };
            try
            {
                window.Show(); Harness.Pump(window);
                Assert.True(diagram.ElementCount > 0);
                if (typeof(DiagramControl).GetField("_flow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(diagram) is FlowchartLayout layout)
                {
                    foreach (var edge in layout.Edges)
                    {
                        if (edge.LabelBounds is not { } label) continue;
                        Assert.All(layout.Nodes, node => Assert.False(label.Overlaps(node.Bounds), edge.Edge.Label));
                        foreach (var other in layout.Edges.Where(other => !ReferenceEquals(edge, other)))
                        {
                            if (other.LabelBounds is { } otherLabel) Assert.False(label.Overlaps(otherLabel), edge.Edge.Label);
                            for (var i = 1; i < other.Points.Count; i++)
                            {
                                var a = other.Points[i - 1]; var b = other.Points[i];
                                var intersects = Math.Abs(a.X - b.X) < 0.001
                                    ? a.X > label.X && a.X < label.Right && Math.Max(a.Y, b.Y) > label.Y && Math.Min(a.Y, b.Y) < label.Bottom
                                    : a.Y > label.Y && a.Y < label.Bottom && Math.Max(a.X, b.X) > label.X && Math.Min(a.X, b.X) < label.Right;
                                Assert.False(intersects, $"{edge.Edge.Label} masks {other.Edge.From}->{other.Edge.To}");
                            }
                        }
                    }
                }
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                using var file = File.Create(Path.Combine(output, $"pcb-{(paper ? "paper" : "midnight")}-{++index:D2}.png"));
                frame.Save(file, new PngBitmapEncoderOptions());
            }
            finally { window.Close(); }
        }
        Assert.Equal(10, index);
        }
        finally
        {
            Application.Current.Resources.MergedDictionaries.Clear();
            foreach (var resource in resources) Application.Current.Resources.MergedDictionaries.Add(resource);
        }
    }
}
