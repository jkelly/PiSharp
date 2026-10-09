// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/tui/src/latex.ts.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Tui.Pi;

/// <summary>Renders basic LaTeX math as terminal-friendly Unicode text.</summary>
public static partial class Latex
{
    private static readonly Dictionary<string, string> Symbols = new(StringComparer.Ordinal)
    {
        ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ", ["epsilon"] = "ϵ", ["varepsilon"] = "ε", ["zeta"] = "ζ", ["eta"] = "η",
        ["theta"] = "θ", ["vartheta"] = "ϑ", ["iota"] = "ι", ["kappa"] = "κ", ["varkappa"] = "ϰ", ["lambda"] = "λ", ["mu"] = "μ", ["nu"] = "ν",
        ["xi"] = "ξ", ["pi"] = "π", ["varpi"] = "ϖ", ["rho"] = "ρ", ["varrho"] = "ϱ", ["sigma"] = "σ", ["varsigma"] = "ς", ["tau"] = "τ",
        ["upsilon"] = "υ", ["phi"] = "ϕ", ["varphi"] = "φ", ["chi"] = "χ", ["psi"] = "ψ", ["omega"] = "ω", ["Gamma"] = "Γ", ["Delta"] = "Δ",
        ["Theta"] = "Θ", ["Lambda"] = "Λ", ["Xi"] = "Ξ", ["Pi"] = "Π", ["Sigma"] = "Σ", ["Upsilon"] = "Υ", ["Phi"] = "Φ", ["Psi"] = "Ψ", ["Omega"] = "Ω",
        ["pm"] = "±", ["mp"] = "∓", ["times"] = "×", ["div"] = "÷", ["cdot"] = "·", ["ast"] = "∗", ["star"] = "⋆", ["circ"] = "∘", ["bullet"] = "•",
        ["oplus"] = "⊕", ["ominus"] = "⊖", ["otimes"] = "⊗", ["oslash"] = "⊘", ["odot"] = "⊙", ["bigcirc"] = "○", ["dagger"] = "†", ["ddagger"] = "‡",
        ["amalg"] = "⨿", ["uplus"] = "⊎", ["sqcap"] = "⊓", ["sqcup"] = "⊔", ["bowtie"] = "⋈", ["Join"] = "⋈", ["ltimes"] = "⋉", ["rtimes"] = "⋊",
        ["leftouterjoin"] = "⟕", ["rightouterjoin"] = "⟖", ["fullouterjoin"] = "⟗", ["triangleleft"] = "◁", ["triangleright"] = "▷", ["wr"] = "≀",
        ["cap"] = "∩", ["cup"] = "∪", ["bigcap"] = "⋂", ["bigcup"] = "⋃", ["bigwedge"] = "⋀", ["bigvee"] = "⋁", ["bigsqcup"] = "⨆", ["biguplus"] = "⨄",
        ["bigoplus"] = "⨁", ["bigotimes"] = "⨂", ["bigodot"] = "⨀", ["setminus"] = "∖", ["in"] = "∈", ["notin"] = "∉", ["ni"] = "∋", ["subset"] = "⊂",
        ["supset"] = "⊃", ["subseteq"] = "⊆", ["supseteq"] = "⊇", ["sqsubset"] = "⊏", ["sqsupset"] = "⊐", ["sqsubseteq"] = "⊑", ["sqsupseteq"] = "⊒",
        ["prec"] = "≺", ["preceq"] = "≼", ["succ"] = "≻", ["succeq"] = "≽", ["ll"] = "≪", ["gg"] = "≫", ["le"] = "≤", ["leq"] = "≤", ["leqslant"] = "≤",
        ["ge"] = "≥", ["geq"] = "≥", ["geqslant"] = "≥", ["ne"] = "≠", ["neq"] = "≠", ["equiv"] = "≡", ["approx"] = "≈", ["sim"] = "∼", ["simeq"] = "≃",
        ["cong"] = "≅", ["asymp"] = "≍", ["doteq"] = "≐", ["propto"] = "∝", ["parallel"] = "∥", ["perp"] = "⊥", ["mid"] = "∣", ["vdash"] = "⊢",
        ["dashv"] = "⊣", ["models"] = "⊨", ["Vdash"] = "⊩", ["Vvdash"] = "⊪", ["nvdash"] = "⊬", ["nvDash"] = "⊭", ["forall"] = "∀", ["exists"] = "∃",
        ["nexists"] = "∄", ["neg"] = "¬", ["land"] = "∧", ["wedge"] = "∧", ["lor"] = "∨", ["vee"] = "∨", ["to"] = "→", ["rightarrow"] = "→",
        ["longrightarrow"] = "→", ["leftarrow"] = "←", ["longleftarrow"] = "←", ["gets"] = "←", ["leftrightarrow"] = "↔", ["longleftrightarrow"] = "↔",
        ["hookleftarrow"] = "↩", ["hookrightarrow"] = "↪", ["twoheadleftarrow"] = "↞", ["twoheadrightarrow"] = "↠", ["leftharpoonup"] = "↼",
        ["leftharpoondown"] = "↽", ["rightharpoonup"] = "⇀", ["rightharpoondown"] = "⇁", ["rightleftharpoons"] = "⇌", ["leftrightharpoons"] = "⇋",
        ["nearrow"] = "↗", ["searrow"] = "↘", ["swarrow"] = "↙", ["nwarrow"] = "↖", ["rightsquigarrow"] = "⇝", ["leadsto"] = "⇝", ["Rightarrow"] = "⇒",
        ["Longrightarrow"] = "⇒", ["Leftarrow"] = "⇐", ["Longleftarrow"] = "⇐", ["Leftrightarrow"] = "⇔", ["Longleftrightarrow"] = "⇔", ["implies"] = "⇒",
        ["iff"] = "⇔", ["mapsto"] = "↦", ["longmapsto"] = "↦", ["uparrow"] = "↑", ["downarrow"] = "↓", ["partial"] = "∂", ["nabla"] = "∇", ["int"] = "∫",
        ["iint"] = "∬", ["iiint"] = "∭", ["oint"] = "∮", ["sum"] = "∑", ["prod"] = "∏", ["coprod"] = "∐", ["infty"] = "∞", ["emptyset"] = "∅",
        ["varnothing"] = "∅", ["angle"] = "∠", ["therefore"] = "∴", ["because"] = "∵", ["aleph"] = "ℵ", ["beth"] = "ℶ", ["gimel"] = "ℷ", ["daleth"] = "ℸ",
        ["top"] = "⊤", ["bot"] = "⊥", ["triangle"] = "△", ["square"] = "□", ["lozenge"] = "◊", ["checkmark"] = "✓", ["complement"] = "∁", ["wp"] = "℘",
        ["prime"] = "′", ["ldots"] = "…", ["dots"] = "…", ["cdots"] = "⋯", ["vdots"] = "⋮", ["ddots"] = "⋱", ["ell"] = "ℓ", ["hbar"] = "ℏ", ["Im"] = "ℑ",
        ["Re"] = "ℜ", ["langle"] = "⟨", ["rangle"] = "⟩", ["vert"] = "|", ["lvert"] = "|", ["rvert"] = "|", ["Vert"] = "‖", ["lVert"] = "‖", ["rVert"] = "‖",
        ["lbrace"] = "{", ["rbrace"] = "}", ["backslash"] = "\\", ["lfloor"] = "⌊", ["rfloor"] = "⌋", ["lceil"] = "⌈", ["rceil"] = "⌉", ["colon"] = ":",
    };

