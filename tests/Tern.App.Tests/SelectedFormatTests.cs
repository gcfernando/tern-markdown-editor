using Tern.App.Documents;
using Tern.Domain.Text;
using Tern.Filesystem;
using Tern.Markdown;

namespace Tern.App.Tests;

public sealed class SelectedFormatTests
{
    [Theory]
    [InlineData(LineEnding.Lf, "\n")]
    [InlineData(LineEnding.CrLf, "\r\n")]
    [InlineData(LineEnding.Cr, "\r")]
    public async Task Selected_ending_converts_immediately_and_survives_save_and_reopen(LineEnding ending, string terminator)
    {
        using var directory = new TempDir();
        var files = new DocumentFileService();
        using var tab = DocumentTab.CreateUntitled(files, MarkdownProfile.Gfm, "first\nsecond\rthird\r\n");
        tab.ConvertLineEndings(ending);
        var expected = $"first{terminator}second{terminator}third{terminator}";
        Assert.Equal(expected, tab.CurrentText);
        Assert.Equal(ending, tab.Session.Format.DominantLineEnding);
        Assert.False(tab.Session.Format.HasMixedLineEndings);
        var path = directory.File("selected.md");
        Assert.IsType<SaveSucceeded>(await tab.SaveAsAsync(path, false));
        Assert.Equal(expected, await File.ReadAllTextAsync(path));
        using var reopened = await DocumentTab.OpenAsync(path, files, MarkdownProfile.Gfm);
        Assert.Equal(expected, reopened.CurrentText);
        Assert.Equal(ending, reopened.Session.Format.DominantLineEnding);
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public async Task Invalid_reopen_keeps_original_text_identity_and_format()
    {
        using var directory = new TempDir();
        var path = directory.File("encoding.md");
        var kind = TextEncodingIds.FromCodePage(932);
        var format = LineEndingAnalyzer.Describe(kind, false, "日本語\n");
        await File.WriteAllBytesAsync(path, TextFileCodec.Encode("日本語\n", format));
        using var tab = await DocumentTab.OpenAsync(path, new DocumentFileService(), MarkdownProfile.Gfm, kind);
        var revision = tab.Session.Revision;
        Assert.False(await tab.ReopenWithEncodingAsync(TextEncodingKind.Utf8));
        Assert.Equal("日本語\n", tab.CurrentText);
        Assert.Equal(kind, tab.Session.Format.Encoding);
        Assert.Equal(revision, tab.Session.Revision);
        Assert.False(tab.Session.IsReadOnly);
        Assert.NotNull(tab.LastSaveError);
        Assert.False(tab.TryConvertEncoding(TextEncodingKind.Windows1252, false));
        Assert.Equal(kind, tab.Session.Format.Encoding);
    }
}
