using System.Text;
using System.Xml.Linq;
using Tern.Rendering.Preview;
using Tern.Rendering.Resources;

namespace Tern.Rendering.Tests;

public sealed class StandaloneSvgTests
{
    [Fact]
    public void Exact_local_circuit_assets_flatten_only_their_inert_styles()
    {
        foreach (var filename in new[] { "led-series.svg", "voltage-divider.svg" })
        {
            var original = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MarkdownSamples", "assets", filename));
            Assert.False(SafeSvgPolicy.IsSafe(original));
            Assert.True(SafeSvgPolicy.TryNormalize(original, out var normalized, out var name));
            Assert.True(SafeSvgPolicy.IsSafe(normalized));
            Assert.NotEqual("SVG image", name);
            var root = XDocument.Parse(Encoding.UTF8.GetString(normalized)).Root!;
            Assert.DoesNotContain(root.DescendantsAndSelf(), element => element.Name.LocalName == "style");
            Assert.DoesNotContain(root.DescendantsAndSelf().Attributes(), attribute => attribute.Name.LocalName == "class");
            Assert.All(root.Descendants().Where(element => element.Name.LocalName == "text"), text =>
            {
                Assert.Equal("Arial,sans-serif", text.Attribute("font-family")!.Value);
                Assert.Equal("20px", text.Attribute("font-size")!.Value);
            });
            Assert.Contains(root.Descendants().Where(element => element.Name.LocalName == "g"), group => group.Attribute("stroke")?.Value == "#152238" && group.Attribute("fill")?.Value == "none");
        }
    }

    [Fact]
    public void Class_specificity_and_later_rule_order_survive_flattening()
    {
        var data = Encoding.UTF8.GetBytes("<svg><style>.wire{fill:#0000ff}text{fill:#ff0000}.wire{fill:#00ff00}</style><text class='wire' fill='black'>Label</text></svg>");
        Assert.True(SafeSvgPolicy.TryNormalize(data, out var normalized, out _));
        var text = XDocument.Parse(Encoding.UTF8.GetString(normalized)).Descendants("text").Single();
        Assert.Equal("#00ff00", text.Attribute("fill")!.Value);
    }

    [Theory]
    [InlineData("@import url(https://example.com/theme.css);")]
    [InlineData("text:hover{fill:red}")]
    [InlineData("g text{fill:red}")]
    [InlineData(".wire{background-image:url(https://example.com/x)}")]
    [InlineData(".wire{fill:url(#local)}")]
    [InlineData(".wire{fill:expression(alert(1))}")]
    [InlineData("text{font-family:bad\\font}")]
    [InlineData(".wire{filter:blur(2px)}")]
    public void Unsupported_or_active_styles_are_rejected_instead_of_partially_applied(string css)
    {
        var data = Encoding.UTF8.GetBytes("<svg><style>" + css + "</style><text class='wire'>Label</text></svg>");
        Assert.False(SafeSvgPolicy.TryNormalize(data, out _, out _));
    }

    [Fact]
    public void Style_expansion_and_rule_count_are_bounded()
    {
        var data = Encoding.UTF8.GetBytes("<svg><style>.wire{font-family:" + new string('a', 500) + "}</style>"
            + string.Concat(Enumerable.Repeat("<text class='wire'>x</text>", 3000)) + "</svg>");
        Assert.True(data.Length < SafeSvgPolicy.MaxBytes);
        Assert.False(SafeSvgPolicy.TryNormalize(data, out _, out _));
        data = Encoding.UTF8.GetBytes("<svg><style>" + string.Concat(Enumerable.Range(0, 65).Select(i => $".c{i}{{fill:red}}")) + "</style><text>Label</text></svg>");
        Assert.False(SafeSvgPolicy.TryNormalize(data, out _, out _));
    }

    [Fact]
    public void Actual_pcb_document_preserves_both_embedded_schematics_as_safe_images()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MarkdownSamples", "pcb_circuit_diagram.md"));
        var document = PreviewBuilder.Build(source, new PreviewOptions());
        var images = PreviewWalker.All(document).OfType<ImageNode>().ToArray();
        Assert.Equal(2, images.Length);
        Assert.Equal("5 V LED indicator schematic", images[0].Alt);
        Assert.Equal("LED indicator PCB top view", images[1].Alt);
        foreach (var image in images)
        {
            var bytes = Assert.IsType<InlineImage>(image.Resolution);
            Assert.Equal("image/svg+xml", bytes.MediaType);
            Assert.True(SafeSvgPolicy.IsSafe(bytes.Data));
            Assert.Contains("<svg", source[image.Source.StartOffset..image.Source.EndOffset], StringComparison.Ordinal);
        }
        Assert.Single(PreviewWalker.All(document).OfType<DiagramNode>());
        Assert.Contains(PreviewWalker.All(document).OfType<HeadingNode>(), heading => heading.PlainText == "7. Turning the illustration into a PCB");
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<foreignObject><div>active HTML</div></foreignObject>")]
    [InlineData("<image href='https://example.com/tracker.svg'/>")]
    [InlineData("<rect width='10' height='10' onload='alert(1)'/>")]
    [InlineData("<rect width='10' height='10' fill='url(https://example.com/x)'/>")]
    public void Unsafe_standalone_svg_never_becomes_an_image(string content)
    {
        var svg = "<svg xmlns='http://www.w3.org/2000/svg'>" + content + "</svg>";
        Assert.False(SafeSvgPolicy.IsSafe(Encoding.UTF8.GetBytes(svg)));
        Assert.Empty(PreviewWalker.All(PreviewBuilder.Build(svg, new PreviewOptions())).OfType<ImageNode>());
    }

    [Fact]
    public void Image_budget_and_document_type_declarations_remain_rejected()
    {
        var oversized = "<svg><title>" + new string('x', SafeSvgPolicy.MaxBytes) + "</title></svg>";
        Assert.False(SafeSvgPolicy.IsSafe(Encoding.UTF8.GetBytes(oversized)));
        Assert.Empty(PreviewWalker.All(PreviewBuilder.Build(oversized, new PreviewOptions())).OfType<ImageNode>());
        Assert.False(SafeSvgPolicy.IsSafe(Encoding.UTF8.GetBytes("<!DOCTYPE svg [<!ENTITY x SYSTEM 'file:///private'>]><svg><title>&x;</title></svg>")));
        Assert.False(SafeSvgPolicy.IsSafe(Encoding.UTF8.GetBytes("<svg>" + string.Concat(Enumerable.Repeat("<circle r='1'/>", 10_001)) + "</svg>")));
    }

    [Fact]
    public void Generic_foreign_content_sanitizer_still_drops_unrecognized_svg_containers()
    {
        var document = PreviewBuilder.Build("<div><svg><rect width='10' height='10'/></svg></div>\n\n# After", new PreviewOptions());
        Assert.Empty(PreviewWalker.All(document).OfType<ImageNode>());
        Assert.Contains(PreviewWalker.All(document).OfType<HeadingNode>(), heading => heading.PlainText == "After");
    }
}
