namespace Tern.Rendering.Mathematics;

/// <summary>Typeface variant a run should be drawn in.</summary>
public enum MathStyle
{
    /// <summary>Upright text as written.</summary>
    Normal,

    /// <summary>Italic, the default for single-letter variables.</summary>
    Italic,

    /// <summary>Bold (<c>\mathbf</c>, <c>\boldsymbol</c>).</summary>
    Bold,

    /// <summary>Upright roman (<c>\mathrm</c>, function names, <c>\text</c>).</summary>
    Roman,

    /// <summary>Blackboard bold (<c>\mathbb</c>); the run text is already converted to the Unicode double-struck letters.</summary>
    DoubleStruck,

    /// <summary>Calligraphic (<c>\mathcal</c>).</summary>
    Calligraphic,

    /// <summary>Fraktur (<c>\mathfrak</c>).</summary>
    Fraktur,

    /// <summary>Sans-serif (<c>\mathsf</c>).</summary>
    SansSerif,

    /// <summary>Monospace (<c>\mathtt</c>).</summary>
    Monospace,
}

/// <summary>What a run means, so layout can space it correctly.</summary>
public enum MathRunKind
{
    /// <summary>A variable name.</summary>
    Identifier,

    /// <summary>A number.</summary>
    Number,

    /// <summary>A binary or prefix operator such as + or ×.</summary>
    Operator,

    /// <summary>A relation such as = or ≤.</summary>
    Relation,

    /// <summary>A comma, semicolon or similar.</summary>
    Punctuation,

    /// <summary>A symbol such as ∞ or ∂.</summary>
    Symbol,

    /// <summary>A big operator such as ∑ or ∫.</summary>
    BigOperator,

    /// <summary>A function name such as sin or lim.</summary>
    FunctionName,

    /// <summary>Ordinary text from <c>\text</c>.</summary>
    Text,

    /// <summary>A bracket or fence.</summary>
    Delimiter,
}

/// <summary>Layout shape of a matrix-like environment.</summary>
public enum MathMatrixKind
{
    /// <summary>matrix, pmatrix, bmatrix, ...</summary>
    Matrix,

    /// <summary>cases</summary>
    Cases,

    /// <summary>aligned, align, gathered, split</summary>
    Aligned,
}

/// <summary>A node of the parsed formula. Layout code switches over the sealed subtypes.</summary>
public abstract record MathNode;

/// <summary>A run of text in one style.</summary>
public sealed record MathRun(string Text, MathStyle Style, MathRunKind Kind) : MathNode;

/// <summary>Nodes laid out left to right.</summary>
public sealed record MathGroup(IReadOnlyList<MathNode> Children) : MathNode;

/// <summary>A fraction; <paramref name="HasRule"/> is false for binomials.</summary>
public sealed record MathFraction(MathNode Numerator, MathNode Denominator, bool HasRule) : MathNode;

/// <summary>A square root, or an n-th root when <paramref name="Index"/> is given.</summary>
public sealed record MathRoot(MathNode Radicand, MathNode? Index) : MathNode;

/// <summary>
/// A base with subscript and/or superscript. When <paramref name="UseLimits"/> is true the scripts belong above and below
/// the base (as for ∑). <see cref="SubscriptUnicode"/> and <see cref="SuperscriptUnicode"/> hold the scripts as Unicode
/// super/subscript characters when every character has one, otherwise null.
/// </summary>
public sealed record MathScripts(MathNode Base, MathNode? Subscript, MathNode? Superscript, bool UseLimits) : MathNode
{
    /// <summary>The subscript as Unicode subscript characters, or null when not representable.</summary>
    public string? SubscriptUnicode => Subscript is null ? null : UnicodeScripts.TryConvert(MathPlainText.Compact(Subscript), superscript: false);

    /// <summary>The superscript as Unicode superscript characters, or null when not representable.</summary>
    public string? SuperscriptUnicode => Superscript is null ? null : UnicodeScripts.TryConvert(MathPlainText.Compact(Superscript), superscript: true);
}

/// <summary>A body between fences; an empty fence string means none was drawn.</summary>
public sealed record MathDelimited(string Left, string Right, MathNode Body) : MathNode;

/// <summary>A matrix, cases or alignment environment.</summary>
public sealed record MathMatrix(IReadOnlyList<IReadOnlyList<MathNode>> Rows, string Left, string Right, MathMatrixKind Kind) : MathNode;

/// <summary>A base with a combining accent or bar, e.g. <c>\hat{x}</c>. <paramref name="Mark"/> is the Unicode combining character.</summary>
public sealed record MathAccent(MathNode Base, string Mark) : MathNode;

/// <summary>Horizontal space in em; negative for a negative thin space.</summary>
public sealed record MathSpace(double Em) : MathNode;

/// <summary>A forced line break (<c>\\</c> outside an environment).</summary>
public sealed record MathLineBreak : MathNode;

/// <summary>The outcome of rendering a formula. It never throws; on failure <see cref="Root"/> is null and the source is kept for display.</summary>
public sealed record MathResult(MathNode? Root, string Source, string? ErrorMessage, int ErrorPosition)
{
    /// <summary>True when the formula could not be rendered.</summary>
    public bool IsError => ErrorMessage is not null;

    /// <summary>The formula as plain Unicode text, or the original source when it could not be parsed.</summary>
    public string ToPlainUnicode() => Root is null ? Source : MathPlainText.Render(Root);
}
