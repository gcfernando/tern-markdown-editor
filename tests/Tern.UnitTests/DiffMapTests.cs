using Tern.Domain.Merging;

namespace Tern.UnitTests;

public sealed class DiffMapTests
{
    [Fact]
    public void Identical_texts_have_no_blocks_even_when_only_line_endings_differ()
    {
        var map = DiffMap.Compute("a\r\nb\r\n", "a\nb\n");
        Assert.True(map.Analyzed);
        Assert.Empty(map.Blocks);
        Assert.Equal(0, map.LeftChangedCount + map.RightChangedCount);
    }

    [Fact]
    public void Changed_inserted_and_removed_lines_form_separate_navigable_blocks()
    {
        var map = DiffMap.Compute("one\ntwo\nthree\nfour\nfive\n", "one\nTWO\nthree\nfour\nfive\nsix\n");
        Assert.Equal([new DiffBlock(1, 1, 1, 1), new DiffBlock(5, 0, 5, 1)], map.Blocks);
        Assert.Equal([false, true, false, false, false], map.LeftChanged);
        Assert.Equal([false, true, false, false, false, true], map.RightChanged);
        Assert.Equal(1, map.LeftChangedCount);
        Assert.Equal(2, map.RightChangedCount);
    }

    [Fact]
    public void Deleting_a_line_reports_a_left_only_block_at_the_right_insertion_point()
    {
        var map = DiffMap.Compute("a\nb\nc\n", "a\nc\n");
        Assert.Equal([new DiffBlock(1, 1, 1, 0)], map.Blocks);
    }

    [Fact]
    public void Texts_beyond_the_highlight_limit_stay_unanalyzed()
    {
        var big = string.Concat(Enumerable.Repeat("x\n", DiffMap.MaxLines + 1));
        var map = DiffMap.Compute(big, "y\n");
        Assert.False(map.Analyzed);
        Assert.Empty(map.Blocks);
    }
}
