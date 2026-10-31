using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AvaloniaEdit.Document;
using Tern.Export;
using Tern.Filesystem;
using Tern.Recovery;
using Tern.Rendering.Preview;
using Tern.Search;
using Tern.Workspaces;

namespace Tern.Benchmarks;

internal sealed record Measurement(string Name, string Unit, int Runs, double Min, double P50, double P95, double Max, string Notes);

internal static class Program
{
    private static readonly List<Measurement> Results = [];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--large-file-safety")) return await LargeFileSafety.RunAsync();
        var quick = args.Contains("--quick");
        var work = Path.Combine(Path.GetTempPath(), "tern-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            Console.WriteLine($"Tern benchmarks {(quick ? "(quick)" : string.Empty)}");
            Console.WriteLine($"OS: {RuntimeInformation.OSDescription}; CPU cores: {Environment.ProcessorCount}; .NET: {RuntimeInformation.FrameworkDescription}");
#if DEBUG
            Console.WriteLine("WARNING: Debug build. Numbers are not representative; run with -c Release.");
#endif
            await OpenAndSaveAsync(work, quick);
            DocumentModel(work, quick);
            PreviewAndExport(quick);
            await WorkspaceAsync(work, 2_000, quick);
            await WorkspaceAsync(work, 10_000, quick);
            await RecoveryAsync(work, quick);
            await ByteIdentityAsync(work);
            Report();
            return 0;
        }
        finally
        {
            try
            {
                Directory.Delete(work, true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ---- Fixtures --------------------------------------------------------------------------------------------------

    /// <summary>Deterministic, ordinary GFM: headings, paragraphs with emphasis and links, lists, tables, code, quotes, task lists.</summary>
    internal static string Gfm(int targetBytes, int seed = 1)
    {
        var random = new Random(seed);
        var words = new[] { "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet", "kilo", "lima", "mike", "november", "oscar", "papa", "quebec", "romeo", "sierra", "tango", "uniform", "victor", "whiskey", "xray", "yankee", "zulu", "markdown", "editor", "workspace", "document", "offline", "search", "preview", "export" };
        string Sentence(int n) => string.Join(' ', Enumerable.Range(0, n).Select(_ => words[random.Next(words.Length)]));
        var builder = new StringBuilder(targetBytes + 4096);
        var section = 0;
        while (builder.Length < targetBytes)
        {
            section++;
            builder.Append("## Section ").Append(section).Append(' ').Append(Sentence(3)).Append("\n\n");
            builder.Append(Sentence(25)).Append(" **").Append(Sentence(2)).Append("** and *").Append(Sentence(2)).Append("* with a [link](notes/").Append(words[random.Next(words.Length)]).Append(".md) and `").Append(Sentence(1)).Append("`.\n\n");
            builder.Append("- ").Append(Sentence(6)).Append("\n- ").Append(Sentence(7)).Append("\n  - ").Append(Sentence(4)).Append("\n- [ ] ").Append(Sentence(5)).Append("\n\n");
            builder.Append("| Name | Value | Notes |\n|---|---:|---|\n| ").Append(Sentence(1)).Append(" | ").Append(random.Next(1000)).Append(" | ").Append(Sentence(3)).Append(" |\n| ").Append(Sentence(1)).Append(" | ").Append(random.Next(1000)).Append(" | ").Append(Sentence(3)).Append(" |\n\n");
            builder.Append("```csharp\nvar x = ").Append(random.Next(100)).Append(";\nConsole.WriteLine(x);\n```\n\n");
            builder.Append("> ").Append(Sentence(12)).Append("\n\n");
        }

        return builder.ToString();
    }

    /// <summary>Text of exactly <paramref name="lines"/> lines, around <paramref name="bytes"/> bytes.</summary>
    private static string Lines(int lines, int bytes)
    {
        var perLine = Math.Max(1, bytes / lines - 1);
        var random = new Random(7);
        var builder = new StringBuilder(bytes + lines);
        for (var i = 0; i < lines; i++)
        {
            builder.Append(i % 50 == 0 ? "# " : string.Empty);
            while (builder.Length < 0 || LineLength(builder) < perLine)
            {
                builder.Append((char)('a' + random.Next(26)));
                if (random.Next(7) == 0)
                {
                    builder.Append(' ');
                }
            }

            builder.Append('\n');
        }

        return builder.ToString();

        static int LineLength(StringBuilder b)
        {
            var count = 0;
            for (var i = b.Length - 1; i >= 0 && b[i] != '\n'; i--)
            {
                count++;
            }

            return count;
        }
    }

    // ---- Measuring -------------------------------------------------------------------------------------------------

    private static void Measure(string name, int runs, Action action, string notes = "", int warmup = 1)
    {
        for (var i = 0; i < warmup; i++)
        {
            action();
        }

        var samples = new double[runs];
        for (var i = 0; i < runs; i++)
        {
            var timer = Stopwatch.StartNew();
            action();
            timer.Stop();
            samples[i] = timer.Elapsed.TotalMilliseconds;
        }

        Record(name, "ms", samples, notes);
    }

    private static async Task MeasureAsync(string name, int runs, Func<Task> action, string notes = "", int warmup = 1)
    {
        for (var i = 0; i < warmup; i++)
        {
            await action();
        }

        var samples = new double[runs];
        for (var i = 0; i < runs; i++)
        {
            var timer = Stopwatch.StartNew();
            await action();
            timer.Stop();
            samples[i] = timer.Elapsed.TotalMilliseconds;
        }

        Record(name, "ms", samples, notes);
    }

    private static void Record(string name, string unit, double[] samples, string notes)
    {
        var sorted = samples.Order().ToArray();
        double Percentile(double p) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];
        var m = new Measurement(name, unit, sorted.Length, sorted[0], Percentile(0.5), Percentile(0.95), sorted[^1], notes);
        Results.Add(m);
        Console.WriteLine($"{m.Name,-58} n={m.Runs,-4} min {m.Min,9:F2}  p50 {m.P50,9:F2}  p95 {m.P95,9:F2}  max {m.Max,9:F2} {m.Unit}  {m.Notes}");
    }

    private static void Single(string name, string unit, double value, string notes = "") =>
        Record(name, unit, [value], notes);

    // ---- Scenarios -------------------------------------------------------------------------------------------------

    private static async Task OpenAndSaveAsync(string work, bool quick)
    {
        var service = new DocumentFileService();
        var runs = quick ? 3 : 15;

        var big = Path.Combine(work, "big.md");
        var bigText = Lines(100_000, 10 * 1024 * 1024);
        await File.WriteAllTextAsync(big, bigText, new UTF8Encoding(false));
        var info = new FileInfo(big);
        var lineCount = bigText.Count(c => c == '\n');
        Console.WriteLine($"Fixture: big.md {info.Length / 1024.0 / 1024.0:F2} MB, {lineCount:N0} lines");

        await MeasureAsync("open 10 MB / 100k lines (read+decode+hash)", runs, async () => await service.LoadAsync(big), $"{info.Length / 1024.0 / 1024.0:F1} MB, {lineCount:N0} lines");

        var loaded = await service.LoadAsync(big);
        var small = Path.Combine(work, "small.md");
        var smallText = Gfm(100 * 1024);
        await File.WriteAllTextAsync(small, smallText, new UTF8Encoding(false));
        var smallLoaded = await service.LoadAsync(small);

        await MeasureAsync("open 100 KB GFM", quick ? 5 : 50, async () => await service.LoadAsync(small), "100 KB");

        var baseline = smallLoaded.Baseline;
        await MeasureAsync("save 100 KB (atomic, verified against baseline)", quick ? 5 : 40, async () =>
        {
            var outcome = await service.SaveAsync(small, smallText + " ", smallLoaded.Format, baseline, overwriteExisting: true);
            if (outcome is not SaveSucceeded ok)
            {
                var diagnostic = outcome is SaveFailed { Exception: IOException error }
                    ? $" (native error {error.HResult & 0xffff}; files: {string.Join(", ", Directory.GetFiles(work).Select(Path.GetFileName))})" : "";
                throw new InvalidOperationException("Save failed: " + outcome + diagnostic);
            }

            baseline = ok.Baseline;
            smallText = smallText.Length % 2 == 0 ? smallText + " " : smallText[..^1];
        }, "includes encode, temp write, flush, replace, re-hash");

        var bigBaseline = loaded.Baseline;
        var bigText2 = loaded.Text;
        await MeasureAsync("save 10 MB (atomic, verified against baseline)", quick ? 3 : 10, async () =>
        {
            var outcome = await service.SaveAsync(big, bigText2, loaded.Format, bigBaseline, overwriteExisting: true);
            if (outcome is not SaveSucceeded ok)
            {
                throw new InvalidOperationException("Save failed: " + outcome);
            }

            bigBaseline = ok.Baseline;
        }, "10 MB");
    }

    private static void DocumentModel(string work, bool quick)
    {
        _ = work;
        var text = Lines(100_000, 10 * 1024 * 1024);
        TextDocument? document = null;
        Measure("editor document model: create from 10 MB", quick ? 3 : 10, () => document = new TextDocument(text), "AvaloniaEdit TextDocument, no UI/paint");

        document = new TextDocument(text);
        var random = new Random(3);
        Measure("editor document model: 100 random single-char inserts", quick ? 3 : 10, () =>
        {
            for (var i = 0; i < 100; i++)
            {
                document.Insert(random.Next(document.TextLength), "x");
            }
        }, "per batch of 100 inserts in a 10 MB document; no paint");

        Measure("editor document model: 1,000 offset->line lookups", quick ? 3 : 10, () =>
        {
            var lookup = new Random(5);
            for (var i = 0; i < 1000; i++)
            {
                _ = document.GetLineByOffset(lookup.Next(document.TextLength)).LineNumber;
            }
        }, "10 MB document");

        Measure("find next in 10 MB (ordinal, no match, worst case)", quick ? 3 : 10, () => _ = document.Text.IndexOf("zzzzzzzzzz", StringComparison.Ordinal), "full scan of document.Text");

        Measure("document.Text materialisation (10 MB)", quick ? 3 : 10, () => _ = document.Text, "snapshot used for preview/save");
    }

    private static void PreviewAndExport(bool quick)
    {
        var options = new PreviewOptions();
        var s100 = Gfm(100 * 1024);
        var s1m = Gfm(1024 * 1024);
        var s10m = Gfm(10 * 1024 * 1024);

        Measure("preview build 100 KB GFM (parse + node tree)", quick ? 5 : 60, () => PreviewBuilder.Build(s100, options), $"{s100.Length / 1024} KB", warmup: 5);
        Measure("preview build 1 MB mixed GFM", quick ? 3 : 20, () => PreviewBuilder.Build(s1m, options), $"{s1m.Length / 1024} KB", warmup: 3);

        PreviewDocument? tenMb = null;
        Measure("preview build 10 MB (limits apply)", quick ? 2 : 5, () => tenMb = PreviewBuilder.Build(s10m, options), "see truncation note", warmup: 1);
        Console.WriteLine($"   10 MB preview: truncated={tenMb!.Truncated} reason={tenMb.Truncation} nodes={tenMb.NodeCount}");

        Measure("HTML export 100 KB", quick ? 3 : 20, () => HtmlExporter.Export(s100), "", warmup: 2);
        Measure("HTML export 1 MB", quick ? 3 : 10, () => HtmlExporter.Export(s1m), "", warmup: 1);
        Measure("plain-text export 1 MB", quick ? 3 : 10, () => PlainTextExporter.Export(s1m), "", warmup: 1);
        Measure("PDF export 100 KB", quick ? 2 : 5, () => PdfExporter.Export(s100), "", warmup: 1);
    }

    private static async Task WorkspaceAsync(string work, int notes, bool quick)
    {
        var root = Path.Combine(work, $"ws{notes}");
        Directory.CreateDirectory(root);
        var folders = 40;
        var random = new Random(notes);
        var buildTimer = Stopwatch.StartNew();
        for (var i = 0; i < notes; i++)
        {
            var folder = Path.Combine(root, "folder" + (i % folders));
            Directory.CreateDirectory(folder);
            var body = Gfm(1500, seed: i);
            var link = i > 0 ? $"\nSee [[note{random.Next(i)}]] and [prev](../folder{(i - 1) % folders}/note{i - 1}.md).\n" : string.Empty;
            await File.WriteAllTextAsync(Path.Combine(folder, $"note{i}.md"), $"---\ntitle: Note {i}\ntags: [t{i % 20}]\n---\n# Note {i}\n\n{body}{link}");
        }

        buildTimer.Stop();
        Console.WriteLine($"Fixture: {notes:N0} notes generated in {buildTimer.Elapsed.TotalSeconds:F1} s (not part of any measurement)");

        var scanTimer = Stopwatch.StartNew();
        var snapshot = await new WorkspaceScanner().ScanAsync(root);
        scanTimer.Stop();
        Single($"workspace scan {notes:N0} notes", "ms", scanTimer.Elapsed.TotalMilliseconds, $"{snapshot.Files.Count} files found");

        var db = Path.Combine(work, $"index{notes}.db");
        using var index = SearchIndex.Open(db, root);
        using var indexer = new WorkspaceIndexer(root, index);
        var indexTimer = Stopwatch.StartNew();
        var summary = await indexer.IndexWorkspaceAsync();
        indexTimer.Stop();
        Single($"workspace index {notes:N0} notes (initial, full)", "ms", indexTimer.Elapsed.TotalMilliseconds, $"indexed {summary.Indexed}, failed {summary.Failed}");

        var againTimer = Stopwatch.StartNew();
        var again = await indexer.IndexWorkspaceAsync();
        againTimer.Stop();
        Single($"workspace re-index {notes:N0} notes (nothing changed)", "ms", againTimer.Elapsed.TotalMilliseconds, $"unchanged {again.Unchanged}, indexed {again.Indexed}");

        var service = indexer.CreateSearchService();
        var queries = new[] { "alpha", "markdown editor", "\"offline search\"", "tango uniform", "Note 17", "zulu", "preview export", "t7", "workspace document", "nosuchwordanywhere" };
        var first = Stopwatch.StartNew();
        var firstPage = service.Query(queries[0]);
        first.Stop();
        Single($"search first query after index ({notes:N0} notes)", "ms", first.Elapsed.TotalMilliseconds, $"{firstPage.Hits.Count} hits (first query, cold)");

        var runs = quick ? 30 : 300;
        var index2 = 0;
        Measure($"search query latency, {notes:N0} notes, 20 results", runs, () => service.Query(queries[index2++ % queries.Length], new SearchOptions(20)), "mixed 10-query set, includes snippets", warmup: 10);

        var lookupGraph = Stopwatch.StartNew();
        var graph = new LinkGraph(root);
        await graph.LoadAsync(snapshot.Files.Select(f => f.FullPath), new DiskReader());
        lookupGraph.Stop();
        Single($"link graph build {notes:N0} notes", "ms", lookupGraph.Elapsed.TotalMilliseconds, "parse every note for links");
    }

    private static async Task RecoveryAsync(string work, bool quick)
    {
        var store = new RecoveryStore(Path.Combine(work, "recovery"));
        var text = Gfm(100 * 1024);
        var id = Guid.NewGuid();
        var revision = 0L;
        await MeasureAsync("recovery checkpoint write 100 KB (flush to disk)", quick ? 5 : 40, async () =>
            await store.WriteCheckpointAsync(new RecoveryRecord(id, null, "bench.md", ++revision, DateTime.UtcNow, null, Tern.Domain.Text.TextFileFormat.NewFileDefault, text)), "excludes the idle timer that schedules it");
    }

    private static async Task ByteIdentityAsync(string work)
    {
        var service = new DocumentFileService();
        var text = "# Title\r\nZ\u00FCrich \u2014 caf\u00E9\r\n\r\nline\r\n";
        var cases = new Dictionary<string, byte[]>
        {
            ["utf8-lf"] = new UTF8Encoding(false).GetBytes(text.Replace("\r\n", "\n")),
            ["utf8-crlf"] = new UTF8Encoding(false).GetBytes(text),
            ["utf8-bom-crlf"] = [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. new UTF8Encoding(false).GetBytes(text)],
            ["utf16le-bom"] = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)],
            ["utf16be-bom"] = [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes(text)],
            ["latin1"] = Encoding.Latin1.GetBytes("caf\u00E9 na\u00EFve\n"),
            ["no-final-newline"] = new UTF8Encoding(false).GetBytes("no newline at end"),
            ["mixed-endings"] = new UTF8Encoding(false).GetBytes("a\r\nb\nc\rd\n"),
            ["empty"] = [],
        };

        var identical = 0;
        foreach (var (name, bytes) in cases)
        {
            var path = Path.Combine(work, $"identity-{name}.md");
            await File.WriteAllBytesAsync(path, bytes);
            var loaded = await service.LoadAsync(path, name == "latin1" ? Tern.Domain.Text.TextEncodingKind.Latin1 : null);
            var outcome = await service.SaveAsync(path, loaded.Text, loaded.Format, loaded.Baseline, overwriteExisting: true);
            var after = await File.ReadAllBytesAsync(path);
            var same = outcome is SaveSucceeded && after.AsSpan().SequenceEqual(bytes);
            if (same)
            {
                identical++;
            }
            else
            {
                Console.WriteLine($"   NOT identical: {name}");
            }
        }

        Single("byte-preserving no-op save (fixtures identical)", "count", identical, $"{identical} of {cases.Count} fixtures byte-identical");
    }

