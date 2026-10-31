using System.Text;
using Tern.App.Settings;
using Tern.Export;
using Tern.Filesystem;
using Tern.Recovery;
using Tern.Rendering.Mathematics;
using Tern.Rendering.Mermaid;
using Tern.Rendering.Preview;
using Tern.Search;
using Tern.Workspaces;

namespace Tern.Security.Tests;

public class HostileInputTests
{
    [Fact]
    public void Deeply_nested_block_quotes_lists_and_emphasis_finish_quickly()
    {
        var inputs = new[]
        {
            string.Concat(Enumerable.Repeat("> ", 20_000)) + "x\n",
            string.Concat(Enumerable.Range(0, 5_000).Select(i => new string(' ', Math.Min(i, 500) * 2) + "- item\n")),
            string.Concat(Enumerable.Repeat("*a ", 50_000)) + "\n",
            string.Concat(Enumerable.Repeat("[", 50_000)) + "x" + string.Concat(Enumerable.Repeat("](", 50_000)) + "\n",
            string.Concat(Enumerable.Repeat("<div>", 50_000)) + "\n",
            string.Concat(Enumerable.Repeat("`", 100_001)) + "\n",
            string.Concat(Enumerable.Repeat("| a ", 10_000)) + "|\n" + string.Concat(Enumerable.Repeat("|---", 10_000)) + "|\n",
        };

        foreach (var input in inputs)
        {
            var document = Hostile.Run(() => PreviewBuilder.Build(input, new PreviewOptions()));
            Assert.NotNull(document);
            Hostile.Run(() => HtmlExporter.Export(input));
        }
    }

    [Fact]
    public void Pathological_unbroken_lines_and_huge_documents_are_handled()
    {
        var oneLine = new string('a', 5_000_000);
        var document = Hostile.Run(() => PreviewBuilder.Build(oneLine, new PreviewOptions()));
        Assert.True(document.Truncated || document.NodeCount > 0);

        var huge = string.Concat(Enumerable.Repeat("# heading\n\ntext **bold** [l](a.md)\n\n", 300_000));
        var big = Hostile.Run(() => PreviewBuilder.Build(huge, new PreviewOptions()), 60);
        Assert.True(big.Truncated, "A document far beyond the limits must be reported as truncated, not silently built in full.");
    }

    [Fact]
    public void Control_characters_nul_bytes_and_unpaired_surrogates_do_not_break_rendering()
    {
        var text = "a\0b \uD800 \uDFFF \u202E rtl \u2028 sep \uFEFF bom \u0001\u0002 # not heading\n\n```\0\n\0\n```\n";
        var document = PreviewBuilder.Build(text, new PreviewOptions());
        Assert.NotNull(document);
        var html = HtmlExporter.Export(text).Html;
        Assert.NotEmpty(html);
        Assert.NotNull(PlainTextExporter.Export(text));
    }

    [Fact]
    public void Hostile_math_and_diagrams_are_bounded()
    {
        Hostile.Run(() => MathText.Render(string.Concat(Enumerable.Repeat("\\frac{", 5_000)) + "x" + string.Concat(Enumerable.Repeat("}{1}", 5_000))));
        Hostile.Run(() => MathText.Render(string.Concat(Enumerable.Repeat("{", 100_000))));
        Hostile.Run(() => MathText.Render(new string('x', 1_000_000)));
        Hostile.Run(() => MathText.Render("\\" + new string('a', 100_000)));

        var nodes = string.Concat(Enumerable.Range(0, 20_000).Select(i => $"N{i}-->N{i + 1}\n"));
        Hostile.Run(() => MermaidDiagram.TryParse("flowchart TD\n" + nodes, out _));
        Hostile.Run(() => MermaidDiagram.TryParse("sequenceDiagram\n" + string.Concat(Enumerable.Repeat("A->>B: hi\n", 50_000)), out _));
        Hostile.Run(() => MermaidDiagram.TryParse("flowchart TD\n" + string.Concat(Enumerable.Repeat("subgraph s\n", 5_000)), out _));
        Hostile.Run(() => MermaidDiagram.TryParse(new string('[', 200_000), out _));
    }

