namespace Tern.Filesystem.Tests;

public sealed class FileContentStampTests
{
    [Fact]
    public void Kernel_change_time_detects_same_length_edits_with_restored_last_write_time()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var folder = new StampFolder();
        var path = Path.Combine(folder.Path, "source.md");
        File.WriteAllText(path, "before");
        var written = File.GetLastWriteTimeUtc(path);
        var before = FileIdentity.TryGetContentStamp(path);
        Assert.NotNull(before);
        File.WriteAllText(path, "after!");
        File.SetLastWriteTimeUtc(path, written);
        var after = FileIdentity.TryGetContentStamp(path);
        Assert.NotNull(after);
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        Assert.Equal(before.FileId, after.FileId);
        Assert.Equal(before.Length, after.Length);
        Assert.NotEqual(before.ChangeTime, after.ChangeTime);
    }

    [Fact]
    public void File_identity_detects_atomic_replacement_even_when_length_and_last_write_time_match()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var folder = new StampFolder();
        var path = Path.Combine(folder.Path, "source.md");
        var replacement = Path.Combine(folder.Path, "replacement.md");
        File.WriteAllText(path, "before");
        var written = File.GetLastWriteTimeUtc(path);
        var before = FileIdentity.TryGetContentStamp(path);
        Assert.NotNull(before);
        File.WriteAllText(replacement, "after!");
        File.SetLastWriteTimeUtc(replacement, written);
        File.Move(replacement, path, overwrite: true);
        var after = FileIdentity.TryGetContentStamp(path);
        Assert.NotNull(after);
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        Assert.Equal(before.Length, after.Length);
        Assert.NotEqual(before.FileId, after.FileId);
    }

    private sealed class StampFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tern-stamp-" + Guid.NewGuid().ToString("N"));
        public StampFolder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
