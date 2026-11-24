using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tern.Export;
using Tern.Markdown;

namespace Tern.App.Knowledge;

public static partial class KnowledgeReportBuilder
{
    private static readonly JsonSerializerOptions ReportJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Production sharing gate. Legacy drafts can still load, but a management report
    /// needs substantive findings and verified source passages before it can be checked/shared.
    /// Call <see cref="ValidateEvidence"/> as well to recheck those passages against current read notes.
    /// </summary>
    public static void ValidateManagementContent(DecisionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Findings is null || report.Findings.Count == 0 || report.Findings.Any(f => f is null || string.IsNullOrWhiteSpace(f.Detail)))
            throw new InvalidOperationException("AI did not provide detailed findings. Prepare the report again before sharing it.");
        if (report.Evidence is null || report.Evidence.Count == 0 || report.Evidence.Any(e => e is null || !e.QuoteVerified || string.IsNullOrWhiteSpace(e.Quote)
            || e.Id < 1 || e.SourceId < 1 || e.Line is not > 0 || string.IsNullOrWhiteSpace(e.RelativePath)
            || string.IsNullOrWhiteSpace(e.SourceDigest) || e.SourceDigest.Length != 64 || e.SourceDigest.Any(c => !Uri.IsHexDigit(c)))
            || report.Evidence.Select(e => e.Id).Distinct().Count() != report.Evidence.Count)
            throw new InvalidOperationException("AI did not provide verified supporting passages. Prepare the report again before sharing it.");
        var verified = report.Evidence.Select(e => e.Id).ToHashSet();
        foreach (var finding in report.Findings)
        {
            if (finding.Kind is not ("Fact" or "Calculation" or "Target" or "Interpretation" or "Recommendation"))
                throw new InvalidOperationException("Choose whether each detailed finding is a fact, calculation, target, interpretation or recommendation, then check the report again.");
            var references = finding.EvidenceIds ?? [];
            if (references.Any(id => !verified.Contains(id)))
                throw new InvalidOperationException("A detailed finding refers to evidence that was not verified. Prepare the report again before sharing it.");
            if (references.Count == 0)
            {
                // An ungrounded interpretation is a visibly unresolved question, never an
                // established fact. Both the finding and the final missing-information table
                // must make its gap explicit; a vague caveat alone cannot bypass the gate.
                var missingBasis = (finding.Kind is "Interpretation" or "Recommendation")
                    && !string.IsNullOrWhiteSpace(finding.Title) && !string.IsNullOrWhiteSpace(finding.Limitations)
                    && report.Missing?.Any(m => m is not null && string.Equals(m.Item, finding.Title, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(m.WhyItMatters) && !string.IsNullOrWhiteSpace(m.WhoCanConfirm)) == true;
                if (!missingBasis)
                    throw new InvalidOperationException("A detailed finding has no verified supporting passage. Prepare the report again, or explicitly record the missing evidence for an interpretation.");
            }
            if (finding.Kind == "Calculation" && string.IsNullOrWhiteSpace(finding.Derivation))
                throw new InvalidOperationException("A calculation is missing its inputs or method. Add them and check the report again.");
        }
    }

