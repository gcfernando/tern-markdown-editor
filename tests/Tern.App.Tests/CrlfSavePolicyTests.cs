using Tern.App.Commands;
using Tern.App.Documents;
using Tern.Domain.Text;
using Tern.Filesystem;
using Tern.Markdown;

namespace Tern.App.Tests;

public sealed class CrlfSavePolicyTests
{
    [Theory]
    [InlineData(TextEncodingKind.Utf8, false)]
    [InlineData(TextEncodingKind.Utf8, true)]
    [InlineData(TextEncodingKind.Utf16LittleEndian, true)]
    [InlineData(TextEncodingKind.Utf16BigEndian, true)]
    [InlineData(TextEncodingKind.Windows1252, false)]
    public async Task Opening_and_saving_preserve_original_terminators_encoding_and_BOM(TextEncodingKind encoding, bool bom)
    {
        using var directory = new TempDir();
        var path = directory.File("mixed.md");
        const string original = "Café\nsecond\rthird\r\nfourth\n";
        const string saved = original;
        var format = LineEndingAnalyzer.Describe(encoding, bom, original);
        var originalBytes = TextFileCodec.Encode(original, format);
        await File.WriteAllBytesAsync(path, originalBytes);
        using var tab = await DocumentTab.OpenAsync(path, new DocumentFileService(), MarkdownProfile.Gfm, encoding);
        Assert.Equal(original, tab.CurrentText);
        Assert.False(tab.IsDirty);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
        Assert.IsType<SaveSucceeded>(await tab.SaveAsync());
        Assert.Equal(saved, tab.CurrentText);
        Assert.Equal(TextFileCodec.Encode(saved, format with { DominantLineEnding = LineEnding.CrLf, HasMixedLineEndings = false }), await File.ReadAllBytesAsync(path));
        Assert.Equal(encoding, tab.Session.Format.Encoding);
        Assert.Equal(bom, tab.Session.Format.HasBom);
        Assert.Equal("LF (mixed)", tab.LineEndingText);
        Assert.False(tab.IsDirty);
        Assert.Equal(saved, tab.MergeBase);
    }