    private static readonly HashSet<string> NamedOperators = new(StringComparer.Ordinal)
    {
        "arccos", "arcsin", "arctan", "arg", "cos", "cosh", "cot", "coth", "csc", "deg", "det", "dim", "exp", "gcd", "hom", "inf", "ker", "lg", "lim",
        "liminf", "limsup", "ln", "log", "max", "min", "Pr", "sec", "sin", "sinh", "sup", "tan", "tanh",
    };
    private static readonly HashSet<string> LimitOperators = new(StringComparer.Ordinal)
    {
        "argmax", "argmin", "inf", "injlim", "lim", "liminf", "limsup", "max", "min", "projlim", "sup",
    };
    private static readonly HashSet<string> DisplayLimitSymbols = new(StringComparer.Ordinal)
    {
        "bigcap", "bigcup", "bigodot", "bigoplus", "bigotimes", "bigsqcup", "biguplus", "bigvee", "bigwedge", "coprod", "int", "iint", "iiint", "oint", "prod", "sum",
    };
    private static readonly HashSet<string> RelationCommands = new(StringComparer.Ordinal)
    {
        "Leftarrow", "Leftrightarrow", "Longleftarrow", "Longleftrightarrow", "Longrightarrow", "Rightarrow", "Join", "Vdash", "Vvdash", "approx", "asymp",
        "bowtie", "cong", "dashv", "fullouterjoin", "doteq", "downarrow", "equiv", "ge", "geq", "geqslant", "gets", "gg", "hookleftarrow", "hookrightarrow",
        "iff", "implies", "in", "leadsto", "le", "leftarrow", "leftharpoondown", "leftharpoonup", "leftrightarrow", "leftrightharpoons", "leftouterjoin", "leq",
        "leqslant", "ll", "longleftarrow", "longleftrightarrow", "longmapsto", "longrightarrow", "ltimes", "mapsto", "mid", "models", "ne", "nearrow", "neq",
        "ni", "notin", "nvdash", "nvDash", "nwarrow", "parallel", "perp", "prec", "preceq", "propto", "rightharpoondown", "rightharpoonup", "rightleftharpoons",
        "rightouterjoin", "rightarrow", "rightsquigarrow", "rtimes", "searrow", "sim", "simeq", "sqsubset", "sqsubseteq", "sqsupset", "sqsupseteq", "subset",
        "subseteq", "succ", "succeq", "supset", "supseteq", "swarrow", "to", "triangleleft", "triangleright", "twoheadleftarrow", "twoheadrightarrow", "uparrow", "vdash",
    };

    private static readonly Dictionary<string, string> NegatedSymbols = new(StringComparer.Ordinal)
    {
        ["<"] = "≮", [">"] = "≯", ["="] = "≠", ["∈"] = "∉", ["∋"] = "∌", ["∣"] = "∤", ["∥"] = "∦", ["∼"] = "≁", ["≃"] = "≄", ["≅"] = "≇", ["≈"] = "≉",
        ["≡"] = "≢", ["≤"] = "≰", ["≥"] = "≱", ["≺"] = "⊀", ["≻"] = "⊁", ["⊂"] = "⊄", ["⊃"] = "⊅", ["⊆"] = "⊈", ["⊇"] = "⊉", ["⊢"] = "⊬", ["⊨"] = "⊭",
        ["↔"] = "↮", ["←"] = "↚", ["→"] = "↛", ["⇒"] = "⇏", ["⇐"] = "⇍", ["⇔"] = "⇎", ["≼"] = "⋠", ["≽"] = "⋡",
    };
    private static readonly Dictionary<string, string> Blackboard = new(StringComparer.Ordinal)
    {
        ["C"] = "ℂ", ["H"] = "ℍ", ["N"] = "ℕ", ["P"] = "ℙ", ["Q"] = "ℚ", ["R"] = "ℝ", ["Z"] = "ℤ",
    };
    private static readonly Dictionary<string, string> Superscripts = new(StringComparer.Ordinal)
    {
        ["0"] = "⁰", ["1"] = "¹", ["2"] = "²", ["3"] = "³", ["4"] = "⁴", ["5"] = "⁵", ["6"] = "⁶", ["7"] = "⁷", ["8"] = "⁸", ["9"] = "⁹", ["+"] = "⁺", ["-"] = "⁻",
        ["="] = "⁼", ["("] = "⁽", [")"] = "⁾", ["a"] = "ᵃ", ["b"] = "ᵇ", ["c"] = "ᶜ", ["d"] = "ᵈ", ["e"] = "ᵉ", ["f"] = "ᶠ", ["g"] = "ᵍ", ["h"] = "ʰ", ["i"] = "ⁱ",
        ["j"] = "ʲ", ["k"] = "ᵏ", ["l"] = "ˡ", ["m"] = "ᵐ", ["n"] = "ⁿ", ["o"] = "ᵒ", ["p"] = "ᵖ", ["r"] = "ʳ", ["s"] = "ˢ", ["t"] = "ᵗ", ["u"] = "ᵘ", ["v"] = "ᵛ",
        ["w"] = "ʷ", ["x"] = "ˣ", ["y"] = "ʸ", ["z"] = "ᶻ",
    };
    private static readonly Dictionary<string, string> Subscripts = new(StringComparer.Ordinal)
    {
        ["0"] = "₀", ["1"] = "₁", ["2"] = "₂", ["3"] = "₃", ["4"] = "₄", ["5"] = "₅", ["6"] = "₆", ["7"] = "₇", ["8"] = "₈", ["9"] = "₉", ["+"] = "₊", ["-"] = "₋",
        ["="] = "₌", ["("] = "₍", [")"] = "₎", ["a"] = "ₐ", ["e"] = "ₑ", ["h"] = "ₕ", ["i"] = "ᵢ", ["j"] = "ⱼ", ["k"] = "ₖ", ["l"] = "ₗ", ["m"] = "ₘ", ["n"] = "ₙ",
        ["o"] = "ₒ", ["p"] = "ₚ", ["r"] = "ᵣ", ["s"] = "ₛ", ["t"] = "ₜ", ["u"] = "ᵤ", ["v"] = "ᵥ", ["x"] = "ₓ",
    };

