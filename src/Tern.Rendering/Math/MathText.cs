using System.Globalization;

namespace Tern.Rendering.Mathematics;

/// <summary>
/// Renders a restricted TeX subset into a structured, UI-independent formula tree. It never throws: malformed or
/// over-budget input yields a <see cref="MathResult"/> with an error message and the original source.
/// </summary>
public static class MathText
{
    /// <summary>Maximum accepted formula length in UTF-16 characters.</summary>
    public const int MaxLength = 10_000;

    /// <summary>Maximum nesting depth of groups, scripts, fractions and environments.</summary>
    public const int MaxDepth = 32;

    /// <summary>Parses <paramref name="latex"/> into a formula tree.</summary>
    /// <param name="latex">The TeX source without surrounding dollar signs.</param>
    public static MathResult Render(string? latex)
    {
        var source = latex ?? string.Empty;
        if (source.Length > MaxLength)
        {
            return new MathResult(null, source, $"Formula is longer than {MaxLength} characters.", 0);
        }

        var parser = new Parser(source);
        try
        {
            var root = parser.ParseDocument();
            return new MathResult(root, source, null, -1);
        }
        catch (MathSyntaxException ex)
        {
            return new MathResult(null, source, ex.Message, ex.Position);
        }
    }

    private sealed class MathSyntaxException(string message, int position) : Exception(message)
    {
        public int Position { get; } = position;
    }

    private enum Stop
    {
        End,
        Brace,
        Right,
        Environment,
    }

    private sealed class Parser(string text)
    {
        private int pos;
        private int depth;
        private MathStyle? style;

        public MathNode ParseDocument()
        {
            var node = ParseExpression(Stop.End);
            return node;
        }

        private MathSyntaxException Error(string message) => new(message, pos);

        private void Enter()
        {
            if (++depth > MaxDepth)
            {
                throw Error($"Formula is nested deeper than {MaxDepth} levels.");
            }
        }

        private void Leave() => depth--;

        private void SkipSpaces()
        {
            while (pos < text.Length && char.IsWhiteSpace(text[pos]))
            {
                pos++;
            }
        }

        private bool LookingAt(string value) => string.CompareOrdinal(text, pos, value, 0, value.Length) == 0;

        private bool AtCommand(string name)
        {
            if (!LookingAt("\\" + name))
            {
                return false;
            }

            var end = pos + name.Length + 1;
            return end >= text.Length || !char.IsAsciiLetter(text[end]);
        }

        private MathNode ParseExpression(Stop stop)
        {
            var items = new List<MathNode>();
            while (true)
            {
                SkipSpaces();
                if (pos >= text.Length)
                {
                    if (stop is Stop.Brace)
                    {
                        throw Error("Missing closing }.");
                    }

                    if (stop is Stop.Right)
                    {
                        throw Error("Missing \\right.");
                    }

                    if (stop is Stop.Environment)
                    {
                        throw Error("Missing \\end.");
                    }

                    break;
                }

                var c = text[pos];
                if (c == '}')
                {
                    if (stop == Stop.Brace)
                    {
                        break;
                    }

                    throw Error("Unmatched }.");
                }

                if (stop == Stop.Environment && (c == '&' || LookingAt("\\\\") || AtCommand("end")))
                {
                    break;
                }

                if (stop == Stop.Right && AtCommand("right"))
                {
                    break;
                }

                if (c == '&')
                {
                    throw Error("Unexpected & outside an environment.");
                }

                if (LookingAt("\\\\"))
                {
                    pos += 2;
                    items.Add(new MathLineBreak());
                    continue;
                }

                if (AtCommand("right") || AtCommand("end"))
                {
                    throw Error("Unexpected " + (AtCommand("right") ? "\\right" : "\\end") + ".");
                }

                items.Add(ParseWithScripts());
            }

            return items.Count == 1 ? items[0] : new MathGroup(items);
        }

