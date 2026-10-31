using System.Text;

namespace Tern.Search;

public enum QueryField
{
    /// <summary>Title, headings, tags, path and body.</summary>
    Any,
    Title,
    Heading,
    Tag,
    Path,
}

/// <param name="Words">Folded words of the clause. More than one word means a phrase (the words must be adjacent, in order).</param>
/// <param name="Prefix">The last word is a prefix (<c>word*</c>).</param>
public sealed record QueryClause(QueryField Field, IReadOnlyList<string> Words, bool Prefix, bool Negated);

/// <summary>
/// A user query reduced to safe building blocks. Only folded alphanumeric words survive parsing, so the FTS5 expression
/// produced from it can never contain syntax the user typed.
/// </summary>
public sealed class ParsedQuery
{
    internal const int MaxClauses = 32;

    public ParsedQuery(IReadOnlyList<QueryClause> clauses)
    {
        Positive = [.. clauses.Where(c => !c.Negated)];
        Negative = [.. clauses.Where(c => c.Negated)];
    }

    public IReadOnlyList<QueryClause> Positive { get; }

    public IReadOnlyList<QueryClause> Negative { get; }

    public bool IsEmpty => Positive.Count == 0 && Negative.Count == 0;

    /// <summary>The FTS5 MATCH expression for the positive clauses, with exclusions applied when there are any; null if there is nothing to match.</summary>
    public string? ToMatchExpression()
    {
        if (Positive.Count == 0)
        {
            return null;
        }

        var positive = "(" + string.Join(" AND ", Positive.Select(ToFts)) + ")";
        return Negative.Count == 0 ? positive : positive + " NOT (" + string.Join(" OR ", Negative.Select(ToFts)) + ")";
    }

    /// <summary>The FTS5 expression matching any excluded clause; null if there are none.</summary>
    public string? ToExclusionExpression() =>
        Negative.Count == 0 ? null : string.Join(" OR ", Negative.Select(ToFts));

    private static string ToFts(QueryClause clause)
    {
        var builder = new StringBuilder("(");
        var column = clause.Field switch
        {
            QueryField.Title => "title",
            QueryField.Heading => "headings",
            QueryField.Tag => "tags",
            QueryField.Path => "path",
            _ => null,
        };
        if (column is not null)
        {
            builder.Append(column).Append(" : ");
        }

        builder.Append('"').Append(string.Join(' ', clause.Words)).Append('"');
        if (clause.Prefix)
        {
            builder.Append('*');
        }

        return builder.Append(')').ToString();
    }
}

public static class QueryParser
{
    private static readonly Dictionary<string, QueryField> Fields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["title"] = QueryField.Title,
        ["tag"] = QueryField.Tag,
        ["tags"] = QueryField.Tag,
        ["path"] = QueryField.Path,
        ["heading"] = QueryField.Heading,
        ["headings"] = QueryField.Heading,
    };

    /// <summary>
    /// Understands plain words, <c>"quoted phrases"</c>, <c>prefix*</c>, <c>-excluded</c> and <c>title:</c>, <c>tag:</c>, <c>path:</c>,
    /// <c>heading:</c> filters. Anything else (operators, brackets, stray quotes) is treated as punctuation. Never throws.
    /// </summary>
    public static ParsedQuery Parse(string? input)
    {
        var clauses = new List<QueryClause>();
        if (string.IsNullOrWhiteSpace(input))
        {
            return new ParsedQuery(clauses);
        }

        var i = 0;
        while (i < input.Length && clauses.Count < ParsedQuery.MaxClauses)
        {
            while (i < input.Length && char.IsWhiteSpace(input[i]))
            {
                i++;
            }

            if (i >= input.Length)
            {
                break;
            }

            var negated = false;
            if (input[i] == '-' && i + 1 < input.Length && !char.IsWhiteSpace(input[i + 1]))
            {
                negated = true;
                i++;
            }

            var field = QueryField.Any;
            var colon = FieldPrefixEnd(input, i, out var parsedField);
            if (colon > 0)
            {
                field = parsedField;
                i = colon;
            }

            string text;
            var prefix = false;
            if (i < input.Length && input[i] == '"')
            {
                var close = input.IndexOf('"', i + 1);
                var end = close < 0 ? input.Length : close;
                text = input[(i + 1)..end];
                i = close < 0 ? input.Length : close + 1;
                if (i < input.Length && input[i] == '*')
                {
                    prefix = true;
                    i++;
                }
            }
            else
            {
                var start = i;
                while (i < input.Length && !char.IsWhiteSpace(input[i]))
                {
                    i++;
                }

                text = input[start..i];
                prefix = text.EndsWith('*');
            }

            var words = Tokenizer.Words(text);
            if (words.Count > 0)
            {
                clauses.Add(new QueryClause(field, words, prefix, negated));
            }
        }

        return new ParsedQuery(clauses);
    }

    // Returns the index just past "name:" when a known field name starts at <paramref name="start"/>.
    private static int FieldPrefixEnd(string input, int start, out QueryField field)
    {
        field = QueryField.Any;
        var colon = input.IndexOf(':', start);
        if (colon < 0 || colon == start || colon + 1 >= input.Length || char.IsWhiteSpace(input[colon + 1]))
        {
            return -1;
        }

        for (var i = start; i < colon; i++)
        {
            if (!char.IsLetter(input[i]))
            {
                return -1;
            }
        }

        return Fields.TryGetValue(input[start..colon], out field) ? colon + 1 : -1;
    }
}
