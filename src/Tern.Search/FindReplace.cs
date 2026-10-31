using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Tern.Domain.Text;
using Tern.Filesystem;
using Tern.Markdown;
using Tern.Workspaces;

namespace Tern.Search;

public sealed record FindOptions(
    string Pattern,
    string Replacement = "",
    bool MatchCase = false,
    bool WholeWord = false,
    bool UseRegex = false,
    int ContextLines = 1,
    int MaxMatchesPerFile = 5000,
    int MaxTotalMatches = 50000,
    TimeSpan? RegexTimeout = null);

/// <param name="Line">1-based line where the match starts.</param>
/// <param name="Replacement">The text this match would be replaced with (capture groups already expanded).</param>
/// <param name="Before">The affected line(s) as they are now.</param>
/// <param name="After">The same line(s) with this one match replaced.</param>
public sealed record FindMatch(
    int Start,
    int Length,
    int Line,
    int Column,
    string MatchedText,
    string Replacement,
    string Before,
    string After,
    IReadOnlyList<string> ContextBefore,
    IReadOnlyList<string> ContextAfter);

/// <param name="Truncated">The per-file or total match limit was hit; the list is incomplete and the file is not written by <see cref="FindReplace.ApplyAsync"/>.</param>
/// <param name="TimedOut">The regex exceeded its time budget on this file.</param>
public sealed record FileMatches(
    string Path,
    string OriginalText,
    IReadOnlyList<FindMatch> Matches,
    IReadOnlyList<TextEdit> Edits,
    bool Truncated,
    bool TimedOut);

public sealed record FindReplacePreview(IReadOnlyList<FileMatches> Files, int TotalMatches, bool Truncated, string? PatternError, IReadOnlyList<string> UnreadableFiles)
{
    public bool HasError => PatternError is not null;
}

public sealed record ReplaceFileResult(string Path, RepairStatus Status, string? Message, int Replacements);

