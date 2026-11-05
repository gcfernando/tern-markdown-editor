using Tern.Domain.Merging;

namespace Tern.UnitTests;

public sealed class MergeMemoryTests
{
    [Fact]
    public void Unrelated_large_inputs_use_bounded_trace_and_conservative_fallback()
    {
        var a = Enumerable.Range(0, 5000).Select(i => "before " + i).ToArray();
        var b = Enumerable.Range(0, 5000).Select(i => "after " + i).ToArray();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var matches = LineDiff.MatchLines(a, b);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.All(matches, value => Assert.Equal(-1, value));
        Assert.True(allocated < 9 * 1024 * 1024, $"Diff allocated {allocated:N0} bytes.");
    }
}
