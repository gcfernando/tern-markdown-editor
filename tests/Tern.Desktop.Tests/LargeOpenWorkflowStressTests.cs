using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace Tern.Desktop.Tests;

public sealed class LargeOpenWorkflowStressTests
{
    [AvaloniaFact]
    public async Task Opening_ten_thousand_files_shows_the_tree_early_and_keeps_the_window_responsive()
    {
        using var f = new DesktopFixture();
        var body = "# Note\n\n" + string.Concat(Enumerable.Repeat("Synthetic workspace text for indexing.\n", 12));
        await Task.Run(() => Parallel.For(0, 100, folder =>
        {
            var directory = Path.Combine(f.Docs, $"folder-{folder:D3}");
            Directory.CreateDirectory(directory);
            for (var file = 0; file < 100; file++)
                File.WriteAllText(Path.Combine(directory, $"note-{file:D3}.md"), body + $"[next](note-{(file + 1) % 100:D3}.md)\n");
        }));

        var running = true;
        var longestStall = TimeSpan.Zero;
        var heartbeat = Task.Run(async () =>
        {
            while (Volatile.Read(ref running))
            {
                var turn = Stopwatch.StartNew();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Input);
                if (turn.Elapsed > longestStall) longestStall = turn.Elapsed;
                await Task.Delay(10);
            }
        });

        var clock = Stopwatch.StartNew();
        var treeAt = TimeSpan.Zero;
        var preparingAtTree = false;
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs, treeReady: () =>
        {
            treeAt = clock.Elapsed;
            preparingAtTree = f.Shell.Workspace!.IsPreparingContent;
        }));
        var indexedAt = clock.Elapsed;
        Volatile.Write(ref running, false);
        await heartbeat;

        var findClock = Stopwatch.StartNew();
        var found = await new Tern.App.Knowledge.KnowledgeRetrieval().SearchAsync(f.Shell.Workspace!, "What does the synthetic workspace text say about indexing?", new Tern.App.Knowledge.KnowledgeLimits());
        var findMs = findClock.Elapsed.TotalMilliseconds;
        Assert.NotEmpty(found.Sources);
        Assert.True(findMs < 1000, $"Finding notes took {findMs:F0} ms.");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "large-open-metrics.txt"),
            $"files=10000 findNotesMs={findMs:F0} treeMs={treeAt.TotalMilliseconds:F0} indexedMs={indexedAt.TotalMilliseconds:F0} longestUiStallMs={longestStall.TotalMilliseconds:F0} managedMB={GC.GetTotalMemory(true) / 1048576} workingSetMB={Environment.WorkingSet / 1048576}");
        Assert.True(preparingAtTree);
        Assert.True(f.Shell.Workspace!.IsContentReady);
        Assert.Equal(10000, f.Shell.Workspace.Snapshot.Files.Count);
        Assert.True(treeAt < indexedAt);
        Assert.True(longestStall < TimeSpan.FromSeconds(3), $"The UI stalled for {longestStall.TotalMilliseconds:F0} ms.");
    }
}
