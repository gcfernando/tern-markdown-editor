using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Tern.App.Shell;
using Tern.Markdown;
using Tern.Workspaces;

namespace Tern.App.Knowledge;

public enum KnowledgeEmptyReason { None, IndexIncomplete, NamedSubjectMissing, UnreadableNotes, ExcludedFiles, UnsupportedFiles, NoMatch }

/// <summary>What a search could and could not look at, so an empty or partial result is never presented as complete.</summary>
public sealed record KnowledgeCoverage
{
    public static KnowledgeCoverage Unknown { get; } = new();
    public int FilesInFolder { get; init; }
    public bool IndexComplete { get; init; } = true;
    public string? MissingSubject { get; init; }
    public int HiddenMatches { get; init; }
    public int OnlineOnlyNotes { get; init; }
    public int ExcludedMatches { get; init; }
    public IReadOnlyList<string> UnreadableNotes { get; init; } = [];
    public IReadOnlyList<string> UnsupportedMatches { get; init; } = [];
    /// <summary>No note covers the whole question, so AI should help look for other words with the same meaning.</summary>
    public bool WeakMatch { get; init; }

    public bool IsPartial => !IndexComplete || UnreadableNotes.Count > 0 || OnlineOnlyNotes > 0;

    public KnowledgeEmptyReason EmptyReason =>
        !IndexComplete ? KnowledgeEmptyReason.IndexIncomplete
        : MissingSubject is not null ? KnowledgeEmptyReason.NamedSubjectMissing
        : UnreadableNotes.Count > 0 || OnlineOnlyNotes > 0 ? KnowledgeEmptyReason.UnreadableNotes
        : ExcludedMatches > 0 || HiddenMatches > 0 ? KnowledgeEmptyReason.ExcludedFiles
        : UnsupportedMatches.Count > 0 ? KnowledgeEmptyReason.UnsupportedFiles
        : KnowledgeEmptyReason.NoMatch;

    /// <summary>Why nothing was found, in plain words, with one concrete next step.</summary>
    public (string Title, string Why, string Next) ExplainEmpty() => EmptyReason switch
    {
        KnowledgeEmptyReason.IndexIncomplete => ("Tern has not finished reading this folder",
            "Some notes are not ready yet, so the answer may be in a note Tern has not read.", "Press Finish reading my notes, then ask again."),
        KnowledgeEmptyReason.NamedSubjectMissing => ($"No matching note found for {MissingSubject}",
            $"This search did not find a readable matching note for “{MissingSubject}”. A note using another spelling or different words may have been missed.", "Check the spelling, try another name for it, or search a different folder."),
        KnowledgeEmptyReason.UnreadableNotes => ("Some notes could not be read",
            UnreadableText(), "Make those notes available on this computer, then ask again."),
        KnowledgeEmptyReason.ExcludedFiles => ("The matching files are left out of this search",
            ExcludedText(), "Name those files in your question, or change the folder's ignore rules if you want them included."),
        KnowledgeEmptyReason.UnsupportedFiles => ("The matching files are a type Tern cannot read",
            $"These look related, but Tern reads only Markdown and text notes: {string.Join(", ", UnsupportedMatches)}.", "Save the content as a Markdown note in this folder, then ask again."),
        _ => ("We could not find this in your notes",
            "This search did not find a matching note. Notes using different words may have been missed.", "Use a word you remember from the note, or search a different folder."),
    };

    /// <summary>What the search covered, shown above the ticked notes. It never promises that every relevant note was found.</summary>
    public (bool Warning, string Text) Summary(int ticked, bool aiChose = false)
    {
        var picked = ticked == 1 ? "ticked the 1 note that fits best" : $"ticked the {ticked} notes that fit best";
        if (!IndexComplete) return (true, $"Tern is still reading this folder, so this list may be incomplete. It {picked}.");
        if (UnreadableNotes.Count > 0 || OnlineOnlyNotes > 0) return (true, UnreadableText() + $" Tern {picked} from the rest.");
        return (false, $"Tern searched the {Count(FilesInFolder)} in this folder and {(aiChose ? "AI " : "")}{picked}. A note using very different words may be missed, so press Add a note if you know of one.");
    }

