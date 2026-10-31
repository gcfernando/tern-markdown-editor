using System.Globalization;
using System.Text;
using Tern.Workspaces;

namespace Tern.Search;

public sealed record QuickOpenMatch<T>(T Item, double Score, IReadOnlyList<int> MatchedIndices);

/// <param name="Path">Absolute path of the document containing the heading.</param>
public sealed record HeadingCandidate(string Path, string Text, int Level, int Line)
{
    public static IEnumerable<HeadingCandidate> FromGraph(LinkGraph graph) =>
        graph.GetHeadings().Select(h => new HeadingCandidate(h.Path, h.Heading.Text, h.Heading.Level, h.Heading.Line));
}

public enum QuickOpenMode
{
    Files,
    Headings,
}

public sealed record QuickOpenResults(
    QuickOpenMode Mode,
    IReadOnlyList<QuickOpenMatch<string>> Files,
    IReadOnlyList<QuickOpenMatch<HeadingCandidate>> Headings);

/// <summary>
/// Fuzzy matching for the file switcher and command palette. A query matches when its characters appear in order
/// (case and diacritics ignored). Scores favour characters at word starts and after path separators, consecutive runs,
/// matches within the file name, and short candidates. A query with spaces is several fuzzy terms that must all match.
/// </summary>
public static class QuickOpen
{
    private const double GapLeading = -0.02;
    private const double GapTrailing = -0.005;
    private const double GapInner = -0.01;
    private const double Consecutive = 1.0;
    private const double FileNameBonus = 1.5;
    private const int MaxNeedle = 64;
    private const int MaxHaystack = 600;

    public static IReadOnlyList<QuickOpenMatch<string>> Match(string? query, IEnumerable<string> candidates, int limit = 50) =>
        Match(query, candidates, static s => s, limit);

