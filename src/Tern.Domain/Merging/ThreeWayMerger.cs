using System.Text;

namespace Tern.Domain.Merging;

public abstract record MergeRegion;

/// <summary>Text on which both sides agree, or where only one side changed.</summary>
public sealed record ResolvedRegion(string Text) : MergeRegion;

/// <summary>Both sides changed the same lines differently; a human must decide.</summary>
public sealed record ConflictRegion(string Base, string Mine, string Disk) : MergeRegion;

public sealed record MergeResult(IReadOnlyList<MergeRegion> Regions)
{
    public bool HasConflicts => Regions.Any(r => r is ConflictRegion);

    public int ConflictCount => Regions.Count(r => r is ConflictRegion);

    /// <summary>The merged text; only meaningful when <see cref="HasConflicts"/> is false.</summary>
    public string MergedText => Render(null);

    /// <summary>Renders the result, substituting each conflict using the supplied resolver (default: conflict markers).</summary>
    public string Render(Func<ConflictRegion, string>? resolveConflict)
    {
        var builder = new StringBuilder();
        foreach (var region in Regions)
        {
            switch (region)
            {
                case ResolvedRegion resolved:
                    builder.Append(resolved.Text);
                    break;
                case ConflictRegion conflict:
                    builder.Append(resolveConflict is null ? WithMarkers(conflict) : resolveConflict(conflict));
                    break;
            }
        }

        return builder.ToString();
    }

    private static string WithMarkers(ConflictRegion c)
    {
        static string EnsureNewline(string s) => s.Length == 0 || s[^1] is '\n' or '\r' ? s : s + "\n";
        return $"<<<<<<< Mine\n{EnsureNewline(c.Mine)}||||||| Base\n{EnsureNewline(c.Base)}=======\n{EnsureNewline(c.Disk)}>>>>>>> Disk\n";
    }
}

/// <summary>
/// Line-based diff3. Lines unchanged on both sides anchor the merge; each gap between anchors is
/// accepted if only one side (or both identically) changed it, otherwise it is reported as a conflict.
/// </summary>
public static class ThreeWayMerger
{
    public static MergeResult Merge(string baseText, string mine, string disk)
    {
        if (mine == disk)
        {
            return new MergeResult([new ResolvedRegion(mine)]);
        }

        if (mine == baseText)
        {
            return new MergeResult([new ResolvedRegion(disk)]);
        }

        if (disk == baseText)
        {
            return new MergeResult([new ResolvedRegion(mine)]);
        }

        var o = LineSplitter.Split(baseText);
        var a = LineSplitter.Split(mine);
        var b = LineSplitter.Split(disk);
        var matchA = LineDiff.MatchLines(o, a);
        var matchB = LineDiff.MatchLines(o, b);

        var regions = new List<MergeRegion>();
        int oPrev = -1, aPrev = -1, bPrev = -1;

        for (var i = 0; i <= o.Length; i++)
        {
            var isAnchor = i == o.Length || (matchA[i] >= 0 && matchB[i] >= 0);
            if (!isAnchor)
            {
                continue;
            }

            var aNext = i == o.Length ? a.Length : matchA[i];
            var bNext = i == o.Length ? b.Length : matchB[i];

            var oChunk = Slice(o, oPrev + 1, i);
            var aChunk = Slice(a, aPrev + 1, aNext);
            var bChunk = Slice(b, bPrev + 1, bNext);
            AddChunk(regions, oChunk, aChunk, bChunk);

            if (i < o.Length)
            {
                AddResolved(regions, o[i]);
            }

            oPrev = i;
            aPrev = aNext;
            bPrev = bNext;
        }

        return new MergeResult(regions);
    }

    private static void AddChunk(List<MergeRegion> regions, string[] o, string[] a, string[] b)
    {
        if (o.Length == 0 && a.Length == 0 && b.Length == 0)
        {
            return;
        }

        if (a.AsSpan().SequenceEqual(o))
        {
            AddResolved(regions, string.Concat(b));
        }
        else if (b.AsSpan().SequenceEqual(o) || a.AsSpan().SequenceEqual(b))
        {
            AddResolved(regions, string.Concat(a));
        }
        else
        {
            regions.Add(new ConflictRegion(string.Concat(o), string.Concat(a), string.Concat(b)));
        }
    }

    private static void AddResolved(List<MergeRegion> regions, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (regions.Count > 0 && regions[^1] is ResolvedRegion previous)
        {
            regions[^1] = new ResolvedRegion(previous.Text + text);
        }
        else
        {
            regions.Add(new ResolvedRegion(text));
        }
    }

    private static string[] Slice(string[] lines, int start, int end) =>
        end <= start ? [] : lines[start..end];
}
