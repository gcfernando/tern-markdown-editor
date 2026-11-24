using Tern.Markdown;

namespace Tern.App.Editing;

public sealed record SpellingIssue(int Start, int Length, string Word, IReadOnlyList<string> Suggestions);

public static class SpellingText
{
    public static string Prepare(string text, MarkdownAnalysis analysis, int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (start > text.Length - length) throw new ArgumentOutOfRangeException(nameof(length));
        var characters = text.AsSpan(start, length).ToArray();
        IEnumerable<SourceRange> excluded = analysis.CodeBlocks.Select(block => block.Range)
            .Concat(analysis.CodeSpans).Concat(analysis.Links.Select(link => link.Range));
        if (analysis.FrontMatter is { } front) excluded = excluded.Append(front.Range);
        foreach (var range in excluded)
            for (var index = Math.Max(start, range.Start); index < Math.Min(start + length, range.End); index++)
                if (characters[index - start] is not '\r' and not '\n') characters[index - start] = ' ';
        return new string(characters);
    }
}
