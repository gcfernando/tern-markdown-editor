using System.Globalization;
using Microsoft.Data.Sqlite;
using Tern.Workspaces;

namespace Tern.Search;

/// <summary>A span inside <see cref="SearchHit.Snippet"/> (or the title) that matched the query.</summary>
public readonly record struct HighlightRange(int Start, int Length);

/// <param name="Path">Absolute path.</param>
/// <param name="Score">Higher is better. Comparable between indexed and unsaved-buffer hits.</param>
/// <param name="Snippet">One line of context around the first match; highlight ranges index into it.</param>
/// <param name="Line">1-based line of the first body match, or of the first non-empty line when only title, tags or path matched.</param>
public sealed record SearchHit(
    string Path,
    string RelativePath,
    string Title,
    double Score,
    string Snippet,
    IReadOnlyList<HighlightRange> Highlights,
    IReadOnlyList<HighlightRange> TitleHighlights,
    int Line,
    IReadOnlyList<string> Tags,
    bool FromUnsavedBuffer);

public sealed record SearchPage(IReadOnlyList<SearchHit> Hits, int Offset, bool HasMore)
{
    public static SearchPage Empty { get; } = new([], 0, false);
    public string? Error { get; init; }
}

public sealed record SearchOptions(int Limit = 20, int Offset = 0, int SnippetLength = 160);

/// <summary>
/// Runs user queries against the index, merging in unsaved editor buffers. Any text is a valid query: it is parsed into
/// safe building blocks. Database failures retain unsaved-buffer matches and carry an actionable diagnostic.
/// </summary>
public sealed class SearchService
{
    private const int MaxLimit = 500;
    private const int MaxOverlayExclusions = 1000;

    private readonly SearchIndex _index;
    private readonly OverlayStore? _overlay;

    public SearchService(SearchIndex index, OverlayStore? overlay = null)
    {
        _index = index;
        _overlay = overlay;
    }

    public SearchPage Query(string? text, SearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new SearchOptions();
        var limit = Math.Clamp(options.Limit, 1, MaxLimit);
        var offset = Math.Max(0, options.Offset);
        var parsed = QueryParser.Parse(text);
        if (parsed.IsEmpty)
        {
            return SearchPage.Empty with { Offset = offset };
        }

        cancellationToken.ThrowIfCancellationRequested();
        var overlayDocs = _overlay?.Snapshot() ?? [];
        var overlayPaths = new HashSet<string>(overlayDocs.Select(d => d.RelativePath), WorkspacePaths.Comparer);
        var need = offset + limit + 1;

        List<Candidate> candidates;
        var corpus = new CorpusStats(0, 0);
        var indexFailed = false;
        try
        {
            (candidates, corpus) = _index.Read(connection => (
                QueryDisk(connection, parsed, need, overlayPaths, cancellationToken),
                overlayDocs.Count > 0 ? SearchIndex.ReadCorpusStats(connection) : new CorpusStats(0, 0)));
        }
        catch (SqliteException)
        {
            candidates = [];
            indexFailed = true;
        }

        foreach (var document in overlayDocs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Match(document, parsed, corpus) is { } score)
            {
                candidates.Add(new Candidate(document.RelativePath, document.Content.Title, [.. document.Content.Tags], score, -1, document));
            }
        }

