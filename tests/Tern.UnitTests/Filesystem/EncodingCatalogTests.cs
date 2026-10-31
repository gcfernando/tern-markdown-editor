using Tern.Domain.Text;
using Tern.Filesystem;

namespace Tern.UnitTests.Filesystem;

public sealed class EncodingCatalogTests
{
    [Fact]
    public void Catalog_offers_registered_codepages_and_unicode_bom_variants_without_utf7()
    {
        var choices = TextFileCodec.AvailableEncodings;
        Assert.True(choices.Count > 50);
        Assert.DoesNotContain(choices, c => c.CodePage == 65000);
        Assert.Contains(choices, c => c.CodePage == 932 && !c.HasBom);
        Assert.Contains(choices, c => c.CodePage == 1251 && !c.HasBom);
        foreach (var codePage in new[] { 65001, 1200, 1201, 12000, 12001 })
        {
            Assert.Contains(choices, c => c.CodePage == codePage && c.HasBom);
            Assert.Contains(choices, c => c.CodePage == codePage && !c.HasBom);
        }
        Assert.All(choices, choice => Assert.Contains(choice.CodePage.ToString(System.Globalization.CultureInfo.InvariantCulture), choice.Name, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(932, "日本語\r\n")]
    [InlineData(1251, "Привет\n")]
    [InlineData(1256, "مرحبا\r")]
    public void Additional_codepages_roundtrip_strictly_and_keep_explicit_identity(int codePage, string text)
    {
        var kind = TextEncodingIds.FromCodePage(codePage);
        Assert.Equal(codePage, TextEncodingIds.GetCodePage(kind));
        Assert.True(TextFileCodec.CanRepresent(text, kind));
        var format = LineEndingAnalyzer.Describe(kind, false, text);
        var bytes = TextFileCodec.Encode(text, format);
        var decoded = TextFileCodec.Decode(bytes, kind);
        Assert.True(decoded.IsLossless);
        Assert.Equal(text, decoded.Text);
        Assert.Equal(kind, decoded.Format.Encoding);
        Assert.False(TextFileCodec.CanRepresent("😀", kind));
    }
}