    private static readonly HashSet<string> SpacingCommands = new(StringComparer.Ordinal)
    {
        ",", ":", ";", " ", ">", "enspace", "enskip", "medspace", "quad", "qquad", "thickspace", "thinspace",
    };
    private static readonly HashSet<string> NegativeSpacingCommands = new(StringComparer.Ordinal) { "!", "negmedspace", "negthickspace", "negthinspace" };
    private const string NegativeSpace = "\0";
    private static readonly HashSet<string> FontSwitchCommands = new(StringComparer.Ordinal) { "bf", "cal", "it", "rm", "sf", "sl", "tt" };
    private static readonly HashSet<string> IgnoredCommands = new(StringComparer.Ordinal)
    {
        "displaystyle", "limits", "nolimits", "scriptstyle", "scriptscriptstyle", "textstyle",
    };
    private static readonly HashSet<string> SizeCommands = new(StringComparer.Ordinal)
    {
        "big", "Big", "bigg", "Bigg", "bigl", "Bigl", "biggl", "Biggl", "bigr", "Bigr", "biggr", "Biggr",
    };
    private static readonly HashSet<string> PlainWrappers = new(StringComparer.Ordinal)
    {
        "emph", "mathcal", "mathbf", "mathfrak", "mathit", "mathrm", "mathnormal", "mathscr", "mathsf", "mathtt", "mathup", "mbox", "overbrace", "pmb",
        "smash", "substack", "text", "textbf", "textit", "textmd", "textnormal", "textrm", "textsc", "textsf", "textsl", "texttt", "textup", "underbrace",
        "bm", "boldsymbol",
    };
    private static readonly Dictionary<string, string> Accents = new(StringComparer.Ordinal)
    {
        ["acute"] = "\x0301", ["bar"] = "\x0305", ["breve"] = "\x0306", ["check"] = "\x030c", ["ddot"] = "\x0308", ["dot"] = "\x0307", ["grave"] = "\x0300",
        ["hat"] = "\x0302", ["mathring"] = "\x030a", ["overleftarrow"] = "\x20d6", ["overleftrightarrow"] = "\x20e1", ["overline"] = "\x0305",
        ["overrightarrow"] = "\x20d7", ["tilde"] = "\x0303", ["underline"] = "\x0332", ["vec"] = "\x20d7", ["widehat"] = "\x0302", ["widetilde"] = "\x0303",
    };
    private static readonly Dictionary<string, string[]> MatrixDelimiters = new(StringComparer.Ordinal)
    {
        ["pmatrix"] = ["⎛", "⎞", "⎜", "⎟", "⎝", "⎠"], ["bmatrix"] = ["⎡", "⎤", "⎢", "⎥", "⎣", "⎦"], ["Bmatrix"] = ["⎧", "⎫", "⎨", "⎬", "⎩", "⎭"],
        ["vmatrix"] = ["│", "│", "│", "│", "│", "│"], ["Vmatrix"] = ["║", "║", "║", "║", "║", "║"],
    };

    private static readonly string LayoutMarkerStart = char.ConvertFromUtf32(0xF0000);
    private static readonly string LayoutMarkerEnd = char.ConvertFromUtf32(0xF0001);
    private static readonly string ProtectedSpace = char.ConvertFromUtf32(0xF0002);
    private static readonly string NamedOperatorStart = char.ConvertFromUtf32(0xF0004);
    private static readonly string NamedOperatorEnd = char.ConvertFromUtf32(0xF0005);
    private static readonly Regex LayoutMarkerPattern = new(LayoutMarkerStart + "([0-9]+)" + LayoutMarkerEnd, RegexOptions.CultureInvariant);
    private static readonly Regex TrailingLayoutMarkerPattern = new(LayoutMarkerStart + "([0-9]+)" + LayoutMarkerEnd + @"\z", RegexOptions.CultureInvariant);
    [GeneratedRegex("[ \t]+")] private static partial Regex HorizontalWhitespace();
    [GeneratedRegex(@"\\\\(?:\[[^\]\n]*\])?")] private static partial Regex EnvironmentRowSeparator();
    [GeneratedRegex(@"\G\\(limits|nolimits)(?![A-Za-z])")] private static partial Regex LimitsModifier();

    /// <summary>
    /// Render a basic LaTeX math expression as terminal-friendly Unicode text (upstream <c>renderLatex</c>).
    /// <paramref name="display"/> stacks fractions and operator limits vertically. Returns null for unsupported or malformed syntax.
    /// </summary>
    public static string? Render(string source, bool display = false)
    {
        var layoutNodes = new List<LayoutNode>();
        var rendered = new LatexParser(source, layoutNodes, display).Render();
        if (rendered is null) return null;
        if (layoutNodes.Count == 0) return rendered.Replace(ProtectedSpace, " ", StringComparison.Ordinal);
        var lines = RenderLayout(rendered, layoutNodes).Lines;
        var indentation = int.MaxValue;
        foreach (var line in lines) if (TextUtils.JsTrim(line).Length > 0) indentation = Math.Min(indentation, line.Length - TextUtils.JsTrimStart(line).Length);
        return TextUtils.JsTrimEnd(string.Join("\n", lines.Select(line => TextUtils.JsTrimEnd(indentation >= line.Length ? "" : line[indentation..]))))
            .Replace(ProtectedSpace, " ", StringComparison.Ordinal);
    }