    private string UnreadableText()
    {
        var parts = new List<string>();
        if (UnreadableNotes.Count > 0) parts.Add($"Tern could not read {string.Join(", ", UnreadableNotes.Take(3))}{(UnreadableNotes.Count > 3 ? $" and {UnreadableNotes.Count - 3} more" : "")}.");
        if (OnlineOnlyNotes > 0) parts.Add(OnlineOnlyNotes == 1 ? "1 note is stored online only." : $"{OnlineOnlyNotes} notes are stored online only.");
        return string.Join(' ', parts);
    }

    private string ExcludedText()
    {
        var total = ExcludedMatches + HiddenMatches;
        return (total == 1 ? "1 file matches" : $"{total} files match") + ", but Tern leaves out license, test, sample and ignored files unless you ask for them.";
    }

    private static string Count(int files) => files == 1 ? "1 note" : $"{files:N0} notes";
}

internal sealed record SearchConcept(string Label, IReadOnlyList<string> Stems, bool Named);

internal readonly record struct ReleaseSection(int HeadingIndex, Version? Number, DateTime? Date, string? DocumentedDate);

/// <summary>Finds what a question means and scores notes by name, headings and content. Common words count for little.</summary>
internal static partial class KnowledgeRanking
{
    private const int MaximumStems = 18;
    internal const string MeaningLabel = "similar meaning";
    private static readonly string[] ChangeStems = ["changelog", "chang", "releas", "version", "histor", "updat", "whatsnew", "news", "revision"];
    private static readonly string[][] MeaningGroups =
    [
        ["privat", "privacy", "confidential"], ["cost", "budget", "price", "spend", "expens"], ["deadline", "due", "schedul"],
        ["owner", "responsib", "assign"], ["decision", "decid", "approv"], ["task", "todo", "action"], ["install", "setup"],
    ];

    internal static bool AsksForChanges(string question) => !AsksForVersionRange(question)
        && (WhatsNew().IsMatch(question) || (RecentWord().IsMatch(question) && ChangeWord().IsMatch(question)));

    /// <summary>"From version 1 to latest" or "all versions" means the whole range, not only the newest release.</summary>
    internal static bool AsksForVersionRange(string question) => VersionRange().IsMatch(question);

    // Preserve dotted identifiers as one search concept. Tokenizing a release or
    // other numeric identifier into separate digits loses the subject of the question.
    internal static IEnumerable<string> NumericIdentifiers(string question) => NumericIdentifier().Matches(question).Select(match => match.Groups[1].Value);