    public static IReadOnlyList<QuickOpenMatch<T>> Match<T>(string? query, IEnumerable<T> candidates, Func<T, string> text, int limit = 50)
    {
        limit = Math.Max(1, limit);
        var terms = SplitTerms(query);
        var results = new List<QuickOpenMatch<T>>();
        foreach (var candidate in candidates)
        {
            var value = text(candidate);
            if (TryMatchTerms(terms, value, out var score, out var indices))
            {
                results.Add(new QuickOpenMatch<T>(candidate, score, indices));
            }
        }

        return [.. results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => text(r.Item).Length)
            .ThenBy(r => text(r.Item), StringComparer.Ordinal)
            .Take(limit)];
    }

    public static bool TryMatch(string? query, string candidate, out double score, out IReadOnlyList<int> matchedIndices) =>
        TryMatchTerms(SplitTerms(query), candidate, out score, out matchedIndices);

    public static IReadOnlyList<QuickOpenMatch<HeadingCandidate>> MatchHeadings(string? query, IEnumerable<HeadingCandidate> headings, int limit = 50) =>
        Match(query, headings, static h => h.Text, limit);

    /// <summary>A query starting with <c>#</c> searches headings across the workspace (the rest of the query is the heading text); anything else searches files.</summary>
    public static QuickOpenResults Search(string? query, IEnumerable<string> files, IEnumerable<HeadingCandidate> headings, int limit = 50)
    {
        var trimmed = query?.TrimStart() ?? string.Empty;
        if (trimmed.StartsWith('#'))
        {
            return new QuickOpenResults(QuickOpenMode.Headings, [], MatchHeadings(trimmed.TrimStart('#'), headings, limit));
        }

        return new QuickOpenResults(QuickOpenMode.Files, Match(trimmed, files, limit), []);
    }

    private static List<Rune[]> SplitTerms(string? query)
    {
        var terms = new List<Rune[]>();
        if (string.IsNullOrWhiteSpace(query))
        {
            return terms;
        }

        foreach (var part in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var runes = Fold(part).Select(f => f.Folded).ToArray();
            if (runes.Length > 0)
            {
                terms.Add(runes.Length > MaxNeedle ? runes[..MaxNeedle] : runes);
            }
        }

        return terms;
    }

    private static bool TryMatchTerms(List<Rune[]> terms, string candidate, out double score, out IReadOnlyList<int> indices)
    {
        score = 0;
        indices = [];
        if (terms.Count == 0)
        {
            return true;
        }

        var hay = Fold(candidate);
        if (hay.Count > MaxHaystack)
        {
            hay = hay.GetRange(0, MaxHaystack);
        }

        var fileNameStart = FileNameStart(hay);
        var all = new SortedSet<int>();
        foreach (var term in terms)
        {
            if (!ScoreTerm(term, hay, out var termScore, out var positions))
            {
                score = 0;
                indices = [];
                return false;
            }

            score += termScore;
            foreach (var position in positions)
            {
                all.Add(hay[position].Index);
            }

            if (positions.All(p => p >= fileNameStart))
            {
                score += FileNameBonus;
            }
        }

        indices = [.. all];
        return true;
    }

    private static bool ScoreTerm(Rune[] needle, List<FoldedRune> hay, out double score, out int[] positions)
    {
        score = 0;
        positions = [];
        var n = needle.Length;
        var m = hay.Count;
        if (n > m || !IsSubsequence(needle, hay))
        {
            return false;
        }

        var bonus = new double[m];
        for (var j = 0; j < m; j++)
        {
            bonus[j] = Bonus(hay, j);
        }

        var d = new double[n, m];
        var best = new double[n, m];
        for (var i = 0; i < n; i++)
        {
            var previous = double.NegativeInfinity;
            var gap = i == n - 1 ? GapTrailing : GapInner;
            for (var j = 0; j < m; j++)
            {
                if (needle[i] == hay[j].Folded)
                {
                    var value = double.NegativeInfinity;
                    if (i == 0)
                    {
                        value = (j * GapLeading) + bonus[j];
                    }
                    else if (j > 0)
                    {
                        value = Math.Max(best[i - 1, j - 1] + bonus[j], d[i - 1, j - 1] + Consecutive);
                    }

                    d[i, j] = value;
                    previous = Math.Max(value, previous + gap);
                }
                else
                {
                    d[i, j] = double.NegativeInfinity;
                    previous += gap;
                }

                best[i, j] = previous;
            }
        }

        score = best[n - 1, m - 1];
        positions = new int[n];
        var matchRequired = false;
        var column = m - 1;
        for (var i = n - 1; i >= 0; i--)
        {
            for (; column >= 0; column--)
            {
                if (!double.IsNegativeInfinity(d[i, column]) && (matchRequired || d[i, column] == best[i, column]))
                {
                    matchRequired = i > 0 && column > 0 && best[i, column] == d[i - 1, column - 1] + Consecutive;
                    positions[i] = column--;
                    break;
                }
            }
        }

        return true;
    }

    private static double Bonus(List<FoldedRune> hay, int j)
    {
        if (j == 0)
        {
            return 0.8;
        }

        var previous = hay[j - 1].Original;
        var current = hay[j].Original;
        if (previous.Value is '/' or '\\')
        {
            return 0.9;
        }

        if (previous.Value is ' ' or '-' or '_')
        {
            return 0.8;
        }

        if (previous.Value == '.')
        {
            return 0.6;
        }

        if (Rune.IsLower(previous) && Rune.IsUpper(current))
        {
            return 0.7;
        }

        return 0;
    }

    private static int FileNameStart(List<FoldedRune> hay)
    {
        for (var j = hay.Count - 1; j >= 0; j--)
        {
            if (hay[j].Original.Value is '/' or '\\')
            {
                return j + 1;
            }
        }

        return 0;
    }

    private static bool IsSubsequence(Rune[] needle, List<FoldedRune> hay)
    {
        var i = 0;
        foreach (var rune in hay)
        {
            if (i < needle.Length && rune.Folded == needle[i])
            {
                i++;
            }
        }

        return i == needle.Length;
    }

    // Each rune keeps its UTF-16 index so highlights map back onto the original string, surrogate pairs included.
    private static List<FoldedRune> Fold(string text)
    {
        var result = new List<FoldedRune>(text.Length);
        var index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            result.Add(new FoldedRune(index, rune, FoldRune(rune)));
            index += rune.Utf16SequenceLength;
        }

        return result;
    }

    private static Rune FoldRune(Rune rune)
    {
        if (rune.IsAscii)
        {
            return Rune.ToLowerInvariant(rune);
        }

        var decomposed = rune.ToString().Normalize(NormalizationForm.FormD);
        foreach (var candidate in decomposed.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(candidate) != UnicodeCategory.NonSpacingMark)
            {
                return Rune.ToLowerInvariant(candidate);
            }
        }

        return Rune.ToLowerInvariant(rune);
    }

    private readonly record struct FoldedRune(int Index, Rune Original, Rune Folded);
}
