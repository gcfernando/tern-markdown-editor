namespace Tern.App.Help;

/// <summary>Offline task vocabulary for Tern help; no model or network request.</summary>
public static class GuideQuery
{
    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
    { "how", "do", "does", "i", "we", "can", "could", "would", "should", "to", "use", "using", "the", "a", "an", "for", "in", "on", "with", "my", "me", "please", "tern", "what", "is", "are", "of", "it", "want", "make", "where", "why", "when", "explain", "show", "tell", "about", "help" };

    public static string[] Words(string? text)
    {
        var normalized = new string((text ?? "").Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ').ToArray())
            .Replace("artificial intelligence", "ai", StringComparison.Ordinal);
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !Fillers.Contains(w))
            .Select(w => w switch
            {
                "md" => "markdown", "chatgpt" or "chatbot" => "ai", "saving" or "saved" or "saves" => "save",
                "printing" => "print", "searching" => "search", "formatting" => "format", "opening" => "open",
                "pictures" or "picture" or "images" => "image", "pasting" => "paste", "recovered" or "recovering" => "recovery",
                _ => w
            }).Distinct(StringComparer.Ordinal).ToArray();
        if (words.Contains("ai", StringComparer.Ordinal) && words.Any(w => w is "benefit" or "benefits" or "advantage" or "advantages" or "disadvantage" or "disadvantages" or "without" or "connecting" or "assistance" or "modify" or "modifying" or "creating"))
            return ["ai"];
        // Document context should not prevent a specific task such as “AI for MD files”.
        return words.Any(w => w is not ("markdown" or "file" or "files" or "document" or "documents"))
            ? words.Where(w => w is not ("markdown" or "file" or "files" or "document" or "documents")).ToArray() : words;
    }

    public static bool Matches(string text, IReadOnlyList<string> words)
    {
        var tokens = Words(text);
        return words.All(w => w.Length <= 2 ? tokens.Contains(w, StringComparer.Ordinal) : text.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    public static string? Lesson(string? query)
    {
        var question = query ?? "";
        if (Words(query).Contains("ai", StringComparer.Ordinal) && new[] { "benefit", "advantage", "disadvantage", "without", "assistance", "modify", "modifying", "creating" }.Any(w => question.Contains(w, StringComparison.OrdinalIgnoreCase))) return "ai-choice";
        var words = Words(query);
        if (words.Contains("ai", StringComparer.Ordinal)) return "ai";
        if (words.Any(w => w is "image" or "paste" or "clipboard" or "attachment")) return "images";
        if (words.Any(w => w is "recovery" or "recover" or "undo" or "mistake" or "delete")) return "recover";
        if (words.Any(w => w is "practice" or "practise")) return "practice";
        if (words.Contains("print", StringComparer.Ordinal)) return "print";
        if (words.Contains("save", StringComparer.Ordinal)) return "save";
        if (words.Contains("pdf", StringComparer.Ordinal)) return "pdf";
        if (words.Contains("format", StringComparer.Ordinal)) return "format";
        return null;
    }
}