/// <summary>
/// Find and replace across many files. <see cref="PreviewAsync"/> only reads: it returns every match grouped by file with
/// before/after context and a ready-to-apply <see cref="TextEdit"/> list per file, so the app can apply edits through an
/// open editor or call <see cref="ApplyAsync"/> to write closed files with the safe-save service.
/// Regexes are run with a time limit, and the linear-time engine is preferred whenever the pattern allows it, so a
/// catastrophic pattern ends in a reported timeout rather than a hang.
/// </summary>
public sealed class FindReplace
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private readonly IDocumentTextProvider _provider;

    /// <param name="provider">Where text comes from. Use an <see cref="OverlayTextProvider"/> to include unsaved buffers; applying such a file to disk is then refused as a conflict rather than overwriting the user's newer disk-or-buffer state.</param>
    public FindReplace(IDocumentTextProvider provider)
    {
        _provider = provider;
    }

    public async Task<FindReplacePreview> PreviewAsync(IEnumerable<string> paths, FindOptions options, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(options.Pattern))
        {
            return new FindReplacePreview([], 0, false, "Enter text to find.", []);
        }

        var matcher = Matcher.Create(options, out var error);
        if (matcher is null)
        {
            return new FindReplacePreview([], 0, false, error, []);
        }

        var results = new ConcurrentBag<FileMatches>();
        var unreadable = new ConcurrentBag<string>();
        var total = 0;
        var truncated = false;
        var limit = Math.Max(1, options.MaxTotalMatches);

        await Parallel.ForEachAsync(
            paths,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8), CancellationToken = cancellationToken },
            async (path, token) =>
            {
                if (Volatile.Read(ref total) >= limit)
                {
                    truncated = true;
                    return;
                }

                string? text;
                try
                {
                    text = await _provider.ReadTextAsync(path, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    text = null;
                }

                if (text is null)
                {
                    unreadable.Add(path);
                    return;
                }

                var file = Scan(path, text, matcher, options);
                if (file is not null)
                {
                    results.Add(file);
                    if (Interlocked.Add(ref total, file.Matches.Count) >= limit)
                    {
                        truncated = true;
                    }
                }
            }).ConfigureAwait(false);

        var files = results.OrderBy(f => f.Path, WorkspacePaths.Comparer).ToList();
        truncated |= files.Any(f => f.Truncated);
        return new FindReplacePreview(files, files.Sum(f => f.Matches.Count), truncated, null, [.. unreadable.Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Writes the replacements of a preview to disk, one file at a time through the safe-save service. A file that changed
    /// since the preview, or whose preview was incomplete or timed out, is left untouched and reported.
    /// </summary>
    public static async Task<IReadOnlyList<ReplaceFileResult>> ApplyAsync(
        DocumentFileService files,
        FindReplacePreview preview,
        Func<FileMatches, bool>? filter = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ReplaceFileResult>();
        foreach (var file in preview.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Edits.Count == 0 || (filter is not null && !filter(file)))
            {
                results.Add(new ReplaceFileResult(file.Path, RepairStatus.Skipped, null, 0));
                continue;
            }

            if (file.Truncated || file.TimedOut)
            {
                results.Add(new ReplaceFileResult(file.Path, RepairStatus.Skipped, "The match list for this file is incomplete, so it was not changed.", 0));
                continue;
            }

            var (status, message) = await SafeEditApplier.ApplyAsync(files, file.Path, file.OriginalText, file.Edits, cancellationToken).ConfigureAwait(false);
            results.Add(new ReplaceFileResult(file.Path, status, message, status == RepairStatus.Applied ? file.Edits.Count : 0));
        }

        return results;
    }

    private static FileMatches? Scan(string path, string text, Matcher matcher, FindOptions options)
    {
        var perFile = Math.Max(1, options.MaxMatchesPerFile);
        var raw = new List<RawMatch>();
        var truncated = false;
        var timedOut = false;
        try
        {
            foreach (var match in matcher.Matches(text))
            {
                if (raw.Count >= perFile)
                {
                    truncated = true;
                    break;
                }

                // A replacement that leaves empty text empty is a no-op, not a match worth listing.
                if (match.Length == 0 && match.Replacement.Length == 0)
                {
                    continue;
                }

                raw.Add(match);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            timedOut = true;
        }

        if (raw.Count == 0 && !timedOut)
        {
            return null;
        }

        var lines = new LineIndex(text);
        var matches = new List<FindMatch>(raw.Count);
        var edits = new List<TextEdit>(raw.Count);
        foreach (var m in raw)
        {
            matches.Add(Describe(text, lines, m, options.ContextLines));
            if (!text.AsSpan(m.Start, m.Length).SequenceEqual(m.Replacement))
            {
                edits.Add(new TextEdit(m.Start, m.Length, m.Replacement));
            }
        }

        return new FileMatches(path, text, matches, edits, truncated, timedOut);
    }

    private static FindMatch Describe(string text, LineIndex lines, RawMatch m, int contextLines)
    {
        var startLine = lines.LineOf(m.Start);
        var endLine = lines.LineOf(Math.Max(m.Start, m.Start + m.Length - (m.Length > 0 ? 1 : 0)));
        var blockStart = lines.LineStart(startLine);
        // LineEnd excludes its terminator. A match may consume that terminator (or start
        // between CR and LF), so the preview must include the whole match as well.
        var blockEnd = Math.Max(lines.LineEnd(text, endLine), m.Start + m.Length);
        var before = text[blockStart..blockEnd];
        var after = string.Concat(text.AsSpan(blockStart, m.Start - blockStart), m.Replacement, text.AsSpan(m.Start + m.Length, blockEnd - (m.Start + m.Length)));

        var context = Math.Max(0, contextLines);
        var above = new List<string>();
        for (var l = Math.Max(1, startLine - context); l < startLine; l++)
        {
            above.Add(text[lines.LineStart(l)..lines.LineEnd(text, l)]);
        }

        var below = new List<string>();
        for (var l = endLine + 1; l <= Math.Min(lines.LineCount, endLine + context); l++)
        {
            below.Add(text[lines.LineStart(l)..lines.LineEnd(text, l)]);
        }

        return new FindMatch(m.Start, m.Length, startLine, m.Start - blockStart + 1, text.Substring(m.Start, m.Length), m.Replacement, before, after, above, below);
    }

    private readonly record struct RawMatch(int Start, int Length, string Replacement);

    private sealed class Matcher
    {
        private readonly Regex? _regex;
        private readonly string? _literal;
        private readonly StringComparison _comparison;
        private readonly string _replacement;
        private readonly bool _expandGroups;

        private Matcher(Regex? regex, string? literal, StringComparison comparison, string replacement, bool expandGroups)
        {
            _regex = regex;
            _literal = literal;
            _comparison = comparison;
            _replacement = replacement;
            _expandGroups = expandGroups;
        }

        public static Matcher? Create(FindOptions options, out string? error)
        {
            error = null;
            var timeout = options.RegexTimeout ?? DefaultTimeout;
            var flags = RegexOptions.CultureInvariant | RegexOptions.Multiline | (options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);

            if (!options.UseRegex && !options.WholeWord)
            {
                return new Matcher(null, options.Pattern, options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase, options.Replacement, false);
            }

            var pattern = options.UseRegex
                ? (options.WholeWord ? @"(?<![\p{L}\p{N}_])(?:" + options.Pattern + @")(?![\p{L}\p{N}_])" : options.Pattern)
                : @"(?<![\p{L}\p{N}_])" + Regex.Escape(options.Pattern) + @"(?![\p{L}\p{N}_])";

            try
            {
                Regex regex;
                try
                {
                    // Linear-time engine first: it cannot backtrack catastrophically. It rejects lookarounds and backreferences.
                    regex = new Regex(pattern, flags | RegexOptions.NonBacktracking, timeout);
                }
                catch (NotSupportedException)
                {
                    regex = new Regex(pattern, flags, timeout);
                }

                return new Matcher(regex, null, StringComparison.Ordinal, options.Replacement, options.UseRegex);
            }
            catch (ArgumentException ex)
            {
                error = "Invalid regular expression: " + ex.Message;
                return null;
            }
        }

        public IEnumerable<RawMatch> Matches(string text)
        {
            if (_regex is null)
            {
                var index = 0;
                while (index <= text.Length - _literal!.Length)
                {
                    var found = text.IndexOf(_literal, index, _comparison);
                    if (found < 0)
                    {
                        yield break;
                    }

                    yield return new RawMatch(found, _literal.Length, _replacement);
                    index = found + _literal.Length;
                }

                yield break;
            }

            foreach (Match match in _regex.EnumerateMatchesAsMatches(text))
            {
                yield return new RawMatch(match.Index, match.Length, _expandGroups ? match.Result(_replacement) : _replacement);
            }
        }
    }
}

internal static class RegexExtensions
{
    // Regex.Matches is lazy for the next match only when enumerated one at a time; this keeps timeouts surfacing mid-enumeration.
    public static IEnumerable<Match> EnumerateMatchesAsMatches(this Regex regex, string text)
    {
        var match = regex.Match(text);
        while (match.Success)
        {
            yield return match;
            match = match.NextMatch();
        }
    }
}