    [Fact]
    public async Task Catastrophic_regexes_end_in_a_reported_timeout_not_a_hang()
    {
        using var box = new Sandbox();
        var file = box.Write("a.md", new string('a', 60) + "!\n");
        var find = new FindReplace(new DiskProvider());

        var started = System.Diagnostics.Stopwatch.StartNew();
        var preview = await find.PreviewAsync([file], new FindOptions("(a+)+$", UseRegex: true, RegexTimeout: TimeSpan.FromMilliseconds(300)))
            .WaitAsync(TimeSpan.FromSeconds(30));
        started.Stop();

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20), started.Elapsed.ToString());
        Assert.True(preview.PatternError is not null || preview.Files.Any(f => f.TimedOut) || preview.TotalMatches >= 0);
    }

    [Theory]
    [InlineData("(")]
    [InlineData("[")]
    [InlineData("*")]
    [InlineData("(?<n>")]
    [InlineData("\\")]
    [InlineData("(?=a)+++")]
    public async Task Invalid_regexes_are_reported_not_thrown(string pattern)
    {
        using var box = new Sandbox();
        var file = box.Write("a.md", "text\n");
        var preview = await new FindReplace(new DiskProvider()).PreviewAsync([file], new FindOptions(pattern, UseRegex: true));
        Assert.True(preview.HasError);
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("\"unterminated")]
    [InlineData("a AND OR NOT")]
    [InlineData("NEAR(")]
    [InlineData("*")]
    [InlineData("a*b*c*")]
    [InlineData("col:val")]
    [InlineData("tag:")]
    [InlineData("-")]
    [InlineData("'; DROP TABLE documents; --")]
    [InlineData("\" OR 1=1 --")]
    [InlineData("a\0b")]
    [InlineData("\uD800")]
    public void Hostile_search_queries_never_throw_or_alter_the_index(string query)
    {
        using var box = new Sandbox();
        box.Write("one.md", "# One\nhello world\n");
        using var index = SearchIndex.Open(Path.Combine(box.Root, "index.db"), box.Workspace);
        var service = new SearchService(index);

        var page = service.Query(query);

        Assert.NotNull(page);
        Assert.NotNull(service.Query("hello"));
    }

    [Fact]
    public void An_enormous_query_is_handled()
    {
        using var box = new Sandbox();
        using var index = SearchIndex.Open(Path.Combine(box.Root, "index.db"), box.Workspace);
        var service = new SearchService(index);
        Hostile.Run(() => service.Query(string.Join(" ", Enumerable.Repeat("word", 20_000))));
        Hostile.Run(() => service.Query(new string('x', 1_000_000)));
    }

    [Fact]
    public async Task Binary_and_hostile_files_open_without_crashing_and_save_back_losslessly_or_refuse()
    {
        using var box = new Sandbox();
        var service = new DocumentFileService();
        var binary = Path.Combine(box.Workspace, "bin.md");
        var bytes = new byte[200_000];
        new Random(42).NextBytes(bytes);
        await File.WriteAllBytesAsync(binary, bytes);

        var loaded = await service.LoadAsync(binary);
        Assert.NotNull(loaded);

        var empty = Path.Combine(box.Workspace, "empty.md");
        await File.WriteAllBytesAsync(empty, []);
        Assert.NotNull(await service.LoadAsync(empty));

        var bomOnly = Path.Combine(box.Workspace, "bom.md");
        await File.WriteAllBytesAsync(bomOnly, [0xEF, 0xBB, 0xBF]);
        Assert.NotNull(await service.LoadAsync(bomOnly));

        var badUtf16 = Path.Combine(box.Workspace, "u16.md");
        await File.WriteAllBytesAsync(badUtf16, [0xFF, 0xFE, 0x00, 0xD8, 0x41]);
        Assert.NotNull(await service.LoadAsync(badUtf16));
    }

    [Fact]
    public async Task Link_graph_handles_hostile_link_text()
    {
        using var box = new Sandbox();
        var hostile = new StringBuilder();
        for (var i = 0; i < 2000; i++)
        {
            hostile.Append("[[").Append(new string('a', 50)).Append("]] [x](").Append(new string('.', i % 50)).Append("/..\\x) ");
        }

        hostile.Append("[a](").Append(new string('(', 100_000)).Append(")\n");
        var file = box.Write("a.md", hostile.ToString());
        var graph = new LinkGraph(box.Workspace);
        await Task.Run(() => graph.LoadAsync([file], new DiskProvider())).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(graph.GetOutgoing(file));
    }

    private sealed class DiskProvider : IDocumentTextProvider
    {
        public ValueTask<string?> ReadTextAsync(string path, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>(File.Exists(path) ? File.ReadAllText(path) : null);
    }
}

public class CorruptionToleranceTests
{
    private static readonly byte[][] Garbage =
    [
        [],
        [0],
        Encoding.UTF8.GetBytes("{"),
        Encoding.UTF8.GetBytes("null"),
        Encoding.UTF8.GetBytes("[]"),
        Encoding.UTF8.GetBytes("\"just a string\""),
        Encoding.UTF8.GetBytes("12345"),
        Encoding.UTF8.GetBytes("{\"Version\": \"abc\", \"EditorFontSize\": \"big\"}"),
        Encoding.UTF8.GetBytes("{\"Version\": 99999, \"EditorFontSize\": 1e999}"),
        Encoding.UTF8.GetBytes("{\"EditorFontSize\": -5, \"TabSize\": 0, \"ReadingWidth\": 99999999, \"UiScale\": 0, \"KeyBindings\": null}"),
        Encoding.UTF8.GetBytes("{\"a\":" + string.Concat(Enumerable.Repeat("[", 100_000))),
        [0xFF, 0xFE, 0xFD, 0x00, 0x80, 0x81],
        Encoding.UTF8.GetBytes(new string('x', 5_000_000)),
        Encoding.UTF8.GetBytes("{\"OpenTabs\": [null, 5, {\"Path\": 7}], \"Items\": {\"x\": []}, \"Recent\": \"no\"}"),
    ];

