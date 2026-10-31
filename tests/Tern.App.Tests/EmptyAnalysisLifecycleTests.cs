using System.Diagnostics;
using Tern.App.Documents;
using Tern.Domain.Text;
using Tern.Filesystem;
using Tern.Markdown;

namespace Tern.App.Tests;

/// <summary>Checks queued publication ordering rather than timing or implementation counters.</summary>
public sealed class EmptyAnalysisLifecycleTests
{
    [Theory]
    [InlineData(MarkdownProfile.CommonMark)]
    [InlineData(MarkdownProfile.Gfm)]
    [InlineData(MarkdownProfile.Extended)]
    public void Empty_analysis_matches_the_public_analyzer_contract(MarkdownProfile profile)
    {
        var context = new ManualContext();
        using var tab = NewTab(context, profile);
        context.DrainLatestFirst();
        var expected = MarkdownAnalyzer.Analyze(string.Empty, profile);
        Assert.NotNull(tab.Analysis);
        Assert.Equal(expected.Profile, tab.Analysis.Profile);
        Assert.Equal(expected.Lines.LineCount, tab.Analysis.Lines.LineCount);
        Assert.Equal(expected.WordCount, tab.Analysis.WordCount);
        Assert.Equal(expected.CharacterCount, tab.Analysis.CharacterCount);
        Assert.Empty(tab.Analysis.Headings);
        Assert.Empty(tab.Analysis.Links);
        Assert.Empty(tab.Analysis.Tasks);
        Assert.Empty(tab.Analysis.CodeBlocks);
        Assert.Empty(tab.Analysis.Tables);
        Assert.Empty(tab.Analysis.Footnotes);
        Assert.Empty(tab.Analysis.ExplicitAnchors);
        Assert.Null(tab.Analysis.FrontMatter);
    }

    [Fact]
    public async Task Clearing_text_rejects_an_already_queued_nonempty_analysis_result()
    {
        var context = new ManualContext();
        using var tab = NewTab(context, MarkdownProfile.Extended);
        context.DrainLatestFirst();
        tab.ApplyEdits([TextEdit.Insert(0, "# Previous heading\n\n[old](old.md)\n")]);
        var clock = Stopwatch.StartNew();
        while (context.Count == 0 && clock.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
        Assert.True(context.Count > 0, "The old nonempty result must be queued before clearing.");
        tab.ApplyEdits([TextEdit.Delete(0, tab.CurrentText.Length)]);
        context.DrainLatestFirst();
        Assert.NotNull(tab.Analysis);
        Assert.Equal(0, tab.Analysis.CharacterCount);
        Assert.Empty(tab.Analysis.Headings);
        Assert.Empty(tab.Analysis.Links);
        Assert.Equal(1, tab.Analysis.Lines.LineCount);
    }

    [Fact]
    public void Profile_change_on_empty_text_supersedes_an_earlier_queued_profile()
    {
        var context = new ManualContext();
        using var tab = NewTab(context, MarkdownProfile.CommonMark);
        tab.SetProfile(MarkdownProfile.Extended);
        context.DrainLatestFirst();
        Assert.NotNull(tab.Analysis);
        Assert.Equal(MarkdownProfile.Extended, tab.Analysis.Profile);
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public void Disposed_document_cannot_publish_a_queued_empty_result()
    {
        var context = new ManualContext();
        var tab = NewTab(context, MarkdownProfile.Extended);
        var events = 0;
        tab.AnalysisChanged += (_, _) => events++;
        tab.Dispose();
        context.DrainLatestFirst();
        Assert.Equal(0, events);
        Assert.Null(tab.Analysis);
    }

    private static DocumentTab NewTab(ManualContext context, MarkdownProfile profile)
    {
        var previous = SynchronizationContext.Current;
        try { SynchronizationContext.SetSynchronizationContext(context); return DocumentTab.CreateUntitled(new DocumentFileService(), profile); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private sealed class ManualContext : SynchronizationContext
    {
        private readonly object _gate = new();
        private readonly List<(SendOrPostCallback Callback, object? State)> _pending = [];
        public int Count { get { lock (_gate) return _pending.Count; } }
        public override void Post(SendOrPostCallback d, object? state) { lock (_gate) _pending.Add((d, state)); }
        public void DrainLatestFirst()
        {
            (SendOrPostCallback Callback, object? State)[] callbacks;
            lock (_gate) { callbacks = _pending.ToArray(); _pending.Clear(); }
            foreach (var (callback, state) in callbacks.Reverse()) callback(state);
        }
    }
}
