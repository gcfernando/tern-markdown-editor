using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Tern.App.Shell;
using Tern.Markdown;
using Tern.Search;
using Tern.Workspaces;

namespace Tern.App.Knowledge;

/// <summary>SendCharacters always comes from the selected AI connection. Notes are limited by the per-question total, not by a note count.</summary>
public sealed record KnowledgeLimits(int Sources = KnowledgeLimits.MaximumListedNotes, int ExcerptCharacters = 1200, int SendCharacters = KnowledgeLimits.DefaultSendCharacters,
    int ConversationTurns = 6, int SourceCharacters = KnowledgeLimits.MaximumNoteCharacters)
{
    /// <summary>How many notes start ticked from local search. People can tick more; only what they keep ticked is ever sent.</summary>
    public const int SuggestedNotes = 6;
    public const int MaximumListedNotes = 40, DefaultSendCharacters = 256_000, MinimumSendCharacters = 8_000, MaximumSendCharacters = 1_000_000;
    public const int MaximumQuestionCharacters = 1_000_000, MaximumNoteCharacters = 2_000_000;

    public void Validate()
    {
        if (Sources is < 1 or > MaximumListedNotes || ExcerptCharacters is < 200 or > 4000 || SendCharacters is < MinimumSendCharacters or > MaximumSendCharacters
            || ConversationTurns is < 1 or > 12 || SourceCharacters is < 1000 or > MaximumNoteCharacters)
            throw new ArgumentOutOfRangeException(nameof(KnowledgeLimits), "Assistant limits are outside their supported bounds.");
    }
}

public sealed record KnowledgeSource(int Id, string WorkspaceRoot, string Path, string RelativePath, string Title,
    string? Heading, int Line, string Excerpt, string ContentDigest, IReadOnlyList<string> Tags)
{
    /// <summary>Tern thinks this note fits the question, so it starts ticked.</summary>
    public bool Recommended { get; init; } = true;
    /// <summary>A release date written in the note. This is never the file's saved date.</summary>
    public string? DocumentedDate { get; init; }
    /// <summary>When the file was last saved on this computer. Only set for questions about recent changes.</summary>
    public DateTime? FileSavedUtc { get; init; }
    /// <summary>AI's short reason for choosing this note.</summary>
    public string? WhyChosen { get; init; }
}

/// <summary>A possible note as AI sees it when choosing: its name and headings, never its text.</summary>
public sealed record KnowledgeShortlistNote(int Id, string Path, string RelativePath, string Title, IReadOnlyList<string> Headings);

public sealed record KnowledgeRelatedNote(string Path, string RelativePath, string Title, string Relationship);

public sealed record KnowledgeSearchResult(IReadOnlyList<KnowledgeSource> Sources,
    IReadOnlyList<KnowledgeRelatedNote> Related, IReadOnlyList<string> Notices)
{
    public KnowledgeCoverage Coverage { get; init; } = KnowledgeCoverage.Unknown;
    /// <summary>Possible notes for AI to choose from, including ones local search was unsure about.</summary>
    public IReadOnlyList<KnowledgeShortlistNote> Shortlist { get; init; } = [];
}

