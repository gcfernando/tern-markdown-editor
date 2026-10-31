using System.Globalization;
using System.Xml.Linq;

namespace Tern.Export.Tests;

public sealed class EntityDiagramExportTests
{
    private const string Entities = """
        CUSTOMER {
            int id PK "Unique identifier"
            string name UK "<script>keep this literal</script>"
        }
        ORDER {
            int id PK
            int customerId FK
        }
        ITEM {
            int id PK
        }
        CUSTOMER ||--o{ ORDER : places
        ORDER |o..|{ ITEM : includes
        """;

    private static string Markdown(string direction) => "```mermaid\nerDiagram\ndirection " + direction + "\n" + Entities + "\n```\n";

    [Theory]
    [InlineData("LR")]
    [InlineData("RL")]
    [InlineData("TD")]
    [InlineData("BT")]
    public void Html_retains_entity_compartments_and_distinct_cardinality_marks(string direction)
    {
        var markdown = Markdown(direction);
        var result = HtmlExporter.Export(markdown);
        StrictExportedHtml.AssertSafe(result.Html, markdown);
        Assert.Empty(result.Warnings);
        var start = result.Html.IndexOf("<svg ", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = result.Html.IndexOf("</svg>", start, StringComparison.Ordinal) + "</svg>".Length;
        var svg = XDocument.Parse(result.Html[start..end]);
        var elements = svg.Descendants().ToList();
        Assert.Equal("Entity relationship diagram", svg.Root!.Attribute("aria-label")!.Value);
        Assert.Equal(3, elements.Count(e => HasClass(e, "d-entity-divider")));
        var attributes = string.Join(' ', elements.Where(e => HasClass(e, "d-entity-attribute")).Select(e => e.Value));
        Assert.Contains("int id [PK]", attributes, StringComparison.Ordinal);
        Assert.Contains("int customerId [FK]", attributes, StringComparison.Ordinal);
        Assert.Contains("string name [UK]", attributes, StringComparison.Ordinal);
        Assert.Contains("keep this literal", attributes, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", result.Html, StringComparison.Ordinal);
        Assert.Contains(elements, e => e.Name.LocalName == "text" && e.Value == "places");
        Assert.Contains(elements, e => e.Name.LocalName == "text" && e.Value == "includes");

        // || is two bars, |o is one bar+circle, |{ is three prongs+bar,
        // and o{ is three prongs+circle. They must not collapse into ordinary crosses.
        var bars = elements.Where(e => HasClass(e, "d-er-bar")).ToList();
        Assert.Equal(4, bars.Count);
        Assert.Equal(6, elements.Count(e => HasClass(e, "d-er-prong")));
        var zeroes = elements.Where(e => HasClass(e, "d-er-zero")).ToList();
        Assert.Equal(2, zeroes.Count);
        Assert.All(zeroes, e =>
        {
            Assert.Equal(3.5, Number(e, "rx"));
            Assert.Equal(3.5, Number(e, "ry"));
        });
        Assert.All(bars, e => Assert.InRange(Math.Sqrt(Math.Pow(Number(e, "x2") - Number(e, "x1"), 2)
            + Math.Pow(Number(e, "y2") - Number(e, "y1"), 2)), 11.9, 12.1));
        Assert.Contains(elements, e => HasClass(e, "d-dotted"));
    }

    [Fact]
    public void Pdf_retains_entity_attribute_text_keys_comments_and_relationship_labels()
    {
        var result = PdfExporter.Export(Markdown("LR"), new PdfExportOptions());
        Assert.Empty(result.Warnings);
        Assert.True(result.PageCount > 0);
        var text = PdfText.AllText(result.Data);
        // The independent reader concatenates positioned DrawString runs without
        // reconstructing the visual line breaks introduced by diagram wrapping.
        var characters = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        foreach (var expected in new[] { "CUSTOMER", "ORDER", "ITEM", "int id [PK]", "int customerId [FK]", "string name [UK]", "Unique identifier", "keep this literal", "places", "includes" })
            Assert.Contains(string.Concat(expected.Where(c => !char.IsWhiteSpace(c))), characters, StringComparison.Ordinal);
    }

    private static bool HasClass(XElement element, string value) =>
        element.Attribute("class")?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(value, StringComparer.Ordinal) == true;

    private static double Number(XElement element, string attribute) =>
        double.Parse(element.Attribute(attribute)!.Value, CultureInfo.InvariantCulture);
}
