using Tern.Workspaces;

namespace Tern.Workspaces.Tests;

public sealed class SaveFragmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tern-fragments-" + Guid.NewGuid().ToString("N"));

    public SaveFragmentTests() => Directory.CreateDirectory(Path.Combine(_root, "sub"));

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private string Plant(string relative, DateTime writtenUtc)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllText(path, "note text");
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }

    [Fact]
    public void Stale_backups_are_listed_and_kept_while_stale_temporary_files_are_removed_and_fresh_ones_ignored()
    {
        var now = DateTime.UtcNow;
        var guid = Guid.NewGuid().ToString("N");
        var backup = Plant($"sub/.x.md.{guid}.tern-save-backup", now.AddDays(-3));
        var staleTemp = Plant($"sub/.x.md.{guid}.tern-tmp", now.AddDays(-2));
        var atomicTemp = Plant($".y.json.{guid}.tmp", now.AddDays(-2));
        var fresh = Plant($".z.md.{guid}.tern-save-backup", now.AddHours(-1));
        var ordinary = Plant("notes.tmp", now.AddDays(-9));

        var scan = SaveFragmentScanner.Scan(_root, now, deleteStaleTemporaries: true);

        var listed = Assert.Single(scan.Fragments);
        Assert.Equal(backup, listed.Path);
        Assert.Equal(SaveFragmentKind.SaveBackup, listed.Kind);
        Assert.Equal(Path.Combine(_root, "sub", "x.md"), listed.OriginalPath);
        Assert.Equal(2, scan.DeletedTemporaryFiles);
        Assert.False(File.Exists(staleTemp));
        Assert.False(File.Exists(atomicTemp));
        Assert.True(File.Exists(backup));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(ordinary));
    }
}
