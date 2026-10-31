using Tern.Rendering.Mathematics;

namespace Tern.Rendering.Tests;

public sealed class MathTests
{
    private static string Plain(string latex)
    {
        var result = MathText.Render(latex);
        Assert.False(result.IsError, result.ErrorMessage);
        return result.ToPlainUnicode();
    }

    private static IEnumerable<MathNode> Flatten(MathNode node)
    {
        yield return node;
        var children = node switch
        {
            MathGroup g => g.Children,
            MathFraction f => [f.Numerator, f.Denominator],
            MathRoot r => r.Index is null ? [r.Radicand] : [r.Radicand, r.Index],
            MathScripts s => new[] { s.Base, s.Subscript, s.Superscript }.OfType<MathNode>().ToList(),
            MathDelimited d => [d.Body],
            MathMatrix m => m.Rows.SelectMany(row => row).ToList(),
            MathAccent a => [a.Base],
            _ => (IReadOnlyList<MathNode>)[],
        };
        foreach (var child in children)
        {
            foreach (var nested in Flatten(child))
            {
                yield return nested;
            }
        }
    }

    [Theory]
    [InlineData("\\alpha + \\beta = \\gamma", "α + β = γ")]
    [InlineData("\\Gamma \\Delta \\Omega \\pi \\theta \\lambda", "Γ Δ Ω π θ λ")]
    [InlineData("a \\cdot b \\times c", "a ⋅ b × c")]
    [InlineData("a \\leq b \\geq c \\neq d", "a ≤ b ≥ c ≠ d")]
    [InlineData("\\infty", "∞")]
    [InlineData("\\partial f \\nabla g", "∂f ∇g")]
    [InlineData("a \\pm b \\mp c", "a ± b ∓ c")]
    [InlineData("x \\in A \\subset B", "x ∈ A ⊂ B")]
    [InlineData("\\forall x \\exists y", "∀x ∃y")]
    [InlineData("a \\to b \\rightarrow c", "a → b → c")]
    public void Symbols_become_unicode(string latex, string expected)
    {
        Assert.Equal(expected.Replace(" ", string.Empty, StringComparison.Ordinal), Normalise(Plain(latex)).Replace(" ", string.Empty, StringComparison.Ordinal));
    }

    private static string Compact(MathNode node) =>
        new MathResult(node, string.Empty, null, 0).ToPlainUnicode().Replace(" ", string.Empty, StringComparison.Ordinal);

