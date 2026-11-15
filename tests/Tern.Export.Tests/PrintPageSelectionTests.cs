using PdfSharp.Pdf.IO;
using Tern.Export;

namespace Tern.Export.Tests;

public sealed class PrintPageSelectionTests
{
    [Theory]
    [InlineData("1-3, 5", new[] { 1, 2, 3, 5 })]
    [InlineData("5, 2-3, 2", new[] { 2, 3, 5 })]
    [InlineData("8", new[] { 8 })]
    [InlineData("1-3,5", new[] { 1, 2, 3, 5 })]
    [InlineData("8,1,3-5,2,4-6", new[] { 1, 2, 3, 4, 5, 6, 8 })]
    [InlineData("1–3, 5—6,8", new[] { 1, 2, 3, 5, 6, 8 })]
    [InlineData(" 1 - 3 , 5, 7 - 8 ", new[] { 1, 2, 3, 5, 7, 8 })]
    [InlineData("1,2,3,4,5,6,7,8", new[] { 1, 2, 3, 4, 5, 6, 7, 8 })]
    public void Ranges_refer_to_original_pages_and_are_sorted_without_duplicates(string input, int[] expected)
    {
        Assert.True(PrintPageSelection.TryParse(input, 8, out var pages, out var error), error);
        Assert.Equal(expected, pages);
    }
    [Theory]
    [InlineData("")][InlineData("0")][InlineData("1-3, 9")][InlineData("3-1")][InlineData("1,")][InlineData("-1")][InlineData("1--3")][InlineData("1.5")][InlineData("2147483648")]
    public void Invalid_ranges_do_not_fall_back_to_all(string input)
    {
        Assert.False(PrintPageSelection.TryParse(input, 8, out var pages, out var error));
        Assert.Empty(pages); Assert.NotNull(error);
    }
    [Fact]
    public void Extreme_numbers_do_not_overflow_or_expand_unbounded_ranges()
    {
        Assert.True(PrintPageSelection.TryParse("2147483646-2147483647", int.MaxValue, out var pages, out var error), error);
        Assert.Equal(new[] { int.MaxValue - 1, int.MaxValue }, pages);
        Assert.False(PrintPageSelection.TryParse("1-2147483647", int.MaxValue, out pages, out error));
        Assert.Empty(pages); Assert.NotNull(error);
    }
    [Fact]
    public void Selected_output_contains_only_selected_original_pages()
    {
        var source = PdfExporter.Export(string.Join("\n\n", Enumerable.Range(1, 300).Select(n => "## Section " + n + "\n\nParagraph " + n)));
        Assert.True(source.PageCount > 5);
        var selected = PrintPageSelection.Extract(source.Data, [1, 3, 5]);
        using var original = PdfReader.Open(new MemoryStream(source.Data), PdfDocumentOpenMode.Import);
        using var subset = PdfReader.Open(new MemoryStream(selected), PdfDocumentOpenMode.Import);
        Assert.Equal(3, subset.PageCount);
        for (var i = 0; i < 3; i++) Assert.Equal(original.Pages[i * 2].Contents.Elements.Count, subset.Pages[i].Contents.Elements.Count);
    }
}
