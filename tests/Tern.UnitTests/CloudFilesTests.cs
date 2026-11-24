using Tern.Filesystem;

namespace Tern.UnitTests;

public sealed class CloudFilesTests
{
    [Theory]
    [InlineData(0x1000, true)]
    [InlineData(0x400000, true)]
    [InlineData(0x400400, true)]
    [InlineData(0x400, false)]
    [InlineData(0x40000, false)]
    [InlineData(0x100000, false)]
    public void Availability_uses_offline_or_recall_data_not_reparse_unpinned_or_extended_attributes(int attributes, bool expected) =>
        Assert.Equal(expected, CloudFiles.IsOnlineOnly((FileAttributes)attributes));

    [Fact]
    public void Cloud_tags_are_allowed_but_links_junctions_and_unknown_reparse_tags_are_not()
    {
        for (uint variant = 0; variant < 16; variant++)
            Assert.False(CloudFiles.IsUnsafeReparsePoint(FileAttributes.ReparsePoint, () => 0x9000001Au | variant << 12));
        foreach (var tag in new uint[] { 0xA000000C, 0xA0000003, 0x8000001B, 0x9001001A, 0 })
            Assert.True(CloudFiles.IsUnsafeReparsePoint(FileAttributes.ReparsePoint | FileAttributes.Offline, () => tag));
        Assert.False(CloudFiles.IsUnsafeReparsePoint(FileAttributes.Normal, () => throw new InvalidOperationException()));
    }

    [Fact]
    public void Native_metadata_check_rejects_an_actual_symbolic_link()
    {
        var root = Path.Combine(Path.GetTempPath(), "tern-cloud-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "target.md");
        var link = Path.Combine(root, "link.md");
        try
        {
            File.WriteAllText(target, "synthetic");
            File.CreateSymbolicLink(link, target);
            Assert.True(CloudFiles.IsUnsafeReparsePoint(new FileInfo(link)));
            Assert.False(CloudFiles.IsUnsafeReparsePoint(new FileInfo(target)));
        }
        finally
        {
            File.Delete(link);
            File.Delete(target);
            Directory.Delete(root);
        }
    }
}
