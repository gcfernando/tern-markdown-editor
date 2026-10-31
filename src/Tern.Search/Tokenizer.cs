using System.Globalization;
using System.Text;

namespace Tern.Search;

/// <summary>One token of a text: where it sits and its folded (lower-case, diacritic-free) form.</summary>
public readonly record struct TextToken(int Start, int Length, string Folded);

/// <summary>
/// Splits text into search tokens the way the index's <c>unicode61</c> tokenizer does (letters and digits make words,
/// case and diacritics are ignored), so matches computed in memory agree with matches computed by SQLite.
/// </summary>
public static class Tokenizer
{
    public static List<TextToken> Tokenize(string text)
    {
        var tokens = new List<TextToken>();
        var start = -1;
        var index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var isWordChar = Rune.IsLetterOrDigit(rune) || IsCombiningMark(rune);
            if (isWordChar && start < 0)
            {
                start = index;
            }
            else if (!isWordChar && start >= 0)
            {
                tokens.Add(new TextToken(start, index - start, Fold(text.AsSpan(start, index - start))));
                start = -1;
            }

            index += rune.Utf16SequenceLength;
        }

        if (start >= 0)
        {
            tokens.Add(new TextToken(start, index - start, Fold(text.AsSpan(start, index - start))));
        }

        return tokens;
    }

    public static List<string> Words(string text) => [.. Tokenize(text).Select(t => t.Folded).Where(w => w.Length > 0)];

    public static string Fold(ReadOnlySpan<char> word)
    {
        var ascii = true;
        foreach (var c in word)
        {
            if (c >= 128)
            {
                ascii = false;
                break;
            }
        }

        if (ascii)
        {
            return string.Create(word.Length, word.ToString(), static (span, source) =>
            {
                for (var i = 0; i < span.Length; i++)
                {
                    span[i] = char.ToLowerInvariant(source[i]);
                }
            });
        }

        var decomposed = word.ToString().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    private static bool IsCombiningMark(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
}
