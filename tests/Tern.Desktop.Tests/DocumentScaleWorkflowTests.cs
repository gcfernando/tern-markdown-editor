using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.App.Shell;
using Tern.Desktop.ViewModels;
using Tern.Desktop.Services;
using Tern.Workspaces;

namespace Tern.Desktop.Tests;

public sealed class DocumentScaleWorkflowTests
{
    [AvaloniaFact]
    public void Command_index_tracks_subtrees_reparenting_tags_and_disposal()
    {
        var root = new StackPanel();
        var first = new StackPanel();
        var second = new ContentControl();
        root.Children.Add(first);
        root.Children.Add(second);
        var command = new Button { Tag = "command" };
        first.Children.Add(command);
        using var index = new CommandControlIndex(root, control => Equals(control.Tag, "command"));
        Assert.Same(command, Assert.Single(index.Snapshot()));

        first.Children.Remove(command);
        Assert.Empty(index.Snapshot());
        second.Content = command;
        Assert.Same(command, Assert.Single(index.Snapshot()));
        command.Tag = "other";
        Assert.Empty(index.Snapshot());
        command.Tag = "command";
        Assert.Same(command, Assert.Single(index.Snapshot()));
        root.Children.Clear();
        Assert.Empty(index.Snapshot());
        root.Children.Add(second);
        Assert.Same(command, Assert.Single(index.Snapshot()));
        index.Dispose();
        command.Tag = "other";
        command.Tag = "command";
        Assert.Empty(index.Snapshot());
    }

    [AvaloniaFact]
    public async Task Dynamic_command_buttons_follow_document_state_and_detached_buttons_stop_receiving_updates()
    {
        using var f = new DesktopFixture();
        var host = Assert.IsAssignableFrom<Panel>(f.Window.Content);
        var button = new Button { Tag = CommandIds.Save };
        host.Children.Add(button);
        f.Shell.NewDocument("draft");
        Pump(f);
        Assert.True(button.IsEnabled);
        f.Dialogs.SaveChoice = SaveChoice.Discard;
        Assert.True(await f.Shell.CloseDocumentsAsync());
        Pump(f);
        Assert.False(button.IsEnabled);
        button.Tag = CommandIds.NewFile;
        f.Shell.SetStatus("Synthetic command state refresh", StatusSeverity.Info);
        Assert.True(button.IsEnabled);
        host.Children.Remove(button);
        button.IsEnabled = false;
        f.Shell.NewDocument("another draft");
        Pump(f);
        Assert.False(button.IsEnabled);
    }

