using System.Reflection;
using AvaloniaEdit.Document;
using Tern.Desktop.Editing;

namespace Tern.Desktop.Tests;

public sealed class HighlightingPerformanceTests
{
    private static readonly MethodInfo Refresh = typeof(MarkdownColorizer).GetMethod("EnsureKinds", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public void Body_edit_in_large_document_rechecks_only_changed_state_and_fences_propagate()
    {
        var text = new TextDocument(string.Join('\n', Enumerable.Repeat("body text", 100_000)));
        var colorizer = new MarkdownColorizer();
        colorizer.Attach(text);
        Refresh.Invoke(colorizer, [text]);
        text.Insert(text.GetLineByNumber(50_000).Offset, "x");
        Refresh.Invoke(colorizer, [text]);
        Assert.InRange(colorizer.StateLinesVisited, 1, 2);
        text.Insert(text.GetLineByNumber(50_000).Offset, "```\n");
        Refresh.Invoke(colorizer, [text]);
        var kinds = (Array)typeof(MarkdownColorizer).GetField("_kinds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(colorizer)!;
        Assert.Equal("FenceEdge", kinds.GetValue(49_999)!.ToString());
        Assert.Equal("FenceBody", kinds.GetValue(99_999)!.ToString());
        colorizer.Attach(null);
    }
}