        private MathNode ParseWithScripts()
        {
            MathNode baseNode;
            if (text[pos] is '^' or '_')
            {
                baseNode = new MathGroup([]);
            }
            else
            {
                baseNode = ParseAtom();
            }

            MathNode? sub = null;
            MathNode? sup = null;
            while (true)
            {
                SkipSpaces();
                if (pos >= text.Length || text[pos] is not ('^' or '_'))
                {
                    break;
                }

                var isSuper = text[pos] == '^';
                pos++;
                if ((isSuper && sup is not null) || (!isSuper && sub is not null))
                {
                    throw Error(isSuper ? "Double superscript." : "Double subscript.");
                }

                Enter();
                var script = ParseArgument();
                Leave();
                if (isSuper)
                {
                    sup = script;
                }
                else
                {
                    sub = script;
                }
            }

            if (sub is null && sup is null)
            {
                return baseNode;
            }

            return new MathScripts(baseNode, sub, sup, WantsLimits(baseNode));
        }

        private static bool WantsLimits(MathNode node) =>
            node is MathRun { Kind: MathRunKind.BigOperator } run && run.Text is not ("∫" or "∬" or "∭" or "∮")
            || node is MathRun { Kind: MathRunKind.FunctionName } fn && fn.Text is "lim" or "max" or "min" or "sup" or "inf" or "limsup" or "liminf";

        private MathNode ParseArgument()
        {
            SkipSpaces();
            if (pos >= text.Length)
            {
                throw Error("Missing argument.");
            }

            var c = text[pos];
            if (c == '{')
            {
                return ParseBraceGroup();
            }

            if (c is '}' or '&' or '^' or '_')
            {
                throw Error("Missing argument.");
            }

            if (c == '\\')
            {
                return ParseAtom();
            }

            pos++;
            return Letter(c);
        }

        private MathNode ParseBraceGroup()
        {
            Enter();
            pos++;
            var saved = style;
            var body = ParseExpression(Stop.Brace);
            style = saved;
            pos++;
            Leave();
            return body is MathGroup ? body : new MathGroup([body]);
        }

        private MathNode ParseAtom()
        {
            var c = text[pos];
            if (c == '{')
            {
                return ParseBraceGroup();
            }

            if (c == '\\')
            {
                return ParseCommand();
            }

            if (char.IsAsciiDigit(c) || (c == '.' && pos + 1 < text.Length && char.IsAsciiDigit(text[pos + 1])))
            {
                var start = pos;
                while (pos < text.Length && (char.IsAsciiDigit(text[pos]) || (text[pos] == '.' && pos + 1 < text.Length && char.IsAsciiDigit(text[pos + 1]))))
                {
                    pos++;
                }

                return new MathRun(Styled(text[start..pos]), style ?? MathStyle.Normal, MathRunKind.Number);
            }

            pos++;
            if (char.IsLetter(c))
            {
                return Letter(c);
            }

            return c switch
            {
                '+' => Run("+", MathRunKind.Operator),
                '-' => Run("−", MathRunKind.Operator),
                '*' => Run("∗", MathRunKind.Operator),
                '/' => Run("/", MathRunKind.Operator),
                '=' or '<' or '>' => Run(c.ToString(), MathRunKind.Relation),
                ':' => Run(":", MathRunKind.Relation),
                ',' or ';' => Run(c.ToString(), MathRunKind.Punctuation),
                '!' => Run("!", MathRunKind.Punctuation),
                '\'' => Run("′", MathRunKind.Symbol),
                '(' or ')' or '[' or ']' or '|' => Run(c.ToString(), MathRunKind.Delimiter),
                '~' => new MathSpace(0.5),
                '.' => Run(".", MathRunKind.Punctuation),
                _ => Run(c.ToString(), MathRunKind.Symbol),
            };
        }

        private MathRun Run(string value, MathRunKind kind) => new(value, style ?? MathStyle.Normal, kind);

        private MathRun Letter(char c)
        {
            var effective = style ?? MathStyle.Italic;
            return new MathRun(Styled(c.ToString()), effective, char.IsLetter(c) ? MathRunKind.Identifier : MathRunKind.Symbol);
        }

