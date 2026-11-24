using System.Globalization;
using System.Text.RegularExpressions;

namespace Tern.App.Knowledge;

/// <summary>What AI read from the ticked notes, and the text the final answer or report is written from.</summary>
public sealed record KnowledgeReadResult(string Material, int CharactersRead, int CharactersTotal, int Parts, int Notes, IReadOnlyList<string> NotRead);

public sealed record KnowledgeAnswerResult(KnowledgeRequest Request, KnowledgeAnswer Answer, string Coverage, bool ReadEverything);

/// <summary>
/// Reads ticked notes within one connection's "Characters to send at one time" value. Text that does not fit is sent in parts
/// and combined, never silently cut. A size rejection only makes this one request use smaller parts; the saved value is not changed.
/// </summary>
public sealed partial class KnowledgeReader(string connectionName, int sendCharacters,
    Func<string, CancellationToken, Task<string>> ask, Func<Exception, bool> rejectedForSize, IProgress<string>? progress = null,
    Func<AIRequest, CancellationToken, Task<string>>? askRequest = null)
{
    private const string TooMuch = "AI could not fit everything from your ticked notes into one answer. Untick some notes and try again. Your notes were not changed.";
    private const int FitRounds = 4;
    private int _limit = sendCharacters;
    private bool _reduced;
    private readonly Dictionary<string, IReadOnlyList<KnowledgeSourcePassages.Span>> _sourceMaterials = new(StringComparer.Ordinal);

    /// <summary>The largest request this reader will send now. It only goes down, and only for this request.</summary>
    public int Limit => _limit;

    /// <summary>Transfers the current task's source basis to a new reader; any refit selects original passages, never authored prose.</summary>
    public void AdoptSourceMaterial(string material, IReadOnlyList<(KnowledgeSource Source, string Text)> admittednotes)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(admittednotes);
        var (within, _, _) = Within(admittednotes);
        RememberSources(material, SourceSpans(within));
    }

    public string? LowerLimitAdvice => _reduced
        ? $"{connectionName} accepted less than your limit. Try {Number(Math.Max(KnowledgeLimits.MinimumSendCharacters, _limit / 1000 * 1000))} characters." : null;

    /// <summary>Simple one-fact questions may start from the best sections. Anything broader reads everything.</summary>
    public static bool IsOneFactQuestion(string question) =>
        question.Length <= 120 && OneFactStart().IsMatch(question.Trim()) && !BroadWords().IsMatch(question) && !KnowledgeRanking.AsksForVersionRange(question);

    /// <summary>Applies the per-question total. Anything left out is named, with how much of it was not read.</summary>
    public static (IReadOnlyList<(KnowledgeSource Source, string Text)> Read, IReadOnlyList<string> NotRead, int Total) Within(
        IReadOnlyList<(KnowledgeSource Source, string Text)> notes, int maximum = KnowledgeLimits.MaximumQuestionCharacters)
    {
        var read = new List<(KnowledgeSource, string)>(notes.Count);
        var notRead = new List<string>();
        var left = maximum;
        var total = 0;
        foreach (var (source, text) in notes)
        {
            total += text.Length;
            var name = Path.GetFileName(source.RelativePath);
            if (text.Length <= left) { read.Add((source, text)); left -= text.Length; continue; }
            var length = left;
            if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
            if (length > 0)
            {
                read.Add((source, text[..length]));
                notRead.Add($"{name} (the last {Number(text.Length - length)} characters)");
            }
            else notRead.Add(name);
            left = 0;
        }
        return (read, notRead, total);
    }

    public async Task<KnowledgeAnswerResult> AnswerAsync(KnowledgeConversation conversation, KnowledgeRequest request,
        IReadOnlyList<(KnowledgeSource Source, string Text)> notes, KnowledgeLimits limits, CancellationToken token)
    {
        var (within, notRead, _) = Within(notes);
        var characters = within.Sum(note => note.Text.Length);
        RememberSources(string.Concat(KnowledgeReportBuilder.Blocks(within)), SourceSpans(within));
        // A selected note beyond the total allowance is not evidence. Do not offer its ID
        // to the final answer, or accept a citation to text that was never supplied.
        var supplied = within.Select(note => note.Source.Id).ToHashSet();
        if (request.Sources.Any(source => !supplied.Contains(source.Id)))
            request = conversation.PrepareReading(request.Question, request.Operation,
                request.Sources.Where(source => supplied.Contains(source.Id)).ToArray(), limits);
        if (notRead.Count == 0 && conversation.PrepareWhole(request, within, limits, _limit) is { } whole)
        {
            progress?.Report($"AI will read {Number(characters)} characters…");
            if (await TrySendAsync(whole.Prompt!, token).ConfigureAwait(true) is { } reply)
            {
                var answer = conversation.ReadAnswer(reply, whole);
                var checkedAnswer = await ReviewRangeAsync(conversation, whole, answer, string.Concat(KnowledgeReportBuilder.Blocks(within)), token).ConfigureAwait(true);
                return Finish(checkedAnswer.Request, checkedAnswer.Answer, notes.Count, notRead, characters, bestSections: false);
            }
        }
        // An excerpt may come from beyond a note's admitted prefix. Once the total cap
        // leaves anything out, read only the admitted text instead of using excerpts.
        else if (notRead.Count == 0 && IsOneFactQuestion(request.Question) && request.Prompt is { } sections && sections.CharacterCount <= _limit)
        {
            progress?.Report("AI is reading the best sections of your notes…");
            if (await TrySendAsync(sections, token).ConfigureAwait(true) is { } reply)
            {
                var first = conversation.ReadAnswer(reply, request);
                // "Not found" from the best sections is never final: every ticked note is read first.
                if (!first.InsufficientEvidence) return Finish(request, first, notes.Count, notRead, characters, bestSections: true);
            }
        }
        int Room() => conversation.FactsRoom(request, limits, _limit);
        var read = await ReadAllAsync(request.Question, notes, Room, token).ConfigureAwait(true);
        var material = read.Material;
        for (var attempt = 0; attempt <= FitRounds; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (conversation.PrepareFromFacts(request, material, limits, _limit) is not { } final)
            {
                material = await FitAsync(request.Question, material, () => Math.Min(Room(), material.Length * 4 / 5), token).ConfigureAwait(true);
                continue;
            }
            progress?.Report("AI is writing your answer…");
            if (await TrySendAsync(final.Prompt!, token).ConfigureAwait(true) is { } reply)
            {
                var answer = conversation.ReadAnswer(reply, final);
                var checkedAnswer = await ReviewRangeAsync(conversation, final, answer, read.Material, token).ConfigureAwait(true);
                return Finish(checkedAnswer.Request, checkedAnswer.Answer, notes.Count, read.NotRead, read.CharactersRead, bestSections: false);
            }
            material = await FitAsync(request.Question, material, Room, token).ConfigureAwait(true);
        }
        throw new InvalidOperationException(TooMuch);
    }

    private async Task<(KnowledgeRequest Request, KnowledgeAnswer Answer)> ReviewRangeAsync(KnowledgeConversation conversation,
        KnowledgeRequest request, KnowledgeAnswer draft, string material, CancellationToken token)
    {
        if (!KnowledgeRanking.AsksForVersionRange(request.Question)) return (request, draft);
        progress?.Report("AI is checking the version range against your notes…");
        try
        {
            int Room() => _limit - conversation.ReviewRequest(request, draft, "").CharacterCount;
            var (reply, checkedMaterial) = await WriteRequestAsync(request.Question,
                text => conversation.ReviewRequest(request, draft, text), material, Room, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            var prompt = conversation.ReviewRequest(request, draft, checkedMaterial);
            var reviewed = request with { Message = prompt.Text, Prompt = prompt };
            return (reviewed, conversation.ReadAnswer(reply, reviewed));
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or System.Text.Json.JsonException or TimeoutException)
        {
            throw new InvalidOperationException("AI could not check this version-range answer. Your question is kept. Try again.");
        }
    }

    /// <summary>Reads every ticked note, in parts when needed, and returns text that fits <paramref name="finalRoom"/>.</summary>
    public async Task<KnowledgeReadResult> ReadAllAsync(string question, IReadOnlyList<(KnowledgeSource Source, string Text)> notes,
        Func<int> finalRoom, CancellationToken token)
    {
        var (within, notRead, total) = Within(notes);
        var characters = within.Sum(note => note.Text.Length);
        var whole = string.Concat(KnowledgeReportBuilder.Blocks(within));
        var originals = SourceSpans(within);
        RememberSources(whole, originals);
        if (whole.Length <= finalRoom()) return new(whole, characters, total, 1, notes.Count, notRead);
        progress?.Report($"AI will read {Number(characters)} characters in source parts…");
        var selected = await SelectSourcesAsync(question, originals, AIPurpose.ExtractFacts, token).ConfigureAwait(true);
        var material = RememberSources(KnowledgeSourcePassages.Render(selected.Passages), selected.Passages);
        material = await FitAsync(question, material, finalRoom, token).ConfigureAwait(true);
        return new(material, characters, total, selected.Parts, notes.Count, notRead);
    }

    /// <summary>Sends one final prompt built from the read material, making the material smaller if this AI rejects the size.</summary>
    public Task<(string Reply, string Material)> WriteAsync(string question, Func<string, string> prompt, string material, Func<int> room, CancellationToken token) =>
        WriteRequestAsync(question, text => AIRequest.CurrentText(prompt(text)), material, room, token);

    public async Task<(string Reply, string Material)> WriteRequestAsync(string question, Func<string, AIRequest> prompt, string material, Func<int> room, CancellationToken token)
    {
        for (var attempt = 0; attempt <= FitRounds; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (material.Length > room()) material = await FitAsync(question, material, room, token).ConfigureAwait(true);
            var message = prompt(material);
            if (message.CharacterCount > _limit)
            {
                var target = material.Length - (message.CharacterCount - _limit);
                material = await FitAsync(question, material, () => Math.Min(room(), target), token).ConfigureAwait(true);
                continue;
            }
            if (await TrySendAsync(message, token).ConfigureAwait(true) is { } reply) return (reply, material);
        }
        throw new InvalidOperationException(TooMuch);
    }

    public string Coverage(KnowledgeReadResult read) => Describe(read.Notes, read.NotRead, read.CharactersRead, bestSections: false);

    private KnowledgeAnswerResult Finish(KnowledgeRequest request, KnowledgeAnswer answer, int notes, IReadOnlyList<string> notRead, int characters, bool bestSections) =>
        new(request, answer, Describe(notes, notRead, characters, bestSections), !bestSections && notRead.Count == 0);

    private string Describe(int notes, IReadOnlyList<string> notRead, int characters, bool bestSections)
    {
        var ticked = notes == 1 ? "your 1 ticked note" : $"your {notes} ticked notes";
        var missing = notRead.Count > 0 ? $" Not read: {string.Join(", ", notRead)}." : "";
        var text = bestSections ? $"AI read the best sections of {ticked}.{missing}"
            : notRead.Count > 0 ? $"AI read {Number(characters)} characters of {ticked}.{missing}"
            : $"AI read 100% of {ticked}.";
        return LowerLimitAdvice is { } advice ? text + " " + advice : text;
    }

    private async Task<string> FitAsync(string question, string material, Func<int> room, CancellationToken token)
    {
        if (room() < 1) throw new InvalidOperationException(TooMuch);
        for (var round = 0; material.Length > room(); round++)
        {
            if (round == FitRounds) throw new InvalidOperationException(TooMuch);
            progress?.Report("AI is combining what it read…");
            if (_sourceMaterials.TryGetValue(material, out var originals))
            {
                var selection = await SelectSourcesAsync(question, originals, AIPurpose.CombineFacts, token).ConfigureAwait(true);
                var selected = KnowledgeSourcePassages.Render(selection.Passages);
                if (selected.Length >= material.Length) throw new InvalidOperationException(TooMuch);
                material = RememberSources(selected, selection.Passages);
                continue;
            }
            int ChunkRoom() => Math.Max(1, _limit - KnowledgeReportBuilder.CombineRequest(question, "").CharacterCount - 16);
            var pending = new LinkedList<string>(KnowledgeReportBuilder.SplitText(material, ChunkRoom()));
            var combined = new List<string>(pending.Count);
            while (pending.First is { } next)
            {
                token.ThrowIfCancellationRequested();
                pending.RemoveFirst();
                if (next.Value.Length > ChunkRoom()) { AddHalves(pending, next.Value); continue; }
                if (await TrySendAsync(KnowledgeReportBuilder.CombineRequest(question, next.Value), token).ConfigureAwait(true) is { } reply) combined.Add(reply);
                else AddHalves(pending, next.Value);
            }
            var shorter = string.Join('\n', combined);
            if (shorter.Length >= material.Length) throw new InvalidOperationException(TooMuch);
            material = shorter;
        }
        return material;
    }

    private static KnowledgeSourcePassages.Span[] SourceSpans(IReadOnlyList<(KnowledgeSource Source, string Text)> notes) =>
        notes.Where(note => note.Text.Length > 0).Select(note => new KnowledgeSourcePassages.Span(note.Source, note.Text, 0, note.Text.Length)).ToArray();

    private string RememberSources(string material, IReadOnlyList<KnowledgeSourcePassages.Span> originals)
    { _sourceMaterials[material] = originals; return material; }

    private async Task<(IReadOnlyList<KnowledgeSourcePassages.Span> Passages, int Parts)> SelectSourcesAsync(string question,
        IReadOnlyList<KnowledgeSourcePassages.Span> originals, AIPurpose purpose, CancellationToken token)
    {
        var selected = new List<KnowledgeSourcePassages.Span>();
        var parts = 0;
        foreach (var original in originals)
        {
            for (var offset = 0; offset < original.Length;)
            {
                token.ThrowIfCancellationRequested();
                var remaining = original with { Start = original.Start + offset, Length = original.Length - offset };
                AIRequest Build(int count) => purpose == AIPurpose.ExtractFacts
                    ? KnowledgeReportBuilder.FactsRequest(question, remaining with { Length = count })
                    : KnowledgeReportBuilder.CombineRequest(question, remaining with { Length = count });
                var low = 0; var high = Math.Min(remaining.Length, _limit);
                while (low < high)
                {
                    var middle = low + (high - low + 1) / 2;
                    if (Build(middle).CharacterCount <= _limit) low = middle; else high = middle - 1;
                }
                if (low > 0 && char.IsHighSurrogate(remaining.Original[remaining.Start + low - 1])) low--;
                if (low == 0) throw new InvalidOperationException(TooMuch);
                var supplied = remaining with { Length = low };
                progress?.Report($"Reading your notes, part {parts + 1}…");
                if (await TrySendAsync(Build(low), token).ConfigureAwait(true) is not { } reply) continue;
                selected.AddRange(KnowledgeSourcePassages.Read(reply, supplied));
                parts++;
                offset += low;
            }
        }
        return (Coalesce(selected), parts);
    }

    private static IReadOnlyList<KnowledgeSourcePassages.Span> Coalesce(IReadOnlyList<KnowledgeSourcePassages.Span> selected)
    {
        var result = new List<KnowledgeSourcePassages.Span>();
        foreach (var span in selected)
        {
            if (result.Count > 0 && result[^1] is var previous && previous.Source.Id == span.Source.Id
                && ReferenceEquals(previous.Original, span.Original) && previous.Start + previous.Length == span.Start)
                result[^1] = previous with { Length = previous.Length + span.Length };
            else result.Add(span);
        }
        return result;
    }

    private async Task<string?> TrySendAsync(AIRequest message, CancellationToken token)
    {
        if (message.CharacterCount > _limit) throw new InvalidOperationException(TooMuch);
        try { return await (askRequest is null ? ask(message.Text, token) : askRequest(message, token)).ConfigureAwait(true); }
        catch (Exception ex) when (message.CharacterCount > KnowledgeLimits.MinimumSendCharacters && rejectedForSize(ex))
        {
            _limit = Math.Max(KnowledgeLimits.MinimumSendCharacters, Math.Min(_limit, message.CharacterCount) / 2);
            _reduced = true;
            return null;
        }
    }

    private static void AddHalves(LinkedList<string> pending, string part)
    {
        var (first, second) = KnowledgeReportBuilder.SplitInHalf(part);
        pending.AddFirst(second);
        pending.AddFirst(first);
    }

    private static string Number(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^(?:who(?:'s| is| was)|when|where|which \w+ (?:is|was)|what(?:'s| is| was| date| time| day)|how (?:much|long|old))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex OneFactStart();

    [GeneratedRegex(@"\b(?:all|every|each|list|summari[sz]e|summary|compare|explain|overview|report|changes|features|history|why|are|were|total|sum|count|number\s+of|mention|mentions)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex BroadWords();
}