        var ordered = candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.RelativePath, WorkspacePaths.Comparer)
            .ToList();
        var page = ordered.Skip(offset).Take(limit).ToList();
        var terms = HighlightTerms(parsed);

        var bodies = ReadBodies(page.Where(c => c.Overlay is null).Select(c => c.Id), out var bodyReadFailed);
        var hits = new List<SearchHit>(page.Count);
        foreach (var candidate in page)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = candidate.Overlay is { } overlay ? overlay.Content.Body : bodies.GetValueOrDefault(candidate.Id, string.Empty);
            hits.Add(BuildHit(candidate, body, terms, Math.Max(40, options.SnippetLength)));
        }

        return new SearchPage(hits, offset, ordered.Count > offset + limit)
        {
            Error = indexFailed || bodyReadFailed
                ? "The search index could not be read. Try again. If this continues, close all workspaces, choose Reset Tern → Search indexes, then reopen the workspace. Matches from unsaved notes may still be shown."
                : null,
        };
    }

    /// <summary>Streams every result, fetching one page at a time so very large result sets never sit in memory at once.</summary>
    public IEnumerable<SearchHit> QueryAll(string? text, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        var offset = 0;
        while (true)
        {
            var page = Query(text, new SearchOptions(pageSize, offset), cancellationToken);
            foreach (var hit in page.Hits)
            {
                yield return hit;
            }

            if (!page.HasMore || page.Hits.Count == 0)
            {
                yield break;
            }

            offset += page.Hits.Count;
        }
    }

    private static double? Match(OverlayDocument document, ParsedQuery parsed, CorpusStats corpus) =>
        InMemoryMatcher.Score(document, parsed, corpus);

    private static List<Candidate> QueryDisk(SqliteConnection connection, ParsedQuery parsed, int need, HashSet<string> overlayPaths, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        var exclusion = string.Empty;
        if (overlayPaths.Count is > 0 and <= MaxOverlayExclusions)
        {
            var names = new List<string>();
            var i = 0;
            foreach (var path in overlayPaths)
            {
                var name = "@o" + i++.ToString(CultureInfo.InvariantCulture);
                names.Add(name);
                command.Parameters.AddWithValue(name, path);
            }

            exclusion = " AND d.path NOT IN (" + string.Join(',', names) + ")";
        }

        var match = parsed.ToMatchExpression();
        if (match is not null)
        {
            command.CommandText = "SELECT d.id, d.path, d.title, d.tags, bm25(fts, 10.0, 5.0, 4.0, 2.0, 1.0) AS score FROM fts JOIN docs d ON d.id = fts.rowid WHERE fts MATCH @match" + exclusion + " ORDER BY score, d.path LIMIT @limit";
            command.Parameters.AddWithValue("@match", match);
        }
        else
        {
            command.CommandText = "SELECT d.id, d.path, d.title, d.tags, 0.0 AS score FROM docs d WHERE d.id NOT IN (SELECT rowid FROM fts WHERE fts MATCH @neg)" + exclusion + " ORDER BY d.path LIMIT @limit";
            command.Parameters.AddWithValue("@neg", parsed.ToExclusionExpression());
        }

        command.Parameters.AddWithValue("@limit", overlayPaths.Count > MaxOverlayExclusions ? (long)need + overlayPaths.Count : need);
        var result = new List<Candidate>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            // SQLite parameter limits must never weaken unsaved-buffer precedence.
            if (overlayPaths.Contains(reader.GetString(1)))
            {
                continue;
            }
            var tags = reader.GetString(3);
            result.Add(new Candidate(
                reader.GetString(1),
                reader.GetString(2),
                tags.Length == 0 ? [] : [.. tags.Split(' ')],
                -reader.GetDouble(4),
                reader.GetInt64(0),
                null));
        }

        return result;
    }

    private SearchHit BuildHit(Candidate candidate, string body, List<HighlightTerm> terms, int snippetLength)
    {
        var snippet = SnippetBuilder.Build(body, terms, snippetLength);
        var titleHighlights = SnippetBuilder.FindHighlights(candidate.Title, terms);
        return new SearchHit(
            WorkspacePaths.Normalize(Path.Combine(_index.Root, candidate.RelativePath.Replace('/', Path.DirectorySeparatorChar))),
            candidate.RelativePath,
            candidate.Title,
            candidate.Score,
            snippet.Text,
            snippet.Highlights,
            titleHighlights,
            snippet.Line,
            candidate.Tags,
            candidate.Overlay is not null);
    }

    private Dictionary<long, string> ReadBodies(IEnumerable<long> ids, out bool failed)
    {
        failed = false;
        var wanted = ids.ToList();
        var bodies = new Dictionary<long, string>(wanted.Count);
        if (wanted.Count == 0)
        {
            return bodies;
        }

        try
        {
            _index.Read(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT body FROM fts WHERE rowid = @id";
                var parameter = command.Parameters.Add("@id", SqliteType.Integer);
                foreach (var id in wanted)
                {
                    parameter.Value = id;
                    bodies[id] = command.ExecuteScalar() as string ?? string.Empty;
                }

                return 0;
            });
        }
        catch (SqliteException)
        {
            failed = true;
        }

        return bodies;
    }

    // Path and tag filters are not shown as highlights in body text.
    private static List<HighlightTerm> HighlightTerms(ParsedQuery parsed)
    {
        var terms = new List<HighlightTerm>();
        foreach (var clause in parsed.Positive.Where(c => c.Field is QueryField.Any or QueryField.Title or QueryField.Heading))
        {
            for (var i = 0; i < clause.Words.Count; i++)
            {
                terms.Add(new HighlightTerm(clause.Words[i], clause.Prefix && i == clause.Words.Count - 1));
            }
        }

        return terms;
    }

    private sealed record Candidate(string RelativePath, string Title, IReadOnlyList<string> Tags, double Score, long Id, OverlayDocument? Overlay);
}