        private string Styled(string value) => style == MathStyle.DoubleStruck ? Mappings.DoubleStruck(value) : value;

        private string ReadName()
        {
            pos++;
            if (pos >= text.Length)
            {
                throw Error("Dangling backslash.");
            }

            if (!char.IsAsciiLetter(text[pos]))
            {
                return text[pos++].ToString();
            }

            var start = pos;
            while (pos < text.Length && char.IsAsciiLetter(text[pos]))
            {
                pos++;
            }

            return text[start..pos];
        }

        private string ReadRawGroup()
        {
            SkipSpaces();
            if (pos >= text.Length || text[pos] != '{')
            {
                throw Error("Expected {.");
            }

            var start = ++pos;
            var nesting = 1;
            while (pos < text.Length)
            {
                if (text[pos] == '\\' && pos + 1 < text.Length)
                {
                    pos += 2;
                    continue;
                }

                if (text[pos] == '{')
                {
                    nesting++;
                }
                else if (text[pos] == '}' && --nesting == 0)
                {
                    return text[start..pos++];
                }

                pos++;
            }

            throw Error("Missing closing }.");
        }

        private MathNode ParseCommand()
        {
            var commandStart = pos;
            var name = ReadName();
            switch (name)
            {
                case "frac" or "dfrac" or "tfrac" or "binom" or "dbinom":
                {
                    Enter();
                    var numerator = ParseArgument();
                    var denominator = ParseArgument();
                    Leave();
                    return new MathFraction(numerator, denominator, !name.Contains("binom", StringComparison.Ordinal));
                }

                case "sqrt":
                    return ParseRoot();
                case "text" or "textrm" or "mbox" or "textit" or "textbf" or "operatorname":
                    return ParseText(name);
                case "mathbf" or "boldsymbol" or "bm":
                    return WithStyle(MathStyle.Bold);
                case "mathrm":
                    return WithStyle(MathStyle.Roman);
                case "mathbb":
                    return WithStyle(MathStyle.DoubleStruck);
                case "mathcal" or "mathscr":
                    return WithStyle(MathStyle.Calligraphic);
                case "mathfrak":
                    return WithStyle(MathStyle.Fraktur);
                case "mathsf":
                    return WithStyle(MathStyle.SansSerif);
                case "mathtt":
                    return WithStyle(MathStyle.Monospace);
                case "mathit":
                    return WithStyle(MathStyle.Italic);
                case "left":
                    return ParseDelimited();
                case "begin":
                    return ParseEnvironment();
                case "hat" or "bar" or "tilde" or "vec" or "dot" or "ddot" or "overline" or "underline" or "widehat" or "widetilde" when Mappings.Accents.ContainsKey(name):
                {
                    Enter();
                    var inner = ParseArgument();
                    Leave();
                    return new MathAccent(inner, Mappings.Accents[name]);
                }

                case "," or ";" or ":" or "!" or " " or "quad" or "qquad" or "thinspace" or "enspace":
                    return new MathSpace(name switch
                    {
                        "," or "thinspace" => 0.17,
                        ":" => 0.22,
                        ";" => 0.28,
                        "!" => -0.17,
                        " " => 0.33,
                        "enspace" => 0.5,
                        "quad" => 1,
                        _ => 2,
                    });
                case "{" or "}" or "%" or "$" or "&" or "#" or "_":
                    return Run(name, MathRunKind.Symbol);
                case "\\":
                    return new MathLineBreak();
            }

            if (Mappings.Symbols.TryGetValue(name, out var symbol))
            {
                return new MathRun(symbol.Text, style ?? (symbol.Kind == MathRunKind.Identifier ? MathStyle.Italic : MathStyle.Normal), symbol.Kind);
            }

            if (Mappings.FunctionNames.Contains(name))
            {
                return new MathRun(name, MathStyle.Roman, MathRunKind.FunctionName);
            }

            pos = commandStart;
            throw Error($"Unsupported command \\{name}.");
        }