    [AvaloniaFact]
    public async Task Library_refresh_reuses_unchanged_rows_and_containers_without_losing_selection_or_counts()
    {
        using var f = new DesktopFixture();
        var keptPath = await f.WriteAsync("kept.md", "# Keep\n");
        var removedPath = await f.WriteAsync("removed.md", "# Remove\n");
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));
        Pump(f);
        var root = Assert.Single(f.Window.FileTree.Items.OfType<TreeRow>());
        var kept = Assert.Single(root.Children, row => row.FullPath == keptPath);
        f.Window.FileTree.SelectedItem = kept;
        Pump(f);
        var container = f.Window.FileTree.TreeContainerFromItem(kept);
        Assert.NotNull(container);

        var addedPath = await f.WriteAsync("added.md", "# Added\n");
        await f.Shell.Workspace!.RescanTreeAsync();
        f.Window.RefreshLibraryContents();
        Pump(f);

        Assert.Same(root, Assert.Single(f.Window.FileTree.Items.OfType<TreeRow>()));
        Assert.Same(kept, Assert.Single(root.Children, row => row.FullPath == keptPath));
        Assert.Same(container, f.Window.FileTree.TreeContainerFromItem(kept));
        Assert.Same(kept, f.Window.FileTree.SelectedItem);
        Assert.True(root.IsExpanded);
        Assert.StartsWith("3 document(s)", root.SectionCount);
        Assert.Contains(root.Children, row => row.FullPath == addedPath);

        File.Delete(removedPath);
        await f.Shell.Workspace.RescanTreeAsync();
        f.Window.RefreshLibraryContents();
        Pump(f);

        Assert.DoesNotContain(root.Children, row => row.FullPath == removedPath);
        Assert.StartsWith("2 document(s)", root.SectionCount);
        Assert.Same(container, f.Window.FileTree.TreeContainerFromItem(kept));
        Assert.Same(kept, f.Window.FileTree.SelectedItem);
    }

    [AvaloniaFact]
    public async Task Large_library_realization_yields_to_input_and_finishes_all_rows()
    {
        using var f = new DesktopFixture();
        for (var index = 0; index < 200; index++)
            await f.WriteAsync($"library{index:000}.md", "# Note\n");
        var tab = f.Shell.NewDocument("# Editable\n");
        var enteredBeforeCompletion = false;
        f.Shell.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName != nameof(ShellViewModel.Workspace)
                || f.Window.FileTree.Items.OfType<TreeRow>().FirstOrDefault() is not { } root) return;
            Dispatcher.UIThread.Post(() =>
            {
                enteredBeforeCompletion = root.PresentedChildren.Count < root.Children.Count;
                f.Window.EditorControl.TextArea.Focus();
                f.Window.EditorControl.TextArea.RaiseEvent(new TextInputEventArgs
                { RoutedEvent = InputElement.TextInputEvent, Text = "input during Library loading" });
            }, DispatcherPriority.Input);
        };
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));
        Pump(f);
        Assert.True(enteredBeforeCompletion);
        Assert.Contains("input during Library loading", tab.CurrentText);
        var root = Assert.Single(f.Window.FileTree.Items.OfType<TreeRow>());
        Assert.Equal(200, root.Children.Count);
        Assert.Equal(root.Children, root.PresentedChildren);
        Assert.False(root.IsLoadingChildren);
        await f.Window.RunCommandAsync(CommandIds.Undo);
        Assert.Equal("# Editable\n", tab.CurrentText);
    }

    [AvaloniaFact]
    public async Task Thousand_open_documents_preserve_input_undo_search_session_and_bounded_tab_chrome()
    {
        using var f = new DesktopFixture();
        using var process = Process.GetCurrentProcess();
        var clock = Stopwatch.StartNew();
        var cpuStart = process.TotalProcessorTime;
        var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var opens = new List<double>();
        var selections = new List<double>();
        var selectionModels = new List<double>();
        var selectionLayouts = new List<double>();
        var searches = new List<double>();
        var saves = new List<double>();
        var heartbeats = new List<double>();
        var heartbeatPhases = new List<(string Phase, double Milliseconds)>();
        var saveActivityTrace = new ConcurrentQueue<object>();
        var samples = new List<object>();
        var result = new Dictionary<string, object?>();
        var pendingProbes = 0;
        var maximumPendingProbes = 0;
        EventHandler traceSave = (_, _) =>
        {
            if (f.Shell.Activities.Items.LastOrDefault(item => item.Kind is "Saving document" or "Save As") is { } item)
                saveActivityTrace.Enqueue(new { elapsedMs = clock.Elapsed.TotalMilliseconds, item.Id, item.Kind, item.Phase, item.State });
        };

        void Sample(string phase)
        {
            process.Refresh();
            var working = process.WorkingSet64 / 1048576d;
            var committed = process.PrivateMemorySize64 / 1048576d;
            samples.Add(new
            {
                phase,
                elapsedMs = clock.Elapsed.TotalMilliseconds,
                workingMiB = working,
                privateMiB = committed,
                managedMiB = GC.GetTotalMemory(false) / 1048576d,
                threadPoolPendingWorkItems = ThreadPool.PendingWorkItemCount,
                threadPoolThreads = ThreadPool.ThreadCount,
                postedInputProbesPending = pendingProbes,
                cpuSeconds = (process.TotalProcessorTime - cpuStart).TotalSeconds
            });
            File.WriteAllText(Path.Combine(f.AppData, "scale-progress.json"), JsonSerializer.Serialize(new
            {
                phase,
                elapsedSeconds = clock.Elapsed.TotalSeconds,
                openDocuments = f.Shell.Tabs.Count,
                completedOpens = opens.Count,
                result,
                samples
            }));
            Assert.True(working <= 2048 && committed <= 3072, $"Shared-host resource cap exceeded in {phase}: {working:N0}/{committed:N0} MiB.");
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(240), $"Cooperative wall-time cap exceeded in {phase}.");
        }

        void Probe(string phase)
        {
            var timer = Stopwatch.StartNew();
            maximumPendingProbes = Math.Max(maximumPendingProbes, ++pendingProbes);
            Dispatcher.UIThread.Post(() =>
            {
                var elapsed = timer.Elapsed.TotalMilliseconds;
                heartbeats.Add(elapsed);
                heartbeatPhases.Add((phase, elapsed));
                pendingProbes--;
            }, DispatcherPriority.Input);
        }

        void Type(string text)
        {
            f.Window.EditorControl.TextArea.Focus();
            f.Window.EditorControl.TextArea.RaiseEvent(new TextInputEventArgs
            {
                RoutedEvent = InputElement.TextInputEvent,
                Text = text
            });
            Pump(f);
        }

        try
        {
            var paths = new string[1000];
            for (var index = 0; index < paths.Length; index++)
                paths[index] = await f.WriteAsync($"note{index:0000}.md",
                    $"# Note {index}\n\nRelease planning alpha {index}.\n\n- [ ] Review the release\n\n[Next](note{(index + 1) % paths.Length:0000}.md)\n");

            var opening = Stopwatch.StartNew();
            for (var index = 0; index < paths.Length; index++)
            {
                Probe($"open-{index}");
                var timer = Stopwatch.StartNew();
                Assert.NotNull(await f.Shell.OpenFileAsync(paths[index], activate: index == 0, recordRecent: false));
                Pump(f);
                opens.Add(timer.Elapsed.TotalMilliseconds);
                if (index % 25 == 0) Sample($"open-{index + 1}");
            }
            opening.Stop();
            result["openThousandMs"] = opening.Elapsed.TotalMilliseconds;
            Assert.Equal(1000, f.Shell.Tabs.Count);
            Sample("thousand-open");

            var first = f.Shell.Tabs[0];
            var firstOriginal = first.CurrentText;
            var indexing = f.Shell.OpenWorkspaceAsync(f.Docs);
            var typedDuringPreparation = 0;
            while (!indexing.IsCompleted)
            {
                Probe("workspace-preparation");
                Type("x");
                typedDuringPreparation++;
                Sample("typing-while-opening-workspace");
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
            Assert.True(await indexing);
            result["logicalTreeElementsAfterWorkspace"] = f.Window.GetLogicalDescendants().Count();
            Assert.True(typedDuringPreparation > 0);
            Assert.True(first.IsDirty);
            result["inputBatchesDuringWorkspacePreparation"] = typedDuringPreparation;
            for (var index = 0; index < typedDuringPreparation; index++)
                await f.Window.RunCommandAsync(CommandIds.Undo);
            Assert.Equal(firstOriginal, first.CurrentText);

            for (var index = 0; index < 100; index++)
            {
                Probe($"selection-{index}");
                var selected = f.Shell.Tabs[index * 97 % 1000];
                var timer = Stopwatch.StartNew();
                f.Shell.SelectTab(selected, TabActivationReason.Programmatic);
                selectionModels.Add(timer.Elapsed.TotalMilliseconds);
                var layout = Stopwatch.StartNew();
                Pump(f);
                selectionLayouts.Add(layout.Elapsed.TotalMilliseconds);
                selections.Add(timer.Elapsed.TotalMilliseconds);
                Assert.Same(selected, f.Shell.ActiveTab);
                Assert.Equal(selected.CurrentText, f.Window.EditorControl.Document.Text);
                if (index % 10 == 0) Sample($"switch-{index}");
            }

            for (var index = 0; index < 20; index++)
            {
                Probe($"search-{index}");
                var timer = Stopwatch.StartNew();
                var page = await Task.Run(() => f.Shell.Workspace!.Search("release", limit: 50),
                    TestContext.Current.CancellationToken);
                Pump(f);
                searches.Add(timer.Elapsed.TotalMilliseconds);
                Assert.Null(page.Error);
                Assert.NotEmpty(page.Hits);
            }
            Assert.True(f.Shell.SaveSession());
            result["selection"] = Distribution(selections);
            result["search"] = Distribution(searches);
            var persisted = new SessionStore(f.Shell.Paths.StateDirectory).Load();
            result["persistedTabs"] = persisted.Tabs.Count;
            result["realizedTabContainers"] = f.Window.FindControl<ListBox>("TabStrip")!
                .GetVisualDescendants().OfType<ListBoxItem>().Count();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            var beforeCycles = GC.GetTotalMemory(true);
            f.Shell.Activities.Changed += traceSave;
            for (var index = 0; index < 30; index++)
            {
                Sample($"cycle-{index}");
                var tab = f.Shell.NewDocument($"# Cycle {index}\n");
                Pump(f);
                Type("actual input");
                Assert.Contains("actual input", tab.CurrentText);
                f.Dialogs.SavePath = Path.Combine(f.Docs, $"cycle{index:00}.md");
                var timer = Stopwatch.StartNew();
                Assert.True(await f.Shell.SaveTabAsync(tab));
                saves.Add(timer.Elapsed.TotalMilliseconds);
                Assert.Equal(tab.CurrentText, await File.ReadAllTextAsync(f.Dialogs.SavePath, TestContext.Current.CancellationToken));
                Assert.True(await f.Shell.CloseTabAsync(tab));
                Pump(f);
                Assert.Equal(1000, f.Shell.Tabs.Count);
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var growth = (GC.GetTotalMemory(true) - beforeCycles) / 1048576d;
            result["postCollectionCycleManagedGrowthMiB"] = growth;
            Sample("finished");

            Assert.Equal(1000, persisted.Tabs.Count);
            Assert.InRange((int)result["realizedTabContainers"]!, 1, 64);
            Assert.True((double)result["openThousandMs"]! <= 120000, "Opening 1,000 documents exceeded the predeclared 120-second threshold.");
            Assert.True(Percentile(selections, .5) <= 75 && Percentile(selections, .95) <= 250 && selections.Max() <= 1500,
                "Tab selection exceeded the predeclared median/p95/worst threshold.");
            Assert.True(Percentile(searches, .95) <= 2000, "Search p95 exceeded the predeclared threshold.");
            Assert.True(Percentile(saves, .95) <= 2000, "Save p95 exceeded the predeclared threshold.");
            Assert.True(heartbeats.Count > 0 && heartbeats.Max() <= 1500, "Posted input-priority dispatcher work was delayed beyond the predeclared threshold.");
            Assert.True(growth <= 128, "Repeated edit/save/close exceeded the predeclared post-collection managed growth threshold.");
            result["outcome"] = "passed";
        }
        finally
        {
            f.Shell.Activities.Changed -= traceSave;
            result.TryAdd("outcome", "failed-or-interrupted");
            result["open"] = Distribution(opens);
            result["selection"] = Distribution(selections);
            result["selectionModel"] = Distribution(selectionModels);
            result["selectionLayout"] = Distribution(selectionLayouts);
            result["search"] = Distribution(searches);
            result["save"] = Distribution(saves);
            result["dispatcherHeartbeat"] = Distribution(heartbeats);
            result["worstHeartbeatPhases"] = heartbeatPhases.OrderByDescending(sample => sample.Milliseconds)
                .Take(10).Select(sample => new { sample.Phase, sample.Milliseconds }).ToArray();
            result["saveActivityTrace"] = saveActivityTrace.ToArray();
            result["maximumOutstandingProbes"] = maximumPendingProbes;
            result["remainingOutstandingProbes"] = pendingProbes;
            result["samples"] = samples;
            result["gcCollections"] = Enumerable.Range(0, 3).Select(index => GC.CollectionCount(index) - collections[index]).ToArray();
            result["lastGcPauseMilliseconds"] = GC.GetGCMemoryInfo().PauseDurations.ToArray().Select(pause => pause.TotalMilliseconds).ToArray();
            result["wallSeconds"] = clock.Elapsed.TotalSeconds;
            result["cpuSeconds"] = (process.TotalProcessorTime - cpuStart).TotalSeconds;
            result["normalizedCpuPercent"] = (process.TotalProcessorTime - cpuStart).TotalSeconds / clock.Elapsed.TotalSeconds
                / Environment.ProcessorCount * 100;
            result["scope"] = "Synthetic headless actual-window/files/index/input/undo/save/session workload; not native FPS or physical-input certification.";
            TestContext.Current.TestOutputHelper!.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static double Percentile(List<double> values, double percentile) =>
        values.Count == 0 ? 0 : values.Order().ElementAt(Math.Clamp((int)Math.Ceiling(values.Count * percentile) - 1, 0, values.Count - 1));

    private static object Distribution(List<double> values) => new
    {
        count = values.Count,
        medianMs = Percentile(values, .5),
        p95Ms = Percentile(values, .95),
        worstMs = values.Count == 0 ? 0 : values.Max()
    };

    private static void Pump(DesktopFixture f)
    {
        f.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
