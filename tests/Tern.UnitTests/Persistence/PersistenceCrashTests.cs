using System.ComponentModel;
using System.Diagnostics;
using Tern.Recovery;
using Tern.UnitTests.Filesystem;

namespace Tern.UnitTests.Persistence;

/// <summary>Process termination at acknowledged real persistence boundaries, not a power-loss simulation.</summary>
public sealed class PersistenceCrashTests
{
    [Theory]
    [InlineData("temp-written-before-flush")]
    [InlineData("flushed-before-replace")]
    [InlineData("replaced-before-verify")]
    public async Task Terminating_a_save_process_never_leaves_a_partial_backing_file(string phase)
    {
        using var directory = new TempDirectory();
        var path = directory.File("source.md");
        await File.WriteAllTextAsync(path, "previous saved revision\r\n");

        await TerminateAtPhaseAsync("save", phase, directory.Path, Guid.NewGuid());

        if (phase == "replaced-before-verify")
        {
            Assert.Equal("new saved revision\n", await File.ReadAllTextAsync(path));
            var backup = Assert.Single(Directory.GetFiles(directory.Path, "*.tern-save-backup"));
            Assert.Equal("previous saved revision\r\n", await File.ReadAllTextAsync(backup));
        }
        else
        {
            Assert.Equal("previous saved revision\r\n", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.GetFiles(directory.Path, "*.tern-save-backup"));
        }
    }

    [Theory]
    [InlineData("payload-written-before-flush")]
    [InlineData("flushed-before-publish")]
    [InlineData("published-before-prune")]
    public async Task Terminating_a_checkpoint_process_preserves_a_valid_confirmed_generation(string phase)
    {
        using var directory = new TempDirectory();
        var id = Guid.NewGuid();
        var store = new RecoveryStore(directory.Path);
        await store.WriteCheckpointAsync(PersistenceFailureTests.Checkpoint(id, "older checkpoint", 1));
        await store.WriteCheckpointAsync(PersistenceFailureTests.Checkpoint(id, "previous confirmed checkpoint", 2));

        await TerminateAtPhaseAsync("recovery", phase, directory.Path, id);

        var scan = store.Scan();
        Assert.Empty(scan.Corrupt);
        var recovered = Assert.Single(scan.Entries);
        Assert.Equal(phase == "published-before-prune" ? "new durable checkpoint" : "previous confirmed checkpoint", recovered.Record.Text);
        Assert.Equal(phase == "published-before-prune" ? 3 : 2, recovered.Record.Revision);
        // Pruning was not reached. Neither previously confirmed generation may disappear.
        Assert.True(File.Exists(Path.Combine(directory.Path, $"{id:N}.00000001.tern-recovery")));
        Assert.True(File.Exists(Path.Combine(directory.Path, $"{id:N}.00000002.tern-recovery")));
    }

    private static async Task TerminateAtPhaseAsync(string mode, string phase, string root, Guid sessionId)
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("TERN_TEST_PWSH") ?? "pwsh",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root,
        };
        start.Environment["TEMP"] = root;
        start.Environment["TMP"] = root;
        foreach (var argument in new[]
        {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-File", Path.Combine(AppContext.BaseDirectory, "Fixtures", "PersistenceCrashHost.ps1"),
            "-Mode", mode, "-Phase", phase, "-FixtureRoot", root, "-AssemblyDirectory", AppContext.BaseDirectory,
            "-SessionId", sessionId.ToString(),
        }) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        var started = false;
        try
        {
            try
            {
                started = process.Start();
                Assert.True(started);
            }
            catch (Win32Exception ex)
            {
                throw new Xunit.Sdk.XunitException("UNMET prerequisite: PowerShell 7 is required for the isolated process-crash fixture. Set TERN_TEST_PWSH to its executable. " + ex.Message);
            }
            var errors = process.StandardError.ReadToEndAsync();
            var reached = false;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (await process.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
            {
                if (line != "TERN-PERSISTENCE-PHASE:" + phase) continue;
                reached = true;
                break;
            }
            Assert.True(reached, "The child did not acknowledge the actual persistence boundary. " + (process.HasExited ? await errors : string.Empty));
            // Terminate only the Process instance created above after its acknowledged boundary.
            process.Kill();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(process.HasExited);
        }
        finally
        {
            if (started && !process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }
}