    // JavaScript `[...value]`: code points, keeping lone surrogates as single elements.
    private static List<string> CodePoints(string value)
    {
        var result = new List<string>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) { result.Add(value.Substring(i, 2)); i++; }
            else result.Add(value[i].ToString());
        }
        return result;
    }

    private static int CodePointCount(string value)
    {
        var count = 0;
        for (var i = 0; i < value.Length; i++, count++) if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) i++;
        return count;
    }

    private static UnicodeCategory? Category(string codePoint) => codePoint.Length == 2 ? CharUnicodeInfo.GetUnicodeCategory(char.ConvertToUtf32(codePoint[0], codePoint[1]))
        : char.IsSurrogate(codePoint[0]) ? null : CharUnicodeInfo.GetUnicodeCategory(codePoint[0]);
    private static bool IsLetter(string codePoint) => Category(codePoint) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
        UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter;
    private static bool IsNumber(string codePoint) => Category(codePoint) is UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;

    // /^[\p{L}\p{N}.]+$/u (letters allowed) or /^[\p{N}.]+$/u.
    private static bool IsSimple(string value, bool letters)
    {
        if (value.Length == 0) return false;
        foreach (var codePoint in CodePoints(value)) if (codePoint != "." && !IsNumber(codePoint) && !(letters && IsLetter(codePoint))) return false;
        return true;
    }

    private static string? ReplaceCharacters(string value, Dictionary<string, string> replacements)
    {
        var result = new StringBuilder();
        foreach (var character in CodePoints(value))
        {
            if (!replacements.TryGetValue(character, out var replacement)) return null;
            result.Append(replacement);
        }
        return result.ToString();
    }

    // value.trim().replace(/\s*([=+-])\s*/g, "$1")
    private static string NormalizeScriptValue(string value)
    {
        value = TextUtils.JsTrim(value);
        var result = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is '=' or '+' or '-')
            {
                var end = result.Length;
                while (end > 0 && TextUtils.IsJsWhitespace(result[end - 1])) end--;
                result.Length = end;
                result.Append(c);
                while (i + 1 < value.Length && TextUtils.IsJsWhitespace(value[i + 1])) i++;
            }
            else result.Append(c);
        }
        return result.ToString();
    }

    private static string? FormatUnicodeScript(string value, bool sub) => ReplaceCharacters(NormalizeScriptValue(value), sub ? Subscripts : Superscripts);

    private static string FormatScript(string value, bool sub)
    {
        value = NormalizeScriptValue(value);
        var unicode = FormatUnicodeScript(value, sub);
        if (unicode is not null) return unicode;
        var prefix = sub ? "_" : "^";
        if (CodePointCount(value) == 1 || (sub && value.Length > 0 && value.All(char.IsAsciiLetter))) return $"{prefix}{value}";
        return $"{prefix}({value})";
    }

    private static string FormatFraction(string numerator, string denominator)
    {
        numerator = TextUtils.JsTrim(numerator);
        denominator = TextUtils.JsTrim(denominator);
        var simpleNumerator = IsSimple(numerator, true);
        var simpleDenominator = IsSimple(denominator, false) || CodePointCount(denominator) == 1;
        return $"{(simpleNumerator ? numerator : $"({numerator})")}/{(simpleDenominator ? denominator : $"({denominator})")}";
    }

    private static string FormatRoot(string value, string symbol = "√")
    {
        value = TextUtils.JsTrim(value);
        return IsSimple(value, true) ? $"{symbol}{value}" : $"{symbol}({value})";
    }

    private static string? PreviousCodePoint(string value, int index) => index <= 0 ? null
        : index >= 2 && char.IsLowSurrogate(value[index - 1]) && char.IsHighSurrogate(value[index - 2]) ? value.Substring(index - 2, 2) : value[index - 1].ToString();
    private static string? NextCodePoint(string value, int index) => index >= value.Length ? null
        : index + 1 < value.Length && char.IsHighSurrogate(value[index]) && char.IsLowSurrogate(value[index + 1]) ? value.Substring(index, 2) : value[index].ToString();

    // Replaces /(?<=[\p{L}\p{N})\]}\u{f0001}])\u{f0004}/gu with " " (other starts removed), then /\u{f0005}(?=[\p{L}\p{N}√\u{f0000}])/gu likewise.
    private static string SpaceNamedOperators(string value)
    {
        var left = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (string.CompareOrdinal(value, i, NamedOperatorStart, 0, NamedOperatorStart.Length) == 0)
            {
                var previous = PreviousCodePoint(value, i);
                if (previous is not null && (IsLetter(previous) || IsNumber(previous) || previous is ")" or "]" or "}" || previous == LayoutMarkerEnd)) left.Append(' ');
                i += NamedOperatorStart.Length - 1;
            }
            else left.Append(value[i]);
        }
        var spaced = left.ToString();
        var right = new StringBuilder(spaced.Length);
        for (var i = 0; i < spaced.Length; i++)
        {
            if (string.CompareOrdinal(spaced, i, NamedOperatorEnd, 0, NamedOperatorEnd.Length) == 0)
            {
                var next = NextCodePoint(spaced, i + NamedOperatorEnd.Length);
                if (next is not null && (IsLetter(next) || IsNumber(next) || next == "√" || next == LayoutMarkerStart)) right.Append(' ');
                i += NamedOperatorEnd.Length - 1;
            }
            else right.Append(spaced[i]);
        }
        return right.ToString();
    }

    private static string NormalizeOutput(string value)
    {
        var lines = SpaceNamedOperators(value).Split('\n').Select(line => TextUtils.JsTrim(HorizontalWhitespace().Replace(line, " "))).ToArray();
        return TextUtils.JsTrim(string.Join("\n", lines.Where((line, index) => line.Length > 0 || (index > 0 && index < lines.Length - 1))));
    }

    private enum NodeType { Fraction, Operator, Script, Matrix }

    private sealed class LayoutNode(NodeType type)
    {
        public NodeType Type { get; } = type;
        public string Numerator { get; init; } = "";
        public string Denominator { get; init; } = "";
        public string Operator { get; init; } = "";
        public string? Lower { get; init; }
        public string? Upper { get; init; }
        public List<string> Lines { get; init; } = [];
        public int Baseline { get; init; }
    }

    private sealed record Layout(List<string> Lines, int Width, int Baseline);

    private static string PadLayoutLine(string line, int width, bool centered = false)
    {
        var padding = Math.Max(0, width - TextUtils.VisibleWidth(line));
        var left = centered ? padding / 2 : 0;
        return $"{new string(' ', left)}{line}{new string(' ', padding - left)}";
    }

    private static Layout JoinLayouts(List<Layout> layouts)
    {
        if (layouts.Count == 0) return new([""], 0, 0);
        var baseline = layouts.Max(layout => layout.Baseline);
        var below = layouts.Max(layout => layout.Lines.Count - layout.Baseline - 1);
        var lines = new List<string>();
        for (var row = 0; row <= baseline + below; row++)
        {
            var line = new StringBuilder();
            foreach (var layout in layouts)
            {
                var sourceRow = row - baseline + layout.Baseline;
                line.Append(sourceRow >= 0 && sourceRow < layout.Lines.Count ? PadLayoutLine(layout.Lines[sourceRow], layout.Width) : new string(' ', layout.Width));
            }
            lines.Add(TextUtils.JsTrimEnd(line.ToString()));
        }
        return new(lines, layouts.Sum(layout => layout.Width), baseline);
    }

    private static LayoutNode? NodeAt(List<LayoutNode> nodes, string index) =>
        int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value < nodes.Count ? nodes[value] : null;

    private static Layout RenderLayout(string source, List<LayoutNode> nodes)
    {
        var renderedLines = new List<string>();
        var firstBaseline = 0;
        foreach (var sourceLine in source.Split('\n'))
        {
            var layouts = new List<Layout>();
            var position = 0;
            LayoutNode? previousNode = null;
            foreach (Match match in LayoutMarkerPattern.Matches(sourceLine))
            {
                var index = match.Index;
                var node = NodeAt(nodes, match.Groups[1].Value);
                if (node is null) continue;
                if (index > position)
                {
                    var sliced = sourceLine[position..index];
                    var trimmed = TextUtils.JsTrimEnd(previousNode is not null ? TextUtils.JsTrimStart(sliced) : sliced);
                    var preserveLeadingSpace = previousNode?.Type == NodeType.Matrix && TextUtils.IsJsWhitespace(sliced[0]);
                    var preserveTrailingSpace = node.Type == NodeType.Matrix && TextUtils.IsJsWhitespace(sliced[^1]);
                    var text = trimmed.Length > 0 ? $"{(preserveLeadingSpace ? " " : "")}{trimmed}{(preserveTrailingSpace ? " " : "")}"
                        : preserveLeadingSpace || preserveTrailingSpace ? " " : "";
                    layouts.Add(new([text], TextUtils.VisibleWidth(text), 0));
                }
                if (node.Type == NodeType.Fraction)
                {
                    var numerator = RenderLayout(node.Numerator, nodes);
                    var denominator = RenderLayout(node.Denominator, nodes);
                    var contentWidth = Math.Max(Math.Max(numerator.Width, denominator.Width), 1);
                    var width = contentWidth + 2;
                    var lines = numerator.Lines.Select(line => PadLayoutLine(line, width, true)).ToList();
                    lines.Add($" {new string('─', contentWidth)} ");
                    lines.AddRange(denominator.Lines.Select(line => PadLayoutLine(line, width, true)));
                    layouts.Add(new(lines, width, numerator.Lines.Count));
                }
                else if (node.Type == NodeType.Operator)
                {
                    var contentWidth = Math.Max(TextUtils.VisibleWidth(node.Operator),
                        Math.Max(node.Lower is null ? 0 : TextUtils.VisibleWidth(node.Lower), node.Upper is null ? 0 : TextUtils.VisibleWidth(node.Upper)));
                    var lines = new List<string>();
                    if (node.Upper is not null) lines.Add($"{PadLayoutLine(node.Upper, contentWidth, true)} ");
                    lines.Add($"{PadLayoutLine(node.Operator, contentWidth, true)} ");
                    if (node.Lower is not null) lines.Add($"{PadLayoutLine(node.Lower, contentWidth, true)} ");
                    layouts.Add(new(lines, contentWidth + 1, node.Upper is null ? 0 : 1));
                }
                else if (node.Type == NodeType.Script)
                {
                    var upper = node.Upper is null ? null : RenderLayout(node.Upper, nodes);
                    var lower = node.Lower is null ? null : RenderLayout(node.Lower, nodes);
                    var width = Math.Max(upper?.Width ?? 0, lower?.Width ?? 0);
                    var lines = upper?.Lines.Select(line => PadLayoutLine(line, width)).ToList() ?? [];
                    lines.Add(new string(' ', width));
                    if (lower is not null) lines.AddRange(lower.Lines.Select(line => PadLayoutLine(line, width)));
                    layouts.Add(new(lines, width, upper?.Lines.Count ?? 0));
                }
                else
                {
                    var width = node.Lines.Count == 0 ? 0 : Math.Max(0, node.Lines.Max(TextUtils.VisibleWidth));
                    layouts.Add(new(node.Lines.Select(line => PadLayoutLine(line, width)).ToList(), width, node.Baseline));
                }
                position = index + match.Length;
                previousNode = node;
            }
            if (position < sourceLine.Length)
            {
                var sliced = sourceLine[position..];
                var trimmed = previousNode is not null ? TextUtils.JsTrimStart(sliced) : sliced;
                var text = previousNode?.Type == NodeType.Matrix && TextUtils.IsJsWhitespace(sliced[0]) ? $" {trimmed}" : trimmed;
                layouts.Add(new([text], TextUtils.VisibleWidth(text), 0));
            }
            var lineLayout = JoinLayouts(layouts);
            if (renderedLines.Count == 0) firstBaseline = lineLayout.Baseline;
            renderedLines.AddRange(lineLayout.Lines);
        }
        return new(renderedLines, renderedLines.Count == 0 ? 0 : Math.Max(0, renderedLines.Max(TextUtils.VisibleWidth)), firstBaseline);
    }

    // body.replace(/^\s*\{[^}]*\}/, "")
    private static string StripLeadingBraceGroup(string body)
    {
        var i = 0;
        while (i < body.Length && TextUtils.IsJsWhitespace(body[i])) i++;
        if (i >= body.Length || body[i] != '{') return body;
        var end = body.IndexOf('}', i + 1);
        return end < 0 ? body : body[(end + 1)..];
    }

    // value.replace(/,\s*$/, "")
    private static string StripTrailingComma(string value)
    {
        var end = value.Length;
        while (end > 0 && TextUtils.IsJsWhitespace(value[end - 1])) end--;
        return end > 0 && value[end - 1] == ',' ? value[..(end - 1)] : value;
    }

    // /^(?:if|when|for|otherwise)\b/i
    private static bool StartsWithConditionWord(string condition)
    {
        foreach (var word in (string[])["if", "when", "for", "otherwise"])
        {
            if (condition.Length < word.Length) continue;
            var matches = true;
            for (var i = 0; i < word.Length && matches; i++) matches = condition[i] < 128 && char.ToLowerInvariant(condition[i]) == word[i];
            if (matches && (condition.Length == word.Length || !(char.IsAsciiLetterOrDigit(condition[word.Length]) || condition[word.Length] == '_'))) return true;
        }
        return false;
    }

    private sealed class LatexParser(string source, List<LayoutNode> layoutNodes, bool display)
    {
        private int position;
        private bool supported = true;
        private bool stackFractions = true;
        private int scriptDepth;

        public string? Render()
        {
            var rendered = ParseSequence();
            if (!supported || position != source.Length) return null;
            return NormalizeOutput(rendered);
        }

        private string PushNode(LayoutNode node)
        {
            layoutNodes.Add(node);
            return $"{LayoutMarkerStart}{layoutNodes.Count - 1}{LayoutMarkerEnd}";
        }

        private string ParseSequence(char? endCharacter = null)
        {
            var result = "";
            while (position < source.Length)
            {
                var character = source[position];
                if (endCharacter is not null && character == endCharacter) { position++; return result; }
                if (character == '}') { supported = false; return result; }
                if (character == '{') { position++; result += ParseSequence('}'); continue; }
                if (character == '\\')
                {
                    var command = ParseCommand();
                    if (command == NegativeSpace)
                    {
                        result = TextUtils.JsTrimEnd(result);
                        if (result.EndsWith(NamedOperatorEnd, StringComparison.Ordinal)) result = result[..^NamedOperatorEnd.Length];
                    }
                    else result += command;
                    continue;
                }
                if (character is '^' or '_')
                {
                    position++;
                    result = TextUtils.JsTrimEnd(result);
                    var script = ParseScripts(character);
                    result = result.EndsWith(NamedOperatorEnd, StringComparison.Ordinal) ? $"{result[..^NamedOperatorEnd.Length]}{script}{NamedOperatorEnd}" : result + script;
                    continue;
                }
                if (TextUtils.IsJsWhitespace(character)) { result += ParseWhitespace(); continue; }
                if (character is '=' or '<' or '>') { result = $"{TextUtils.JsTrimEnd(result)} {character} "; position++; continue; }
                if (character == '&') { position++; continue; }
                if (character == '~') { position++; result += " "; continue; }
                if (character == '.')
                {
                    var marker = TrailingLayoutMarkerPattern.Match(result);
                    var node = marker.Success ? NodeAt(layoutNodes, marker.Groups[1].Value) : null;
                    if (node?.Type == NodeType.Matrix)
                    {
                        var lastLine = node.Lines.Count - 1;
                        node.Lines[lastLine] = $"{node.Lines[lastLine]}{character}";
                        position++;
                        continue;
                    }
                }
                result += character;
                position++;
            }
            if (endCharacter is not null) supported = false;
            return result;
        }

        private string ParseScripts(char initialMarker)
        {
            string? sub = null, sup = null;
            var order = new List<bool>();
            void Parse(char marker)
            {
                var isSub = marker == '_';
                string value;
                scriptDepth++;
                try { value = ParseRequiredArgument(false); }
                finally { scriptDepth--; }
                if (isSub) sub = value; else sup = value;
                order.Add(isSub);
            }

            Parse(initialMarker);
            var nextPosition = position;
            while (nextPosition < source.Length && TextUtils.IsJsWhitespace(source[nextPosition])) nextPosition++;
            if (nextPosition < source.Length && source[nextPosition] is '^' or '_' && source[nextPosition] != initialMarker)
            {
                position = nextPosition + 1;
                Parse(source[nextPosition]);
            }

            var subUnicode = sub is null ? null : FormatUnicodeScript(sub, true);
            var supUnicode = sup is null ? null : FormatUnicodeScript(sup, false);
            static bool Blocks(string? value) => value is not null && (value.Contains('/') || (!value.Contains(LayoutMarkerStart, StringComparison.Ordinal) &&
                CodePointCount(value) > 1 && !value.Any(c => c is >= 'A' and <= 'Z' or '*' or '∗')));
            var canUseLayout = !Blocks(sub) && !Blocks(sup);
            var needsLayout = display && canUseLayout && (scriptDepth > 0 || (sub is not null && subUnicode is null) || (sup is not null && supUnicode is null));
            if (!needsLayout) return string.Concat(order.Select(isSub => isSub ? subUnicode ?? FormatScript(sub ?? "", true) : supUnicode ?? FormatScript(sup ?? "", false)));
            return PushNode(new(NodeType.Script) { Lower = sub is null ? null : NormalizeOutput(sub), Upper = sup is null ? null : NormalizeOutput(sup) });
        }

        private string ParseWhitespace()
        {
            while (position < source.Length && TextUtils.IsJsWhitespace(source[position])) position++;
            return " ";
        }

        private string ParseCommand()
        {
            position++;
            if (position >= source.Length) { supported = false; return ""; }

            string command;
            var first = source[position];
            if (first is '\n' or '\r')
            {
                position++;
                if (first == '\r' && position < source.Length && source[position] == '\n') position++;
                return " ";
            }
            if (char.IsAsciiLetter(first))
            {
                var start = position;
                while (position < source.Length && char.IsAsciiLetter(source[position])) position++;
                command = source[start..position];
            }
            else
            {
                command = first.ToString();
                position++;
            }

            if (command == "\\") return "\n";
            if (SpacingCommands.Contains(command)) return " ";
            if (NegativeSpacingCommands.Contains(command)) return NegativeSpace;
            if (FontSwitchCommands.Contains(command))
            {
                while (position < source.Length && TextUtils.IsJsWhitespace(source[position])) position++;
                return "";
            }
            if (IgnoredCommands.Contains(command)) return "";
            if (command is "{" or "}" or "$" or "%" or "#" or "_" or "&") return command;
            if (command == "|") return "‖";
            if (command == "not")
            {
                var value = TextUtils.JsTrim(ParseRequiredArgument(false));
                if (NegatedSymbols.TryGetValue(value, out var negated)) return $" {negated} ";
                var characters = CodePoints(value);
                if (characters.Count == 0) { supported = false; return ""; }
                return $" {characters[0]}\x0338{string.Concat(characters.Skip(1))} ";
            }
            if (LimitOperators.Contains(command)) return ParseOperator(command, true, true, true);

            if (Symbols.TryGetValue(command, out var symbol))
            {
                if (DisplayLimitSymbols.Contains(command)) return ParseOperator(symbol, false, true);
                return command is "cdot" or "times" || RelationCommands.Contains(command) ? $" {symbol} " : symbol;
            }
            if (NamedOperators.Contains(command)) return $"{NamedOperatorStart}{command}{NamedOperatorEnd}";
            if (SizeCommands.Contains(command)) return "";
            if (command is "left" or "middle" or "right")
            {
                if (position < source.Length && source[position] == '.') position++;
                return "";
            }
            if (command is "frac" or "dfrac" or "tfrac")
            {
                var shouldStack = display && stackFractions && command != "tfrac";
                var numerator = ParseRequiredArgument(!shouldStack);
                var denominator = ParseRequiredArgument(!shouldStack);
                if (shouldStack) return PushNode(new(NodeType.Fraction) { Numerator = NormalizeOutput(numerator), Denominator = NormalizeOutput(denominator) });
                return FormatFraction(numerator, denominator);
            }
            if (command == "sqrt")
            {
                var degree = ParseOptionalArgument() is { } optional ? TextUtils.JsTrim(optional) : null;
                var value = ParseRequiredArgument();
                if (degree is null or "2") return FormatRoot(value);
                if (degree == "3") return FormatRoot(value, "∛");
                if (degree == "4") return FormatRoot(value, "∜");
                return $"{FormatScript(degree, false)}{FormatRoot(value)}";
            }
            if (command is "boxed" or "fbox") return $"[{TextUtils.JsTrim(ParseRequiredArgument())}]";
            if (command is "binom" or "dbinom" or "tbinom")
            {
                var top = ParseRequiredArgument();
                return $"({top} choose {ParseRequiredArgument()})";
            }
            if (Accents.TryGetValue(command, out var accent))
            {
                var value = ParseRequiredArgument();
                return CodePointCount(value) == 1 ? $"{value}{accent}" : $"{command}({value})";
            }
            if (command == "mathbb") return string.Concat(CodePoints(ParseRequiredArgument()).Select(c => Blackboard.TryGetValue(c, out var letter) ? letter : c));
            if (command == "operatorname")
            {
                var starred = position < source.Length && source[position] == '*';
                if (starred) position++;
                var @operator = TextUtils.JsTrim(NormalizeOutput(ParseRequiredArgument()));
                return ParseOperator(@operator, true, starred, true);
            }
            if (command is "mod" or "bmod") return " mod ";
            if (command is "pmod" or "pod")
            {
                var value = TextUtils.JsTrim(ParseRequiredArgument());
                return command == "pmod" ? $" (mod {value})" : $" ({value})";
            }
            if (command is "overset" or "stackrel")
            {
                var upper = ParseRequiredArgument();
                var value = TextUtils.JsTrim(ParseRequiredArgument());
                return $"{value}{FormatScript(upper, false)}";
            }
            if (command == "underset")
            {
                var lower = ParseRequiredArgument();
                var value = TextUtils.JsTrim(ParseRequiredArgument());
                return $"{value}{FormatScript(lower, true)}";
            }
            if (PlainWrappers.Contains(command))
            {
                var value = ParseRequiredArgument();
                return command.StartsWith("text", StringComparison.Ordinal) || command == "mbox" ? value : TextUtils.JsTrim(value);
            }
            if (command == "begin") return ParseEnvironment();
            if (command == "end") { supported = false; return ""; }

            supported = false;
            return $"\\{command}";
        }

        private string ParseOperator(string @operator, bool bracketLower, bool displayLimits, bool spaced = false)
        {
            var useDisplayLimits = displayLimits;
            var modifierPosition = position;
            while (modifierPosition < source.Length && source[modifierPosition] is ' ' or '\t') modifierPosition++;
            var modifier = LimitsModifier().Match(source, Math.Min(modifierPosition, source.Length));
            if (modifier.Success)
            {
                useDisplayLimits = modifier.Groups[1].Value == "limits";
                position = modifierPosition + modifier.Length;
            }

            string? lower = null, upper = null;
            while (true)
            {
                var scriptPosition = position;
                while (scriptPosition < source.Length && source[scriptPosition] is ' ' or '\t') scriptPosition++;
                if (scriptPosition >= source.Length || source[scriptPosition] is not ('_' or '^')) break;
                var kind = source[scriptPosition];
                position = scriptPosition + 1;
                var value = NormalizeOutput(ParseRequiredArgument(false)).Replace(" ", "", StringComparison.Ordinal);
                if (kind == '_')
                {
                    if (lower is not null) supported = false;
                    lower = value;
                }
                else
                {
                    if (upper is not null) supported = false;
                    upper = value;
                }
            }

            if (display && useDisplayLimits && (lower is not null || upper is not null))
                return PushNode(new(NodeType.Operator) { Operator = @operator, Lower = lower, Upper = upper });

            var rendered = @operator;
            if (lower is not null) rendered += bracketLower ? $"[{lower}]" : FormatScript(lower, true);
            if (upper is not null) rendered += FormatScript(upper, false);
            return spaced ? $" {rendered} " : rendered;
        }

        private string ParseRequiredArgument(bool stackFractions = true)
        {
            var previousStackFractions = this.stackFractions;
            this.stackFractions = previousStackFractions && stackFractions;
            var value = ParseRequiredArgumentValue();
            this.stackFractions = previousStackFractions;
            return value;
        }

        private string ParseRequiredArgumentValue()
        {
            while (position < source.Length && TextUtils.IsJsWhitespace(source[position])) position++;
            if (position >= source.Length) { supported = false; return ""; }
            if (source[position] == '{') { position++; return ParseSequence('}'); }
            if (source[position] == '\\') return ParseCommand();
            return source[position++].ToString();
        }

        private string? ParseOptionalArgument()
        {
            while (position < source.Length && source[position] is ' ' or '\t') position++;
            if (position >= source.Length || source[position] != '[') return null;
            var end = source.IndexOf(']', position + 1);
            if (end < 0) { supported = false; return null; }
            var value = source[(position + 1)..end];
            position = end + 1;
            return RenderNested(value);
        }

        private string? ReadRawGroup()
        {
            while (position < source.Length && source[position] is ' ' or '\t') position++;
            if (position >= source.Length || source[position] != '{') { supported = false; return null; }

            var start = ++position;
            var depth = 1;
            while (position < source.Length)
            {
                var character = source[position];
                if (character == '\\') { position += 2; continue; }
                if (character == '{') depth++;
                if (character == '}') depth--;
                if (depth == 0)
                {
                    var value = source[start..position];
                    position++;
                    return value;
                }
                position++;
            }
            supported = false;
            return null;
        }

        private static string[] SplitEnvironmentRows(string body) => EnvironmentRowSeparator().Split(body);

        private string ParseEnvironment()
        {
            var environment = ReadRawGroup();
            if (string.IsNullOrEmpty(environment)) return "";
            var endMarker = $"\\end{{{environment}}}";
            var end = source.IndexOf(endMarker, position, StringComparison.Ordinal);
            if (end < 0) { supported = false; return ""; }
            var body = source[position..end];
            position = end + endMarker.Length;

            if (environment is "equation" or "equation*" or "displaymath") return TextUtils.JsTrim(RenderNested(body));

            if (environment is "aligned" or "align" or "align*" or "alignedat" or "alignat" or "alignat*" or "gather" or "gathered" or "multline" or "multline*" or "split")
            {
                var alignedAt = environment is "alignedat" or "alignat" or "alignat*";
                var alignedBody = alignedAt ? StripLeadingBraceGroup(body) : body;
                var rows = new List<string>();
                foreach (var row in SplitEnvironmentRows(alignedBody))
                {
                    var cells = row.Split('&');
                    var rowSource = alignedAt
                        ? string.Join(" ", Enumerable.Range(0, (cells.Length + 1) / 2).Select(index => string.Concat(cells.Skip(index * 2).Take(2))))
                        : string.Concat(cells);
                    rows.Add(TextUtils.JsTrim(RenderNested(rowSource)));
                }
                return string.Join("\n", rows.Where(row => row.Length > 0));
            }

            if (environment is "cases" or "cases*") return RenderCases(body);

            if (environment is "array" or "matrix" or "smallmatrix" or "pmatrix" or "bmatrix" or "Bmatrix" or "vmatrix" or "Vmatrix")
                return RenderMatrix(environment, environment == "array" ? StripLeadingBraceGroup(body) : body);

            supported = false;
            return body;
        }

        private List<List<string>> RenderCells(string body)
        {
            var rows = new List<List<string>>();
            foreach (var row in SplitEnvironmentRows(body)) rows.Add(row.Split('&').Select(cell => TextUtils.JsTrim(RenderNested(cell, false))).ToList());
            return rows.Where(row => row.Any(cell => cell.Length > 0)).ToList();
        }

        private string RenderCases(string body)
        {
            var rows = RenderCells(body);
            var valueWidth = rows.Count == 0 ? 0 : Math.Max(0, rows.Max(row => TextUtils.VisibleWidth(StripTrailingComma(row[0]))));
            var contents = rows.Select(row =>
            {
                var value = StripTrailingComma(row[0]);
                var condition = row.Count > 1 ? row[1] : "";
                if (condition.Length == 0) return value;
                var conditionPrefix = StartsWithConditionWord(condition) ? " " : " if ";
                return $"{value}{TextUtils.Repeat(ProtectedSpace, valueWidth - TextUtils.VisibleWidth(value))}{conditionPrefix}{condition}";
            }).ToList();
            if (contents.Count <= 1) return contents.Count == 0 ? "" : $"⎧ {contents[0]}";

            var middle = contents.Count / 2;
            var visualRows = new List<string?>(contents);
            if (contents.Count % 2 == 0) visualRows.Insert(middle, null);
            var lines = visualRows.Select((content, index) =>
            {
                var delimiter = index == 0 ? "⎧" : index == visualRows.Count - 1 ? "⎩" : "⎨";
                return content is null ? delimiter : $"{delimiter} {content}";
            }).ToList();
            return PushNode(new(NodeType.Matrix) { Lines = lines, Baseline = middle });
        }

        private string RenderMatrix(string environment, string body)
        {
            var matrix = RenderCells(body);
            var columnCount = matrix.Count == 0 ? 0 : matrix.Max(row => row.Count);
            var columnWidths = Enumerable.Range(0, columnCount).Select(column => matrix.Max(row => column < row.Count ? TextUtils.VisibleWidth(row[column]) : 0)).ToArray();
            var rows = matrix.Select(row => string.Join(" │ ", Enumerable.Range(0, columnCount).Select(column =>
            {
                var cell = column < row.Count ? row[column] : "";
                return $"{cell}{TextUtils.Repeat(ProtectedSpace, Math.Max(0, columnWidths[column] - TextUtils.VisibleWidth(cell)))}";
            }))).ToList();

            List<string> lines;
            if (environment is "array" or "matrix" or "smallmatrix") lines = rows;
            else
            {
                if (!MatrixDelimiters.TryGetValue(environment, out var delimiter))
                {
                    supported = false;
                    return string.Join("\n", rows);
                }
                lines = rows.Select((row, index) =>
                {
                    var left = index == 0 ? delimiter[0] : index == rows.Count - 1 ? delimiter[4] : delimiter[2];
                    var right = index == 0 ? delimiter[1] : index == rows.Count - 1 ? delimiter[5] : delimiter[3];
                    return $"{left} {row} {right}";
                }).ToList();
            }

            if (lines.Count <= 1) return lines.Count == 0 ? "" : lines[0];
            return PushNode(new(NodeType.Matrix) { Lines = lines, Baseline = 0 });
        }

        private string RenderNested(string nested, bool stackFractions = true)
        {
            var rendered = new LatexParser(nested, layoutNodes, display && stackFractions).Render();
            if (rendered is null)
            {
                supported = false;
                return nested;
            }
            return rendered;
        }
    }
}
