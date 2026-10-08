// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/interactive/theme/system-theme.ts.
namespace PiSharp.CodingAgent.Export;

/// <summary>What the terminal reported (pi-tui TerminalColors): default colors and ANSI palette 0-15.</summary>
public sealed record TerminalColorReport(RgbChannels? Foreground = null, RgbChannels? Background = null, IReadOnlyList<RgbChannels>? Palette = null);

/// <summary>Input of <see cref="SystemTheme.Generate"/> (SystemThemeInput).</summary>
public sealed record SystemThemeInput(RgbChannels? Foreground = null, RgbChannels? Background = null, IReadOnlyList<RgbChannels>? Palette = null,
    double? Saturation = null, string? AppearanceHint = null);

/// <summary>Generated token values in token order: hex strings, palette indices (double) or "" for the terminal default.</summary>
public sealed record SystemThemeColors(IReadOnlyList<KeyValuePair<string, object>> Colors, IReadOnlyList<string> Dim, string? Appearance);

/// <summary>The <c>system</c> theme: pi's colors derived from the terminal's own theme.</summary>
public static class SystemTheme
{
    public const string Name = "system";

    private sealed record Family(double Hue, double Min, double Max, int Slot);
    private static readonly Dictionary<string, Family> Families = new(StringComparer.Ordinal)
    {
        ["neutral"] = new(231.49, 0.02, 0.08, 8), ["blue"] = new(231.49, 0.1, 0.68, 4), ["green"] = new(158.68, 0.1, 0.76, 2),
        ["red"] = new(20, 0.1, 0.92, 1), ["yellow"] = new(82.36, 0.5, 1, 3), ["orange"] = new(52, 0.12, 0.85, 3),
        ["violet"] = new(295, 0.2, 0.6, 5), ["calamine"] = new(202.43, 0.1, 0.74, 6), ["thinkingSlate"] = new(231.49, 0.08, 0.2, 4),
        ["thinkingBlue"] = new(231.49, 0.2, 0.45, 4), ["thinkingPeriwinkle"] = new(263.25, 0.3, 0.6, 6), ["thinkingViolet"] = new(295, 0.4, 0.75, 5),
        ["thinkingMagenta"] = new(337.5, 0.5, 0.85, 13), ["thinkingRed"] = new(20, 0.95, 1, 1)
    };

    /// <summary>TOKEN_FAMILIES in declaration order (Object.keys order).</summary>
    private static readonly (string Token, string Family)[] TokenFamilies =
    [
        ("selectedBg", "blue"), ("searchMatchBg", "orange"), ("userMessageBg", "blue"), ("customMessageBg", "violet"), ("toolPendingBg", "neutral"),
        ("toolSuccessBg", "green"), ("toolErrorBg", "red"),
        ("text", "neutral"), ("userMessageText", "neutral"), ("customMessageText", "neutral"), ("toolTitle", "neutral"), ("syntaxOperator", "neutral"),
        ("syntaxPunctuation", "neutral"), ("muted", "neutral"), ("dim", "neutral"), ("thinkingText", "neutral"), ("toolOutput", "neutral"),
        ("mdLinkUrl", "neutral"), ("mdQuote", "neutral"), ("mdQuoteBorder", "neutral"), ("mdHr", "neutral"), ("mdCodeBlockBorder", "neutral"),
        ("toolDiffContext", "neutral"), ("syntaxComment", "neutral"), ("scrollbarTrack", "neutral"), ("scrollbarThumb", "neutral"),
        ("searchMatchText", "neutral"), ("borderMuted", "neutral"),
        ("accent", "violet"), ("borderAccent", "violet"), ("customMessageLabel", "violet"), ("mdCode", "violet"), ("mdListBullet", "violet"),
        ("syntaxType", "violet"), ("border", "blue"), ("mdLink", "blue"), ("syntaxKeyword", "blue"), ("syntaxVariable", "calamine"),
        ("success", "green"), ("mdCodeBlock", "green"), ("toolDiffAdded", "green"), ("bashMode", "green"), ("syntaxNumber", "green"),
        ("error", "red"), ("toolDiffRemoved", "red"), ("warning", "yellow"), ("mdHeading", "yellow"), ("syntaxFunction", "yellow"),
        ("syntaxString", "orange"),
        ("thinkingOff", "neutral"), ("thinkingMinimal", "thinkingSlate"), ("thinkingLow", "thinkingBlue"), ("thinkingMedium", "thinkingPeriwinkle"),
        ("thinkingHigh", "thinkingViolet"), ("thinkingXhigh", "thinkingMagenta"), ("thinkingMax", "thinkingRed")
    ];
    private static readonly Dictionary<string, string> FamilyOf = TokenFamilies.ToDictionary(pair => pair.Token, pair => pair.Family, StringComparer.Ordinal);
    private static readonly Dictionary<string, int> TokenSlots = new(StringComparer.Ordinal) { ["syntaxString"] = 2, ["syntaxNumber"] = 5, ["searchMatchBg"] = 3 };

