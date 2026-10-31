using Avalonia;
using Avalonia.Headless.XUnit;

namespace Tern.PreviewView.Tests;

public sealed class MermaidNativeViewTests
{
    [AvaloniaFact]
    public void Entity_relationships_and_attribute_compartments_render_in_both_themes()
    {
        const string markdown = """
            ```mermaid
            erDiagram
                CUSTOMER {
                    int id PK "Primary identifier"
                    string email UK
                }
                ORDER {
                    int customer_id FK
                }
                CUSTOMER ||--o{ ORDER : places
            ```
            """;
        try
        {
            foreach (var theme in new[] { PreviewTheme.Midnight(), PreviewTheme.Paper() })
            {
                Application.Current!.Resources.MergedDictionaries.Clear();
                Application.Current.Resources.MergedDictionaries.Add(theme);
                var (window, view) = Harness.Show(Harness.Build(markdown), 800, 700);
                try
                {
                    var diagram = Assert.Single(Harness.All<DiagramControl>(view));
                    Assert.True(diagram.IsEntityRelationship);
                    Assert.Equal(2, diagram.ElementCount);
                    Assert.Empty(Harness.All<CodeBlockView>(view));
                    Assert.InRange(diagram.Bounds.Height, 150, 650);
                    using var frame = Harness.Capture(window);
                    Assert.NotNull(frame);
                    Assert.True(Harness.NonBackgroundPixels(frame, Harness.RegionOf(diagram, window)) > 500);
                }
                finally
                {
                    window.Close();
                }
            }
        }
        finally
        {
            Application.Current!.Resources.MergedDictionaries.Clear();
            Application.Current.Resources.MergedDictionaries.Add(PreviewTheme.Midnight());
        }
    }
}
