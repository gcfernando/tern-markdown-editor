using Tern.Domain.Text;

namespace Tern.Markdown;

/// <summary>Result of a structured edit: the edits to apply as one undo step and where the selection should end up afterwards.</summary>
public sealed record EditResult(IReadOnlyList<TextEdit> Edits, int SelectionStart, int SelectionLength)
{
    public string ApplyTo(string text) => TextEdit.ApplyAll(text, Edits);
}

public static class TaskEdits
{
    /// <summary>Flips only the single character inside the brackets so nothing else on the line is disturbed.</summary>
    public static TextEdit Toggle(string text, TaskItemInfo task)
    {
        var markPosition = task.Marker.Start + 1;
        if (markPosition >= text.Length || text[markPosition] is not (' ' or 'x' or 'X'))
        {
            throw new ArgumentException("The task marker no longer matches the text; re-analyse the document.", nameof(task));
        }

        return new TextEdit(markPosition, 1, task.Checked ? " " : "x");
    }

    public static TextEdit? ToggleOnLine(string text, MarkdownAnalysis analysis, int line)
    {
        var task = analysis.Tasks.FirstOrDefault(t => t.Line == line);
        return task is null ? null : Toggle(text, task);
    }

    public static (int Done, int Total) Progress(MarkdownAnalysis analysis) =>
        (analysis.Tasks.Count(t => t.Checked), analysis.Tasks.Count);
}

/// <summary>Rewrites link destinations in place; used when a linked file is moved or renamed.</summary>
public static class LinkRewriter
{
    /// <summary>
    /// <paramref name="map"/> returns the new destination for a link, or null to leave it alone.
    /// Only the destination range is replaced, so link text, titles and surrounding formatting are untouched.
    /// </summary>
    public static IReadOnlyList<TextEdit> Rewrite(string text, MarkdownAnalysis analysis, Func<LinkInfo, string?> map)
    {
        var edits = new List<TextEdit>();
        foreach (var link in analysis.Links)
        {
            if (link.Kind == LinkKind.Autolink)
            {
                continue;
            }

            var replacement = map(link);
            if (replacement is null || replacement == link.Destination)
            {
                continue;
            }

            // Destinations containing spaces are only valid inside angle brackets when written by hand;
            // the formatter percent-encodes instead, so nothing here needs wrapping.
            edits.Add(new TextEdit(link.Range.Start, link.Range.Length, replacement));
        }

        return edits;
    }
}