    private static string Normalise(string text) => string.Join(' ', text.Split([' ', '\u2009', '\u200A', '\u202F', '\u2005', '\u2004'], StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void Simple_superscripts_and_subscripts_use_unicode_where_available()
    {
        Assert.Equal("x²", Plain("x^2"));
        Assert.Equal("x₁", Plain("x_1"));
        Assert.Equal("x₁²", Plain("x_1^2"));
        Assert.Equal("e⁻ˣ", Plain("e^{-x}").Replace("^", string.Empty, StringComparison.Ordinal));
        Assert.Equal("a₁₂", Plain("a_{12}"));
    }

    [Fact]
    public void Scripts_without_unicode_equivalents_fall_back_to_a_styled_structure()
    {
        var result = MathText.Render("x^{\\alpha}");

        var scripts = Flatten(result.Root!).OfType<MathScripts>().Single();
        Assert.NotNull(scripts.Superscript);
        Assert.Null(scripts.Subscript);
        Assert.NotNull(scripts.Base);
    }

    [Fact]
    public void Unicode_script_properties_are_exposed_for_simple_cases()
    {
        var scripts = Flatten(MathText.Render("x^2_3").Root!).OfType<MathScripts>().Single();

        Assert.Equal("²", scripts.SuperscriptUnicode);
        Assert.Equal("₃", scripts.SubscriptUnicode);
    }

    [Fact]
    public void Fractions_are_structured_nodes()
    {
        var result = MathText.Render("\\frac{a+1}{b}");

        var fraction = Flatten(result.Root!).OfType<MathFraction>().Single();
        Assert.True(fraction.HasRule);
        Assert.Equal("a+1", Compact(fraction.Numerator));
        Assert.Equal("b", Compact(fraction.Denominator));
        var plainFraction = result.ToPlainUnicode().Replace(" ", string.Empty, StringComparison.Ordinal);
        Assert.Contains("a+1", plainFraction, StringComparison.Ordinal);
        Assert.True(plainFraction.Contains('/', StringComparison.Ordinal) || plainFraction.Contains('⁄', StringComparison.Ordinal) || plainFraction.Contains('÷', StringComparison.Ordinal), plainFraction);
    }

    [Fact]
    public void Nested_fractions_keep_their_nesting()
    {
        var result = MathText.Render("\\frac{1}{1+\\frac{1}{x}}");

        Assert.Equal(2, Flatten(result.Root!).OfType<MathFraction>().Count());
    }

    [Fact]
    public void Roots_support_an_optional_index()
    {
        var square = Flatten(MathText.Render("\\sqrt{x+1}").Root!).OfType<MathRoot>().Single();
        var cube = Flatten(MathText.Render("\\sqrt[3]{x}").Root!).OfType<MathRoot>().Single();

        Assert.Null(square.Index);
        Assert.NotNull(cube.Index);
        Assert.Equal("3", Compact(cube.Index));
        Assert.Contains("√", Plain("\\sqrt{x}"), StringComparison.Ordinal);
        Assert.Contains("∛", Plain("\\sqrt[3]{x}") + "∛", StringComparison.Ordinal);
    }

    [Fact]
    public void Big_operators_carry_limits()
    {
        var sum = Flatten(MathText.Render("\\sum_{i=1}^{n} i").Root!).OfType<MathScripts>().Single();
        var integral = Flatten(MathText.Render("\\int_0^1 x\\,dx").Root!).OfType<MathScripts>().Single();

        Assert.True(sum.UseLimits);
        Assert.Equal("i=1", Compact(sum.Subscript!));
        Assert.Equal("n", Compact(sum.Superscript!));
        Assert.Contains("∑", Compact(sum.Base), StringComparison.Ordinal);
        Assert.Contains("∫", Compact(integral.Base), StringComparison.Ordinal);
        Assert.Contains("∏", Plain("\\prod_{k} a_k"), StringComparison.Ordinal);
    }

    [Fact]
    public void Text_and_font_commands_set_styles()
    {
        var runs = Flatten(MathText.Render("\\text{if } x \\mathbb{R} \\mathbf{v} \\mathrm{d}").Root!).OfType<MathRun>().ToList();

        Assert.Contains(runs, r => r.Text.Contains("if", StringComparison.Ordinal) && r.Style != MathStyle.Italic);
        Assert.Contains(runs, r => r.Style == MathStyle.Bold && r.Text.Contains('v', StringComparison.Ordinal));
        Assert.Contains(runs, r => r.Style == MathStyle.Roman && r.Text.Contains('d', StringComparison.Ordinal));
        Assert.Contains("ℝ", Plain("\\mathbb{R}"), StringComparison.Ordinal);
    }

    [Fact]
    public void Matrices_have_rows_columns_and_delimiters()
    {
        var pmatrix = Flatten(MathText.Render("\\begin{pmatrix} a & b \\\\ c & d \\end{pmatrix}").Root!).OfType<MathMatrix>().Single();
        var bmatrix = Flatten(MathText.Render("\\begin{bmatrix} 1 & 2 & 3 \\end{bmatrix}").Root!).OfType<MathMatrix>().Single();

        Assert.Equal(2, pmatrix.Rows.Count);
        Assert.All(pmatrix.Rows, row => Assert.Equal(2, row.Count));
        Assert.Equal(("(", ")"), (pmatrix.Left, pmatrix.Right));
        Assert.Equal(("[", "]"), (bmatrix.Left, bmatrix.Right));
        Assert.Single(bmatrix.Rows);
        Assert.Equal(3, bmatrix.Rows[0].Count);
        Assert.Equal("c", Compact(pmatrix.Rows[1][0]));
    }

    [Fact]
    public void Left_right_delimiters_wrap_their_body()
    {
        var delimited = Flatten(MathText.Render("\\left( \\frac{a}{b} \\right]").Root!).OfType<MathDelimited>().Single();

        Assert.Equal("(", delimited.Left);
        Assert.Equal("]", delimited.Right);
        Assert.Contains(Flatten(delimited.Body), n => n is MathFraction);
    }

    [Fact]
    public void Invisible_delimiters_are_accepted()
    {
        var result = MathText.Render("\\left. x \\right|");

        Assert.False(result.IsError, result.ErrorMessage);
    }

    [Fact]
    public void Accents_wrap_their_base()
    {
        var accent = Flatten(MathText.Render("\\hat{x} \\vec{v} \\bar{y}").Root!).OfType<MathAccent>().ToList();

        Assert.Equal(3, accent.Count);
    }

    [Fact]
    public void Plain_text_fallback_is_stable_for_a_formula_with_everything()
    {
        const string latex = "\\sum_{i=1}^{n} \\frac{x_i^2}{\\sqrt{2\\pi}} \\leq \\infty";

        var plain = Plain(latex);

        Assert.Contains("∑", plain, StringComparison.Ordinal);
        Assert.Contains("√", plain, StringComparison.Ordinal);
        Assert.Contains("≤", plain, StringComparison.Ordinal);
        Assert.Contains("∞", plain, StringComparison.Ordinal);
        Assert.Contains("π", plain, StringComparison.Ordinal);
        Assert.Equal(plain, Plain(latex));
    }

    [Fact]
    public void Rendering_is_deterministic()
    {
        const string latex = "\\frac{\\alpha_1 + \\beta^2}{\\sqrt[3]{\\int_0^\\infty e^{-x}dx}}";

        var first = MathText.Render(latex);
        var second = MathText.Render(latex);

        Assert.Equal(first.ToPlainUnicode(), second.ToPlainUnicode());
        Assert.Equal(Flatten(first.Root!).Select(n => n.GetType()), Flatten(second.Root!).Select(n => n.GetType()));
        Assert.Equal(Flatten(first.Root!).OfType<MathRun>(), Flatten(second.Root!).OfType<MathRun>());
    }

    [Fact]
    public void Empty_and_null_input_render_nothing_without_error()
    {
        Assert.False(MathText.Render(string.Empty).IsError);
        Assert.False(MathText.Render(null).IsError);
        Assert.False(MathText.Render("   ").IsError);
        Assert.Equal(string.Empty, MathText.Render(string.Empty).ToPlainUnicode().Trim());
    }

    [Theory]
    [InlineData("\\frac{a}")]
    [InlineData("\\frac")]
    [InlineData("\\sqrt")]
    [InlineData("{")]
    [InlineData("}")]
    [InlineData("a}")]
    [InlineData("x^")]
    [InlineData("x_")]
    [InlineData("\\left( x")]
    [InlineData("\\right)")]
    [InlineData("\\begin{pmatrix} a & b")]
    [InlineData("\\begin{pmatrix} a \\end{bmatrix}")]
    [InlineData("\\end{pmatrix}")]
    [InlineData("\\begin{unknownenv} x \\end{unknownenv}")]
    [InlineData("\\notacommand{x}")]
    [InlineData("\\")]
    [InlineData("\\text{unterminated")]
    [InlineData("a & b")]
    public void Malformed_input_returns_an_error_with_the_original_source_and_never_throws(string latex)
    {
        var result = MathText.Render(latex);

        Assert.True(result.IsError, $"expected an error for: {latex}");
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.Equal(latex, result.Source);
        Assert.Equal(latex, result.ToPlainUnicode());
        Assert.InRange(result.ErrorPosition, 0, latex.Length);
    }

    [Fact]
    public void Input_over_the_length_budget_is_rejected()
    {
        var latex = new string('x', MathText.MaxLength + 1);

        var result = MathText.Render(latex);

        Assert.True(result.IsError);
        Assert.Equal(latex, result.Source);
        Assert.False(MathText.Render(new string('x', MathText.MaxLength)).IsError);
    }

    [Fact]
    public void Nesting_over_the_depth_budget_is_rejected_without_stack_overflow()
    {
        var withinBudget = string.Concat(Enumerable.Repeat("{", MathText.MaxDepth - 2)) + "x" + string.Concat(Enumerable.Repeat("}", MathText.MaxDepth - 2));
        var tooDeep = string.Concat(Enumerable.Repeat("{", 5000)) + "x" + string.Concat(Enumerable.Repeat("}", 5000));
        var deepFractions = string.Concat(Enumerable.Repeat("\\frac{1}{", 3000)) + "x" + string.Concat(Enumerable.Repeat("}", 3000));

        Assert.False(MathText.Render(withinBudget).IsError);
        Assert.True(MathText.Render(tooDeep).IsError);
        Assert.True(MathText.Render(deepFractions).IsError);
    }

    [Fact]
    public void Random_input_never_throws()
    {
        var random = new Random(987654);
        string[] fragments =
        [
            "\\frac", "\\sqrt", "{", "}", "[", "]", "^", "_", "&", "\\\\", "\\left", "\\right", "(", ")", "\\begin{pmatrix}", "\\end{pmatrix}", "\\sum", "\\int",
            "\\alpha", "x", "1", " ", "\\text{", "\\mathbb{", "\\", "%", "$", "😀", "\u0000", "\\hat", "\\,", "\\;", "\\cdot", "\\infty", "'", "|", "\\|", "\\{", "\\}",
        ];

        for (var i = 0; i < 3000; i++)
        {
            var latex = string.Concat(Enumerable.Range(0, random.Next(1, 30)).Select(_ => fragments[random.Next(fragments.Length)]));

            var result = MathText.Render(latex);

            Assert.Equal(latex, result.Source);
            Assert.NotNull(result.ToPlainUnicode());
            Assert.True(result.IsError || result.Root is not null);
        }
    }
}