    private sealed record Curve(double[] Coefficients, double ReachableLow, double ReachableHigh);
    private static Curve C(double low, double high, params double[] coefficients) => new(coefficients, low, high);
    private static readonly Dictionary<string, (Curve Dark, Curve Light)> Levels = new(StringComparer.Ordinal)
    {
        ["panel"] = (C(0, 0.979, 0.29131, -0.39746, 2.33185, -0.85524, -1.2076, 0.86276), C(0.348, 1, -3.74073, 27.94549, -78.44258, 112.6798, -79.60015, 22.11277)),
        ["track"] = (C(0, 0.946, 0.39028, -0.23015, 0.83573, 2.43829, -4.38292, 2.01582), C(0.368, 1, -5.24921, 38.37322, -107.28833, 152.10005, -106.17127, 29.18061)),
        ["thinking0"] = (C(0, 0.873, 0.52988, -0.05809, -0.30924, 4.63567, -6.52933, 2.89108), C(0.51, 1, -28.27749, 182.85284, -469.62416, 603.15916, -384.59976, 97.35147)),
        ["thinking1"] = (C(0, 0.858, 0.55278, -0.03667, -0.45659, 4.95347, -6.90265, 3.0706), C(0.535, 1, -37.10484, 235.86282, -596.62344, 754.3633, -474.00763, 118.3551)),
        ["thinking2"] = (C(0, 0.842, 0.57486, -0.01765, -0.58987, 5.25227, -7.27175, 3.25532), C(0.556, 1, -59.89653, 377.05024, -945.07843, 1182.03145, -734.96375, 181.68658)),
        ["thinking3"] = (C(0, 0.827, 0.59621, -0.00062, -0.71148, 5.53588, -7.6392, 3.44606), C(0.58, 1, -72.07122, 445.84082, -1099.57352, 1353.88793, -829.53392, 202.26164)),
        ["thinking4"] = (C(0, 0.811, 0.61691, 0.01462, -0.82288, 5.80651, -8.00641, 3.64333), C(0.6, 1, -110.14338, 674.21488, -1645.75941, 2004.32367, -1215.15899, 293.3183)),
        ["thinking5"] = (C(0, 0.795, 0.63702, 0.02826, -0.92498, 6.06465, -8.37246, 3.84651), C(0.62, 1, -175.47701, 1063.54495, -2570.70594, 3098.80776, -1860.15527, 444.76392)),
        ["thinking6"] = (C(0, 0.779, 0.65658, 0.04044, -1.01835, 6.30989, -8.73529, 4.05439), C(0.643, 1, -183.81712, 1094.70055, -2602.68539, 3088.71276, -1826.91131, 430.75931)),
        ["subtle"] = (C(0, 0.848, 0.56762, -0.02475, -0.5383, 5.12628, -7.10931, 3.17324), C(0.657, 1, -232.85459, 1376.54473, -3249.11801, 3827.91186, -2248.29472, 526.55751)),
        ["thumb"] = (C(0, 0.823, 0.60323, 0.00278, -0.73328, 5.57157, -7.68067, 3.46933), C(0.586, 1, -82.89897, 511.01355, -1255.98095, 1540.76821, -940.68087, 228.58523)),
        ["readable"] = (C(0, 0.77, 0.66937, 0.04704, -1.06871, 6.43941, -8.9332, 4.17229), C(0.751, 1, -1554.52576, 8733.56817, -19604.93507, 21977.72696, -12300.99599, 2749.81288)),
        ["emphasis"] = (C(0, 0.712, 0.7303, 0.07695, -1.31626, 7.1681, -10.14436, 4.92846), C(0.811, 1, -4948.31942, 26870.91986, -58334.48399, 63280.17197, -34298.01053, 7430.30146)),
        ["textOnPanel"] = (C(0, 0.542, 0.86713, 0.05232, -0.89428, 4.79014, -5.5432, 1.75023), C(0.867, 1, -8570.89457, 43954.60805, -90084.00702, 92220.6791, -47152.15802, 9632.27113)),
        ["text"] = (C(0, 0.5, 0.89242, 0.02311, -0.44862, 2.34417, -0.06084, -2.63844), C(0.894, 1, -2004.67048, 6664.47299, -6060.70202, -1792.61209, 5133.82359, -1939.85583))
    };