        private MathNode WithStyle(MathStyle newStyle)
        {
            var saved = style;
            style = newStyle;
            Enter();
            try
            {
                return ParseArgument();
            }
            finally
            {
                Leave();
                style = saved;
            }
        }

        private MathNode ParseRoot()
        {
            Enter();
            MathNode? index = null;
            SkipSpaces();
            if (pos < text.Length && text[pos] == '[')
            {
                pos++;
                var start = pos;
                var nesting = 0;
                while (pos < text.Length && !(text[pos] == ']' && nesting == 0))
                {
                    nesting += text[pos] switch { '{' => 1, '}' => -1, _ => 0 };
                    pos++;
                }

                if (pos >= text.Length)
                {
                    throw Error("Missing ] in root index.");
                }

                var sub = new Parser(text[start..pos]) { depth = depth };
                index = sub.ParseDocument();
                pos++;
            }

            var radicand = ParseArgument();
            Leave();
            return new MathRoot(radicand, index);
        }

        private MathNode ParseText(string command)
        {
            var raw = ReadRawGroup();
            var textStyle = command switch
            {
                "textbf" => MathStyle.Bold,
                "textit" => MathStyle.Italic,
                _ => MathStyle.Roman,
            };
            var kind = command == "operatorname" ? MathRunKind.FunctionName : MathRunKind.Text;
            return new MathRun(raw.Replace("\\ ", " ", StringComparison.Ordinal), textStyle, kind);
        }

        private string ReadDelimiter()
        {
            SkipSpaces();
            if (pos >= text.Length)
            {
                throw Error("Missing delimiter.");
            }

            if (text[pos] == '.')
            {
                pos++;
                return string.Empty;
            }

            if (text[pos] == '\\')
            {
                var start = pos;
                var name = ReadName();
                if (name is "{" or "}")
                {
                    return name;
                }

                if (name == "|")
                {
                    return "‖";
                }

                if (Mappings.Delimiters.TryGetValue(name, out var delimiter))
                {
                    return delimiter;
                }

                pos = start;
                throw Error($"Unsupported delimiter \\{name}.");
            }

            var c = text[pos++];
            if (c is '(' or ')' or '[' or ']' or '|' or '/' or '<' or '>')
            {
                return c.ToString();
            }

            pos--;
            throw Error("Unsupported delimiter.");
        }

        private MathNode ParseDelimited()
        {
            Enter();
            var left = ReadDelimiter();
            var body = ParseExpression(Stop.Right);
            pos += "\\right".Length;
            var right = ReadDelimiter();
            Leave();
            return new MathDelimited(left, right, body);
        }

        private MathNode ParseEnvironment()
        {
            var name = ReadRawGroup().Trim();
            if (!Mappings.Environments.TryGetValue(name, out var env))
            {
                throw Error($"Unsupported environment {name}.");
            }

            Enter();
            var rows = new List<IReadOnlyList<MathNode>>();
            var cells = new List<MathNode>();
            while (true)
            {
                cells.Add(ParseExpression(Stop.Environment));
                if (pos < text.Length && text[pos] == '&')
                {
                    pos++;
                    continue;
                }

                if (LookingAt("\\\\"))
                {
                    pos += 2;
                    rows.Add(cells);
                    cells = [];
                    continue;
                }

                if (AtCommand("end"))
                {
                    pos += "\\end".Length;
                    var closing = ReadRawGroup().Trim();
                    if (closing != name)
                    {
                        throw Error($"\\end{{{closing}}} does not match \\begin{{{name}}}.");
                    }

                    break;
                }
            }

            if (cells.Count > 1 || cells[0] is not MathGroup { Children.Count: 0 })
            {
                rows.Add(cells);
            }

            Leave();
            return new MathMatrix(rows, env.Left, env.Right, env.Kind);
        }
    }

