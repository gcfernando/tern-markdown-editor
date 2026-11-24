namespace Tern.Domain.Merging;

/// <summary>One run of differing lines; start lines are where each side's changed run begins (both are valid line indexes or the insertion point).</summary>
public sealed record DiffBlock(int LeftStart, int LeftCount, int RightStart, int RightCount);

/// <summary>Line-level comparison of two texts for side-by-side display: which lines differ and where each change block starts.</summary>
public sealed class DiffMap
{
    private DiffMap(string[] left, string[] right, bool[] leftChanged, bool[] rightChanged, IReadOnlyList<DiffBlock> blocks, bool analyzed)
    {
        LeftLines = left; RightLines = right; LeftChanged = leftChanged; RightChanged = rightChanged; Blocks = blocks; Analyzed = analyzed;
    }

    /// <summary>Beyond this many lines per side highlighting is skipped and the panes stay plain text.</summary>
    public const int MaxLines = 5000;

    public string[] LeftLines { get; }
    public string[] RightLines { get; }
    public bool[] LeftChanged { get; }
    public bool[] RightChanged { get; }
    public IReadOnlyList<DiffBlock> Blocks { get; }

    /// <summary>False when the texts were too large to compare; there are then no highlights and no blocks.</summary>
    public bool Analyzed { get; }

    public int LeftChangedCount => LeftChanged.Count(c => c);
    public int RightChangedCount => RightChanged.Count(c => c);

    public static DiffMap Compute(string left, string right)
    {
        var a = LineSplitter.Split(left);
        var b = LineSplitter.Split(right);
        if (a.Length > MaxLines || b.Length > MaxLines)
            return new DiffMap(a, b, new bool[a.Length], new bool[b.Length], [], analyzed: false);

        var matches = LineDiff.MatchLines(LineSplitter.Keys(a), LineSplitter.Keys(b));
        var leftChanged = new bool[a.Length];
        var rightChanged = Enumerable.Repeat(true, b.Length).ToArray();
        for (var i = 0; i < a.Length; i++)
        {
            leftChanged[i] = matches[i] < 0;
            if (matches[i] >= 0) rightChanged[matches[i]] = false;
        }

        var blocks = new List<DiffBlock>();
        int i0 = 0, j0 = 0;
        while (i0 < a.Length || j0 < b.Length)
        {
            if (i0 < a.Length && matches[i0] >= 0 && matches[i0] == j0) { i0++; j0++; continue; }
            var i1 = i0;
            while (i1 < a.Length && matches[i1] < 0) i1++;
            var j1 = Math.Max(i1 < a.Length ? matches[i1] : b.Length, j0);
            blocks.Add(new DiffBlock(i0, i1 - i0, j0, j1 - j0));
            i0 = i1; j0 = j1;
        }

        return new DiffMap(a, b, leftChanged, rightChanged, blocks, analyzed: true);
    }
}