    private sealed record Rule(string Token, string[] On, string Level);
    private static readonly string[] ToolPanels = ["toolPendingBg", "toolSuccessBg", "toolErrorBg"];
    private static readonly string[] MessagePanels = ["userMessageBg", "customMessageBg"];
    private static readonly string[] Panels = ["userMessageBg", "toolPendingBg", "toolSuccessBg", "toolErrorBg", "selectedBg", "searchMatchBg", "customMessageBg"];
    private static readonly string[] Thinking = ["thinkingOff", "thinkingMinimal", "thinkingLow", "thinkingMedium", "thinkingHigh", "thinkingXhigh", "thinkingMax"];
    private static string[] On(params object[] parts) => parts.SelectMany(part => part is string[] many ? many : [(string)part]).ToArray();
    private static IEnumerable<Rule> Each(string[] tokens, string[] on, string level) => tokens.Select(token => new Rule(token, on, level));

    private static readonly Rule[] Rules =
    [
        .. Each(Panels, ["background"], "panel"),
        new("text", ["background"], "text"),
        new("text", ["selectedBg"], "textOnPanel"),
        new("userMessageText", ["userMessageBg"], "textOnPanel"),
        new("toolTitle", ToolPanels, "textOnPanel"),
        .. Each(["accent", "success", "error", "warning"], On("background", "selectedBg", ToolPanels), "readable"),
        new("muted", On("background", "selectedBg", "customMessageBg", ToolPanels), "readable"),
        new("dim", On("background", "selectedBg", "customMessageBg", ToolPanels), "subtle"),
        new("thinkingText", ["background"], "readable"),
        new("customMessageText", On("customMessageBg", ToolPanels), "readable"),
        new("customMessageLabel", On("background", "customMessageBg", "selectedBg", ToolPanels), "readable"),
        new("toolOutput", On("background", ToolPanels), "readable"),
        .. Each(["mdHeading", "mdLink", "mdLinkUrl", "mdCode", "mdQuote", "mdCodeBlockBorder", "mdListBullet"], On("background", MessagePanels), "readable"),
        new("mdCodeBlock", On("background", MessagePanels, ToolPanels), "readable"),
        .. Each(["toolDiffAdded", "toolDiffRemoved", "toolDiffContext"], On("background", ToolPanels), "readable"),
        .. Each(["syntaxComment", "syntaxKeyword", "syntaxFunction", "syntaxVariable", "syntaxString", "syntaxNumber", "syntaxType", "syntaxOperator",
            "syntaxPunctuation"], On("background", MessagePanels, ToolPanels), "readable"),
        new("searchMatchText", ["searchMatchBg"], "readable"),
        .. Each(["bashMode", "border", "borderAccent"], ["background"], "readable"),
        new("borderMuted", ["background"], "subtle"),
        .. Each(["mdQuoteBorder", "mdHr"], On("background", MessagePanels, ToolPanels), "readable"),
        new("scrollbarTrack", ["background"], "track"),
        new("scrollbarThumb", ["scrollbarTrack"], "thumb"),
        .. Thinking.Select((token, index) => new Rule(token, ["background"], "thinking" + index))
    ];

