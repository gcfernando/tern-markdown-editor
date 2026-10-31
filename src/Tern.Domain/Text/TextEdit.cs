using System.Text;

namespace Tern.Domain.Text;

/// <summary>A single replacement expressed in UTF-16 code units against a specific text snapshot.</summary>
public readonly record struct TextEdit(int Start, int Length, string NewText)
{
    public int End => Start + Length;

    public static TextEdit Insert(int offset, string text) => new(offset, 0, text);

    public static TextEdit Delete(int start, int length) => new(start, length, string.Empty);

    /// <summary>
    /// Applies non-overlapping edits that all refer to the original text.
    /// Throws if the edits overlap or fall outside the text, because that indicates a stale snapshot.
    /// </summary>
    public static string ApplyAll(string text, IReadOnlyList<TextEdit> edits)
    {
        if (edits.Count == 0)
        {
            return text;
        }

        var ordered = edits.OrderBy(e => e.Start).ThenBy(e => e.Length).ToArray();
        var builder = new StringBuilder(text.Length);
        var cursor = 0;
        foreach (var edit in ordered)
        {
            if (edit.Start < cursor || edit.Length < 0 || edit.End > text.Length)
            {
                throw new ArgumentException("Edits overlap or lie outside the document.", nameof(edits));
            }

            builder.Append(text, cursor, edit.Start - cursor);
            builder.Append(edit.NewText);
            cursor = edit.End;
        }

        builder.Append(text, cursor, text.Length - cursor);
        return builder.ToString();
    }
}