    private sealed class DiskReader : IDocumentTextProvider
    {
        public ValueTask<string?> ReadTextAsync(string path, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>(File.Exists(path) ? File.ReadAllText(path) : null);
    }

    // ---- Output ----------------------------------------------------------------------------------------------------

    private static void Report()
    {
        var directory = FindArtifacts();
        Directory.CreateDirectory(directory);
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Machine: {RuntimeInformation.OSDescription}, {Environment.ProcessorCount} logical cores, {RuntimeInformation.FrameworkDescription}, {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024 / 1024.0:F1} GB RAM visible to the GC");
        builder.AppendLine();
        builder.AppendLine("| Measurement | Runs | Min | P50 | P95 | Max | Unit | Notes |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---|---|");
        foreach (var m in Results)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"| {m.Name} | {m.Runs} | {m.Min:F2} | {m.P50:F2} | {m.P95:F2} | {m.Max:F2} | {m.Unit} | {m.Notes} |");
        }

        File.WriteAllText(Path.Combine(directory, "results.md"), builder.ToString());
        File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(Results, JsonOptions));
        Console.WriteLine();
        Console.WriteLine("Wrote " + Path.Combine(directory, "results.md"));
    }

    private static string FindArtifacts()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Tern.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? Environment.CurrentDirectory, "artifacts", "benchmarks");
    }
}