    private static string ReadableFloor(string appearance) => appearance == "dark" ? "readable" : "subtle";
    private const string ForegroundLevel = "emphasis";
    private static readonly string[] ForegroundTokens = ["text", "userMessageText", "toolTitle"];
    private const double TextMinimumWcagContrast = 4.5;

    private static readonly string[] SolveOrder = BuildSolveOrder();
    private static string[] BuildSolveOrder()
    {
        var order = new List<string>();
        void Visit(string token)
        {
            if (order.Contains(token)) return;
            foreach (var rule in Rules)
            {
                if (rule.Token != token) continue;
                foreach (var surface in rule.On) if (surface != "background") Visit(surface);
            }
            order.Add(token);
        }
        foreach (var rule in Rules) Visit(rule.Token);
        return [.. order];
    }

    private static double OklabLightness(RgbChannels color) => PiColors.ToOklch(PiColors.Rgb(color.R, color.G, color.B)).L;

    /// <summary>WCAG 2 relative luminance.</summary>
    public static double RelativeLuminance(RgbChannels color)
    {
        static double Linear(double channel) { var value = channel / 255; return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4); }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    /// <summary>WCAG 2 contrast ratio, 1-21.</summary>
    public static double WcagContrast(RgbChannels first, RgbChannels second)
    {
        var a = RelativeLuminance(first); var b = RelativeLuminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>Whether a terminal is dark or light from its reported colors (terminalAppearance).</summary>
    public static string TerminalAppearance(RgbChannels background, RgbChannels? foreground = null)
    {
        var white = new RgbChannels(255, 255, 255); var black = new RgbChannels(0, 0, 0);
        var whiteContrast = WcagContrast(white, background); var blackContrast = WcagContrast(black, background);
        if (foreground is { } fg)
        {
            var foregroundL = OklabLightness(fg); var backgroundL = OklabLightness(background);
            if (Math.Abs(foregroundL - backgroundL) > 0.05)
            {
                var appearance = foregroundL > backgroundL ? "dark" : "light";
                var best = appearance == "dark" ? whiteContrast : blackContrast;
                if (best >= TextMinimumWcagContrast) return appearance;
            }
        }
        return whiteContrast >= blackContrast ? "dark" : "light";
    }

    private static double Clamp(double value, double min, double max) => Math.Min(max, Math.Max(min, value));
    private static string HexOf(RgbChannels color) => "#" + Js.Hex2(color.R) + Js.Hex2(color.G) + Js.Hex2(color.B);

    private static double BellWeight(double lightness)
    {
        static double Gaussian(double x) => Math.Exp(-Math.Pow(x - 0.5, 2) / (2 * Math.Pow(0.25, 2)));
        return (Gaussian(lightness) - Gaussian(0)) / (1 - Gaussian(0));
    }

    private static double SaturationCurve(Family family, double lightness)
    {
        var floor = family.Max > 0 ? family.Min / family.Max : 1;
        return floor + (1 - floor) * BellWeight(lightness);
    }

    private static double? LevelTarget(string level, string appearance, double surfaceL)
    {
        var curve = appearance == "dark" ? Levels[level].Dark : Levels[level].Light;
        if (surfaceL < curve.ReachableLow || surfaceL > curve.ReachableHigh) return null;
        double sum = 0;
        for (var power = 0; power < curve.Coefficients.Length; power++) sum += curve.Coefficients[power] * Math.Pow(surfaceL, power);
        return sum;
    }

    private static RgbChannels ToChannels(PiColor.Rgb color) => new(color.R, color.G, color.B);

    /// <summary>generateSystemThemeColors.</summary>
    public static SystemThemeColors Generate(SystemThemeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var saturation = Clamp(input.Saturation ?? 1, 0, 1);
        if (input.Background is not { } background) return IndexedColors(saturation, input.AppearanceHint);
        var foreground = input.Foreground;
        var palette = input.Palette?.Count == 16 ? input.Palette.Select(SourceOf).ToArray() : null;
        var appearance = TerminalAppearance(background, foreground);
        var lighter = appearance == "dark";
        var extreme = lighter ? 1d : 0d;
        var backgroundL = OklabLightness(background);

        RgbChannels Paint(string token, double oklabL)
        {
            var lightness = Oklab.OklabToOkhslLightness(oklabL);
            var family = Families[FamilyOf[token]];
            if (palette is null)
                return ToChannels(PiColors.Okhsl(family.Hue, (family.Min + (family.Max - family.Min) * BellWeight(lightness)) * saturation, lightness));
            return Anchored(palette[TokenSlots.TryGetValue(token, out var slot) ? slot : family.Slot], family, lightness, saturation);
        }

        double? Target(string level, double surfaceL, double t)
        {
            var reached = LevelTarget(level, appearance, surfaceL);
            if (reached is null && t == 0) return null;
            var distance = (reached ?? extreme) - surfaceL;
            var floor = (LevelTarget(ReadableFloor(appearance), appearance, surfaceL) ?? extreme) - surfaceL;
            var compressed = Math.Abs(distance) > Math.Abs(floor) ? distance - (distance - floor) * Math.Min(t, 1) : distance;
            return surfaceL + compressed * (1 - Math.Max(0, t - 1));
        }

        var extremeText = lighter ? new RgbChannels(255, 255, 255) : new RgbChannels(0, 0, 0);
        bool Readable(RgbChannels color) => WcagContrast(extremeText, color) >= TextMinimumWcagContrast;
        RgbChannels LimitPanel(string token, double l)
        {
            var color = Paint(token, l);
            if (Readable(color)) return color;
            double low = backgroundL, high = l;
            for (var index = 0; index < 20; index++)
            {
                var middle = (low + high) / 2;
                if (Readable(Paint(token, middle))) low = middle; else high = middle;
            }
            return Paint(token, low);
        }

        Dictionary<string, RgbChannels>? Solve(double t)
        {
            var colors = new Dictionary<string, RgbChannels>(StringComparer.Ordinal) { ["background"] = background };
            foreach (var token in SolveOrder)
            {
                var targets = new List<double>();
                foreach (var rule in Rules)
                {
                    if (rule.Token != token) continue;
                    foreach (var surface in rule.On)
                    {
                        var value = Target(rule.Level, OklabLightness(colors.TryGetValue(surface, out var known) ? known : background), t);
                        if (value is not { } v || v < 0 || v > 1) return null;
                        targets.Add(v);
                    }
                }
                var l = lighter ? Js.Max([.. targets]) : Js.Min([.. targets]);
                colors[token] = Panels.Contains(token) ? LimitPanel(token, l) : Paint(token, l);
            }
            return colors;
        }

        double relaxation = 0;
        var solvedColors = Solve(0);
        if (solvedColors is null)
        {
            double low = 0, high = 2;
            solvedColors = Solve(high);
            for (var index = 0; index < 20; index++)
            {
                var middle = (low + high) / 2;
                var attempt = Solve(middle);
                if (attempt is not null) { high = middle; solvedColors = attempt; } else low = middle;
            }
            relaxation = high;
        }
        var solved = solvedColors ?? new Dictionary<string, RgbChannels>(StringComparer.Ordinal);
        List<RgbChannels> SurfacesOf(string token) => Rules.Where(rule => rule.Token == token)
            .SelectMany(rule => rule.On.Select(surface => solved.TryGetValue(surface, out var known) ? known : background)).ToList();

        var result = new List<KeyValuePair<string, object>>();
        var index_ = new Dictionary<string, int>(StringComparer.Ordinal);
        void SetResult(string token, object value)
        {
            if (index_.TryGetValue(token, out var at)) result[at] = new(token, value);
            else { index_[token] = result.Count; result.Add(new(token, value)); }
        }
        foreach (var (token, _) in TokenFamilies) SetResult(token, solved.TryGetValue(token, out var color) ? HexOf(color) : "");

        foreach (var token in ForegroundTokens)
        {
            var surfaces = SurfacesOf(token);
            RgbChannels? text = solved.TryGetValue(token, out var own) ? own : null;
            if (foreground is { } fg)
            {
                var targets = surfaces.Select(surface => Target(ForegroundLevel, OklabLightness(surface), relaxation)).ToList();
                if (targets.All(value => value is { } v && v >= 0 && v <= 1))
                {
                    var values = targets.Select(value => value!.Value).ToArray();
                    var needed = lighter ? Js.Max(values) : Js.Min(values);
                    var foregroundL = OklabLightness(fg);
                    if (lighter ? foregroundL >= needed : foregroundL <= needed) { SetResult(token, ""); continue; }
                    text = Anchored(SourceOf(fg), Families["neutral"], Oklab.OklabToOkhslLightness(needed), saturation);
                }
            }
            if (text is { } value) SetResult(token, HexOf(WithTextContrast(value, surfaces, lighter)));
        }
        return new(result, [], appearance);
    }

    private sealed record SourceColor(double H, double S, double L, double Chroma);

    private static OkhslChannels OkhslOf(RgbChannels color) => PiColors.ToOkhsl(PiColors.Rgb(color.R, color.G, color.B));

    private static SourceColor SourceOf(RgbChannels color)
    {
        var okhsl = OkhslOf(color);
        return new(okhsl.H, okhsl.S, okhsl.L, PiColors.ToOklch(PiColors.Rgb(color.R, color.G, color.B)).C);
    }

    private static RgbChannels Anchored(SourceColor source, Family family, double lightness, double saturation)
    {
        var anchor = SaturationCurve(family, source.L);
        var falloff = anchor > 0 ? Math.Min(1, SaturationCurve(family, lightness) / anchor) : 1;
        var color = PiColors.Okhsl(source.H, source.S * falloff * saturation, lightness);
        var cap = source.Chroma * falloff * saturation;
        var oklch = PiColors.ToOklch(color);
        return oklch.C <= cap ? ToChannels(color) : PiColors.ToRgb(PiColors.Oklch(oklch.L, cap, source.H));
    }

    private static RgbChannels WithTextContrast(RgbChannels color, List<RgbChannels> surfaces, bool lighter)
    {
        bool Meets(RgbChannels candidate) => surfaces.All(surface => WcagContrast(candidate, surface) >= TextMinimumWcagContrast);
        if (Meets(color)) return color;
        var okhsl = OkhslOf(color);
        RgbChannels At(double lightness) => ToChannels(PiColors.Okhsl(okhsl.H, okhsl.S, lightness));
        var extreme = lighter ? 1d : 0d;
        if (!Meets(At(extreme))) return At(extreme);
        double low = okhsl.L, high = extreme;
        for (var index = 0; index < 20; index++)
        {
            var middle = (low + high) / 2;
            if (Meets(At(middle))) high = middle; else low = middle;
        }
        return At(high);
    }

    private static SystemThemeColors IndexedColors(double saturation, string? appearance)
    {
        var colors = new List<KeyValuePair<string, object>>(); var dim = new List<string>();
        foreach (var (token, familyName) in TokenFamilies)
        {
            if (Panels.Contains(token)) { colors.Add(new(token, "")); continue; }
            var neutral = familyName == "neutral";
            colors.Add(new(token, !neutral && saturation > 0 ? (double)(TokenSlots.TryGetValue(token, out var slot) ? slot : Families[familyName].Slot) : ""));
            if (neutral && !ForegroundTokens.Contains(token)) dim.Add(token);
        }
        return new(colors, dim, appearance);
    }
}