internal readonly record struct HighlightTerm(string Word, bool Prefix);

internal sealed record Snippet(string Text, IReadOnlyList<HighlightRange> Highlights, int Line);

internal static class SnippetBuilder
{
    private const string Ellipsis = "\u2026";

    public static Snippet Build(string body, List<HighlightTerm> terms, int maxLength)
    {
        var lines = MarkdownTextExtractor.SplitLines(body);
        var tokens = Tokenizer.Tokenize(body);
        TextToken? first = null;
        foreach (var token in tokens)
        {
            if (IsMatch(token.Folded, terms))
            {
                first = token;
                break;
            }
        }

        int lineIndex;
        int matchColumn;
        if (first is { } hit)
        {
            lineIndex = LineOf(body, hit.Start, out var lineStart);
            matchColumn = hit.Start - lineStart;
        }
        else
        {
            lineIndex = Math.Max(0, lines.FindIndex(l => l.Trim().Length > 0));
            matchColumn = 0;
        }

        var line = lineIndex < lines.Count ? lines[lineIndex] : string.Empty;
        var leading = line.Length - line.TrimStart().Length;
        var windowStart = Math.Max(leading, Math.Min(matchColumn - (maxLength / 3), Math.Max(leading, line.Length - maxLength)));
        windowStart = AlignStart(line, windowStart, leading);
        var windowEnd = AlignEnd(line, Math.Min(line.Length, windowStart + maxLength), windowStart);
        var slice = line[windowStart..windowEnd].TrimEnd();

        var prefix = windowStart > leading ? Ellipsis : string.Empty;
        var text = prefix + slice + (windowEnd < line.TrimEnd().Length ? Ellipsis : string.Empty);
        var highlights = FindHighlights(slice, terms)
            .Select(h => new HighlightRange(h.Start + prefix.Length, h.Length))
            .ToList();
        return new Snippet(text, highlights, lineIndex + 1);
    }

    public static IReadOnlyList<HighlightRange> FindHighlights(string text, List<HighlightTerm> terms)
    {
        if (terms.Count == 0 || text.Length == 0)
        {
            return [];
        }

        return [.. Tokenizer.Tokenize(text).Where(t => IsMatch(t.Folded, terms)).Select(t => new HighlightRange(t.Start, t.Length))];
    }

    private static bool IsMatch(string folded, List<HighlightTerm> terms)
    {
        foreach (var term in terms)
        {
            if (term.Prefix ? folded.StartsWith(term.Word, StringComparison.Ordinal) : string.Equals(folded, term.Word, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // Zero-based line of an offset; lines end at \n, \r\n or a lone \r, matching MarkdownTextExtractor.SplitLines.
    private static int LineOf(string text, int offset, out int lineStart)
    {
        var line = 0;
        lineStart = 0;
        for (var i = 0; i < offset; i++)
        {
            if (text[i] == '\n' || (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')))
            {
                line++;
                lineStart = i + 1;
            }
        }

        return line;
    }

    private static int AlignStart(string line, int start, int minimum)
    {
        if (start <= minimum)
        {
            return minimum;
        }

        var limit = Math.Min(line.Length, start + 20);
        for (var i = start; i < limit; i++)
        {
            if (char.IsWhiteSpace(line[i]))
            {
                return Math.Min(i + 1, line.Length);
            }
        }

        return start;
    }

    private static int AlignEnd(string line, int end, int start)
    {
        if (end >= line.Length)
        {
            return line.Length;
        }

        var limit = Math.Max(start, end - 20);
        for (var i = end; i > limit; i--)
        {
            if (char.IsWhiteSpace(line[i - 1]))
            {
                return i;
            }
        }

        return end;
    }
}
