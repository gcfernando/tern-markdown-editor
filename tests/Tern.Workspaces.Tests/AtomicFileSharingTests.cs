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
            using var releaseStarted = new ManualResetEventSlim();
            // Save retries block their caller. Release the simulated external reader
            // independently of the test runner's potentially saturated thread pool.
            var release = Task.Factory.StartNew(() =>
            {
                releaseStarted.Set();
                Thread.Sleep(80);
                reader.Dispose();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            try
            {
                Assert.True(releaseStarted.Wait(TimeSpan.FromSeconds(10)));
                AtomicFile.WriteAllBytes(path, Encoding.UTF8.GetBytes("new"));
            }
            finally { await release; reader.Dispose(); }
            Assert.Equal("new", File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Persistent_Windows_reader_preserves_original_metadata_and_removes_temp_file()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "tern-atomic-sharing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "state.json");
            File.WriteAllText(path, "old");
            using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.Throws<IOException>(() => AtomicFile.WriteAllBytes(path, Encoding.UTF8.GetBytes("new")));
            }
            Assert.Equal("old", File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