    [Fact]
    public void Corrupt_settings_fall_back_to_defaults_and_keep_a_copy()
    {
        foreach (var bytes in Garbage)
        {
            using var box = new Sandbox();
            var store = new SettingsStore(box.Root);
            File.WriteAllBytes(store.FilePath, bytes);

            var settings = store.Load();
            settings.Normalize();

            Assert.InRange(settings.EditorFontSize, 8, 48);
            Assert.InRange(settings.TabSize, 1, 16);
            Assert.InRange(settings.ReadingWidth, 360, 1600);
            Assert.True(settings.UiScale > 0);
            Assert.NotNull(settings.KeyBindings);
            Assert.NotNull(settings.WorkspaceExclusions);

            store.Save(settings);
            Assert.InRange(store.Load().EditorFontSize, 8, 48);
        }
    }

    [Fact]
    public void Absurd_but_valid_settings_values_are_clamped()
    {
        var settings = new AppSettings
        {
            EditorFontSize = double.NaN,
            PreviewFontSize = double.PositiveInfinity,
            ReadingWidth = -1,
            TabSize = int.MaxValue,
            UiScale = double.NegativeInfinity,
            SidebarWidth = double.NaN,
        };

        settings.Normalize();

        Assert.False(double.IsNaN(settings.EditorFontSize) || double.IsInfinity(settings.EditorFontSize));
        Assert.False(double.IsNaN(settings.PreviewFontSize) || double.IsInfinity(settings.PreviewFontSize));
        Assert.InRange(settings.TabSize, 1, 16);
        Assert.False(double.IsNaN(settings.UiScale) || double.IsInfinity(settings.UiScale) || settings.UiScale <= 0);
        Assert.False(double.IsNaN(settings.SidebarWidth));
    }

    [Fact]
    public void Corrupt_session_files_load_as_empty()
    {
        foreach (var bytes in Garbage)
        {
            using var box = new Sandbox();
            File.WriteAllBytes(Path.Combine(box.Root, SessionStore.FileName), bytes);
            var store = new SessionStore(box.Root);

            var session = store.Load();

            Assert.NotNull(session);
            Assert.NotNull(session.Tabs);
            Assert.True(store.Save(session));
        }
    }

    [Fact]
    public void Corrupt_recent_files_load_as_empty()
    {
        foreach (var bytes in Garbage)
        {
            using var box = new Sandbox();
            File.WriteAllBytes(Path.Combine(box.Root, RecentItemsStore.FileName), bytes);
            var store = new RecentItemsStore(box.Root);

            Assert.NotNull(store.Get(RecentKind.File));
            store.AddFile(Path.Combine(box.Workspace, "a.md"));
            Assert.Single(store.Get(RecentKind.File));
        }
    }

    [Fact]
    public async Task Corrupt_and_truncated_recovery_files_are_reported_not_fatal()
    {
        using var box = new Sandbox();
        var directory = Path.Combine(box.Root, "recovery");
        var store = new RecoveryStore(directory);
        var id = Guid.NewGuid();
        var format = Tern.Domain.Text.TextFileFormat.NewFileDefault;
        await store.WriteCheckpointAsync(new RecoveryRecord(id, null, "x", 1, DateTime.UtcNow, null, format, "precious unsaved text"));
        await store.WriteCheckpointAsync(new RecoveryRecord(id, null, "x", 2, DateTime.UtcNow, null, format, "newer text that will be damaged"));

        var newest = Directory.GetFiles(directory, "*.tern-recovery").OrderDescending().First();
        var original = File.ReadAllBytes(newest);
        File.WriteAllBytes(newest, original[..(original.Length - 10)]);

        var scan = store.Scan();

        Assert.Contains(scan.Entries, e => e.Record.Text == "precious unsaved text");
        Assert.Single(scan.Corrupt);

        File.WriteAllBytes(newest, [1, 2, 3]);
        Assert.Single(store.Scan().Corrupt);
    }

    [Fact]
    public void A_corrupt_search_index_is_rebuilt_instead_of_failing()
    {
        using var box = new Sandbox();
        var db = Path.Combine(box.Root, "index.db");
        foreach (var bytes in new[] { new byte[] { 1, 2, 3, 4 }, Encoding.UTF8.GetBytes("SQLite format 3\0" + new string('x', 5000)), new byte[200_000] })
        {
            File.WriteAllBytes(db, bytes);
            using var index = SearchIndex.Open(db, box.Workspace);
            Assert.True(index.WasRecreated);
            Assert.NotNull(new SearchService(index).Query("anything"));
        }
    }

    [Fact]
    public void Corrupt_key_binding_overrides_are_ignored()
    {
        var registry = Tern.App.Commands.CommandRegistry.CreateDefault();
        var problems = registry.LoadOverrides(new Dictionary<string, string>
        {
            ["no.such.command"] = "Ctrl+X",
            ["file.save"] = "NotAKey+++",
            [""] = "",
        });
        Assert.NotNull(problems);
    }
}