    private static class Mappings
    {
        public static readonly Dictionary<string, string> Accents = new(StringComparer.Ordinal)
        {
            ["hat"] = "\u0302",
            ["widehat"] = "\u0302",
            ["bar"] = "\u0304",
            ["overline"] = "\u0305",
            ["tilde"] = "\u0303",
            ["widetilde"] = "\u0303",
            ["vec"] = "\u20D7",
            ["dot"] = "\u0307",
            ["ddot"] = "\u0308",
            ["underline"] = "\u0332",
        };

        public static readonly Dictionary<string, (string Left, string Right, MathMatrixKind Kind)> Environments = new(StringComparer.Ordinal)
        {
            ["matrix"] = (string.Empty, string.Empty, MathMatrixKind.Matrix),
            ["pmatrix"] = ("(", ")", MathMatrixKind.Matrix),
            ["bmatrix"] = ("[", "]", MathMatrixKind.Matrix),
            ["Bmatrix"] = ("{", "}", MathMatrixKind.Matrix),
            ["vmatrix"] = ("|", "|", MathMatrixKind.Matrix),
            ["Vmatrix"] = ("‖", "‖", MathMatrixKind.Matrix),
            ["cases"] = ("{", string.Empty, MathMatrixKind.Cases),
            ["aligned"] = (string.Empty, string.Empty, MathMatrixKind.Aligned),
            ["align"] = (string.Empty, string.Empty, MathMatrixKind.Aligned),
            ["gathered"] = (string.Empty, string.Empty, MathMatrixKind.Aligned),
            ["split"] = (string.Empty, string.Empty, MathMatrixKind.Aligned),
        };

        public static readonly Dictionary<string, string> Delimiters = new(StringComparer.Ordinal)
        {
            ["langle"] = "⟨",
            ["rangle"] = "⟩",
            ["lfloor"] = "⌊",
            ["rfloor"] = "⌋",
            ["lceil"] = "⌈",
            ["rceil"] = "⌉",
            ["vert"] = "|",
            ["Vert"] = "‖",
            ["lbrace"] = "{",
            ["rbrace"] = "}",
        };

        public static readonly HashSet<string> FunctionNames = new(StringComparer.Ordinal)
        {
            "sin", "cos", "tan", "cot", "sec", "csc", "arcsin", "arccos", "arctan", "sinh", "cosh", "tanh",
            "log", "ln", "lg", "exp", "lim", "limsup", "liminf", "max", "min", "sup", "inf", "det", "dim",
            "ker", "gcd", "deg", "arg", "Pr", "hom", "mod",
        };

        public static readonly Dictionary<string, (string Text, MathRunKind Kind)> Symbols = BuildSymbols();

        private const string DoubleStruckSource = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

        private static readonly string[] DoubleStruckTarget =
        [
            "𝔸", "𝔹", "ℂ", "𝔻", "𝔼", "𝔽", "𝔾", "ℍ", "𝕀", "𝕁", "𝕂", "𝕃", "𝕄", "ℕ", "𝕆", "ℙ", "ℚ", "ℝ", "𝕊", "𝕋", "𝕌", "𝕍", "𝕎", "𝕏", "𝕐", "ℤ",
            "𝕒", "𝕓", "𝕔", "𝕕", "𝕖", "𝕗", "𝕘", "𝕙", "𝕚", "𝕛", "𝕜", "𝕝", "𝕞", "𝕟", "𝕠", "𝕡", "𝕢", "𝕣", "𝕤", "𝕥", "𝕦", "𝕧", "𝕨", "𝕩", "𝕪", "𝕫",
            "𝟘", "𝟙", "𝟚", "𝟛", "𝟜", "𝟝", "𝟞", "𝟟", "𝟠", "𝟡",
        ];

