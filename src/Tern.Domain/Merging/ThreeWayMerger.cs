using System.Text;

namespace Tern.Domain.Merging;

public abstract record MergeRegion;

/// <summary>Text on which both sides agree, or where only one side changed.</summary>
public sealed record ResolvedRegion(string Text) : MergeRegion;

/// <summary>Both sides changed the same lines differently; a human must decide.</summary>
public sealed record ConflictRegion(string Base, string Mine, string Disk) : MergeRegion
{
    /// <summary>Keeps both sides as separate lines; a newline is inserted when Mine ends without one (B-23).</summary>
    public string Both(string newLine) =>
        Mine.Length == 0 || Mine[^1] is '\n' or '\r' || Disk.Length == 0 ? Mine + Disk : Mine + newLine + Disk;
}

/// <param name="NewLine">The line ending of the open document, used wherever the merge has to add a line break.</param>
public sealed record MergeResult(IReadOnlyList<MergeRegion> Regions, string NewLine = "\n")
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

    private string WithMarkers(ConflictRegion c)
    {
        string EnsureNewline(string s) => s.Length == 0 || s[^1] is '\n' or '\r' ? s : s + NewLine;
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
        var newLine = LineEndings.Dominant(mine) ?? LineEndings.Dominant(disk) ?? LineEndings.Dominant(baseText) ?? "\n";
        if (mine == disk)
        {
            return new MergeResult([new ResolvedRegion(mine)], newLine);
        }

        if (mine == baseText)
        {
            return new MergeResult([new ResolvedRegion(disk)], newLine);
        }

        if (disk == baseText)
        {
            return new MergeResult([new ResolvedRegion(mine)], newLine);
        }

        var o = LineSplitter.Split(baseText);
        var a = LineSplitter.Split(mine);
        var b = LineSplitter.Split(disk);
        // Lines match by content, so a tool that only converted CRLF to LF does not turn every line into a conflict (B-34).
        var oKeys = LineSplitter.Keys(o);
        var aKeys = LineSplitter.Keys(a);
        var bKeys = LineSplitter.Keys(b);
        var matchA = LineDiff.MatchLines(oKeys, aKeys);
        var matchB = LineDiff.MatchLines(oKeys, bKeys);

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

            AddChunk(regions, Slice(o, oPrev + 1, i), Slice(a, aPrev + 1, aNext), Slice(b, bPrev + 1, bNext),
                Slice(oKeys, oPrev + 1, i), Slice(aKeys, aPrev + 1, aNext), Slice(bKeys, bPrev + 1, bNext));

            if (i < o.Length)
            {
                // The anchor line is emitted from the open document so its own terminator survives.
                AddResolved(regions, a[matchA[i]]);
            }

            oPrev = i;
            aPrev = aNext;
            bPrev = bNext;
        }

        if (LineEndings.Dominant(disk) is { } diskEnding && diskEnding != newLine)
        {
            regions = [.. regions.Select(r => r switch
            {
                ResolvedRegion resolved => (MergeRegion)new ResolvedRegion(LineEndings.Normalize(resolved.Text, newLine)),
                ConflictRegion c => new ConflictRegion(LineEndings.Normalize(c.Base, newLine), LineEndings.Normalize(c.Mine, newLine), LineEndings.Normalize(c.Disk, newLine)),
                _ => r,
            })];
        }

        return new MergeResult(regions, newLine);
    }

    private static void AddChunk(List<MergeRegion> regions, string[] o, string[] a, string[] b, string[] oKeys, string[] aKeys, string[] bKeys)
    {
        if (o.Length == 0 && a.Length == 0 && b.Length == 0)
        {
            return;
        }

        if (aKeys.AsSpan().SequenceEqual(oKeys))
        {
            AddResolved(regions, string.Concat(b));
        }
        else if (bKeys.AsSpan().SequenceEqual(oKeys) || aKeys.AsSpan().SequenceEqual(bKeys))
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