    [Fact]
    public async Task Save_all_skips_clean_and_readonly_files_and_saves_empty_drafts()
    {
        using var fixture = new ShellFixture();
        foreach (var (name, text) in new[] { ("lf.md", "a\nb\n"), ("cr.md", "a\rb\r"), ("mixed.md", "a\r\nb\n") })
        {
            var tab = (await fixture.Shell.OpenFileAsync(await fixture.WriteAsync(name, text)))!;
            Assert.False(tab.IsDirty);
            Assert.False(DocumentCommandPolicy.CanExecute(CommandIds.Save, fixture.Shell.GetCommandContext()));
        }
        var clean = (await fixture.Shell.OpenFileAsync(await fixture.WriteAsync("clean.md", "a\r\nb\r\n")))!;
        Assert.False(DocumentCommandPolicy.CanExecute(CommandIds.Save, fixture.Shell.GetCommandContext()));
        var path = Path.Combine(fixture.Docs, "readonly.md");
        await File.WriteAllBytesAsync(path, [0xFF]);
        await fixture.Shell.OpenFileAsync(path);
        var empty = fixture.Shell.NewDocument();
        Assert.Equal("CRLF", empty.LineEndingText);
        Assert.True(DocumentCommandPolicy.CanExecute(CommandIds.SaveAll, fixture.Shell.GetCommandContext()));
        fixture.Dialogs.SavePath = Path.Combine(fixture.Docs, "empty.md");
        Assert.Equal(1, await fixture.Shell.SaveAllAsync());
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(fixture.Dialogs.SavePath));
        Assert.Equal(0, await fixture.Shell.SaveAllAsync());
        Assert.False(DocumentCommandPolicy.CanExecute(CommandIds.SaveAll, fixture.Shell.GetCommandContext()));
        foreach (var (name, text) in new[] { ("lf.md", "a\nb\n"), ("cr.md", "a\rb\r"), ("mixed.md", "a\r\nb\n"), ("clean.md", "a\r\nb\r\n") })
            Assert.Equal(text, await File.ReadAllTextAsync(Path.Combine(fixture.Docs, name)));
        Assert.Equal(new byte[] { 0xFF }, await File.ReadAllBytesAsync(path));
        Assert.False(clean.IsDirty);
        Assert.Equal(0, fixture.Dialogs.SavePrompts);
    }

    [Fact]
    public async Task Conflict_does_not_normalize_the_live_buffer_or_overwrite_disk_until_keep_mine_is_accepted()
    {
        using var directory = new TempDir();
        var path = directory.File("conflict.md");
        await File.WriteAllTextAsync(path, "original\n");
        using var tab = await DocumentTab.OpenAsync(path, new DocumentFileService(), MarkdownProfile.Gfm);
        tab.ApplyEdits([TextEdit.Insert(0, "mine\n")]);
        var revision = tab.Session.Revision;
        await File.WriteAllTextAsync(path, "other\n");
        Assert.IsType<SaveConflict>(await tab.SaveAsync());
        Assert.Equal(revision, tab.Session.Revision);
        Assert.Equal("mine\noriginal\n", tab.CurrentText);
        Assert.Equal("other\n", await File.ReadAllTextAsync(path));
        Assert.IsType<SaveSucceeded>(await tab.KeepMineAndOverwriteAsync());
        Assert.Equal("mine\noriginal\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Newer_typing_during_write_is_not_replaced_or_marked_saved()
    {
        using var directory = new TempDir();
        var path = directory.File("race.md");
        await File.WriteAllTextAsync(path, "one\n");
        var files = new DocumentFileService();
        using var tab = await DocumentTab.OpenAsync(path, files, MarkdownProfile.Gfm);
        tab.ApplyEdits([TextEdit.Insert(0, "saved ")]);
        files.BeforeReplace = _ => tab.ApplyEdits([TextEdit.Insert(0, "new typing\n")]);
        Assert.IsType<SaveSucceeded>(await tab.SaveAsync());
        Assert.Equal("saved one\n", await File.ReadAllTextAsync(path));
        Assert.Equal("new typing\nsaved one\n", tab.CurrentText);
        Assert.True(tab.IsDirty);
        Assert.Equal("saved one\n", tab.MergeBase);
        files.BeforeReplace = null;
        Assert.IsType<SaveSucceeded>(await tab.SaveAsync());
        Assert.Equal("new typing\nsaved one\n", await File.ReadAllTextAsync(path));
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public async Task Save_as_and_copy_preserve_selected_ending_and_copy_preserves_source_identity_buffer_and_revision()
    {
        using var directory = new TempDir();
        var files = new DocumentFileService();
        using var tab = DocumentTab.CreateUntitled(files, MarkdownProfile.Gfm, "first\nsecond\r");
        Assert.Equal(LineEnding.Lf, tab.Session.Format.DominantLineEnding);
        Assert.True(tab.Session.Format.HasMixedLineEndings);
        Assert.Equal("first\nsecond\r", tab.CurrentText); // Seeded source is not rewritten before Save.
        var path = directory.File("saved.md");
        tab.ConvertLineEndings(LineEnding.Cr);
        Assert.Equal("first\rsecond\r", tab.CurrentText);
        Assert.IsType<SaveSucceeded>(await tab.SaveAsAsync(path, false));
        Assert.Equal("CR", tab.LineEndingText);
        Assert.Equal("first\rsecond\r", await File.ReadAllTextAsync(path));
        tab.ApplyEdits([TextEdit.Insert(0, "later\r")]);
        var revision = tab.Session.Revision;
        var text = tab.CurrentText;
        var copy = directory.File("copy.md");
        Assert.IsType<SaveSucceeded>(await tab.SaveCopyAsync(copy, false));
        Assert.Equal("later\rfirst\rsecond\r", await File.ReadAllTextAsync(copy));
        Assert.Equal(text, tab.CurrentText);
        Assert.Equal(revision, tab.Session.Revision);
        Assert.Equal(path, tab.FilePath);
        Assert.True(tab.IsDirty);
        Assert.Equal("first\rsecond\r", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Already_canceled_save_does_not_apply_save_options_or_line_conversion()
    {
        using var directory = new TempDir();
        var path = directory.File("cancel.md");
        await File.WriteAllTextAsync(path, "text  \nlast\t");
        using var tab = await DocumentTab.OpenAsync(path, new DocumentFileService(), MarkdownProfile.Gfm);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tab.SaveAsync(new TabSaveOptions(true, true), cancel.Token));
        Assert.Equal("text  \nlast\t", tab.CurrentText);
        Assert.Equal("text  \nlast\t", await File.ReadAllTextAsync(path));
        Assert.False(tab.IsDirty);
    }
}
