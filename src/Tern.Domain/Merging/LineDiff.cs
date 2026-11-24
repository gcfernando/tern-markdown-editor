namespace Tern.Domain.Merging;

/// <summary>Splits text into lines that keep their terminators, so merges never alter line endings.</summary>
public static class LineSplitter
{
    public static string[] Split(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n')
            {
                lines.Add(text[start..(i + 1)]);
                start = i + 1;
            }
            else if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                lines.Add(text[start..(i + 1)]);
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return [.. lines];
    }

    /// <summary>Each line without its terminator; an unterminated last line is marked so adding a final newline still counts as a change.</summary>
    public static string[] Keys(string[] lines)
    {
        var keys = new string[lines.Length];
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var end = line.Length;
            while (end > 0 && line[end - 1] is '\n' or '\r') end--;
            keys[i] = end == line.Length ? line + "\0" : line[..end];
        }

        return keys;
    }
}

/// <summary>Line-ending helpers for merging text whose sides may use different conventions.</summary>
public static class LineEndings
{
    /// <summary>The most frequent terminator in the text (CRLF, LF or CR), or null when it has no line break.</summary>
    public static string? Dominant(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                else cr++;
            }
            else if (text[i] == '\n') lf++;
        }

        if (crlf + lf + cr == 0) return null;
        if (crlf >= lf && crlf >= cr) return "\r\n";
        return lf >= cr ? "\n" : "\r";
    }

    public static string Normalize(string text, string newLine)
    {
        if (text.Length == 0) return text;
        var builder = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                builder.Append(newLine);
            }
            else if (text[i] == '\n') builder.Append(newLine);
            else builder.Append(text[i]);
        }

        return builder.ToString();
    }
}

/// <summary>
/// Longest-common-subsequence matching of two line sequences (Myers O(ND)).
/// When the edit distance is too large to analyse cheaply, the unmatched middle is reported as
/// fully changed, which is the conservative choice for merging.
/// </summary>
public static class LineDiff
{
    private const int MaxEditDistance = 4000;
    private const long MaxTraceBytes = 8L * 1024 * 1024;

    /// <summary>For each index in <paramref name="a"/>, the matching index in <paramref name="b"/> or -1.</summary>
    public static int[] MatchLines(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var matches = new int[a.Count];
        Array.Fill(matches, -1);

        var prefix = 0;
        while (prefix < a.Count && prefix < b.Count && a[prefix] == b[prefix])
        {
            matches[prefix] = prefix;
            prefix++;
        }

        var suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix && a[a.Count - 1 - suffix] == b[b.Count - 1 - suffix])
        {
            matches[a.Count - 1 - suffix] = b.Count - 1 - suffix;
            suffix++;
        }

        var aMid = a.Count - prefix - suffix;
        var bMid = b.Count - prefix - suffix;
        if (aMid > 0 && bMid > 0)
        {
            MyersMatch(a, prefix, aMid, b, prefix, bMid, matches);
        }

        return matches;
    }

    private static void MyersMatch(IReadOnlyList<string> a, int aStart, int n, IReadOnlyList<string> b, int bStart, int m, int[] matches)
    {
        var max = Math.Min(n + m, MaxEditDistance);
        var offset = max + 1;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>();

        var found = false;
        for (var d = 0; d <= max && !found; d++)
        {
            // Fall back to an entirely changed middle before exceeding the budget.
            if ((long)(trace.Count + 1) * v.Length * sizeof(int) > MaxTraceBytes) return;
            trace.Add((int[])v.Clone());
            for (var k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]))
                {
                    x = v[offset + k + 1];
                }
                else
                {
                    x = v[offset + k - 1] + 1;
                }

                var y = x - k;
                while (x < n && y < m && a[aStart + x] == b[bStart + y])
                {
                    x++;
                    y++;
                }

                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    found = true;
                    break;
                }
            }
        }

        if (!found)
        {
            return;
        }

        var px = n;
        var py = m;
        for (var d = trace.Count - 1; d >= 0; d--)
        {
            var vd = trace[d];
            var k = px - py;
            int prevK;
            if (k == -d || (k != d && vd[offset + k - 1] < vd[offset + k + 1]))
            {
                prevK = k + 1;
            }
            else
            {
                prevK = k - 1;
            }

            var prevX = vd[offset + prevK];
            var prevY = prevX - prevK;

            while (px > prevX && py > prevY)
            {
                px--;
                py--;
                matches[aStart + px] = bStart + py;
            }

            if (d > 0)
            {
                px = prevX;
                py = prevY;
            }
        }
    }
}
