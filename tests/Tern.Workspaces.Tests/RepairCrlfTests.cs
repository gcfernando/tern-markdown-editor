using System.Text;
using Tern.Domain.Text;
using Tern.Filesystem;

namespace Tern.Workspaces.Tests;

public sealed class RepairCrlfTests
{
    [Fact]
    public async Task Edits_use_original_offsets_and_preserve_mixed_terminators()
    {
        using var directory = new TempDirectory();
        const string text = "first\nold\rthird\r\nold\n";
        var path = directory.Write("offsets.md", text);
        var edits = new[] { new TextEdit(6, 3, "new"), new TextEdit(17, 3, "last") };

        var (status, _) = await SafeEditApplier.ApplyAsync(new DocumentFileService(), path, text, edits);

        Assert.Equal(RepairStatus.Applied, status);
        Assert.Equal("first\nnew\rthird\r\nlast\n", await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("a\nb\n", 0)]
    [InlineData("a\rb\r", 1)]
    [InlineData("a\r\nb\nc\r", 2)]
    [InlineData("a\nb\n", 3)]
    [InlineData("a\rb\r", 4)]
    public async Task Actual_repair_preserves_terminators_encoding_and_bom(string text, int encodingCase)
    {
        using var directory = new TempDirectory();
        Encoding encoding = encodingCase switch
        {
            0 => new UTF8Encoding(false),
            1 => new UTF8Encoding(true),
            2 => new UnicodeEncoding(false, true),
            3 => new UnicodeEncoding(true, true),
            _ => new UTF32Encoding(false, true),
        };
        var path = directory.Write("repair.md", text, encoding);
        var files = new DocumentFileService();
        var before = await files.LoadAsync(path);
        var repair = new FileRepair(path, null, text, [TextEdit.Insert(0, "updated ")], []);
        var preview = repair.ResultText;

        var result = await new RepairPlan([repair], []).ApplyAsync(files);

        Assert.Equal(RepairStatus.Applied, Assert.Single(result.Files).Status);
        var expected = "updated " + text;
        Assert.Equal(encoding.GetPreamble().Concat(encoding.GetBytes(expected)), await File.ReadAllBytesAsync(path));
        var after = await files.LoadAsync(path);
        Assert.Equal(before.Format.Encoding, after.Format.Encoding);
        Assert.Equal(before.Format.HasBom, after.Format.HasBom);
        Assert.Equal(before.Format.DominantLineEnding, after.Format.DominantLineEnding);
        Assert.Equal(before.Format.HasMixedLineEndings, after.Format.HasMixedLineEndings);
        Assert.Equal(preview, repair.ResultText);
        Assert.Equal("updated " + text, preview);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Noop_and_conflict_leave_original_bytes_unchanged(bool conflict)
    {
        using var directory = new TempDirectory();
        const string text = "original\nsecond\r";
        var path = directory.Write("unchanged.md", text, new UnicodeEncoding(false, true));
        var bytes = await File.ReadAllBytesAsync(path);

        var (status, _) = await SafeEditApplier.ApplyAsync(new DocumentFileService(), path,
            conflict ? "stale\n" : text, conflict ? [TextEdit.Insert(0, "edit")] : [TextEdit.Insert(0, "")]);

        Assert.Equal(conflict ? RepairStatus.Conflict : RepairStatus.Skipped, status);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }
}
