namespace Tern.Search;

/// <summary>
/// Evaluates a <see cref="ParsedQuery"/> against an in-memory document, for unsaved buffers that are not in the SQLite index.
/// Matching follows the same rules as the FTS expression (all positive clauses, no negative clause, phrases adjacent);
/// scores are a BM25-shaped approximation using the on-disk corpus statistics so they sort sensibly next to indexed hits.
/// </summary>
internal static class InMemoryMatcher
{
    // Same column weights as the bm25() call in SearchService: title, headings, tags, path, body.
    internal const double TitleWeight = 10;
    internal const double HeadingWeight = 5;
    internal const double TagWeight = 4;
    internal const double PathWeight = 2;
    internal const double BodyWeight = 1;

    private const double K1 = 1.2;
    private const double B = 0.75;

    /// <returns>The score, or null when the document does not satisfy the query.</returns>
    public static double? Score(OverlayDocument document, ParsedQuery query, CorpusStats corpus)
    {
        var words = document.Words;
        foreach (var negative in query.Negative)
        {
            if (Columns(negative, words).Any(c => Count(c.Words, negative) > 0))
            {
                return null;
            }
        }

        var n = Math.Max(corpus.DocumentCount, 1);
        var idf = Math.Log(1 + ((n - 0.5) / 1.5));
        var avg = corpus.AverageTokenCount > 0 ? corpus.AverageTokenCount : Math.Max(words.Body.Count, 1);
        var score = 0.0;
        foreach (var clause in query.Positive)
        {
            var clauseScore = 0.0;
            foreach (var (columnWords, weight, isBody) in Columns(clause, words))
            {
                var tf = Count(columnWords, clause);
                if (tf == 0)
                {
                    continue;
                }

                var length = isBody ? columnWords.Count : Math.Max(columnWords.Count, 1);
                var average = isBody ? avg : length;
                clauseScore += idf * weight * tf * (K1 + 1) / (tf + (K1 * (1 - B + (B * length / average))));
            }

            if (clauseScore == 0)
            {
                return null;
            }

            score += clauseScore;
        }

        return score;
    }

    private static IEnumerable<(List<string> Words, double Weight, bool IsBody)> Columns(QueryClause clause, FieldWords words)
    {
        switch (clause.Field)
        {
            case QueryField.Title:
                yield return (words.Title, TitleWeight, false);
                break;
            case QueryField.Heading:
                yield return (words.Headings, HeadingWeight, false);
                break;
            case QueryField.Tag:
                yield return (words.Tags, TagWeight, false);
                break;
            case QueryField.Path:
                yield return (words.Path, PathWeight, false);
                break;
            default:
                yield return (words.Title, TitleWeight, false);
                yield return (words.Headings, HeadingWeight, false);
                yield return (words.Tags, TagWeight, false);
                yield return (words.Path, PathWeight, false);
                yield return (words.Body, BodyWeight, true);
                break;
        }
    }

    /// <summary>Number of places in <paramref name="tokens"/> where the clause's words occur adjacent and in order.</summary>
    internal static int Count(List<string> tokens, QueryClause clause)
    {
        var words = clause.Words;
        var count = 0;
        for (var i = 0; i + words.Count <= tokens.Count; i++)
        {
            var ok = true;
            for (var j = 0; j < words.Count && ok; j++)
            {
                var token = tokens[i + j];
                ok = clause.Prefix && j == words.Count - 1
                    ? token.StartsWith(words[j], StringComparison.Ordinal)
                    : string.Equals(token, words[j], StringComparison.Ordinal);
            }

            if (ok)
            {
                count++;
            }
        }

        return count;
    }
}