/// <summary>Local retrieval only. Indexed hits are candidates; every excerpt is read from the current authorized source.</summary>
public sealed class KnowledgeRetrieval
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "is", "are", "was", "were", "be", "been", "being", "to", "of", "for", "in", "on",
        "at", "by", "with", "from", "about", "what", "which", "who", "where", "when", "why", "how", "do", "does", "did",
        "can", "could", "would", "should", "please", "tell", "me", "my", "our", "your", "notes", "note", "this", "that",
        "these", "those", "it", "its", "they", "their", "we", "you", "i", "summarize", "explain", "compare"
    };

    /// <param name="meaningTerms">Other words for the question's meaning, suggested by AI from the question alone.</param>
    public Task<KnowledgeSearchResult> SearchAsync(WorkspaceSession workspace, string question, KnowledgeLimits limits,
        CancellationToken token = default, IProgress<string>? progress = null, IReadOnlyList<string>? meaningTerms = null, string? meaningQuery = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        limits.Validate();
        if (string.IsNullOrWhiteSpace(question) || question.Length > 3000)
            throw new ArgumentException("Enter a question containing 1-3,000 characters.", nameof(question));
        if (meaningQuery is { Length: > 3000 }) throw new ArgumentException("The interpreted question is too long.", nameof(meaningQuery));
        if (!workspace.HasContentIndex)
            throw new InvalidOperationException("Make this folder a workspace before searching its contents.");
        return Task.Run(async () =>
        {
            token.ThrowIfCancellationRequested();
            progress?.Report("Searching");
            var original = QueryParser.Parse(question);
            var structured = IsAdvancedQuery(question, original);
            var retrievalQuestion = !structured && !string.IsNullOrWhiteSpace(meaningQuery) ? meaningQuery : question;
            var parsed = QueryParser.Parse(retrievalQuestion);
            var terms = (structured ? Enumerable.Empty<string>() : KnowledgeRanking.NumericIdentifiers(question))
                .Concat(parsed.Positive.SelectMany(clause => clause.Words))
                .Concat(structured ? Enumerable.Empty<string>() : parsed.Negative.SelectMany(clause => clause.Words))
                .Where(word => word.Length >= 2 && !StopWords.Contains(word)).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
            // Common words such as "main" or "project" match almost every note, so the real topic decides the result.
            var topic = terms.Where(term => !GenericWords.Contains(term)).ToArray();
            var concepts = structured ? [] : KnowledgeRanking.Plan(retrievalQuestion, topic.Length > 0 ? topic : terms, meaningTerms);
            var asksForChanges = !structured && KnowledgeRanking.AsksForChanges(retrievalQuestion);
            var allStems = concepts.SelectMany(concept => concept.Stems).Distinct(StringComparer.Ordinal).ToArray();
            var snapshot = workspace.Snapshot;
            var fileCount = Math.Max(1, snapshot.Files.Count);
            var notices = new List<string>();
            var indexComplete = workspace.IsContentReady && !workspace.IsPreparingContent;
            if (!indexComplete) notices.Add("Workspace indexing is incomplete. Results may omit notes; rescan to finish indexing.");

            var candidates = new Dictionary<string, Candidate>(WorkspacePaths.Comparer);
            var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
            IReadOnlyList<SearchHit> Query(string query)
            {
                token.ThrowIfCancellationRequested();
                var page = workspace.Search(query, limit: CandidatesPerWord, cancellationToken: token, snippetLength: 40);
                if (page.Error is { } error) throw new InvalidOperationException(error);
                foreach (var hit in page.Hits)
                {
                    if (!candidates.TryGetValue(hit.Path, out var candidate)) candidates[hit.Path] = candidate = new(hit.Path, hit.RelativePath);
                    candidate.Hit = candidate.Hit is null || hit.Score > candidate.Hit.Score ? hit : candidate.Hit;
                }
                return page.HasMore ? [.. page.Hits, .. Enumerable.Repeat(page.Hits[0], Math.Max(0, fileCount - page.Hits.Count))] : page.Hits;
            }
            if (structured) Query(question);
            foreach (var stem in allStems) documentFrequency[stem] = Query(stem + "*").Count;
            double Rarity(string stem)
            {
                var notes = documentFrequency.GetValueOrDefault(stem);
                return notes == 0 ? 0 : Math.Max(0.15, Math.Log((fileCount + 1d) / (notes + 0.5)) / Math.Log(fileCount + 1d));
            }
            // Notes holding every topic word must be considered even when a common word has many matches.
            var found = concepts.Where(concept => concept.Stems.Any(stem => documentFrequency.GetValueOrDefault(stem) > 0)).ToArray();
            if (found.Length >= 2) Query(string.Join(' ', found.Select(concept => concept.Stems.Where(stem => documentFrequency.GetValueOrDefault(stem) > 0).MinBy(stem => documentFrequency[stem]) + "*")));
            // A file's name is a strong signal, so matching names are always considered.
            foreach (var file in snapshot.Files)
                if (allStems.Any(stem => KnowledgeRanking.HasWordStart(Path.GetFileNameWithoutExtension(file.RelativePath), stem)))
                {
                    if (!candidates.TryGetValue(file.FullPath, out var candidate)) candidates[file.FullPath] = candidate = new(file.FullPath, file.RelativePath);
                    candidate.NameMatch = true;
                }

            var missing = concepts.FirstOrDefault(concept => concept.Named && concept.Stems.All(stem => documentFrequency.GetValueOrDefault(stem) == 0));
            var wantsLegalText = original.Positive.SelectMany(clause => clause.Words)
                .Any(term => LegalWords.Any(word => word.StartsWith(Stem(term), StringComparison.OrdinalIgnoreCase)));
            var wantsSamples = AsksForSamples.IsMatch(question);
            bool Allowed(Candidate c) => (wantsLegalText || !IsBoilerplate(c.RelativePath))
                && (structured || wantsSamples || (!IsSampleFile(c.RelativePath) && !IsTestOrSample(c.RelativePath)));
            var hiddenMatches = candidates.Values.Count(c => !Allowed(c));
            var allowed = candidates.Values.Where(Allowed).ToArray();
            if (structured)
            {
                var ordered = allowed.OrderBy(c => IsTestOrSample(c.RelativePath)).ThenByDescending(c => c.Hit?.Score ?? 0).ThenBy(c => c.RelativePath, WorkspacePaths.Comparer).ToArray();
                allowed = ordered;
            }
            else
                allowed = allowed.OrderByDescending(c => Prior(c, concepts, Rarity)).ThenBy(c => c.RelativePath, WorkspacePaths.Comparer).Take(NotesReadPerQuestion).ToArray();

            progress?.Report("Retrieving");
            var scored = new List<ScoredNote>();
            var unreadable = new List<string>();
            if (missing is null)
                foreach (var candidate in structured ? allowed.Take(Math.Min(limits.Sources * 3, NotesReadPerQuestion)) : allowed)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var text = await workspace.ReadKnowledgeTextAsync(candidate.Path, limits.SourceCharacters, token).ConfigureAwait(false);
                        var analysis = MarkdownAnalyzer.Analyze(text, workspace.Profile);
                        var title = analysis.Headings.FirstOrDefault()?.Text ?? Path.GetFileNameWithoutExtension(candidate.Path);
                        var (score, fit) = structured ? (candidate.Hit?.Score ?? 0, 1d)
                            : KnowledgeRanking.Score(candidate.RelativePath, title, analysis.Headings, text, concepts, Rarity, asksForChanges);
                        if (structured || score > 0) scored.Add(new(candidate, text, analysis, title, score, fit));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        unreadable.Add(Path.GetFileName(candidate.RelativePath));
                        notices.Add($"{candidate.RelativePath}: {ex.Message}");
                    }
                }

            var top = scored.Count == 0 ? 0 : scored.Max(note => note.Score);
            var relevant = (structured ? scored.AsEnumerable() : scored.Where(note => note.Coverage >= MinimumCoverage)
                .OrderByDescending(note => note.Score).ThenBy(note => note.Candidate.RelativePath, WorkspacePaths.Comparer)).ToArray();
            var sources = new List<KnowledgeSource>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var searchWords = allStems.Length > 0 ? allStems : terms.Select(Stem).ToArray();
            // The person's own words decide the passage. AI's broader words only help when those words are not there.
            var ownWords = concepts.Where(concept => concept.Label != KnowledgeRanking.MeaningLabel).SelectMany(concept => concept.Stems).ToHashSet(StringComparer.Ordinal);
            foreach (var note in relevant)
            {
                token.ThrowIfCancellationRequested();
                var candidate = note.Candidate;
                var lines = note.Text.ReplaceLineEndings("\n").Split('\n');
                var release = asksForChanges ? KnowledgeRanking.NewestRelease(note.Analysis.Headings) : null;
                int first;
                string? headingText;
                string excerpt;
                if (release is { } newest && KnowledgeRanking.ReleaseHeadings(note.Analysis.Headings).Count >= 1)
                {
                    var heading = note.Analysis.Headings[newest.HeadingIndex];
                    first = heading.Line - 1;
                    headingText = heading.Text;
                    excerpt = KnowledgeRanking.ReleaseDigest(lines, note.Analysis.Headings, newest.HeadingIndex, Math.Min(4000, limits.ExcerptCharacters * 2));
                }
                else
                {
                    // The sections that best cover the question, not just the first line containing a word.
                    (first, headingText, excerpt) = KnowledgeRanking.BestPassages(lines, note.Analysis.Headings, searchWords,
                        stem => ownWords.Count > 0 && !ownWords.Contains(stem) ? 0.02 : Math.Max(0.15, Rarity(stem)), limits.ExcerptCharacters, Math.Clamp((candidate.Hit?.Line ?? 1) - 1, 0, lines.Length - 1));
                }
                if (excerpt.Length > 0 && char.IsHighSurrogate(excerpt[^1])) excerpt = excerpt[..^1];
                if (string.IsNullOrWhiteSpace(excerpt)) continue;
                // The same text copied into several files is one result.
                if (!seen.Add(string.Join(' ', excerpt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant())) continue;
                sources.Add(new(sources.Count + 1, workspace.Root, candidate.Path, WorkspacePaths.GetRelative(workspace.Root, candidate.Path),
                    note.Title, headingText, first + 1, excerpt, Digest(note.Text), candidate.Hit?.Tags ?? [])
                {
                    // For change questions a note must actually record a release or date, not merely mention the word "change".
                    Recommended = sources.Count < KnowledgeLimits.SuggestedNotes && (structured || sources.Count == 0 || ((relevant.Length <= FewMatches || (note.Coverage >= RecommendedCoverage && note.Score >= top * RecommendedShareOfBest)) && (!asksForChanges || release is not null))),
                    DocumentedDate = release?.DocumentedDate,
                    FileSavedUtc = asksForChanges ? snapshot.Files.FirstOrDefault(file => WorkspacePaths.PathsEqual(file.FullPath, candidate.Path))?.LastWriteUtc : null,
                });
                if (sources.Count == limits.Sources) break;
            }
            var related = Related(workspace, sources, token);
            var outOfScope = sources.Count == 0 && !structured ? KnowledgeRanking.FindOutOfScope(workspace, allStems, token) : (Excluded: 0, Unsupported: (IReadOnlyList<string>)[]);
            var coverage = new KnowledgeCoverage
            {
                FilesInFolder = snapshot.Files.Count, IndexComplete = indexComplete,
                MissingSubject = sources.Count == 0 ? missing?.Label is { } name ? char.ToUpperInvariant(name[0]) + name[1..] : null : null,
                HiddenMatches = sources.Count == 0 ? hiddenMatches : 0, OnlineOnlyNotes = snapshot.Files.Count(file => file.IsOnlineOnly),
                ExcludedMatches = outOfScope.Excluded, UnsupportedMatches = outOfScope.Unsupported, UnreadableNotes = unreadable,
                WeakMatch = !structured && missing is null && (sources.Count == 0 || relevant.Max(note => note.Coverage) < StrongCoverage),
            };
            var shortlist = structured || missing is not null ? [] : scored.OrderByDescending(note => note.Score).ThenBy(note => note.Candidate.RelativePath, WorkspacePaths.Comparer)
                .Take(ShortlistSize).Select((note, index) => new KnowledgeShortlistNote(index + 1, note.Candidate.Path, WorkspacePaths.GetRelative(workspace.Root, note.Candidate.Path), note.Title,
                    note.Analysis.Headings.Skip(1).Take(6).Select(heading => heading.Text[..Math.Min(60, heading.Text.Length)]).ToArray())).ToArray();
            return new KnowledgeSearchResult(sources, related, notices) { Coverage = coverage, Shortlist = shortlist };
        }, token);
    }

    // With only a few matching notes, ticking them all costs little and avoids missing a source.
    private const int CandidatesPerWord = 80, NotesReadPerQuestion = 40, FewMatches = 3, ShortlistSize = 16;
    private const double MinimumCoverage = 0.5, StrongCoverage = 0.75, RecommendedCoverage = 0.6, RecommendedShareOfBest = 0.25;

    /// <summary>Only explicit search syntax selects exact/filtered search. Tokenizer punctuation is not syntax.</summary>
    public static bool IsAdvancedQuery(string question) => IsAdvancedQuery(question, QueryParser.Parse(question));

    private static bool IsAdvancedQuery(string question, ParsedQuery parsed) => parsed.Negative.Count > 0
        || parsed.Positive.Any(clause => clause.Field != QueryField.Any || clause.Prefix)
        || QuotedSearch.IsMatch(question);

    private static readonly System.Text.RegularExpressions.Regex QuotedSearch = new("\"[^\"\\r\\n]+\"",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private sealed class Candidate(string path, string relativePath)
    {
        public string Path { get; } = path;
        public string RelativePath { get; } = relativePath;
        public SearchHit? Hit { get; set; }
        public bool NameMatch { get; set; }
    }

    private sealed record ScoredNote(Candidate Candidate, string Text, MarkdownAnalysis Analysis, string Title, double Score, double Coverage);

    /// <summary>A cheap first ranking that decides which notes are worth reading in full.</summary>
    private static double Prior(Candidate candidate, IReadOnlyList<SearchConcept> concepts, Func<string, double> rarity)
    {
        var title = candidate.Hit?.Title ?? "";
        var name = System.IO.Path.GetFileNameWithoutExtension(candidate.RelativePath);
        var value = candidate.NameMatch ? 2d : 0;
        foreach (var concept in concepts)
            foreach (var stem in concept.Stems)
                if (KnowledgeRanking.HasWordStart(name, stem) || KnowledgeRanking.HasWordStart(title, stem) || KnowledgeRanking.HasWordStart(candidate.Hit?.Snippet ?? "", stem))
                { value += rarity(stem); break; }
        return value + Math.Min(0.5, Math.Max(0, candidate.Hit?.Score ?? 0) / 20);
    }

    /// <summary>Builds a source for a note the person added themselves. It is read fresh and follows the same access rules.</summary>
    public async Task<KnowledgeSource> ReadChosenNoteAsync(WorkspaceSession workspace, string path, string question, int id, KnowledgeLimits limits, CancellationToken token = default)
    {
        limits.Validate();
        var text = await workspace.ReadKnowledgeTextAsync(path, limits.SourceCharacters, token).ConfigureAwait(false);
        var analysis = await Task.Run(() => MarkdownAnalyzer.Analyze(text, workspace.Profile), token).ConfigureAwait(false);
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var parsed = QueryParser.Parse(question);
        var words = parsed.Positive.SelectMany(clause => clause.Words).Where(word => word.Length >= 2 && !StopWords.Contains(word)).Select(Stem).ToArray();
        var (first, headingText, excerpt) = KnowledgeRanking.BestPassages(lines, analysis.Headings, words, _ => 1, limits.ExcerptCharacters, 0);
        if (excerpt.Length > 0 && char.IsHighSurrogate(excerpt[^1])) excerpt = excerpt[..^1];
        if (string.IsNullOrWhiteSpace(excerpt)) throw new InvalidOperationException("This note is empty, so AI has nothing to read.");
        var full = WorkspacePaths.Normalize(path);
        return new(id, workspace.Root, full, WorkspacePaths.GetRelative(workspace.Root, full),
            analysis.Headings.FirstOrDefault()?.Text ?? System.IO.Path.GetFileNameWithoutExtension(full), headingText, first + 1, excerpt, Digest(text), []);
    }

    private static readonly HashSet<string> GenericWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "main", "project", "projects", "summary", "summarise", "key", "important", "list", "find", "show", "give", "need", "needs", "work",
        "thing", "things", "help", "make", "write", "draft", "choose", "pick", "get", "use", "using", "update", "next", "step", "steps", "still", "before", "after", "any", "all",
        "some", "there", "have", "has", "had", "will", "more", "most", "many", "much", "details", "information", "info", "please", "want"
    };
    private static readonly HashSet<string> TestFolders = new(StringComparer.OrdinalIgnoreCase)
    { "test", "tests", "__tests__", "testing", "fixture", "fixtures", "sample", "samples", "example", "examples", "testdata", "bin", "obj", "dist", "build" };

    internal static bool IsTestOrSample(string relativePath) => relativePath.Split('/', '\\')[..^1].Any(TestFolders.Contains);

    private const System.Text.RegularExpressions.RegexOptions WordOptions = System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant;
    private static readonly System.Text.RegularExpressions.Regex SampleFileName = new(@"(?:^|[-_. ])(?:tests?|specs?|samples?|examples?|fixtures?|dummy)(?:[-_. ]|$)", WordOptions, TimeSpan.FromMilliseconds(100));
    private static readonly System.Text.RegularExpressions.Regex AsksForSamples = new(@"\b(?:tests?|testing|specs?|samples?|examples?|fixtures?|dummy)\b", WordOptions, TimeSpan.FromMilliseconds(100));
    private static readonly System.Text.RegularExpressions.Regex AsksForLegal = new(@"\b(?:licen[sc]es?|copyright|notices?|third[- ]party|attribution)\b", WordOptions, TimeSpan.FromMilliseconds(100));

    internal static bool IsSampleFile(string relativePath) => SampleFileName.IsMatch(System.IO.Path.GetFileNameWithoutExtension(relativePath));

    /// <summary>License, test and sample files stay out of results unless the question asks for them.</summary>
    public static bool IsHiddenResult(string relativePath, string question) =>
        (IsBoilerplate(relativePath) && !AsksForLegal.IsMatch(question))
        || ((IsTestOrSample(relativePath) || IsSampleFile(relativePath)) && !AsksForSamples.IsMatch(question));

    /// <summary>True when a question has no specific topic, so a clearer question will give a better result.</summary>
    public static bool IsVague(string question)
    {
        var words = System.Text.RegularExpressions.Regex.Matches(question, @"[\p{L}\p{N}][\p{L}\p{N}'-]*", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100))
            .Select(match => match.Value).ToArray();
        return !words.Any(word => word.Length >= 2 && !StopWords.Contains(word) && !GenericWords.Contains(word));
    }

    private static readonly string[] LegalWords = ["license", "licence", "copyright", "notice", "third-party", "attribution"];
    private static readonly HashSet<string> BoilerplateFolders = new(StringComparer.OrdinalIgnoreCase)
    { "licenses", "licences", "license", "third-party", "third_party", "thirdparty", "vendor", "vendors", "node_modules", "packages" };

    /// <summary>License and dependency files mention common words but rarely answer a question about the user's work.</summary>
    internal static bool IsBoilerplate(string relativePath)
    {
        var parts = relativePath.Split('/', '\\');
        if (parts[..^1].Any(BoilerplateFolders.Contains)) return true;
        var name = System.IO.Path.GetFileNameWithoutExtension(parts[^1]).Replace('_', '-');
        return name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LICENCE", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("COPYING", StringComparison.OrdinalIgnoreCase) || name.Equals("NOTICE", StringComparison.OrdinalIgnoreCase)
            || name.Contains("THIRD-PARTY", StringComparison.OrdinalIgnoreCase);
    }

    public static string StemForDisplay(string word) => Stem(word);

    internal static string Stem(string word)
    {
        var lower = word.ToLowerInvariant();
        foreach (var ending in new[] { "ing", "ied", "ies", "ed" })
            if (lower.EndsWith(ending, StringComparison.Ordinal) && lower.Length - ending.Length >= 4) return lower[..^ending.Length];
        // "matches" loses "es", but "themes" only loses "s", so it never becomes the unrelated word "them".
        if (lower.Length >= 6 && (lower.EndsWith("ches", StringComparison.Ordinal) || lower.EndsWith("shes", StringComparison.Ordinal)
            || lower.EndsWith("sses", StringComparison.Ordinal) || lower.EndsWith("xes", StringComparison.Ordinal))) return lower[..^2];
        if (lower.EndsWith('s') && !lower.EndsWith("ss", StringComparison.Ordinal) && lower.Length >= 5) lower = lower[..^1];
        return lower.EndsWith('e') && lower.Length >= 6 ? lower[..^1] : lower;
    }

    /// <summary>The line covering the most question words. Body text beats a heading, so an excerpt carries the actual content.</summary>
    private static int BestLine(string[] lines, string[] terms)
    {
        var best = -1;
        var bestScore = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            var matched = terms.Count(term => KnowledgeRanking.HasWordStart(lines[index], term));
            if (matched == 0) continue;
            var score = matched * 2 - (lines[index].TrimStart().StartsWith('#') ? 1 : 0);
            if (score > bestScore) { best = index; bestScore = score; }
        }
        return best;
    }

    public Task ValidateAsync(WorkspaceSession workspace, IEnumerable<KnowledgeSource> sources, KnowledgeLimits limits,
        CancellationToken token = default)
    {
        limits.Validate();
        var selected = sources.ToArray();
        return Task.Run(async () =>
        {
            foreach (var source in selected)
            {
                token.ThrowIfCancellationRequested();
                if (!WorkspacePaths.Comparer.Equals(source.WorkspaceRoot, workspace.Root))
                    throw new InvalidOperationException("This source belongs to a different conversation scope.");
                var text = await workspace.ReadKnowledgeTextAsync(source.Path, limits.SourceCharacters, token).ConfigureAwait(false);
                if (!string.Equals(source.ContentDigest, Digest(text), StringComparison.Ordinal))
                    throw new InvalidOperationException($"The note {System.IO.Path.GetFileNameWithoutExtension(source.RelativePath)} changed. Please ask your question again.");
            }
        }, token);
    }

    /// <summary>Reads the selected notes in full for a report, after confirming they have not changed.</summary>
    public async Task<IReadOnlyList<(KnowledgeSource Source, string Text)>> ReadForReportAsync(WorkspaceSession workspace, IReadOnlyList<KnowledgeSource> sources,
        KnowledgeLimits limits, CancellationToken token = default)
    {
        await ValidateAsync(workspace, sources, limits, token).ConfigureAwait(false);
        var notes = new List<(KnowledgeSource, string)>(sources.Count);
        foreach (var source in sources) notes.Add((source, await workspace.ReadKnowledgeTextAsync(source.Path, limits.SourceCharacters, token).ConfigureAwait(false)));
        return notes;
    }

    public Task ValidateRelatedAsync(WorkspaceSession workspace, KnowledgeRelatedNote note, KnowledgeLimits limits, CancellationToken token = default)
    {
        limits.Validate();
        return Task.Run(async () => { _ = await workspace.ReadKnowledgeTextAsync(note.Path, limits.SourceCharacters, token).ConfigureAwait(false); }, token);
    }

    private static IReadOnlyList<KnowledgeRelatedNote> Related(WorkspaceSession workspace, IReadOnlyList<KnowledgeSource> sources, CancellationToken token)
    {
        var support = sources.Select(source => source.Path).ToHashSet(WorkspacePaths.Comparer);
        var candidates = new Dictionary<string, string>(WorkspacePaths.Comparer);
        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            foreach (var link in workspace.Graph.GetOutgoing(source.Path).Where(link => link.Status == LinkStatus.Ok && link.TargetPath is not null).Take(24))
                candidates.TryAdd(link.TargetPath!, "Linked from a retrieved note");
            foreach (var link in workspace.Graph.GetBacklinks(source.Path).Take(24))
                candidates.TryAdd(link.SourcePath, "Links to a retrieved note");
            foreach (var tag in source.Tags.Take(3))
            {
                var page = workspace.Search("tag:\"" + tag.Replace("\"", "", StringComparison.Ordinal) + "\"", limit: 4, cancellationToken: token);
                if (page.Error is { } error) throw new InvalidOperationException(error);
                foreach (var hit in page.Hits) candidates.TryAdd(hit.Path, "Shares a tag with a retrieved note");
            }
        }
        var filter = new WorkspaceFilter(workspace.Root, workspace.Options, requireReadableIgnoreFile: true);
        return candidates.Where(pair => !support.Contains(pair.Key) && filter.IsDocument(pair.Key) && filter.IsRelevantChange(pair.Key)
                && workspace.IsKnownFile(pair.Key)
                && File.Exists(pair.Key) && !Tern.Filesystem.CloudFiles.IsOnlineOnly(pair.Key)
                && WorkspacePaths.IsPhysicallyContained(workspace.Root, pair.Key)).Take(8)
            .Select(pair => new KnowledgeRelatedNote(pair.Key, WorkspacePaths.GetRelative(workspace.Root, pair.Key),
                workspace.Graph.GetDocument(pair.Key)?.Title ?? System.IO.Path.GetFileNameWithoutExtension(pair.Key), pair.Value)).ToArray();
    }

    private static string Digest(string text) => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(text.AsSpan())));
}