    /// <summary>Rechecks persisted/edited report evidence against the actual notes read for this report.</summary>
    public static void ValidateEvidence(DecisionReport report, IReadOnlyList<(KnowledgeSource Source, string Text)> notes)
    {
        if (report is null || report.Evidence is null || report.Findings is null
            || report.Evidence.Any(e => e is null || e.Quote is null || e.Section is null || e.Note is null || e.RelativePath is null || e.DocumentedDate is null)
            || report.Findings.Any(f => f is null || f.EvidenceIds is null)
            || notes.Select(n => n.Source.Id).Distinct().Count() != notes.Count) throw ReportFailure();
        var supplied = notes.ToDictionary(n => n.Source.Id);
        var digests = notes.ToDictionary(n => n.Source.Id, n => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(n.Text))));
        var ids = report.Evidence.Select(e => e.Id).ToHashSet();
        if (report.Findings.Any(f => f.EvidenceIds.Any(id => !ids.Contains(id)))) throw ReportFailure();
        foreach (var evidence in report.Evidence)
        {
            if (evidence.SourceId > 0 && !supplied.ContainsKey(evidence.SourceId)) throw ReportFailure();
            if (!evidence.QuoteVerified)
            {
                if (evidence.Quote.Length > 0) throw ReportFailure();
                continue;
            }
            if (evidence.Quote.Length == 0 || !supplied.TryGetValue(evidence.SourceId, out var note)
                || !string.Equals(evidence.SourceDigest, digests[evidence.SourceId], StringComparison.Ordinal))
                throw new InvalidOperationException("The report's source notes changed. Read them again before sharing this report.");
            var match = LocateQuote(note.Text, evidence.Quote, evidence.Line);
            if (match.Start < 0 || note.Text.Substring(match.Start, match.Length) != evidence.Quote) throw ReportFailure();
            var analysis = MarkdownAnalyzer.Analyze(note.Text, MarkdownProfile.Extended);
            var heading = analysis.Headings.LastOrDefault(h => h.Range.Start <= match.Start)?.Text ?? "";
            if (evidence.Line != analysis.Lines.LineOf(match.Start) || evidence.Section != heading
                || evidence.RelativePath != note.Source.RelativePath
                || evidence.Note != Path.GetFileNameWithoutExtension(note.Source.RelativePath)
                || evidence.DocumentedDate != WrittenDate(heading, analysis.FrontMatter?.Yaml)) throw ReportFailure();
        }
    }

    private static ReportEvidence[] ReadEvidence(JsonElement root, IReadOnlyList<(KnowledgeSource Source, string Text)> notes)
    {
        if (notes.Select(n => n.Source.Id).Distinct().Count() != notes.Count) throw ReportFailure();
        var supplied = notes.ToDictionary(n => n.Source.Id);
        var parsed = new Dictionary<int, MarkdownAnalysis>();
        var next = 0;
        var ids = new HashSet<int>();
        return Items(root, "evidence", item =>
        {
            if (!item.TryGetProperty("sourceId", out var number) || !KnowledgeConversation.SourceNumber(number, out var sourceId)
                || !supplied.TryGetValue(sourceId, out var note))
                throw new InvalidOperationException("The report refers to a note that was not supplied. Please try again.");
            var id = item.TryGetProperty("id", out var evidenceNumber) && KnowledgeConversation.SourceNumber(evidenceNumber, out var explicitId) ? explicitId : next + 1;
            next = Math.Max(next, id);
            if (id < 1 || !ids.Add(id)) throw new InvalidOperationException("The report contains repeated or invalid evidence references. Please try again.");
            var fact = Text(item, "fact");
            var quote = Raw(item, "quote");
            var hasStart = item.TryGetProperty("quoteStart", out var startValue);
            var hasEnd = item.TryGetProperty("quoteEnd", out var endValue);
            if (hasStart && startValue.ValueKind != JsonValueKind.String || hasEnd && endValue.ValueKind != JsonValueKind.String)
                throw ReportFailure();
            (int Start, int Length)? anchored = null;
            // An app-authorized range repair supplies an explicit strict quotation.
            // Existing anchor strings remain data; they cannot override that quote.
            if (quote.Length == 0 && (hasStart || hasEnd))
            {
                try
                {
                    var opening = hasStart ? startValue.GetString()! : "";
                    quote = ResolveEvidenceAnchors(note.Text, opening, hasEnd ? endValue.GetString()! : "");
                    anchored = (note.Text.IndexOf(opening, StringComparison.Ordinal), quote.Length);
                }
                catch (InvalidOperationException error)
                { KnowledgeReportRepair.MarkLiteralQuoteMismatch(error); throw; }
            }
            // Older replies supplied only a fact and an explicit note ID. It can become a
            // quotation only when those exact words really occur in that same supplied note.
            if (quote.Length == 0 && fact.Length > 0 && note.Text.Contains(fact, StringComparison.Ordinal)) quote = fact;
            var evidence = new ReportEvidence(fact, Path.GetFileNameWithoutExtension(note.Source.RelativePath), "")
            {
                Id = id, SourceId = sourceId, RelativePath = note.Source.RelativePath,
                SourceDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(note.Text))),
            };
            if (quote.Length == 0) return evidence; // Explicitly unverified; never fabricated by source-list order.
            var lineHint = item.TryGetProperty("line", out var line) && KnowledgeConversation.SourceNumber(line, out var hint) && hint > 0 ? hint : (int?)null;
            var match = anchored ?? LocateQuote(note.Text, quote, lineHint);
            if (anchored is not null && lineHint is { } expectedLine && new LineIndex(note.Text).LineOf(match.Start) != expectedLine)
                throw ReportFailure();
            if (match.Start < 0)
            {
                var error = new InvalidOperationException("A quotation in the report does not match the supplied note. Please try again.");
                // Preserve the strict exception and comparison. Only an actual
                // missing literal (not a wrong locator) permits one source-based repair.
                var original = note.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
                var sought = quote.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
                if (!original.Contains(sought, StringComparison.Ordinal)) KnowledgeReportRepair.MarkLiteralQuoteMismatch(error);
                throw error;
            }
            if (!parsed.TryGetValue(sourceId, out var analysis)) parsed[sourceId] = analysis = MarkdownAnalyzer.Analyze(note.Text, MarkdownProfile.Extended);
            var heading = analysis.Headings.LastOrDefault(h => h.Range.Start <= match.Start);
            return evidence with
            {
                Quote = note.Text.Substring(match.Start, match.Length), QuoteVerified = true,
                Line = analysis.Lines.LineOf(match.Start), Section = heading?.Text ?? "",
                DocumentedDate = WrittenDate(heading?.Text ?? "", analysis.FrontMatter?.Yaml),
            };
        });
    }

    internal static string ResolveEvidenceAnchors(string original, string start, string end)
    {
        const string missing = "The report's quotation anchors do not identify one exact source passage. Please try again.";
        if (string.IsNullOrWhiteSpace(start) || string.IsNullOrWhiteSpace(end) || BrokenUnicode(start) || BrokenUnicode(end))
            throw new InvalidOperationException(missing);
        var first = original.IndexOf(start, StringComparison.Ordinal);
        var last = original.IndexOf(end, StringComparison.Ordinal);
        if (first < 0 || last < first || last + end.Length < first + start.Length
            || original.IndexOf(start, first + 1, StringComparison.Ordinal) >= 0
            || original.IndexOf(end, last + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException(missing);
        return original.Substring(first, last + end.Length - first);
    }

    private static bool BrokenUnicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            { if (index + 1 >= value.Length || !char.IsLowSurrogate(value[++index])) return true; }
            else if (char.IsLowSurrogate(value[index])) return true;
        }
        return false;
    }

    private static ReportEvidence[] CaptionEvidence(IReadOnlyList<ReportEvidence> evidence, IReadOnlyList<ReportFinding> findings) =>
        evidence.Select(item => item.Fact.Length > 0 ? item : item with
        {
            Fact = findings.FirstOrDefault(finding => finding.EvidenceIds.Contains(item.Id))?.Title ?? "",
        }).ToArray();

    private static ReportFinding[] ReadFindings(JsonElement root, IReadOnlyList<ReportEvidence> evidence)
    {
        var evidenceIds = evidence.Select(e => e.Id).ToHashSet();
        return Items(root, "findings", item =>
        {
            var detail = Text(item, "detail");
            if (detail.Length == 0) throw new InvalidOperationException("A detailed finding was empty. Please try the report again.");
            var references = new List<int>();
            if (item.TryGetProperty("evidenceIds", out var ids) && ids.ValueKind != JsonValueKind.Null)
            {
                if (ids.ValueKind != JsonValueKind.Array) throw ReportFailure();
                foreach (var value in ids.EnumerateArray())
                {
                    if (!KnowledgeConversation.SourceNumber(value, out var id) || !evidenceIds.Contains(id))
                        throw new InvalidOperationException("A finding refers to evidence that was not supplied. Please try again.");
                    if (!references.Contains(id)) references.Add(id);
                }
            }
            var kind = Text(item, "kind");
            kind = new[] { "Fact", "Calculation", "Target", "Interpretation", "Recommendation" }
                .FirstOrDefault(value => value.Equals(kind, StringComparison.OrdinalIgnoreCase)) ?? "Finding";
            return new ReportFinding(Text(item, "title"), kind, detail)
            {
                Keywords = Strings(item, "keywords").Where(keyword => keyword.Length is >= 2 and <= 80
                    && keyword.Length < detail.Length && detail.Contains(keyword, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                EvidenceIds = references, Derivation = Text(item, "derivation"), Limitations = Text(item, "limitations"),
            };
        });
    }

    private static string Raw(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

    private static (int Start, int Length) LocateQuote(string text, string quote, int? line)
    {
        var original = text;
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var sought = quote.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var offset = normalized.IndexOf(sought, StringComparison.Ordinal);
        if (offset < 0) return (-1, 0);
        var index = new LineIndex(normalized);
        if (line is { } requested)
        {
            while (offset >= 0 && index.LineOf(offset) != requested) offset = normalized.IndexOf(sought, offset + 1, StringComparison.Ordinal);
            if (offset < 0) return (-1, 0);
        }
        else if (normalized.IndexOf(sought, offset + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("A quotation appears more than once in its note. Include its exact source line and try again.");
        int OriginalOffset(int at)
        {
            var canonical = 0;
            for (var i = 0; i < original.Length; i++)
            {
                if (canonical == at) return i;
                if (original[i] == '\r' && i + 1 < original.Length && original[i + 1] == '\n') i++;
                canonical++;
            }
            return original.Length;
        }
        var start = OriginalOffset(offset);
        return (start, OriginalOffset(offset + sought.Length) - start);
    }

    private static string WrittenDate(string actualHeading, string? frontMatter)
    {
        // A file's saved date and the search-hit heading are not evidence for a release date.
        var iso = ReportDate().Match(actualHeading);
        if (iso.Success && DateOnly.TryParseExact(iso.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return iso.Value;
        if (frontMatter is not null)
        {
            var field = FrontMatterDate().Match(frontMatter);
            if (field.Success && DateTime.TryParse(field.Groups[1].Value.Trim(' ', '\'', '"'), CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                return field.Groups[1].Value.Trim(' ', '\'', '"');
        }
        return "";
    }

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}\b", RegexOptions.CultureInvariant, 200)]
    private static partial Regex ReportDate();

    [GeneratedRegex(@"^date\s*:\s*([^\r\n]+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex FrontMatterDate();
}