    [GeneratedRegex(@"(?<![\p{L}\p{N}.])v?(\d+(?:\.\d+)+(?:-[\p{L}\p{N}]+(?:[.-][\p{L}\p{N}]+)*)?)(?![\p{L}\p{N}]|\.\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex NumericIdentifier();

    [System.Text.RegularExpressions.GeneratedRegex(@"\bfrom\s+(?:the\s+)?(?:first|earliest|oldest|(?:version|release)\s+v?\d[\d.]*|v\d[\d.]*)\b.{0,30}?\b(?:to|through|until|till|up\s+to)\b|\bfrom\s+\d[\d.]*\s+(?:to|through|until|till)\s+(?:the\s+)?(?:latest|newest|current|version|release|v\d)|\bsince\s+(?:(?:version|release)\s+v?\d|v\d)|\bbetween\s+(?:(?:version|release)\s+v?\d|v\d)|\b(?:versions?|releases?|v)\s*\d[\d.]*\s*(?:to|through|-|–)\s*(?:v|version\s+)?(?:\d|latest|newest|current)|\b(?:all|every|each)\s+(?:of\s+the\s+)?(?:\w+\s+){0,2}(?:versions?|releases?)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, 100)]
    private static partial System.Text.RegularExpressions.Regex VersionRange();

    internal static IReadOnlyList<SearchConcept> Plan(string question, IReadOnlyList<string> terms, IReadOnlyList<string>? meaningTerms)
    {
        var named = NamedWord().Matches(question).Select(match => match.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var concepts = new List<SearchConcept>();
        var range = AsksForVersionRange(question);
        var changes = range || AsksForChanges(question);
        if (changes) concepts.Add(new(range ? "version history" : "recent changes", ChangeStems, false));
        foreach (var term in terms)
        {
            if (changes && (RecentWord().IsMatch(term) || ChangeWord().IsMatch(term))) continue;
            var stem = KnowledgeRetrieval.Stem(term);
            if (concepts.Any(concept => concept.Stems.Contains(stem, StringComparer.Ordinal))) continue;
            var group = MeaningGroups.FirstOrDefault(words => words.Any(word => stem.StartsWith(word, StringComparison.Ordinal)));
            string[] variants = stem.Length > 0 && char.IsDigit(stem[0]) && stem.Contains('.')
                ? [stem, "v" + stem]
                : group is null ? [stem] : [stem, .. group.Where(word => word != stem)];
            concepts.Add(new(term, variants, named.Contains(term)));
        }
        var extra = (meaningTerms ?? []).SelectMany(term => WordPattern().Matches(term).Select(match => KnowledgeRetrieval.Stem(match.Value)))
            .Where(stem => stem.Length >= 3).Distinct(StringComparer.Ordinal).Take(8).ToArray();
        if (extra.Length > 0) concepts.Add(new(MeaningLabel, extra, false));
        var budget = MaximumStems;
        return concepts.Select(concept =>
        {
            var stems = concept.Stems.Take(Math.Max(1, budget)).ToArray();
            budget -= stems.Length;
            return concept with { Stems = stems };
        }).ToArray();
    }

    /// <summary>Counts a stem only at the start of a word, so "tern" never matches "pattern" or "internal".</summary>
    internal static int WordStartCount(string text, string stem, int limit = 8)
    {
        var count = 0;
        for (var index = text.IndexOf(stem, StringComparison.OrdinalIgnoreCase); index >= 0 && count < limit;
             index = index + stem.Length < text.Length ? text.IndexOf(stem, index + stem.Length, StringComparison.OrdinalIgnoreCase) : -1)
            if (index == 0 || !char.IsLetterOrDigit(text[index - 1])) count++;
        return count;
    }

    internal static bool HasWordStart(string text, string stem) => WordStartCount(text, stem, 1) > 0;

    internal static (double Score, double Coverage) Score(string relativePath, string title, IReadOnlyList<HeadingInfo> headings, string text,
        IReadOnlyList<SearchConcept> concepts, Func<string, double> rarity, bool asksForChanges)
    {
        var name = Path.GetFileNameWithoutExtension(relativePath);
        var headingText = string.Join('\n', headings.Select(heading => heading.Text));
        double score = 0, matched = 0, possible = 0;
        int matchedConcepts = 0, possibleConcepts = 0;
        foreach (var concept in concepts)
        {
            var weight = concept.Stems.Max(rarity);
            if (weight <= 0) continue;
            // AI's extra words can only help a note. A note is never marked down for not using them.
            var optional = concept.Label == MeaningLabel;
            if (!optional) { possible += weight; possibleConcepts++; }
            var best = 0d;
            foreach (var stem in concept.Stems)
            {
                double value = (HasWordStart(name, stem) ? 6 : 0) + (HasWordStart(title, stem) ? 4 : 0) + (HasWordStart(headingText, stem) ? 3 : 0);
                var body = WordStartCount(text, stem);
                // A short note focused on the subject should not lose to a very long note that mentions it in passing.
                if (body > 0) value += Math.Min(2, 0.5 + 0.5 * Math.Log2(1 + body)) + Math.Min(2, body * 400d / Math.Max(400, text.Length));
                best = Math.Max(best, value * rarity(stem));
            }
            if (best <= 0) continue;
            score += best;
            matched += weight;
            matchedConcepts++;
            if (optional) { possible += weight; possibleConcepts++; }
        }
        if (asksForChanges && score > 0 && ReleaseHeadings(headings).Count >= 2) score += 3;
        // A note can answer most of a question without its rarest word, so either measure of fit is enough.
        return (score, possible > 0 ? Math.Max(matched / possible, matchedConcepts / (double)possibleConcepts) : 0);
    }

    internal static IReadOnlyList<ReleaseSection> ReleaseHeadings(IReadOnlyList<HeadingInfo> headings)
    {
        var sections = new List<ReleaseSection>();
        for (var index = 0; index < headings.Count; index++)
        {
            var text = headings[index].Text;
            var version = VersionNumber().Match(text);
            Version? number = version.Success && Version.TryParse(version.Groups[1].Value, out var parsed) ? parsed : null;
            var (date, shown) = ReadDate(text);
            if (number is not null || date is not null) sections.Add(new(index, number, date, shown));
        }
        return sections;
    }

    internal static ReleaseSection? NewestRelease(IReadOnlyList<HeadingInfo> headings)
    {
        var sections = ReleaseHeadings(headings);
        if (sections.Count == 0) return null;
        return sections.Any(section => section.Number is not null)
            ? sections.Where(section => section.Number is not null).OrderByDescending(section => section.Number).ThenBy(section => section.HeadingIndex).First()
            : sections.OrderByDescending(section => section.Date).ThenBy(section => section.HeadingIndex).First();
    }

    /// <summary>A short digest of a release: every sub-heading with its first points, then more points while there is room.</summary>
    internal static string ReleaseDigest(string[] lines, IReadOnlyList<HeadingInfo> headings, int headingIndex, int budget)
    {
        var start = headings[headingIndex];
        var end = headings.Skip(headingIndex + 1).FirstOrDefault(heading => heading.Level <= start.Level)?.Line - 1 ?? lines.Length;
        var groups = new List<List<int>> { new() };
        var keep = new SortedSet<int> { start.Line - 1 };
        for (var index = start.Line; index < end; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index])) continue;
            if (lines[index].TrimStart().StartsWith('#')) { keep.Add(index); groups.Add([]); }
            else groups[^1].Add(index);
        }
        var used = keep.Sum(index => lines[index].Length + 1);
        for (var pass = 0; ; pass++)
        {
            var added = false;
            foreach (var group in groups.Where(group => pass < group.Count))
            {
                var line = group[pass];
                if (used + lines[line].Length + 1 > budget) continue;
                keep.Add(line);
                used += lines[line].Length + 1;
                added = true;
            }
            if (!added) break;
        }
        var digest = string.Join('\n', keep.Select(index => lines[index]));
        return digest.Length <= budget ? digest : digest[..budget];
    }

    /// <summary>Picks the sections that cover the most question words, best first, within the excerpt limit.</summary>
    internal static (int First, string? Heading, string Excerpt) BestPassages(string[] lines, IReadOnlyList<HeadingInfo> headings,
        IReadOnlyList<string> stems, Func<string, double> weight, int limit, int fallbackLine)
    {
        var headingLines = headings.Select(heading => heading.Line - 1).Where(line => line >= 0 && line < lines.Length).ToHashSet();
        var starts = headingLines.Order().ToList();
        if (starts.Count == 0 || starts[0] > 0) starts.Insert(0, 0);
        var sections = new List<(int Start, int End, double Score, int Best)>(starts.Count);
        for (var index = 0; index < starts.Count; index++)
        {
            var start = starts[index];
            var end = index + 1 < starts.Count ? starts[index + 1] : lines.Length;
            double score = 0;
            foreach (var stem in stems)
            {
                var hits = 0;
                for (var line = start; line < end && hits < 3; line++) hits += WordStartCount(lines[line], stem, 3 - hits);
                if (hits == 0) continue;
                score += weight(stem) * (2 + Math.Min(3, hits) + (headingLines.Contains(start) && HasWordStart(lines[start], stem) ? 3 : 0));
            }
            if (score <= 0) continue;
            var best = start;
            var bestWords = -1;
            for (var line = start; line < end; line++)
            {
                var words = stems.Count(stem => HasWordStart(lines[line], stem)) * 2 - (headingLines.Contains(line) ? 1 : 0);
                if (words > bestWords) { best = line; bestWords = words; }
            }
            sections.Add((start, end, score, best));
        }
        string? HeadingAt(int line) => headings.LastOrDefault(heading => heading.Line - 1 <= line)?.Text;
        if (sections.Count == 0)
        {
            var first = Math.Max(0, fallbackLine - 2);
            var plain = string.Join('\n', lines.Skip(first).Take(60));
            return (first, HeadingAt(fallbackLine), plain[..Math.Min(plain.Length, limit)]);
        }
        var text = new StringBuilder();
        var firstLine = -1;
        foreach (var section in sections.OrderByDescending(section => section.Score).ThenBy(section => section.Start))
        {
            var room = limit - text.Length - (text.Length > 0 ? 2 : 0);
            if (room < 120 && text.Length > 0) break;
            var from = section.Start;
            var passage = string.Join('\n', lines[from..Math.Min(section.End, from + 60)]).TrimEnd();
            if (passage.Length > room)
            {
                // Keep the matching line inside the limit, with its heading when there is room.
                from = Math.Max(section.Start, section.Best - 1);
                passage = string.Join('\n', lines[from..Math.Min(section.End, from + 60)]).TrimEnd();
                if (from > section.Start && headingLines.Contains(section.Start) && lines[section.Start].Length + 1 < room / 2)
                    passage = lines[section.Start] + "\n" + passage;
                passage = passage[..Math.Min(passage.Length, room)];
            }
            if (string.IsNullOrWhiteSpace(passage)) continue;
            if (firstLine < 0) firstLine = from;
            if (text.Length > 0) text.Append("\n\n");
            text.Append(passage);
        }
        return (Math.Max(0, firstLine), HeadingAt(Math.Max(0, firstLine)), text.ToString());
    }

    /// <summary>Looks past Tern's scan for related files it does not read. Reads names only, within a fixed limit.</summary>
    internal static (int Excluded, IReadOnlyList<string> Unsupported) FindOutOfScope(WorkspaceSession workspace, IReadOnlyList<string> stems, CancellationToken token)
    {
        if (stems.Count == 0) return (0, []);
        var filter = new WorkspaceFilter(workspace.Root, workspace.Options);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var pending = new Stack<string>([workspace.Root]);
        var excluded = 0;
        var unsupported = new List<string>();
        var visited = 0;
        bool Matches(string name) => stems.Any(stem => HasWordStart(name, stem));
        while (pending.TryPop(out var directory) && visited < 20000)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
                {
                    if (++visited >= 20000) break;
                    var relative = WorkspacePaths.GetRelative(workspace.Root, WorkspacePaths.Normalize(entry.FullName));
                    if (entry is DirectoryInfo)
                    {
                        if (filter.IsDirectoryExcluded(entry.Name, relative, entry.Attributes)) { if (Matches(entry.Name)) excluded++; }
                        else pending.Push(entry.FullName);
                    }
                    else if (filter.IsDocument(entry.FullName)) { if (filter.IsFileExcluded(relative) && Matches(entry.Name)) excluded++; }
                    else if (UnreadableTypes.Contains(entry.Extension) && Matches(entry.Name) && unsupported.Count < 5) unsupported.Add(entry.Name);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return (excluded, unsupported);
    }

    private static readonly HashSet<string> UnreadableTypes = new(StringComparer.OrdinalIgnoreCase)
    { ".pdf", ".doc", ".docx", ".rtf", ".odt", ".ppt", ".pptx", ".xls", ".xlsx", ".csv", ".html", ".htm", ".txt" };

    private static (DateTime? Date, string? Shown) ReadDate(string text)
    {
        var match = DatePattern().Match(text);
        if (!match.Success) return (null, null);
        var shown = match.Value.Trim();
        var clean = shown.Replace(",", "", StringComparison.Ordinal);
        string[] formats = ["yyyy-MM-dd", "MMMM d yyyy", "MMM d yyyy", "d MMMM yyyy", "d MMM yyyy"];
        return DateTime.TryParseExact(clean, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? (date, shown) : (null, null);
    }

    [GeneratedRegex(@"\bwhat(?:'|’)?s\s+new\b|\bwhat\s+is\s+new\b|\bchange\s?log\b|\brelease\s+notes\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex WhatsNew();
    [GeneratedRegex(@"\b(?:latest|recent|recently|newest|new|last|current)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex RecentWord();
    [GeneratedRegex(@"\b(?:change|changes|changed|update|updates|updated|release|releases|released|version|versions|fix|fixes|fixed|added)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex ChangeWord();
    // A capitalized word after the first word is usually a name, such as a person, product or project.
    [GeneratedRegex(@"(?<=[\p{L}\p{N},;:]\s+)\p{Lu}[\p{L}\p{N}]+", RegexOptions.CultureInvariant, 200)]
    private static partial Regex NamedWord();
    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant, 200)]
    private static partial Regex WordPattern();
    [GeneratedRegex(@"(?<![\d.])v?(\d+\.\d+(?:\.\d+){0,2})(?![\d])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex VersionNumber();
    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}\b|\b(?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|June?|July?|Aug(?:ust)?|Sep(?:tember)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\s+\d{1,2},?\s+\d{4}\b|\b\d{1,2}\s+(?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|June?|July?|Aug(?:ust)?|Sep(?:tember)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\s+\d{4}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex DatePattern();
}
