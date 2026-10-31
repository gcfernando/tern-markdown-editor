using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Tern.Filesystem;

namespace Tern.Benchmarks;

internal static class LargeFileSafety
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tern-large-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var results = new List<object>();
        try
        {
            foreach (var size in new long[] { 1024, 100_000, 1_000_000, 10_000_000, 1_000_000_000, 1_073_741_824 })
            {
                foreach (var scenario in new[] { "unicode-malformed", "long-line" })
                {
                    var path = Path.Combine(directory, "input.md");
                    await using (var stream = File.Create(path))
                    {
                        var chunk = Encoding.UTF8.GetBytes(scenario == "long-line" ? new string('x', 8192) : "# 世界 å\n```unclosed\n**malformed [link](\n");
                        // Oversized fixtures exercise the admission boundary with a real-length
                        // file; their unvisited body is zero-filled rather than parsed content.
                        if (size > DocumentFileService.MaximumEagerLoadBytes)
                        {
                            stream.Write(chunk);
                            stream.SetLength(size);
                        }
                        else while (stream.Position < size)
                            await stream.WriteAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, size - stream.Position)));
                    }
                    var before = GC.GetTotalAllocatedBytes(precise: true);
                    var watch = Stopwatch.StartNew();
                    string status;
                    double? saveMs = null;
                    var files = new DocumentFileService();
                    try
                    {
                        var loaded = await files.LoadAsync(path);
                        watch.Stop();
                        var saveWatch = Stopwatch.StartNew();
                        var outcome = await files.SaveAsync(path, loaded.Text, loaded.Format, loaded.Baseline);
                        saveWatch.Stop();
                        saveMs = saveWatch.Elapsed.TotalMilliseconds;
                        status = outcome is SaveSucceeded ? "opened; no-op save verified" : outcome.ToString()!;
                    }
                    catch (IOException exception) when (size > DocumentFileService.MaximumEagerLoadBytes)
                    {
                        watch.Stop();
                        status = "refused safely: " + exception.Message;
                    }
                    using var process = Process.GetCurrentProcess();
                    results.Add(new { size, scenario, elapsedMs = watch.Elapsed.TotalMilliseconds,
                        allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - before,
                        workingSetBytes = process.WorkingSet64, saveMs, status, fileLengthUnchanged = new FileInfo(path).Length == size });
                    Console.WriteLine($"{size} {scenario}: {watch.Elapsed.TotalMilliseconds:F2} ms; {status}");
                    File.Delete(path);
                }
            }
            Directory.CreateDirectory("artifacts/benchmarks");
            await File.WriteAllTextAsync("artifacts/benchmarks/issues-large-file.json", JsonSerializer.Serialize(new
            { measuredAtUtc = DateTime.UtcNow, os = RuntimeInformation.OSDescription, framework = RuntimeInformation.FrameworkDescription,
                logicalProcessors = Environment.ProcessorCount, memoryAvailableBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                method = "Release filesystem admission/decode and no-op save only; no editor, navigation, search, rendering or input-to-paint measured; oversized file bodies zero-filled",
                results }, JsonOptions));
            return 0;
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
