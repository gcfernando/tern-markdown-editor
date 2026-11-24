using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.App.Shell;
using Tern.Desktop.Views;
using Tern.Domain.Text;
using Tern.Export;
using Tern.Recovery;

namespace Tern.Desktop.Tests;

public sealed class WorkflowStressTests
{
    [AvaloniaFact]
    public async Task Large_workspace_notifications_graph_and_workspace_switching_preserve_editing()
    {
        using var evidence = new StressEvidence();
        using var f = new DesktopFixture();
        var paths = new string[1000];
        for (var index = 0; index < paths.Length; index++)
            paths[index] = await f.WriteAsync($"stress{index:0000}.md", Note(index));
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));
        var first = f.Shell.Workspace!;
        for (var index = 0; index < 25; index++) Assert.NotNull(await f.Shell.OpenFileAsync(paths[index], activate: index == 0));
        var target = f.Shell.Tabs[0];
        evidence.Type(f, "local edit ");
        Assert.True(target.IsDirty);
        await Parallel.ForEachAsync(Enumerable.Range(1, 64), new ParallelOptions
        { MaxDegreeOfParallelism = 4, CancellationToken = TestContext.Current.CancellationToken },
            async (index, token) => await File.WriteAllTextAsync(paths[index], Note(index) + "\nburstmarker\n", token));
        await Until(f, () => first.Search("burstmarker", limit: 100).Hits.Count == 64);
        Assert.Equal(1000, first.Graph.DocumentCount);
        Assert.Contains("local edit ", target.CurrentText);
        Assert.DoesNotContain("local edit ", await File.ReadAllTextAsync(paths[0], TestContext.Current.CancellationToken));
        for (var index = 0; index < 100; index++)
        {
            var timer = Stopwatch.StartNew();
            Assert.NotEmpty(first.Graph.GetBacklinks(paths[index]));
            evidence.Record("graph", timer.Elapsed.TotalMilliseconds);
        }
        var secondRoot = Path.Combine(f.AppData, "other-workspace");
        Directory.CreateDirectory(secondRoot);
        await File.WriteAllTextAsync(Path.Combine(secondRoot, "other.md"), "# Other\n", TestContext.Current.CancellationToken);
        Assert.True(await f.Shell.OpenWorkspaceAsync(secondRoot));
        var second = f.Shell.Workspace!;
        for (var index = 0; index < 30; index++)
        {
            var timer = Stopwatch.StartNew();
            f.Shell.SelectWorkspace(index % 2 == 0 ? first : second);
            Pump(f);
            evidence.Record("workspace-switch", timer.Elapsed.TotalMilliseconds);
        }
        Assert.Contains("local edit ", target.CurrentText);
        Assert.True(target.IsDirty);
        evidence.AssertP95("graph", 500);
        evidence.AssertP95("workspace-switch", 1500);
        evidence.Complete();
    }

    [AvaloniaFact]
    public async Task Large_mixed_document_exports_a_snapshot_while_actual_editor_input_remains_undoable()
    {
        using var evidence = new StressEvidence();
        using var f = new DesktopFixture();
        var source = Mixed(1000);
        var path = await f.WriteAsync("mixed.md", source);
        var timer = Stopwatch.StartNew();
        var tab = Assert.IsType<Tern.App.Documents.DocumentTab>(await f.Shell.OpenFileAsync(path));
        Pump(f);
        evidence.Record("activation", timer.Elapsed.TotalMilliseconds);
        Assert.True(timer.Elapsed.TotalMilliseconds <= 5000);
        var preview = ExportDefaults.PreviewOptions with { DocumentPath = path };
        foreach (var html in new[] { true, false })
        {
            var output = Path.Combine(f.AppData, html ? "mixed.html" : "mixed.txt");
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            timer.Restart();
            var export = Task.Run(() =>
            {
                started.SetResult();
                if (html) HtmlExporter.ExportToFile(source, output, new HtmlExportOptions { Preview = preview }, TestContext.Current.CancellationToken);
                else PlainTextExporter.ExportToFile(source, output, new PlainTextExportOptions { Preview = preview }, TestContext.Current.CancellationToken);
            }, TestContext.Current.CancellationToken);
            await started.Task;
            evidence.Type(f, "editor remains usable ");
            await export;
            evidence.Record("export", timer.Elapsed.TotalMilliseconds);
            Assert.True(timer.Elapsed.TotalMilliseconds <= 30000);
            var written = await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken);
            Assert.Contains("Section 999", written);
            Assert.DoesNotContain("editor remains usable", written);
            Assert.Contains("editor remains usable", tab.CurrentText);
            await f.Window.RunCommandAsync(CommandIds.Undo);
            Assert.Equal(source, tab.CurrentText);
        }
        Assert.Equal(source, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        evidence.Complete();
    }

    [AvaloniaFact]
    public async Task Large_batch_and_soak_survive_memory_pressure_cancellation_and_interrupted_save_recovery()
    {
        using var evidence = new StressEvidence();
        using var f = new DesktopFixture();
        var timer = Stopwatch.StartNew();
        for (var index = 0; index < 100; index++)
            Assert.NotNull(await f.Shell.OpenFileAsync(await f.WriteAsync($"batch{index:000}.md", "# Saved\n"), activate: index == 0));
        evidence.Record("batch-open", timer.Elapsed.TotalMilliseconds);
        Assert.True(timer.Elapsed.TotalMilliseconds <= 30000);
        timer.Restart();
        Assert.True(await f.Shell.CloseDocumentsAsync());
        Pump(f);
        evidence.Record("batch-close", timer.Elapsed.TotalMilliseconds);
        Assert.True(timer.Elapsed.TotalMilliseconds <= 30000);
        Assert.Empty(f.Shell.Tabs);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetTotalMemory(true);
        for (var index = 0; index < 60; index++)
        {
            var tab = f.Shell.NewDocument($"# Cycle {index}\n");
            evidence.Type(f, "actual input ");
            f.Dialogs.SavePath = Path.Combine(f.Docs, $"soak{index:000}.md");
            Assert.True(await f.Shell.SaveTabAsync(tab));
            Assert.Equal(tab.CurrentText, await File.ReadAllTextAsync(f.Dialogs.SavePath, TestContext.Current.CancellationToken));
            Assert.True(await f.Shell.CloseTabAsync(tab));
            Pump(f);
        }
        var growth = (GC.GetTotalMemory(true) - before) / 1048576d;
        evidence.Record("post-collection-growth-MiB", growth);
        Assert.True(growth <= 128);
        var protectedPath = await f.WriteAsync("protected.md", "# Disk original\n");
        var protectedTab = (await f.Shell.OpenFileAsync(protectedPath))!;
        evidence.Type(f, "unsaved checkpoint ");
        var record = new RecoveryRecord(protectedTab.Session.Id, protectedPath, protectedTab.DisplayName,
            protectedTab.Session.Revision, DateTime.UtcNow, protectedTab.Session.Baseline?.Sha256,
            protectedTab.Session.Format, protectedTab.CurrentText);
        await f.Shell.RecoveryStore.WriteCheckpointAsync(record);
        f.Shell.BeforeSaveWrite = () => Task.FromException(new IOException("Synthetic interrupted write"));
        await Assert.ThrowsAsync<IOException>(() => f.Shell.SaveTabAsync(protectedTab));
        Assert.Equal(ActivityState.Failed, f.Shell.Activities.Items.Last(item => item.Kind == "Saving document").State);
        Assert.Equal("# Disk original\n", await File.ReadAllTextAsync(protectedPath, TestContext.Current.CancellationToken));
        Assert.True(protectedTab.IsDirty);
        var restored = await f.Shell.RestoreRecoveryAsync(record);
        Assert.Equal(record.Text, restored.CurrentText);
        Assert.True(restored.IsDirty);
        Assert.Null(restored.FilePath);
        f.Shell.BeforeSaveWrite = null;
        Assert.True(await f.Shell.SaveTabAsync(protectedTab));
        var pressure = new byte[128 * 1024 * 1024];
        for (var index = 0; index < pressure.Length; index += 4096) pressure[index] = 1;
        var output = Path.Combine(f.AppData, "cancelled.html");
        await File.WriteAllTextAsync(output, "Verified previous output", TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var cancellableSource = Mixed(10000);
        var export = Task.Run(() =>
        {
            cancellation.CancelAfter(30);
            HtmlExporter.ExportToFile(cancellableSource, output, cancellationToken: cancellation.Token);
        }, TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export);
        Assert.Equal("Verified previous output", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
        Assert.Contains("unsaved checkpoint ", restored.CurrentText);
        GC.KeepAlive(pressure);
        evidence.Complete();
    }

    private static string Note(int index) => $"# Note {index}\n\n[Next](stress{(index + 1) % 1000:0000}.md)\n";
    private static string Mixed(int sections) => string.Join('\n', Enumerable.Range(0, sections).Select(index =>
        $"## Section {index}\n\n| Item | Value |\n|---|---|\n| release | {index} |\n\n[local](#section-{index})\n\n![image](missing-local-image.png)\n\n```csharp\nvar release = {index};\n```\n"));
    internal static void Pump(DesktopFixture f) { f.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(DesktopFixture f, Func<bool> ready)
    {
        var timer = Stopwatch.StartNew();
        while (!ready())
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30), "Stress workflow did not converge.");
            Pump(f);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}

public sealed partial class AIWritingWorkflowTests
{
    [AvaloniaFact]
    public async Task Knowledge_request_under_large_workspace_keeps_editor_usable_and_never_applies_generation()
    {
        using var evidence = new StressEvidence();
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reportSourceId = 0;
        var reportCalls = 0;
        var checkCalls = 0;
        using var f = Fixture(async (request, token) =>
        {
            var text = SentText(await request.Content!.ReadAsStringAsync(token));
            if (HasPurpose(text, Tern.App.AIPurpose.ReportCheck))
            {
                checkCalls++;
                return Reply("""{"unsupported":[]}""");
            }
            Assert.True(HasPurpose(text, Tern.App.AIPurpose.Report));
            var source = System.Text.RegularExpressions.Regex.Match(text, @"\[Note (\d+): launch\d{4}\]\r?\n");
            Assert.True(source.Success, "The held report must receive an actual synthetic launch note.");
            Assert.Contains("Release readiness requires quality review.", text);
            reportSourceId = int.Parse(source.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            reportCalls++;
            started.TrySetResult();
            return await response.Task;
        });
        for (var index = 0; index < 1000; index++)
            await f.WriteAsync($"launch{index:0000}.md", $"# Launch {index}\n\nRelease readiness requires quality review.\n");
        Assert.True(await f.Shell.OpenWorkspaceAsync(f.Docs));
        var target = f.Shell.NewDocument("# Keep this note\n");
        f.Shell.Settings.KnowledgeAllowRemote = true;
        f.Window.ShowKnowledgeAssistant();
        var window = Assert.Single(f.Window.OwnedWindows.OfType<KnowledgeAssistantWindow>());
        var panel = window.Assistant;
        Pump(window);
        Find<TextBox>(panel, "Your question").Text = "What does release readiness require?";
        Click(Find<Button>(panel, "Find my notes"));
        evidence.Type(f, "input while finding sources ");
        await Until(() => Find<Button>(panel, "Get my answer").IsEffectivelyVisible && !Find<Button>(panel, "Cancel").IsEnabled);
        Assert.False(f.Window.IsOverlayOpen);
        
        
        Click(Find<Button>(panel, "Get my answer"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        evidence.Type(f, "input while generating ");
        var edited = target.CurrentText;
        response.SetResult(Reply(JsonSerializer.Serialize(new
        {
            title = "Release readiness report", hasDecision = false, headline = "What does release readiness require?",
            recommendation = "Complete the documented quality review before release.",
            findings = new[] { new { title = "Quality review", kind = "Fact", detail = "Release readiness requires quality review.", evidenceIds = new[] { 1 } } },
            evidence = new[] { new { id = 1, sourceId = reportSourceId, fact = "Release readiness requires quality review.", quote = "Release readiness requires quality review." } },
        })));
        await Until(() => !Find<Button>(panel, "Cancel").IsEnabled);
        Assert.Equal(edited, target.CurrentText);
        Assert.Contains("input while generating ", target.CurrentText);
        Assert.Contains("input while finding sources ", target.CurrentText);
        Assert.NotEmpty(panel.GetVisualDescendants().OfType<Button>().Where(button =>
            Avalonia.Automation.AutomationProperties.GetName(button)?.StartsWith("Open note from answer", StringComparison.Ordinal) == true));
        Assert.Equal(1000, f.Shell.Workspace!.Snapshot.Files.Count);
        Assert.Equal(1, reportCalls);
        Assert.Equal(1, checkCalls);
        evidence.Complete();
    }
}

internal sealed class StressEvidence : IDisposable
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly TimeSpan _cpuStart;
    private readonly Dictionary<string, List<double>> _samples = [];
    private readonly int[] _collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
    private bool _passed;
    public StressEvidence() => _cpuStart = _process.TotalProcessorTime;
    public void Record(string kind, double value)
    {
        if (!_samples.TryGetValue(kind, out var list)) _samples[kind] = list = [];
        list.Add(value);
    }
    public void Type(DesktopFixture f, string text)
    {
        var timer = Stopwatch.StartNew();
        Dispatcher.UIThread.Post(() =>
        {
            Record("posted-input", timer.Elapsed.TotalMilliseconds);
            f.Window.EditorControl.TextArea.Focus();
            f.Window.EditorControl.TextArea.RaiseEvent(new TextInputEventArgs
            { RoutedEvent = InputElement.TextInputEvent, Text = text });
        }, DispatcherPriority.Input);
        WorkflowStressTests.Pump(f);
    }
    public void AssertP95(string kind, double limit) => Assert.True(Percentile(_samples[kind], .95) <= limit, kind + " p95 exceeded its declared limit.");
    public void Complete()
    {
        Assert.True(_clock.Elapsed.TotalSeconds <= 120, "Stress workflow exceeded its predeclared wall cap.");
        if (_samples.TryGetValue("posted-input", out var input)) Assert.True(input.Max() <= 1500);
        _process.Refresh();
        Assert.True(_process.WorkingSet64 / 1048576d <= 2048 && _process.PrivateMemorySize64 / 1048576d <= 3072);
        _passed = true;
    }
    public void Dispose()
    {
        _process.Refresh();
        TestContext.Current.TestOutputHelper!.WriteLine(JsonSerializer.Serialize(new
        {
            outcome = _passed ? "passed" : "failed-or-interrupted",
            wallSeconds = _clock.Elapsed.TotalSeconds,
            cpuSeconds = (_process.TotalProcessorTime - _cpuStart).TotalSeconds,
            workingMiB = _process.WorkingSet64 / 1048576d,
            privateMiB = _process.PrivateMemorySize64 / 1048576d,
            managedMiB = GC.GetTotalMemory(false) / 1048576d,
            threadPoolPendingWorkItems = ThreadPool.PendingWorkItemCount,
            lastGcPauseMilliseconds = GC.GetGCMemoryInfo().PauseDurations.ToArray().Select(pause => pause.TotalMilliseconds).ToArray(),
            gcCollections = Enumerable.Range(0, 3).Select(index => GC.CollectionCount(index) - _collections[index]).ToArray(),
            samples = _samples.ToDictionary(pair => pair.Key, pair => new
            { count = pair.Value.Count, median = Percentile(pair.Value, .5), p95 = Percentile(pair.Value, .95), worst = pair.Value.Max() }),
            scope = "Synthetic headless input/workspace/export/recovery workflow; not native input or real provider certification."
        }));
        _process.Dispose();
    }
    private static double Percentile(List<double> values, double percentile) => values.Order().ElementAt(Math.Clamp((int)Math.Ceiling(values.Count * percentile) - 1, 0, values.Count - 1));
}