        public static string DoubleStruck(string value)
        {
            var builder = new System.Text.StringBuilder(value.Length);
            foreach (var ch in value)
            {
                var index = DoubleStruckSource.IndexOf(ch, StringComparison.Ordinal);
                builder.Append(index >= 0 ? DoubleStruckTarget[index] : ch.ToString(CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private static Dictionary<string, (string, MathRunKind)> BuildSymbols()
        {
            var map = new Dictionary<string, (string, MathRunKind)>(StringComparer.Ordinal);

            void Add(MathRunKind kind, params string[] pairs)
            {
                foreach (var pair in pairs)
                {
                    var split = pair.IndexOf('=', StringComparison.Ordinal);
                    map[pair[..split]] = (pair[(split + 1)..], kind);
                }
            }

            Add(MathRunKind.Identifier,
                "alpha=α", "beta=β", "gamma=γ", "delta=δ", "epsilon=ϵ", "varepsilon=ε", "zeta=ζ", "eta=η", "theta=θ",
                "vartheta=ϑ", "iota=ι", "kappa=κ", "lambda=λ", "mu=μ", "nu=ν", "xi=ξ", "pi=π", "varpi=ϖ", "rho=ρ",
                "varrho=ϱ", "sigma=σ", "varsigma=ς", "tau=τ", "upsilon=υ", "phi=ϕ", "varphi=φ", "chi=χ", "psi=ψ", "omega=ω",
                "Gamma=Γ", "Delta=Δ", "Theta=Θ", "Lambda=Λ", "Xi=Ξ", "Pi=Π", "Sigma=Σ", "Upsilon=Υ", "Phi=Φ", "Psi=Ψ", "Omega=Ω",
                "ell=ℓ", "hbar=ℏ", "imath=ı", "jmath=ȷ");
            Add(MathRunKind.Operator,
                "cdot=⋅", "times=×", "div=÷", "pm=±", "mp=∓", "ast=∗", "star=⋆", "circ=∘", "bullet=•", "oplus=⊕", "otimes=⊗",
                "cup=∪", "cap=∩", "setminus=∖", "wedge=∧", "land=∧", "vee=∨", "lor=∨", "neg=¬", "lnot=¬", "cdots=⋯", "ldots=…",
                "dots=…", "vdots=⋮", "ddots=⋱", "dagger=†", "ddagger=‡");
            Add(MathRunKind.Relation,
                "leq=≤", "le=≤", "geq=≥", "ge=≥", "neq=≠", "ne=≠", "approx=≈", "equiv=≡", "sim=∼", "simeq=≃", "cong=≅", "propto=∝",
                "ll=≪", "gg=≫", "in=∈", "notin=∉", "ni=∋", "subset=⊂", "supset=⊃", "subseteq=⊆", "supseteq=⊇", "perp=⊥",
                "parallel=∥", "mid=∣", "to=→", "rightarrow=→", "leftarrow=←", "leftrightarrow=↔", "Rightarrow=⇒", "Leftarrow=⇐",
                "Leftrightarrow=⇔", "iff=⇔", "implies=⇒", "mapsto=↦", "uparrow=↑", "downarrow=↓", "longrightarrow=⟶", "gets=←",
                "colon=:", "vdash=⊢", "models=⊨");
            Add(MathRunKind.Symbol,
                "infty=∞", "partial=∂", "nabla=∇", "forall=∀", "exists=∃", "nexists=∄", "emptyset=∅", "varnothing=∅", "angle=∠",
                "triangle=△", "prime=′", "aleph=ℵ", "Re=ℜ", "Im=ℑ", "wp=℘", "top=⊤", "bot=⊥", "degree=°", "checkmark=✓",
                "langle=⟨", "rangle=⟩", "lbrace={", "rbrace=}", "vert=|", "Vert=‖", "lfloor=⌊", "rfloor=⌋", "lceil=⌈", "rceil=⌉");
            Add(MathRunKind.BigOperator,
                "sum=∑", "prod=∏", "coprod=∐", "int=∫", "iint=∬", "iiint=∭", "oint=∮", "bigcup=⋃", "bigcap=⋂", "bigoplus=⨁", "bigotimes=⨂",
                "bigwedge=⋀", "bigvee=⋁");
            return map;
        }
    }
}
