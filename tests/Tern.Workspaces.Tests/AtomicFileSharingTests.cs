using System.Text;
using Tern.Workspaces;

namespace Tern.Workspaces.Tests;

public sealed class AtomicFileSharingTests
{
    [Fact]
    public async Task Temporary_Windows_reader_does_not_prevent_atomic_metadata_save()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "tern-atomic-sharing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "state.json");
            File.WriteAllText(path, "old");
            var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var release = Task.Run(async () => { await Task.Delay(80); reader.Dispose(); });
            try { AtomicFile.WriteAllBytes(path, Encoding.UTF8.GetBytes("new")); }
            finally { await release; reader.Dispose(); }
            Assert.Equal("new", File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
