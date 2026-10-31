using System.Text;

namespace Tern.Rendering.Mathematics;

/// <summary>Maps text to Unicode superscript or subscript characters when every character has one.</summary>
internal static class UnicodeScripts
{
    private const string Plain = "0123456789+-−=()abcdefghijklmnoprstuvwxyz";
    private const string Super = "⁰¹²³⁴⁵⁶⁷⁸⁹⁺⁻⁻⁼⁽⁾ᵃᵇᶜᵈᵉᶠᵍʰⁱʲᵏˡᵐⁿᵒᵖʳˢᵗᵘᵛʷˣʸᶻ";
    private const string SubPlain = "0123456789+-−=()aehijklmnoprstuvx";
    private const string Sub = "₀₁₂₃₄₅₆₇₈₉₊₋₋₌₍₎ₐₑₕᵢⱼₖₗₘₙₒₚᵣₛₜᵤᵥₓ";

    private static readonly Dictionary<char, char> SuperMap = Build(Plain, Super);
    private static readonly Dictionary<char, char> SubMap = Build(SubPlain, Sub);

    public static string? TryConvert(string text, bool superscript)
    {
        if (text.Length == 0)
        {
            return null;
        }

        var map = superscript ? SuperMap : SubMap;
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (!map.TryGetValue(ch, out var mapped))
            {
                return null;
            }

            builder.Append(mapped);
        }

        return builder.ToString();
    }

    private static Dictionary<char, char> Build(string from, string to)
    {
        if (from.Length != to.Length)
        {
            throw new InvalidOperationException("Script tables are misaligned.");
        }

        var map = new Dictionary<char, char>(from.Length);
        for (var i = 0; i < from.Length; i++)
        {
            map[from[i]] = to[i];
        }

        return map;
    }
}

/// <summary>Flattens a formula tree into readable Unicode text.</summary>
internal static class MathPlainText
{
    public static string Render(MathNode node)
    {
        var builder = new StringBuilder();
        Append(builder, node);
        return builder.ToString();
    }

    public static string Compact(MathNode node) => Render(node).Replace(" ", string.Empty, StringComparison.Ordinal);

    private static void Append(StringBuilder sb, MathNode node)
    {
        switch (node)
        {
            case MathRun run:
                sb.Append(run.Text);
                break;
            case MathGroup group:
                AppendGroup(sb, group);
                break;
            case MathFraction fraction:
                AppendFraction(sb, fraction);
                break;
            case MathRoot root:
                AppendRoot(sb, root);
                break;
            case MathScripts scripts:
                AppendScripts(sb, scripts);
                break;
            case MathDelimited delimited:
                sb.Append(delimited.Left);
                Append(sb, delimited.Body);
                sb.Append(delimited.Right);
                break;
            case MathMatrix matrix:
                AppendMatrix(sb, matrix);
                break;
            case MathAccent accent:
                Append(sb, accent.Base);
                sb.Append(accent.Mark);
                break;
            case MathSpace space:
                if (space.Em > 0)
                {
                    sb.Append(' ');
                }

                break;
            case MathLineBreak:
                sb.Append('\n');
                break;
        }
    }

    private static void AppendGroup(StringBuilder sb, MathGroup group)
    {
        for (var i = 0; i < group.Children.Count; i++)
        {
            var child = group.Children[i];
            if (child is MathRun { Kind: MathRunKind.Relation or MathRunKind.Operator } op && i > 0)
            {
                sb.Append(' ').Append(op.Text).Append(' ');
                continue;
            }

            Append(sb, child);
            if (child is MathRun { Kind: MathRunKind.FunctionName } && i + 1 < group.Children.Count
                && group.Children[i + 1] is not MathRun { Kind: MathRunKind.Delimiter })
            {
                sb.Append(' ');
            }
            else if (child is MathRun { Kind: MathRunKind.Punctuation } && i + 1 < group.Children.Count)
            {
                sb.Append(' ');
            }
        }
    }

    private static string Operand(MathNode node)
    {
        var text = Render(node);
        return text.All(char.IsLetterOrDigit) ? text : "(" + text + ")";
    }

    private static void AppendFraction(StringBuilder sb, MathFraction fraction)
    {
        if (!fraction.HasRule)
        {
            sb.Append('(').Append(Render(fraction.Numerator)).Append(" choose ").Append(Render(fraction.Denominator)).Append(')');
            return;
        }

        sb.Append(Operand(fraction.Numerator)).Append('/').Append(Operand(fraction.Denominator));
    }

    private static void AppendRoot(StringBuilder sb, MathRoot root)
    {
        var index = root.Index is null ? null : Render(root.Index);
        sb.Append(index switch
        {
            null or "2" => "√",
            "3" => "∛",
            "4" => "∜",
            _ => (UnicodeScripts.TryConvert(index, true) ?? "(" + index + ")") + "√",
        });
        sb.Append(Operand(root.Radicand));
    }

    private static void AppendScripts(StringBuilder sb, MathScripts scripts)
    {
        Append(sb, scripts.Base);
        if (scripts.Subscript is not null)
        {
            sb.Append(scripts.SubscriptUnicode ?? "_(" + Render(scripts.Subscript) + ")");
        }

        if (scripts.Superscript is not null)
        {
            sb.Append(scripts.SuperscriptUnicode ?? "^(" + Render(scripts.Superscript) + ")");
        }
    }

    private static void AppendMatrix(StringBuilder sb, MathMatrix matrix)
    {
        var separator = matrix.Kind == MathMatrixKind.Aligned ? "\n" : "; ";
        var cellSeparator = matrix.Kind == MathMatrixKind.Aligned ? " " : ", ";
        sb.Append(matrix.Left);
        for (var r = 0; r < matrix.Rows.Count; r++)
        {
            if (r > 0)
            {
                sb.Append(separator);
            }

            for (var c = 0; c < matrix.Rows[r].Count; c++)
            {
                if (c > 0)
                {
                    sb.Append(cellSeparator);
                }

                Append(sb, matrix.Rows[r][c]);
            }
        }

        sb.Append(matrix.Right);
    }
}
